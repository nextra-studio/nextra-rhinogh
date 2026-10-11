"""
vision.tag - apply tags and colours with zoom-adaptive conduits.

Tags are short ('#12') rather than GUIDs, for a practical reason: a
36-character GUID costs a lot of tokens to carry and is easy to
mis-transcribe. '#12' is cheap and unambiguous.

Tag lifetime, per the plan's open decision: session-scoped by default,
because persisting every tag pollutes the document with user text nobody
asked for. Pass ``persist=True`` to write the tag into user text so it
survives a save and reopen - which the plan says to do only when the user
asks for it.

The conduit follows live geometry and the document event hooks keep tags
attached through moves and edits, and strip them off copies. That last
part matters: without it, copying a tagged object gives you two things
that both look like '#12'.

Layer A (pure logic)  - ``core_vision``.
Layer B (Rhino entry) - ``run_vision_tag``.
"""

import os
import sys

try:
    _HERE = os.path.dirname(os.path.abspath(__file__))
except NameError:
    _HERE = os.getcwd()
_PARENT = os.path.dirname(os.path.dirname(_HERE))
if _PARENT not in sys.path:
    sys.path.insert(0, _PARENT)

import rhinoscriptsyntax as rs
import Rhino
import scriptcontext as sc

from rhino_tools._shared import coerce, receipts
from rhino_tools._shared import tags as tagmod
from rhino_tools.vision_utils import core_vision


# ---------- Tool entry points (MCP-callable) ----------

def tool_vision_tag(ids, level=2, persist=False):
    """Tag and colour objects. Returns an R2 receipt with the tag map."""
    guids = coerce.to_guids(ids)
    if not guids:
        return receipts.err(
            "vision.tag", "No valid object ids supplied."
            + coerce.missing_note(ids))

    tag_map = core_vision.tag_objects(guids, level, persist)
    if not tag_map:
        return receipts.err("vision.tag", "Nothing could be tagged.",
                            ids=guids)

    warnings = []
    if persist:
        warnings.append("Tags written to object user text - they will survive "
                        "a save. Use vision.tag(op='clear') to remove them.")
    else:
        warnings.append("Tags are session-scoped and will not survive a "
                        "reopen. Pass persist=True if they should.")

    described = {}
    for tag, guid in tag_map.items():
        described[tag] = {
            "type": coerce.object_type_name(guid),
            "layer": coerce.layer_name_of(guid),
        }

    return receipts.r2(
        "vision.tag", described, ids=guids,
        level=int(level), persist=bool(persist),
        count=len(tag_map),
        warnings=warnings,
        hint="These tags are now valid ids for every other tool.")


def tool_vision_untag(ids=None):
    """Remove tags and restore colours. All of them when none named."""
    before = len(core_vision.tagged_ids())
    cleared = core_vision.untag_objects(ids)

    if cleared == 0:
        return receipts.err(
            "vision.tag:clear", "Nothing was untagged.",
            hint=("Nothing is currently tagged." if before == 0
                  else "Check the ids."))

    return receipts.r2("vision.tag:clear", cleared,
                       remaining=len(core_vision.tagged_ids()),
                       hint="Colours restored to what they were.")


def tool_vision_tag_level(level):
    """Change the detail level on every active tag at once.

    Escalating from L2 to L3 across a whole selection is a single call
    rather than a re-tag, which keeps the tag numbers stable - the point
    of escalating is to see MORE about the same objects."""
    core_vision.set_tag_level(int(level))
    return receipts.r2("vision.tag:level", int(level),
                       tagged=len(core_vision.tagged_ids()),
                       hint="2 = object tags, 3 = sub-objects, 4 = vertices.")


def tool_vision_tag_list():
    """What is currently tagged."""
    ids = core_vision.tagged_ids()
    described = {}
    for key in ids:
        tag = tagmod.tag_of(key, create=False)
        if tag:
            described[tag] = coerce.object_type_name(key)
    return receipts.r2("vision.tag:list", described, count=len(described))


# ---------- Rhino execution wrapper ----------

def run_vision_tag():
    go = Rhino.Input.Custom.GetOption()
    go.SetCommandPrompt("Tag operation")
    idx_tag = go.AddOption("Tag")
    idx_clear = go.AddOption("Clear")
    idx_level = go.AddOption("Level")
    go.AddOption("List")
    go.AcceptNothing(True)

    result = go.Get()
    if result == Rhino.Input.GetResult.Nothing:
        return receipts.echo(tool_vision_tag_list())
    if result != Rhino.Input.GetResult.Option:
        print("Cancelled.")
        return None

    chosen = go.Option().Index

    if chosen == idx_tag:
        obj_ids = rs.GetObjects("Select objects to tag", preselect=True)
        if not obj_ids:
            print("Nothing selected.")
            return None
        level = rs.GetInteger("Detail level (2, 3 or 4)", 2, 2, 4)
        if level is None:
            return None
        return receipts.echo(tool_vision_tag(obj_ids, level))

    if chosen == idx_clear:
        return receipts.echo(tool_vision_untag())

    if chosen == idx_level:
        level = rs.GetInteger("Detail level (2, 3 or 4)", 3, 2, 4)
        if level is None:
            return None
        return receipts.echo(tool_vision_tag_level(level))

    return receipts.echo(tool_vision_tag_list())


# From here is only required just to run the above functions in Rhino.

if __name__ == "__main__":
    run_vision_tag()
