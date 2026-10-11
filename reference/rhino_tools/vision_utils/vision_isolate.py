"""
vision.isolate - hide everything except the selection.

Deliberately reversible in a precise way: only the objects THIS tool hid
are unhidden by ``restore``. Objects the user had already hidden for
their own reasons stay hidden, which is the difference between a focus
tool and a blunt "show everything".

Use ``vision.ghost`` instead when the surroundings are part of the
answer - a wall opening only makes sense with the wall around it.

Layer A (pure logic)  - ``core_vision.isolate`` / ``restore_visibility``.
Layer B (Rhino entry) - ``run_vision_isolate``.
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

def tool_vision_isolate(ids, zoom=True):
    """Hide everything else. Returns an R1 receipt."""
    guids = coerce.to_guids(ids)
    if not guids:
        return receipts.err(
            "vision.isolate", "No valid object ids supplied."
            + coerce.missing_note(ids))

    hidden = core_vision.isolate(guids)

    if zoom:
        from rhino_tools.vision_utils import core_view
        from rhino_tools._shared import planes as plane_utils
        core_view.zoom_to(plane_utils.get_view("active"), guids)

    warnings = []
    if hidden == 0:
        warnings.append("Nothing was hidden - the selection was already "
                        "everything visible.")

    return receipts.r1(
        guids, "vision.isolate",
        value={"kept": len(guids), "hidden": hidden},
        warnings=warnings,
        hint="Call vision.isolate(op='restore') to put back exactly what "
             "this hid.")


def tool_vision_restore():
    """Unhide only what ``isolate`` hid. Returns an R1 receipt."""
    restored = core_vision.restore_visibility()

    if restored == 0:
        return receipts.err(
            "vision.isolate:restore", "Nothing to restore.",
            hint="Nothing was hidden by vision.isolate in this session.")

    return receipts.r1(
        [], "vision.isolate:restore",
        value={"restored": restored},
        hint="Objects you hid yourself were left alone.")


# ---------- Rhino execution wrapper ----------

def run_vision_isolate():
    go = Rhino.Input.Custom.GetOption()
    go.SetCommandPrompt("Isolate operation")
    idx_isolate = go.AddOption("Isolate")
    go.AddOption("Restore")
    go.AcceptNothing(True)

    result = go.Get()
    do_isolate = not (result == Rhino.Input.GetResult.Option
                      and go.Option().Index != idx_isolate)

    if not do_isolate:
        return receipts.echo(tool_vision_restore())

    obj_ids = rs.GetObjects("Select the objects to keep visible",
                            preselect=True)
    if not obj_ids:
        print("Nothing selected.")
        return None

    return receipts.echo(tool_vision_isolate(obj_ids))


# From here is only required just to run the above functions in Rhino.

if __name__ == "__main__":
    run_vision_isolate()
