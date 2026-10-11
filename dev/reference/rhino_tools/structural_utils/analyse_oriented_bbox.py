"""
analyse.oriented_bbox - bounding box aligned to the object's own axes.

Distinct from the plain world-aligned bounding box under Breps. A
diagonal beam's world bbox tells you almost nothing; its oriented bbox
gives the real length, depth and width, and the orientation to go with
them.

The principal axes come from ``core_centerline.principal_frame`` - the
same second-moment maths the sliced centerline uses. That reuse is the
point: "the long axis of this member" has exactly one definition in this
package, so a centerline and an oriented bbox can never disagree about
which way a column runs.

Layer A (pure logic)  - ``core_centerline``, plus ``describe_box`` below.
Layer B (Rhino entry) - ``run_oriented_bbox``.
"""
# requirements: numpy
# requirements: cv2

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
from rhino_tools._shared import layers as layer_utils
from rhino_tools.structural_utils import core_centerline
from rhino_tools._shared import redraw


# ---------- Pure logic ----------

def describe_box(box, frame):
    """Turn a Box plus its frame into the JSON the receipt reports.

    Dimensions come back sorted long-to-short and labelled, because
    "length 4200, depth 400, width 300" is what a person reads off a
    schedule, whereas "x 300, y 400, z 4200" makes them work it out."""
    if box is None or frame is None:
        return None

    corners = [coerce.xyz(c) for c in box.GetCorners()]
    dims = sorted([
        float(frame["ext3"].Length),
        float(frame["ext2"].Length),
        float(frame["ext1"].Length),
    ], reverse=True)

    return {
        "corners": corners,
        "dimensions": {"length": dims[0], "depth": dims[1], "width": dims[2]},
        "extents": {
            "along_long_axis": float(frame["ext3"].Length),
            "along_mid_axis": float(frame["ext2"].Length),
            "along_short_axis": float(frame["ext1"].Length),
        },
        "orientation": {
            "long_axis": coerce.xyz(frame["axis3"]),
            "mid_axis": coerce.xyz(frame["axis2"]),
            "short_axis": coerce.xyz(frame["axis1"]),
        },
        "centroid": coerce.xyz(frame["centroid"]),
        "volume": float(frame["volume"]),
    }


def oriented_bbox_for_object(obj_id):
    """One object in, ``(box, frame, warning)`` out."""
    brep = coerce.to_brep(obj_id)
    if brep is None:
        return None, None, "not a brep or extrusion"
    if not brep.IsSolid:
        return None, None, ("not a closed solid - the principal frame needs "
                            "a watertight brep")

    frame = core_centerline.principal_frame(brep)
    if frame is None:
        return None, None, "could not compute the principal frame"

    box = core_centerline.oriented_bbox(frame)
    return box, frame, None


# ---------- Tool entry point (MCP-callable) ----------

def tool_oriented_bbox(ids, bake=False, layer=None):
    """Compute oriented bounding boxes. Returns an R2 receipt.

    ``bake=True`` also adds the boxes to the document, which is how you
    check by eye that the axes came out the way you expected."""
    guids = coerce.to_guids(ids)
    if not guids:
        return receipts.err("analyse.oriented_bbox",
                            "No valid object ids supplied."
                            + coerce.missing_note(ids))

    from rhino_tools._shared import tags as tagmod

    layer_index = layer_utils.ensure_layer_index(layer) if layer else None
    per_object, warnings, baked = {}, [], []

    for guid in guids:
        box, frame, warning = oriented_bbox_for_object(guid)
        tag = tagmod.tag_of(guid)
        if box is None:
            warnings.append("{}: {}".format(tag, warning))
            continue

        per_object[tag] = describe_box(box, frame)

        if bake:
            brep = box.ToBrep()
            if brep is not None:
                new_id = sc.doc.Objects.AddBrep(
                    brep, layer_utils.attr_on_layer(layer_index))
                if new_id:
                    baked.append(new_id)

    if not per_object:
        return receipts.err("analyse.oriented_bbox", "Nothing measurable.",
                            ids=guids, warnings=warnings)

    if bake:
        redraw.request()

    value = (list(per_object.values())[0] if len(per_object) == 1
             else per_object)

    return receipts.r2(
        "oriented_bbox", value, unit=coerce.doc_units(), ids=guids,
        baked=tagmod.tags_of(baked) if baked else None,
        warnings=warnings,
        hint="Axes come from the same principal frame as "
             "centerline.complex_brep.")


# ---------- Rhino execution wrapper ----------

def run_oriented_bbox():
    obj_ids = rs.GetObjects("Select closed breps or extrusions",
                            rs.filter.polysurface | rs.filter.extrusion,
                            preselect=True)
    if not obj_ids:
        print("Nothing selected.")
        return None

    go = Rhino.Input.Custom.GetOption()
    go.SetCommandPrompt("Draw the boxes as well as measuring them?")
    yes_idx = go.AddOption("Yes")
    go.AddOption("No")
    go.AcceptNothing(True)

    result = go.Get()
    bake = (result == Rhino.Input.GetResult.Option
            and go.Option().Index == yes_idx)

    return receipts.echo(tool_oriented_bbox(obj_ids, bake=bake,
                                            layer="OrientedBBox" if bake else None))


# From here is only required just to run the above functions in Rhino.

if __name__ == "__main__":
    run_oriented_bbox()
