"""
Short object tags - the '#12' / '#12.F3' addressing scheme.

Why not just use GUIDs
----------------------
A Rhino GUID is 36 characters. A model with 200 objects costs thousands
of tokens just to name its contents, and a language model transcribing
one by hand will eventually get a character wrong and act on the wrong
object. So every receipt in this package reports objects as short tags
instead:

    #12         the whole object
    #12.F3      face 3 of that object's Brep
    #12.E2      edge 2
    #12.V5      vertex 5
    #12.S       curve start point
    #12.E       curve end point   (no digits - distinguishes it from edges)

The mapping is held in ``session_store`` - a temp file scoped to ONE
conversation. It survives between script runs and between stages, but it
does not pollute the saved file and it does not leak into the next chat
against the same open Rhino. Callers that want a tag to outlive the
conversation pass ``persist=True``, which also stamps it into the
object's user text.

Sub-object indices are resolved against the LIVE Brep every time rather
than cached, so a tag can never drift onto a different face after an
edit - the doc's stated failure mode of "tag one face and act on
another".
"""

import System
import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

from . import coerce
from . import session_store


# User-text key used when a tag is persisted into the document.
TAG_KEY = "RG_TAG"

# session_store buckets
_TAG_BY_GUID = "tag_by_guid"        # str(guid) -> int
_GUID_BY_TAG = "guid_by_tag"        # int -> Guid
_COUNTER = "tag_counter"


# ---------- Session tag store ----------

def _tag_by_guid():
    return session_store.bucket(_TAG_BY_GUID)


def _guid_by_tag():
    return session_store.bucket(_GUID_BY_TAG)


def _next_number():
    """Monotonic per-conversation counter. Never reuses a number, so a
    stale tag in a transcript resolves to nothing rather than to a new
    object that happens to have inherited the slot."""
    _ensure_counter_floor()
    return session_store.bump(_COUNTER)


def _ensure_counter_floor():
    """Start this conversation's numbering ABOVE anything the document
    already carries.

    Persisted tags live in RG_TAG user text and survive a reopen; the
    counter is conversation state and starts at zero. Without this, a
    fresh chat on a saved file mints '#1' for a new object while
    ``_scan_for_persisted`` can still hand back a DIFFERENT object for
    '#1' - two objects answering to one tag, which is the whole failure
    the never-reuse rule exists to prevent.

    One document scan per conversation, on the first mint only."""
    if session_store.counter(_COUNTER) > 0:
        return
    highest = 0
    for rhobj in sc.doc.Objects:
        try:
            stored = rhobj.Attributes.GetUserString(TAG_KEY)
        except Exception:
            continue
        if not stored:
            continue
        try:
            highest = max(highest, int(stored))
        except ValueError:
            continue
    if highest > 0:
        session_store.raise_counter(_COUNTER, highest)


# There is deliberately no reset_tags().
#
# There used to be, documented as "used by vision.tag's clear op", and
# nothing called it. Wiring it up would have been worse than deleting it:
# vision.tag(op='clear') removes the coloured LABELS, and forgetting what
# '#12' means at the same moment is exactly wrong, because every receipt
# already issued still refers to '#12'. The table is per conversation and
# session.end takes it away with everything else, which is the only point
# at which forgetting a tag is correct.


# ---------- Assigning ----------

def tag_of(obj_id, create=True, persist=False):
    """Return the '#N' tag for an object, assigning one on first sight.

    ``create=False`` makes this a pure lookup that returns None for an
    untagged object - used by receipts that should not mint tags as a
    side effect of merely reporting.
    """
    guid = coerce.to_guid(obj_id)
    if guid is None:
        return None

    key = str(guid)
    by_guid = _tag_by_guid()

    if key in by_guid:
        return "#{}".format(by_guid[key])

    # Not in this session's table - it may still carry a persisted tag
    # from an earlier session, which we honour rather than renumbering.
    rhobj = sc.doc.Objects.Find(guid)
    if rhobj is not None:
        stored = rhobj.Attributes.GetUserString(TAG_KEY)
        if stored:
            try:
                number = int(stored)
                by_guid[key] = number
                _guid_by_tag()[number] = guid
                session_store.mark_dirty()
                return "#{}".format(number)
            except ValueError:
                pass

    if not create:
        return None

    number = _next_number()
    by_guid[key] = number
    _guid_by_tag()[number] = guid
    session_store.mark_dirty()

    if persist and rhobj is not None:
        attr = rhobj.Attributes
        attr.SetUserString(TAG_KEY, str(number))
        sc.doc.Objects.ModifyAttributes(rhobj, attr, True)

    return "#{}".format(number)


def tags_of(obj_ids, create=True, persist=False):
    """Vectorised ``tag_of``. Drops ids that cannot be resolved so a
    receipt never contains a null in its id list."""
    out = []
    for oid in coerce.to_guids(obj_ids):
        tag = tag_of(oid, create=create, persist=persist)
        if tag is not None:
            out.append(tag)
    return out


def untag(obj_id):
    """Forget an object's tag and strip any persisted user text."""
    guid = coerce.to_guid(obj_id)
    if guid is None:
        return False

    key = str(guid)
    by_guid = _tag_by_guid()
    number = by_guid.pop(key, None)
    if number is not None:
        _guid_by_tag().pop(number, None)
        session_store.mark_dirty()

    rhobj = sc.doc.Objects.Find(guid)
    if rhobj is not None and rhobj.Attributes.GetUserString(TAG_KEY):
        attr = rhobj.Attributes
        attr.SetUserString(TAG_KEY, None)
        sc.doc.Objects.ModifyAttributes(rhobj, attr, True)
    return number is not None


# ---------- Resolving ----------

def parse_tag(text):
    """'#12.F3' -> (12, 'F', 3). '#12' -> (12, None, None).

    Returns (None, None, None) for anything that is not tag-shaped, so
    callers can fall through to plain GUID parsing."""
    if not isinstance(text, str):
        return None, None, None
    body = text.strip()
    if not body.startswith("#"):
        return None, None, None
    body = body[1:]

    if "." not in body:
        try:
            return int(body), None, None
        except ValueError:
            return None, None, None

    head, sub = body.split(".", 1)
    try:
        number = int(head)
    except ValueError:
        return None, None, None

    sub = sub.strip().upper()
    if not sub:
        return number, None, None

    kind = sub[0]
    rest = sub[1:]
    if rest == "":
        # '#12.S' / '#12.E' - curve start/end, no index
        return number, kind, None
    try:
        return number, kind, int(rest)
    except ValueError:
        return number, None, None


def guid_from_tag(text):
    """'#12' or '#12.F3' -> the owning object's Guid (sub-object part is
    ignored here; use ``resolve`` when you need the component itself)."""
    number, _kind, _index = parse_tag(text)
    if number is None:
        return None

    guid = _guid_by_tag().get(number)
    if guid is not None and sc.doc.Objects.Find(guid) is not None:
        return guid

    # Fall back to a document scan for a persisted tag. This is what lets
    # a tag from a previous session still resolve after a file reopen.
    found = _scan_for_persisted(number)
    if found is not None:
        _guid_by_tag()[number] = found
        _tag_by_guid()[str(found)] = number
        session_store.mark_dirty()
    return found


def is_dead(text):
    """Did this tag name a real object that is now gone?

    ``guid_from_tag`` returns None for a tag it cannot resolve, and that
    one answer covers two completely different situations: a tag that
    never existed (a typo, an invention) and a tag whose object WAS
    built and has since been deleted. The caller can only act on the
    second one, and only if it is told.

    Nothing has to be recorded for this: the tag->guid map still holds
    the number, so a number that maps to a guid the document no longer
    contains is, by definition, a deleted object."""
    number, _kind, _index = parse_tag(text)
    if number is None:
        return False
    guid = _guid_by_tag().get(number)
    if guid is None:
        return False
    if sc.doc.Objects.Find(guid) is not None:
        return False
    return _scan_for_persisted(number) is None


def explain_missing(value, limit=6):
    """'' or a sentence naming the tags in ``value`` that are gone.

    This exists because of one measured failure. A build made a
    rectangle (#10) and a second rectangle (#11), ran region.boolean on
    them - which deletes its inputs - and two steps later passed #11 to
    three more tools. Every one answered 'No valid object ids supplied',
    which reads as "you passed the wrong kind of thing", so the model
    rewrote HOW it passed the id, four times, and never once reached
    the truth: the object was deleted forty lines earlier and no amount
    of rewriting the call could bring it back."""
    tags_seen, dead = [], []
    def walk(item, depth=0):
        if depth > 3 or len(tags_seen) > 64:
            return
        if isinstance(item, str):
            if item.strip().startswith("#"):
                tags_seen.append(item.strip())
        elif isinstance(item, dict):
            walk(item.get("ids"), depth + 1)
            walk(item.get("id"), depth + 1)
        elif isinstance(item, (list, tuple)):
            for sub in item:
                walk(sub, depth + 1)
    walk(value)

    for tag in tags_seen:
        if tag not in dead and is_dead(tag):
            dead.append(tag)
    if not dead:
        return ""

    shown = ", ".join(dead[:limit])
    if len(dead) > limit:
        shown += " and {} more".format(len(dead) - limit)
    return (" {} {} built, then deleted - something removed the object "
            "since. Tools that consume their input (region.boolean, "
            "solid.boolean, solid.split, block.define) delete it unless "
            "you pass delete_input=False. Rebuild what you need, or keep "
            "it in the first place.".format(
                shown, "was" if len(dead) == 1 else "were"))


def _scan_for_persisted(number):
    """Linear scan for an object whose user text carries this tag number.
    Only reached on a cache miss, so the cost is paid once per tag."""
    target = str(number)
    for rhobj in sc.doc.Objects:
        try:
            if rhobj.Attributes.GetUserString(TAG_KEY) == target:
                return rhobj.Id
        except Exception:
            continue
    return None


def resolve(text):
    """Full resolution of a tag string to a concrete thing.

    Returns a dict describing what the tag points at:

        {"tag": "#12.F3", "id": <Guid>, "kind": "face",
         "index": 3, "geometry": <BrepFace|Curve|Point3d|GeometryBase>}

    or None if it does not resolve. This is the backing function for the
    ``vision.resolve_tag`` tool, and it is deliberately re-derived from
    live geometry on every call so an edited object cannot leave a stale
    index behind.
    """
    number, kind, index = parse_tag(text)
    if number is None:
        # Might be a raw GUID string - still worth resolving.
        guid = coerce.to_guid(text)
        if guid is None:
            return None
        return {"tag": tag_of(guid), "id": guid, "kind": "object",
                "index": None, "geometry": coerce.geometry_of(guid)}

    guid = guid_from_tag("#{}".format(number))
    if guid is None:
        return None

    geom = coerce.geometry_of(guid)
    if kind is None:
        return {"tag": text, "id": guid, "kind": "object",
                "index": None, "geometry": geom}

    # Curve start / end have no index and are handled before the Brep path.
    if kind in ("S",) and index is None:
        crv = coerce.to_curve(guid)
        if crv is None:
            return None
        return {"tag": text, "id": guid, "kind": "curve_start",
                "index": None, "geometry": crv.PointAtStart}
    if kind in ("E",) and index is None:
        crv = coerce.to_curve(guid)
        if crv is None:
            return None
        return {"tag": text, "id": guid, "kind": "curve_end",
                "index": None, "geometry": crv.PointAtEnd}

    brep = coerce.to_brep(guid)
    if brep is None or index is None:
        return None

    if kind == "F" and 0 <= index < brep.Faces.Count:
        return {"tag": text, "id": guid, "kind": "face",
                "index": index, "geometry": brep.Faces[index]}
    if kind == "E" and 0 <= index < brep.Edges.Count:
        return {"tag": text, "id": guid, "kind": "edge",
                "index": index, "geometry": brep.Edges[index]}
    if kind == "V" and 0 <= index < brep.Vertices.Count:
        return {"tag": text, "id": guid, "kind": "vertex",
                "index": index, "geometry": brep.Vertices[index].Location}

    return None


# ---------- Sub-object tag enumeration ----------

def face_tags(obj_id):
    """['#12.F0', '#12.F1', ...] for every face of a Brep/Extrusion."""
    base = tag_of(obj_id)
    brep = coerce.to_brep(obj_id)
    if base is None or brep is None:
        return []
    return ["{}.F{}".format(base, i) for i in range(brep.Faces.Count)]


def edge_tags(obj_id):
    """['#12.E0', ...] for every edge of a Brep/Extrusion."""
    base = tag_of(obj_id)
    brep = coerce.to_brep(obj_id)
    if base is None or brep is None:
        return []
    return ["{}.E{}".format(base, i) for i in range(brep.Edges.Count)]


def vertex_tags(obj_id):
    """['#12.V0', ...] for every vertex of a Brep/Extrusion."""
    base = tag_of(obj_id)
    brep = coerce.to_brep(obj_id)
    if base is None or brep is None:
        return []
    return ["{}.V{}".format(base, i) for i in range(brep.Vertices.Count)]
