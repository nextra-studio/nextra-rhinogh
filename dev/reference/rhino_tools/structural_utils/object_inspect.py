"""
object.inspect - facts about a few objects, cheapest first.

The second step of looking at an existing model (the first is
``vision.divide`` / ``vision.capture``, which find WHICH objects). Once
the model has narrowed down to ~20 objects it needs facts - and the
trap is to ask for every vertex and edge, which for a filleted solid is
thousands of tokens that answer nothing. So there is a ladder:

    summary   (default, ~60 tokens an object) what the object IS:
              curve    line / polyline / arc / curve, start, end, length
              solid    linear / planar / block, oriented box, and for a
                       linear member its axis end points + section, for a
                       plate its mid-plane + thickness
              surface  planar or not, area, plane / normal, corners
    topology  + the faces (planar? area, normal), edge / vertex counts,
              naked edges - enough to pick a face or see a hole - and the
              full oriented bounding box (8 corners, 3 axes) of a solid
    full      + vertices and edges as coordinates, capped at max_points.
              Only for a real edit of the geometry.

The summary is usually all a structural job needs: "a 300 x 300 column
from (0,0,0) to (0,0,3500)" is enough to delete the solid and draw its
centreline, which is the usual replace-don't-edit path.

Reuses the package's one definition of a member's axes
(``core_centerline.principal_frame`` via ``core_fe`` and
``analyse_oriented_bbox``), so inspect, the oriented bbox and the
structural convert can never disagree about which way a column runs.

Layer A (pure logic)  - ``core_fe``, ``core_centerline``.
Layer B (Rhino entry) - ``run_object_inspect``.
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
import Rhino.Geometry as rg
import scriptcontext as sc

from rhino_tools._shared import coerce, precision, receipts
from rhino_tools._shared import tags as tagmod
from rhino_tools.structural_utils import core_centerline
from rhino_tools.structural_utils import core_fe
from rhino_tools.structural_utils import analyse_oriented_bbox


DETAILS = ("summary", "topology", "full")
MAX_OBJECTS = 40          # more than this is not "a few" - use map_query / vision.divide
MAX_FACES = 30            # faces listed at 'topology'
MAX_CORNERS = 8           # boundary corners of a planar surface at 'summary'


# ---------- small helpers ----------

def _p(point):
    """Point3d -> [x, y, z] at report precision (settings/general_settings.json)."""
    return precision.point(point)


def _v(vector):
    """Vector3d -> unit [x, y, z] to 0.001."""
    v = rg.Vector3d(vector)
    if v.Length > 0:
        v.Unitize()
    return [round(float(v.X), 3), round(float(v.Y), 3), round(float(v.Z), 3)]


def _n(value):
    return precision.length(value)


def base_facts(obj_id):
    """Level 0: what map_query already knows."""
    bbox = coerce.bbox_of(obj_id)
    row = {"tag": tagmod.tag_of(obj_id), "type": coerce.object_type_name(obj_id),
           "layer": coerce.layer_name_of(obj_id)}
    rhobj = coerce.find_object(obj_id)
    if rhobj is not None and rhobj.Attributes.Name:
        row["name"] = rhobj.Attributes.Name
    if bbox is not None and bbox.IsValid:
        size = bbox.Max - bbox.Min
        row["center"] = _p(bbox.Center)
        row["size"] = [_n(size.X), _n(size.Y), _n(size.Z)]
    return row


# ---------- summary, per kind ----------

def curve_summary(curve):
    out = {"length": _n(curve.GetLength()), "start": _p(curve.PointAtStart),
           "end": _p(curve.PointAtEnd), "closed": bool(curve.IsClosed)}
    if curve.IsLinear(coerce.doc_tol()):
        out["kind"] = "line"
        out["direction"] = _v(curve.PointAtEnd - curve.PointAtStart)
        return out
    ok, polyline = curve.TryGetPolyline()
    if ok:
        out["kind"] = "polyline"
        out["points"] = len(polyline)
        if len(polyline) <= MAX_CORNERS + 1:
            out["corners"] = [_p(p) for p in polyline]
    else:
        ok, arc = curve.TryGetArc(coerce.doc_tol())
        if ok:
            out["kind"] = "circle" if curve.IsClosed else "arc"
            out["centre"] = _p(arc.Center)
            out["radius"] = _n(arc.Radius)
        else:
            out["kind"] = "curve"
            out["mid"] = _p(curve.PointAt(curve.Domain.Mid))
    out["planar"] = bool(curve.IsPlanar(coerce.doc_tol()))
    return out


def solid_summary(obj_id, brep):
    """A closed solid: what shape of member it is, and what replaces it."""
    out = {"solid": True}
    described = core_fe.describe(obj_id, brep)
    kind, facts = core_fe.classify(brep)
    if described:
        out["kind"] = described["kind"]
        if described.get("role"):
            out["role"] = described["role"]
        if described.get("disagreement"):
            out["note"] = described["disagreement"]
    box, frame, _warning = analyse_oriented_bbox.oriented_bbox_for_object(obj_id)
    if box is not None:
        info = analyse_oriented_bbox.describe_box(box, frame)
        out["dimensions"] = {k: _n(v) for k, v in info["dimensions"].items()}
        out["long_axis"] = [round(v, 3) for v in info["orientation"]["long_axis"]]
        out["centroid"] = [_n(v) for v in info["centroid"]]
        out["volume"] = _n(info["volume"])

    if kind == core_fe.LINEAR:
        line, length = core_fe.centreline(brep)
        if line is not None:
            out["axis"] = {"start": _p(line.PointAtStart), "end": _p(line.PointAtEnd),
                           "length": _n(length)}
        out["section"] = core_fe.section_of(facts)
    elif kind == core_fe.PLANAR:
        pair = core_fe.plate_faces(brep)
        if pair is not None:
            _a, _b, plane_a, plane_b = pair
            thickness = abs(plane_a.Origin.DistanceTo(plane_b.ClosestPoint(plane_a.Origin)))
            mid = plane_a.Origin + (plane_b.ClosestPoint(plane_a.Origin) - plane_a.Origin) * 0.5
            out["mid_plane"] = {"point": _p(mid), "normal": _v(plane_a.Normal)}
            out["thickness"] = _n(thickness)
        elif facts:
            out["thickness"] = _n(facts["thickness"])
            out["note"] = (out.get("note", "") + " curved plate - no flat mid-plane").strip()
    return out


def surface_summary(brep):
    """An open surface / polysurface: flat or not, how big, which way it faces."""
    out = {"faces": int(brep.Faces.Count)}
    try:
        amp = rg.AreaMassProperties.Compute(brep)
        if amp is not None:
            out["area"] = _n(amp.Area)
            out["centroid"] = _p(amp.Centroid)
    except Exception:
        pass
    tol = coerce.doc_tol()
    planar = all(face.IsPlanar(tol) for face in brep.Faces)
    out["planar"] = bool(planar)
    face = brep.Faces[0]
    if brep.Faces.Count == 1 and planar:
        ok, plane = face.TryGetPlane(tol)
        if ok:
            out["normal"] = _v(plane.Normal)
        loop = face.OuterLoop.To3dCurve()
        if loop is not None:
            ok, polyline = loop.TryGetPolyline()
            if ok and len(polyline) <= MAX_CORNERS + 1:
                out["corners"] = [_p(p) for p in polyline]
    elif brep.Faces.Count == 1:
        u, v = face.Domain(0).Mid, face.Domain(1).Mid
        out["normal_at_centre"] = _v(face.NormalAt(u, v))
    naked = [e for e in brep.Edges if e.Valence == rg.EdgeAdjacency.Naked]
    out["open_edges"] = len(naked)
    return out


def summary(obj_id):
    geometry = coerce.geometry_of(obj_id)
    if isinstance(geometry, rg.Point):
        return {"point": _p(geometry.Location)}
    if isinstance(geometry, rg.Curve):
        return curve_summary(geometry)
    if isinstance(geometry, rg.Mesh):
        return {"vertices": geometry.Vertices.Count, "faces": geometry.Faces.Count,
                "closed": bool(geometry.IsClosed)}
    brep = coerce.to_brep(obj_id)
    if brep is None:
        return {}
    if brep.IsSolid:
        return solid_summary(obj_id, brep)
    return surface_summary(brep)


# ---------- topology and full ----------

def oriented_box(obj_id):
    """The whole oriented bounding box - 8 corners and all three axes - or None.

    The summary keeps only its size, long axis and centroid; this is the
    rest, for when the model has to place something along the member."""
    try:
        box, frame, _warning = analyse_oriented_bbox.oriented_bbox_for_object(obj_id)
    except Exception:
        return None
    info = analyse_oriented_bbox.describe_box(box, frame)
    if not info:
        return None
    return {"corners": [[_n(v) for v in c] for c in info["corners"]],
            "axes": dict((k, [round(v, 3) for v in a]) for k, a in info["orientation"].items()),
            "extents": dict((k, _n(v)) for k, v in info["extents"].items())}


def topology(obj_id):
    brep = coerce.to_brep(obj_id)
    if brep is None:
        geometry = coerce.geometry_of(obj_id)
        if isinstance(geometry, rg.Curve):
            ok, polyline = geometry.TryGetPolyline()
            return {"segments": (len(polyline) - 1) if ok else geometry.SpanCount}
        return {}
    tol = coerce.doc_tol()
    faces = []
    for i, face in enumerate(brep.Faces):
        if i >= MAX_FACES:
            break
        entry = {"face": "{}.F{}".format(tagmod.tag_of(obj_id), i)}
        try:
            entry["area"] = _n(core_centerline.face_area(face))
        except Exception:
            pass
        ok, plane = face.TryGetPlane(tol)
        entry["planar"] = bool(ok)
        if ok:
            entry["normal"] = _v(plane.Normal)
            entry["point"] = _p(plane.Origin)
        faces.append(entry)
    naked = sum(1 for e in brep.Edges if e.Valence == rg.EdgeAdjacency.Naked)
    out = {"face_count": brep.Faces.Count, "edge_count": brep.Edges.Count,
           "vertex_count": brep.Vertices.Count, "naked_edges": naked,
           "solid": bool(brep.IsSolid), "manifold": bool(brep.IsManifold), "faces_list": faces}
    if brep.Faces.Count > MAX_FACES:
        out["faces_note"] = "first {} of {} faces".format(MAX_FACES, brep.Faces.Count)
    return out


def full(obj_id, max_points):
    """Vertices and edges as coordinates, never more than max_points in all."""
    budget = [int(max_points)]

    def take(points):
        points = points[:max(0, budget[0])]
        budget[0] -= len(points)
        return [_p(p) for p in points]

    geometry = coerce.geometry_of(obj_id)
    if isinstance(geometry, rg.Curve):
        ok, polyline = geometry.TryGetPolyline()
        pts = list(polyline) if ok else [geometry.PointAt(t) for t in
                                         (geometry.DivideByCount(24, True) or [])]
        out = {"points": take(pts)}
    elif isinstance(geometry, rg.Mesh):
        out = {"vertices": take([rg.Point3d(v) for v in geometry.Vertices])}
    else:
        brep = coerce.to_brep(obj_id)
        if brep is None:
            return {}
        out = {"vertices": take([v.Location for v in brep.Vertices]), "edges": []}
        for i, edge in enumerate(brep.Edges):
            if budget[0] <= 0:
                break
            if edge.IsLinear(coerce.doc_tol()):
                pts = take([edge.PointAtStart, edge.PointAtEnd])
            else:
                params = edge.DivideByCount(8, True) or []
                pts = take([edge.PointAt(t) for t in params])
            out["edges"].append({"edge": "{}.E{}".format(tagmod.tag_of(obj_id), i), "points": pts})
    if budget[0] <= 0:
        out["truncated"] = "max_points={} reached - ask for fewer objects".format(max_points)
    return out


# ---------- Tool entry point (MCP-callable) ----------

def tool_object_inspect(ids=None, detail="summary", max_points=200, selected=False):
    """Facts about a few objects. Returns an R2 receipt with one record per object.

    ``detail``: 'summary' (default) -> 'topology' -> 'full'. Start at
    summary; go deeper only for the objects that need it."""
    ids, error = coerce.only_selected("object.inspect", ids, selected)
    if error:
        return error
    detail = str(detail or "summary").lower()
    if detail not in DETAILS:
        return receipts.err("object.inspect", "detail must be one of {}.".format(", ".join(DETAILS)))
    guids = coerce.to_guids(ids)
    if not guids:
        return receipts.err("object.inspect", "No valid object ids supplied."
                            + coerce.missing_note(ids),
                            hint="Pass tags like ['#12'] - from vision.capture or map_query.")
    warnings = []
    if len(guids) > MAX_OBJECTS:
        warnings.append("{} objects - only the first {} inspected. Narrow down first "
                        "(vision.divide, map_query).".format(len(guids), MAX_OBJECTS))
        guids = guids[:MAX_OBJECTS]

    records = []
    for guid in guids:
        record = base_facts(guid)
        try:
            record.update(summary(guid))
            if detail in ("topology", "full"):
                record.update(topology(guid))
                box = oriented_box(guid)
                if box:
                    record["oriented_box"] = box
            if detail == "full":
                record.update(full(guid, max_points))
        except Exception as exc:
            record["error"] = "{}: {}".format(type(exc).__name__, exc)
        records.append(record)

    hint = None
    if detail == "summary":
        hint = ("To replace a member: object.delete it and draw its axis / mid-plane from these "
                "numbers. detail='topology' lists faces; 'full' gives coordinates.")
    return receipts.r2("inspect", records, ids=guids, detail=detail, warnings=warnings, hint=hint)


def tool_object_delete(ids=None, selected=False):
    """Delete objects. Returns an R2 receipt naming what went.

    The tags are read BEFORE deleting - afterwards they point at nothing."""
    ids, error = coerce.only_selected("object.delete", ids, selected)
    if error:
        return error
    guids = coerce.to_guids(ids)
    if not guids:
        return receipts.err("object.delete", "No valid object ids supplied."
                            + coerce.missing_note(ids))
    deleted, failed = [], []
    for guid in guids:
        tag = tagmod.tag_of(guid, create=False) or str(guid)
        if sc.doc.Objects.Delete(guid, True):
            deleted.append(tag)
        else:
            failed.append(tag)
    from rhino_tools._shared import redraw
    redraw.request()
    warnings = ["could not delete: {}".format(", ".join(failed))] if failed else []
    return receipts.r2("delete", len(deleted), deleted=deleted, warnings=warnings)


# ---------- Rhino execution wrapper ----------

def run_object_inspect():
    ids = rs.GetObjects("Objects to inspect", preselect=True)
    if not ids:
        return None
    return receipts.echo(tool_object_inspect(ids))


if __name__ == "__main__":
    run_object_inspect()
