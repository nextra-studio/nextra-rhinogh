"""
plane.push_to_views - copy a CPlane to every viewport.

"Copy CPlane to all". Each Rhino viewport owns its own construction
plane - Top is world XY, Front is world ZX, Perspective is whatever it
was last given - and that independence is usually what you want. It stops
being what you want the moment you are working on a wall face and switch
viewports mid-task: the new viewport is still on its own plane, and the
next thing you draw lands somewhere unexpected.

Pushing the plane everywhere removes that whole class of surprise for the
duration of a task.

Listed under Vision Utils in the plan, though it is a plane operation -
kept there because it is about what the viewports SHOW, and it pairs with
``vision.zoom`` in practice.

Layer A (pure logic)  - ``_shared.planes``.
Layer B (Rhino entry) - ``run_plane_push_to_views``.
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

from rhino_tools._shared import receipts
from rhino_tools._shared import planes as plane_utils
from rhino_tools._shared import redraw
from rhino_tools.vision_utils import core_view


# ---------- Pure logic ----------

def planes_before():
    """Snapshot every viewport's CPlane before the push.

    Reported so the change is reversible by eye - you can see which
    viewports actually moved, and what they were on."""
    out = {}
    for view in plane_utils.all_views():
        vp = view.ActiveViewport
        out[vp.Name] = plane_utils.plane_to_dict(vp.ConstructionPlane())
    return out


# ---------- Tool entry point (MCP-callable) ----------

def tool_plane_push_to_views(plane=None, source="active", switch_to=None,
                             skip_layouts=True):
    """Copy one CPlane to every viewport. Returns an R1 receipt.

    ``plane`` wins if given; otherwise the plane is taken from the
    viewport named by ``source``. ``switch_to`` optionally makes one
    viewport active afterwards, which is the natural follow-on when the
    point was to go and work somewhere else."""
    if plane is not None:
        target = plane_utils.resolve_plane(plane)
    else:
        target = plane_utils.get_cplane(source)

    if target is None:
        return receipts.err("plane.push_to_views",
                            "Could not resolve a plane to push.")

    before = planes_before()

    changed = []
    for view in plane_utils.all_views():
        if skip_layouts and core_view.view_kind(view) == "layout":
            continue
        vp = view.ActiveViewport
        vp.SetConstructionPlane(target)
        redraw.request(view)
        changed.append(vp.Name)

    if not changed:
        return receipts.err("plane.push_to_views",
                            "No model viewports to push to.")

    warnings = []
    if switch_to:
        if core_view.set_active_view(switch_to) is None:
            warnings.append("No viewport named '{}' - the active viewport is "
                            "unchanged.".format(switch_to))

    redraw.request()

    return receipts.r1(
        [], "plane.push_to_views", obj_type="plane",
        value={
            "viewports": changed,
            "count": len(changed),
            "previous": before,
            "active": sc.doc.Views.ActiveView.ActiveViewport.Name,
        },
        plane=plane_utils.plane_to_dict(target),
        warnings=warnings,
        hint="Every model viewport now draws on the same plane - switching "
             "views mid-task will not move your geometry.")


# ---------- Rhino execution wrapper ----------

def run_plane_push_to_views():
    available = plane_utils.list_named()
    source = rs.GetString(
        "Plane to push (Enter for the active CPlane, or a saved name: "
        "{})".format(", ".join(available[:6])), "")
    source = source.strip() if source else None

    plane_arg = source if source else None

    switch = rs.GetString(
        "Switch to viewport afterwards ({}) - Enter to stay".format(
            ", ".join(core_view.view_names())), "")
    switch = switch.strip() if switch else None

    return receipts.echo(tool_plane_push_to_views(plane=plane_arg,
                                                  switch_to=switch))


# From here is only required just to run the above functions in Rhino.

if __name__ == "__main__":
    run_plane_push_to_views()
