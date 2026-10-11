"""
vision.ghost - make everything else transparent but still visible.

The middle setting between "show everything" and "isolate". Use it when
the context is part of the answer: a door opening in a wall is
meaningless with the wall hidden, and a column clash needs both columns
AND the beam between them.

Implemented with per-object display mode overrides rather than a
viewport-wide change, so the effect is per object and can be undone
object by object.

Layer A (pure logic)  - ``core_vision.ghost`` / ``unghost``.
Layer B (Rhino entry) - ``run_vision_ghost``.
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
from rhino_tools.vision_utils import core_vision


# ---------- Tool entry points (MCP-callable) ----------

def tool_vision_ghost(ids, zoom=False):
    """Ghost everything except these objects. Returns an R1 receipt."""
    guids = coerce.to_guids(ids)
    if not guids:
        return receipts.err(
            "vision.ghost", "No valid object ids supplied."
            + coerce.missing_note(ids))

    ghosted = core_vision.ghost(guids)

    if zoom:
        from rhino_tools.vision_utils import core_view
        from rhino_tools._shared import planes as plane_utils
        core_view.zoom_to(plane_utils.get_view("active"), guids)

    warnings = []
    if ghosted == 0:
        warnings.append("Nothing was ghosted - either the selection is the "
                        "whole model, or this Rhino build does not support "
                        "per-object display overrides.")

    return receipts.r1(
        guids, "vision.ghost",
        value={"opaque": len(guids), "ghosted": ghosted},
        warnings=warnings,
        hint="Context stays visible - use vision.isolate when it does not "
             "matter. Call vision.ghost(op='clear') to undo.")


def tool_vision_unghost():
    """Clear every ghosting override. Returns an R1 receipt."""
    cleared = core_vision.unghost()

    if cleared == 0:
        return receipts.err("vision.ghost:clear", "Nothing to clear.",
                            hint="Nothing is currently ghosted.")

    return receipts.r1([], "vision.ghost:clear",
                       value={"cleared": cleared})


def tool_vision_clear_all():
    """Reset tags, isolation AND ghosting in one call.

    Worth having: a workflow that escalated through several capture
    levels leaves three kinds of display state behind, and undoing each
    separately is how a document ends up permanently coloured."""
    result = core_vision.clear_all()
    total = sum(result.values())

    return receipts.r1(
        [], "vision.clear_all", value=result,
        hint=("Reset {} piece(s) of display state.".format(total)
              if total else "Nothing needed clearing."))


# ---------- Rhino execution wrapper ----------

def run_vision_ghost():
    go = Rhino.Input.Custom.GetOption()
    go.SetCommandPrompt("Ghost operation")
    idx_ghost = go.AddOption("Ghost")
    idx_clear = go.AddOption("Clear")
    go.AddOption("ClearAll")
    go.AcceptNothing(True)

    result = go.Get()
    chosen = (go.Option().Index if result == Rhino.Input.GetResult.Option
              else idx_ghost)
    if result not in (Rhino.Input.GetResult.Option, Rhino.Input.GetResult.Nothing):
        print("Cancelled.")
        return None

    if chosen == idx_clear:
        return receipts.echo(tool_vision_unghost())
    if chosen != idx_ghost:
        return receipts.echo(tool_vision_clear_all())

    obj_ids = rs.GetObjects("Select the objects to keep opaque",
                            preselect=True)
    if not obj_ids:
        print("Nothing selected.")
        return None

    return receipts.echo(tool_vision_ghost(obj_ids))


# From here is only required just to run the above functions in Rhino.

if __name__ == "__main__":
    run_vision_ghost()
