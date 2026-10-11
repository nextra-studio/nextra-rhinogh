"""
vision.zoom - zoom to a selection, a layer, or a plane.

Framing before a capture. Three targets:

    selection  fit the named objects
    layer      fit everything on a layer, without having to select it
               first
    plane      look square at a plane and fit what is on it

The resulting view bounds come back in the receipt, which is what lets a
caller confirm the subject is actually in frame before spending a capture
on it.

Layer A (pure logic)  - ``core_view.zoom_to``, ``core_select``.
Layer B (Rhino entry) - ``run_vision_zoom``.
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
import Rhino.Geometry as rg
import scriptcontext as sc

from rhino_tools._shared import coerce, receipts
from rhino_tools._shared import planes as plane_utils
from rhino_tools.vision_utils import core_view
from rhino_tools.vision_utils import core_select
from rhino_tools._shared import redraw


# ---------- Pure logic ----------

def targets_for(target, ids=None, layer=None, plane=None):
    """Work out what to frame. Returns ``(ids, bbox, warning)``.

    The plane case is the interesting one: rather than framing the whole
    plane (which is infinite), it frames the objects lying ON the plane -
    which is what someone asking to "zoom to the wall plane" means."""
    target = str(target).lower()

    if target == "selection":
        guids = coerce.to_guids(ids)
        if not guids:
            guids = [o.Id for o in sc.doc.Objects.GetSelectedObjects(False, False)]
        if not guids:
            return [], None, "nothing selected and no ids given"
        return guids, coerce.bbox_of(guids), None

    if target == "layer":
        if not layer:
            return [], None, "target='layer' needs a layer name"
        guids = core_select.select({"layer": layer})
        if not guids:
            return [], None, "no objects on layer '{}'".format(layer)
        return guids, coerce.bbox_of(guids), None

    if target == "plane":
        resolved = plane_utils.resolve_plane(plane)
        if resolved is None:
            return [], None, "could not resolve a plane"
        guids = core_select.select({"plane": plane, "plane_mode": "on"})
        bbox = coerce.bbox_of(guids) if guids else None
        return guids, bbox, (None if guids else
                             "nothing lies on that plane - framing the "
                             "whole model instead")

    if target in ("all", "extents"):
        guids = [o.Id for o in sc.doc.Objects if not o.IsDeleted]
        return guids, coerce.bbox_of(guids), None

    return [], None, "unknown zoom target '{}'".format(target)


# ---------- Tool entry point (MCP-callable) ----------

def tool_vision_zoom(target="selection", ids=None, layer=None, plane=None,
                     viewport="active", orient=False):
    """Zoom a viewport. Returns an R1 receipt with the resulting bounds."""
    view = plane_utils.get_view(viewport)
    if view is None:
        return receipts.err("vision.zoom",
                            "No viewport named '{}'.".format(viewport))

    guids, bbox, warning = targets_for(target, ids, layer, plane)
    warnings = [warning] if warning else []

    # Orienting first, then zooming, is the order that matters: zooming
    # to a bounding box and then rotating the camera loses the framing.
    if orient and str(target).lower() == "plane":
        resolved = plane_utils.resolve_plane(plane)
        core_view.orient_camera_to_plane(view, resolved, None, bbox)

    if not core_view.zoom_to(view, guids if guids else None, bbox):
        return receipts.err("vision.zoom", "Could not zoom.",
                            warnings=warnings)

    redraw.request()
    info = plane_utils.view_info(view)

    return receipts.r1(
        guids, "vision.zoom", obj_type="view",
        value={
            "target": target,
            "viewport": info["name"],
            "framed": len(guids),
            "bounds": coerce.bbox_dict(bbox),
            "camera": core_view.camera_state(view),
        },
        plane=(info["active_viewplane"] if orient else None),
        warnings=warnings,
        hint="Check 'bounds' covers what you expect before capturing.")


# ---------- Rhino execution wrapper ----------

def run_vision_zoom():
    go = Rhino.Input.Custom.GetOption()
    go.SetCommandPrompt("Zoom to")
    idx_selection = go.AddOption("Selection")
    idx_layer = go.AddOption("Layer")
    idx_plane = go.AddOption("CPlane")
    go.AddOption("All")
    go.AcceptNothing(True)

    result = go.Get()
    chosen = (go.Option().Index if result == Rhino.Input.GetResult.Option
              else idx_selection)
    if result not in (Rhino.Input.GetResult.Option, Rhino.Input.GetResult.Nothing):
        print("Cancelled.")
        return None

    if chosen == idx_selection:
        obj_ids = rs.GetObjects("Select the objects to frame", preselect=True)
        if not obj_ids:
            print("Nothing selected.")
            return None
        return receipts.echo(tool_vision_zoom("selection", ids=obj_ids))

    if chosen == idx_layer:
        from rhino_tools._shared import layers as layer_utils
        available = layer_utils.layer_names()
        layer = rs.GetString("Layer ({}...)".format(", ".join(available[:6])),
                             layer_utils.current_layer_name())
        if not layer:
            return None
        return receipts.echo(tool_vision_zoom("layer", layer=layer.strip()))

    if chosen == idx_plane:
        return receipts.echo(tool_vision_zoom("plane", orient=True))

    return receipts.echo(tool_vision_zoom("all"))


# From here is only required just to run the above functions in Rhino.

if __name__ == "__main__":
    run_vision_zoom()
