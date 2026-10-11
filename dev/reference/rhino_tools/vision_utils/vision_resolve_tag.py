"""
vision.resolve_tag - turn a tag string into a real object reference.

The counterpart to ``vision.tag``. A model reads '#12.F3' off an L3
capture and this is what turns it back into a face it can act on.

The important design point: sub-object indices are resolved against the
LIVE geometry on every call, never from a cache. The plan names the
failure this prevents - tagging one face and then acting on another,
because the object was edited in between and the cached index moved.
Re-deriving costs almost nothing and cannot go stale.

Tag forms:

    #12        the object
    #12.F3     face 3
    #12.E2     edge 2
    #12.V5     vertex 5
    #12.S      curve start point
    #12.E      curve end point (no digits - that is what distinguishes
               it from an edge)

Layer A (pure logic)  - ``_shared.tags.resolve``.
Layer B (Rhino entry) - ``run_vision_resolve_tag``.
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
from rhino_tools._shared import tags as tagmod
from rhino_tools._shared import redraw
from rhino_tools.vision_utils import core_vision


# ---------- Pure logic ----------

def describe(resolved):
    """Turn a resolution result into JSON-safe facts.

    What comes back depends on what was resolved - a face reports its
    area and normal, an edge its length, a vertex its coordinates. Those
    are the things you want to know before acting on it."""
    if resolved is None:
        return None

    kind = resolved["kind"]
    geometry = resolved["geometry"]

    out = {
        "tag": resolved["tag"],
        "kind": kind,
        "index": resolved["index"],
        "object_tag": tagmod.tag_of(resolved["id"]),
        "object_type": coerce.object_type_name(resolved["id"]),
        "layer": coerce.layer_name_of(resolved["id"]),
    }

    if kind == "face" and geometry is not None:
        amp = rg.AreaMassProperties.Compute(geometry.DuplicateFace(False))
        if amp is not None:
            out["area"] = float(amp.Area)
            out["centroid"] = coerce.xyz(amp.Centroid)
        mid_u = geometry.Domain(0).Mid
        mid_v = geometry.Domain(1).Mid
        out["normal"] = coerce.xyz(geometry.NormalAt(mid_u, mid_v))
        ok, plane = geometry.TryGetPlane(coerce.doc_tol())
        out["is_planar"] = bool(ok)

    elif kind == "edge" and geometry is not None:
        out["length"] = float(geometry.GetLength())
        out["start"] = coerce.xyz(geometry.PointAtStart)
        out["end"] = coerce.xyz(geometry.PointAtEnd)

    elif kind in ("vertex", "curve_start", "curve_end") and geometry is not None:
        out["point"] = coerce.xyz(geometry)

    elif kind == "object":
        bbox = coerce.bbox_of(resolved["id"])
        out["bbox"] = coerce.bbox_dict(bbox)

    return out


# ---------- Tool entry point (MCP-callable) ----------

def tool_vision_resolve_tag(tag, highlight=False):
    """Resolve one tag, or a list of them. Returns an R2 receipt."""
    wanted = tag if isinstance(tag, (list, tuple)) else [tag]
    if not wanted:
        return receipts.err("vision.resolve_tag", "No tag supplied.")

    resolved_map, warnings, owners = {}, [], []

    for item in wanted:
        resolved = tagmod.resolve(str(item))
        if resolved is None:
            warnings.append("'{}' does not resolve - the object may have been "
                            "deleted, or the tag is from another "
                            "session.".format(item))
            continue
        resolved_map[str(item)] = describe(resolved)
        owners.append(resolved["id"])

    if not resolved_map:
        return receipts.err(
            "vision.resolve_tag", "Nothing resolved.", warnings=warnings,
            hint="Run vision.tag first, or vision.tag(op='list') to see what "
                 "is currently tagged.")

    if highlight and owners:
        sc.doc.Objects.UnselectAll()
        for guid in owners:
            sc.doc.Objects.Select(guid, True)
        redraw.request()

    value = (list(resolved_map.values())[0] if len(resolved_map) == 1
             else resolved_map)

    return receipts.r2(
        "vision.resolve_tag", value, ids=owners,
        count=len(resolved_map), warnings=warnings,
        hint="Resolved against live geometry, so the index cannot be stale.")


def tool_vision_list_subobjects(ids, kind="face"):
    """Every sub-object tag on an object, without an image.

    The cheap way to find out what '#12.F<n>' values exist before
    spending an L3 capture on finding out visually."""
    guids = coerce.to_guids(ids)
    if not guids:
        return receipts.err("vision.resolve_tag:list",
                            "No valid object ids supplied."
                            + coerce.missing_note(ids))

    kind = str(kind).lower()
    getter = {
        "face": tagmod.face_tags,
        "edge": tagmod.edge_tags,
        "vertex": tagmod.vertex_tags,
    }.get(kind)

    if getter is None:
        return receipts.err("vision.resolve_tag:list",
                            "Unknown kind '{}'.".format(kind),
                            hint="Use face, edge or vertex.")

    result = {}
    for guid in guids:
        result[tagmod.tag_of(guid)] = getter(guid)

    return receipts.r2("vision.resolve_tag:list", result, ids=guids,
                       kind=kind,
                       total=sum(len(v) for v in result.values()))


# ---------- Rhino execution wrapper ----------

def run_vision_resolve_tag():
    go = Rhino.Input.Custom.GetOption()
    go.SetCommandPrompt("Resolve")
    idx_resolve = go.AddOption("Tag")
    go.AddOption("ListSubObjects")
    go.AcceptNothing(True)

    result = go.Get()
    use_resolve = not (result == Rhino.Input.GetResult.Option
                       and go.Option().Index != idx_resolve)

    if use_resolve:
        tag = rs.GetString("Tag to resolve (e.g. #12 or #12.F3)", "#1")
        if not tag:
            return None
        return receipts.echo(tool_vision_resolve_tag(tag.strip(),
                                                     highlight=True))

    obj_ids = rs.GetObjects("Select objects", preselect=True)
    if not obj_ids:
        print("Nothing selected.")
        return None

    kind = rs.GetString("Sub-object kind (face / edge / vertex)", "face")
    if not kind:
        return None

    return receipts.echo(tool_vision_list_subobjects(obj_ids, kind.strip()))


# From here is only required just to run the above functions in Rhino.

if __name__ == "__main__":
    run_vision_resolve_tag()
