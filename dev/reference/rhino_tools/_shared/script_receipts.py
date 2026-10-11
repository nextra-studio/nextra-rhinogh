"""
Script-facing receipt semantics - how a generated script SEES a receipt.

A tool returns a plain dict. A generated script writes ``profile.ids``,
``bases[0]`` and ``cols.axes`` - it treats the receipt as the thing it
stands for, not as a mapping. This module is the adaptor between those
two views, and almost all of it exists to make ONE failure mode cheap:

    a line that reaches for the wrong field must not abort the script

When a tool fails it returns ``{"ok": False, "error": ...}`` with none of
the output keys. The next line of a build script is nearly always
``solid.extrude(ids=profile.ids, ...)``, and a bare AttributeError there
kills the whole ``exec`` - so one failed boolean in a loop leaves the
other ninety-nine elements unbuilt. Every softened lookup below buys back
one of those, and every one of them was added after watching it cost real
repair rounds.

Why this is not in ``rhino_listener``
-------------------------------------
It used to be. The listener is the running server, so ``__reload__``
deliberately refuses to reload it - which meant every fix to these
semantics needed Rhino restarted and the whole session thrown away.
Here it is an ordinary leaf module and reloads with everything else. The
listener looks the wrappers up at call time for the same reason: a
module-level import would bind the old class objects for ever.
"""

import Rhino


class IdList(list):
    """A list of '#12' tags that also answers .ids.

    ``cols.axes`` is a plain list, so ``cols.axes.ids`` raises
    AttributeError - and models write exactly that, because everything
    else in the system is reached through a field. Answering with the
    list itself costs nothing and removes a whole class of repair round.

    A genuine typo still raises: only the id-ish names resolve."""

    _SELF = ("ids", "objects", "axes", "curves")

    def __getattr__(self, name):
        if name in self._SELF:
            return self
        if name == "count":
            return len(self)
        raise AttributeError(
            "a list of object tags has no attribute '{}'".format(name))


class Receipt(dict):
    """A receipt that behaves like an object as well as a dict.

    Generated scripts write ``profile.ids``, not ``profile["ids"]``, so
    attribute access has to work. The reason this class is more than two
    lines is the failure case.

    When a tool fails it returns ``{"ok": False, "error": ...}`` with no
    ``ids`` key at all. The next line of a generated script is nearly
    always ``solid.extrude(ids=profile.ids, ...)``, and a plain
    ``AttributeError`` there aborts the whole ``exec`` - so one failed
    boolean in a loop leaves the remaining ninety-nine elements unbuilt,
    which is the opposite of what anyone wants from a build script.

    So the missing keys that scripts actually chain on degrade to empty
    values: ``.ids`` is ``[]``, ``.id`` is None, ``.ok`` is False. The
    downstream call then does nothing instead of exploding, and the
    script finishes the rest of the model.

    That would hide the failure, which is why every failed receipt is
    also pushed onto the run's failure list - the caller is told exactly
    which tool failed and why, even though the script survived it. A
    typo is still an AttributeError: silently returning None for
    ``.idz`` would turn a bug into a mystery."""

    def __getitem__(self, key):
        """``receipt[0]`` means the first thing this receipt produced.

        A receipt IS a dict, so indexing it with an integer used to do a
        KEY lookup and raise ``KeyError: 0``. Models write
        ``col_bases[0]`` constantly - it reads exactly like the list the
        receipt is standing in for - and the error is a dead end: there
        is no key 0 to suggest, so the repair loop rewrites the line into
        `beam.value[0]['ids']` and other inventions, and burns its rounds.

        An INTEGER index has only one possible meaning here, so it is
        answered: the Nth object the receipt made. String keys are
        untouched, so ``receipt["ids"]`` still means what it always did
        and a genuine typo still raises.

        Same trade as the ``.ids`` fallback below, for the same reason.
        """
        if isinstance(key, (int, slice)) and not isinstance(key, bool):
            source = self.get("ids") or []
            if isinstance(key, slice):
                return IdList(source[key])
            return source[key]
        return dict.__getitem__(self, key)

    def __iter__(self):
        """``for obj in cols`` means the things this receipt produced.

        A receipt IS a dict, so iterating it used to yield its KEYS -
        'ok', 'ids', 'hint' - which are strings, and a string is
        id-shaped enough for a lenient resolver to accept. The script
        gets no error, and every element it acts on afterwards is one it
        did not intend.

        ``__getitem__`` above already decided the other half of this:
        ``cols[0]`` is the first id, not a KeyError. Iteration has to
        agree with indexing, or the object contradicts itself.

        A receipt that produced no objects raises, because there is no
        reading of ``for x in measurement`` worth guessing at - and the
        keys, which is what it used to give, are the one answer that is
        never what was meant.
        """
        source = self.get("ids")
        if not source:
            if "ids" in self:
                return iter([])
            raise TypeError(
                "this {} receipt made no objects to iterate "
                "(keys: {}). If you meant its fields, iterate "
                ".keys()".format(self.get("operation") or self.get("op")
                                 or "tool", ", ".join(sorted(self.keys()))))
        return iter(source)

    _SOFT = {"ids": [], "id": None, "guid": None, "ok": False,
             "count": 0, "warnings": [], "value": None,
             # The generating geometry a member build hands back.
             # A FAILED call returns an error receipt, which has none of
             # these - so `layer.assign_objects(ids=cols.axes, ...)` two
             # lines later raised AttributeError and took the rest of
             # the script with it, burying the real failure (the call
             # that errored) under a cascade about a missing attribute.
             # Empty is the honest answer: the call made no centrelines.
             "axes": [], "axis": None,
             "curves": [], "curve": None}

    # Fields that hold lists of object tags.
    _ID_FIELDS = ("ids", "axes", "curves", "objects")

    def __getattr__(self, name):
        if name in self:
            value = self[name]
            if name in self._ID_FIELDS and isinstance(value, list):
                return IdList(value)
            return value

        if name in self._SOFT:
            soft = self._SOFT[name]
            return list(soft) if isinstance(soft, list) else soft
        raise AttributeError(
            "'Receipt' object has no attribute '{}' (keys: {})".format(
                name, ", ".join(sorted(self.keys()))))

    def __setattr__(self, name, value):
        self[name] = value



def wrap(value):
    """A tool's return value, as a script should see it.

    Called by the listener at the moment a tool returns, and looked up
    through the module rather than imported by name - see the note at the
    top about reloading."""
    return Receipt(value) if isinstance(value, dict) else value


# Back-compatible aliases. The listener used these names for a long time
# and so does anything written against it.
_IdList = IdList
_InRhinoReceipt = Receipt
