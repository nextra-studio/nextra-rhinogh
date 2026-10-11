"""
Coercion helpers - the single place where "whatever the caller gave us"
becomes a concrete Rhino type.

Why this module exists
----------------------
Every tool in this package can be driven from two very different places:

  * a human, via the Rhino-executable ``run_*`` wrapper, who hands us
    real ``System.Guid`` objects straight out of ``rs.GetObjects()``
  * an MCP client, which can only send JSON - so ids arrive as tag
    strings ("#12"), GUID strings, points arrive as ``[x, y, z]`` lists,
    planes arrive as dicts, and so on.

Rather than making all ~60 tools defensive about that, every tool calls
into here first and then works with guaranteed-clean Rhino types.

Nothing in this module prompts the user. It is safe to import and call
from the MCP listener.
"""

import System
import Rhino
import Rhino.Geometry as rg
import rhinoscriptsyntax as rs
import scriptcontext as sc


# ---------- Document tolerances ----------

def doc_tol():
    """Model absolute tolerance. The default for nearly every geometric
    predicate in this package."""
    return sc.doc.ModelAbsoluteTolerance


def doc_angle_tol():
    """Model angle tolerance in RADIANS (Rhino stores it in radians)."""
    return sc.doc.ModelAngleToleranceRadians


def doc_units():
    """Unit system name as a string, e.g. 'Millimeters'."""
    return str(sc.doc.ModelUnitSystem)


def tol_or_default(tol):
    """Let any tool accept ``tol=None`` and fall back to the document."""
    return doc_tol() if tol is None else float(tol)


# ---------- Ids ----------


def describe_input(value, limit=90):
    """What the caller passed, short enough to read in an error.

    ``{!r}`` on a receipt prints the whole thing - ids, bbox, plane dict,
    hint - which on one house build put about four hundred tokens of
    noise into an error message that needed eight. Worse, the useful part
    (that it was a RECEIPT, and which tool made it) was buried in the
    middle of it.

    So a receipt is named by what it is, a list by its length, and
    anything else is repr'd and clipped."""
    if value is None:
        return "nothing"

    if isinstance(value, dict):
        operation = value.get("operation") or "a tool"
        ids = value.get("ids") or []
        if ids:
            return ("the whole receipt from {} ({} id(s)) - pass one of "
                    "its ids, not the receipt".format(operation, len(ids)))
        return "an empty receipt from {}".format(operation)

    if isinstance(value, (list, tuple)):
        if not value:
            return "an empty list"
        return "a list of {} ({!r} ...)".format(
            len(value), value[0])[:limit]

    text = repr(value)
    return text if len(text) <= limit else text[:limit - 3] + "..."

def to_guid(value):
    """Accept a Guid, a GUID string, a RhinoObject, or a '#12' tag and
    return a ``System.Guid``. Returns None if it cannot be resolved.

    The '#12' branch is deferred-imported because ``tags`` imports this
    module - resolving that cycle lazily keeps both importable.
    """
    if value is None:
        return None

    if isinstance(value, System.Guid):
        return value

    if isinstance(value, Rhino.DocObjects.RhinoObject):
        return value.Id

    # A RECEIPT standing in for the thing it made.
    #
    # Models write solid.extrude(ids=deck_outline) where
    # deck_outline is the receipt from curve.create, because the receipt
    # IS the outline as far as the script is concerned. It used to fail
    # with "No curve resolved from {...}" and dump the entire receipt -
    # four hundred tokens of bbox and plane dict - which told the model
    # nothing about what it did wrong. Measured: two tools failed this
    # way in one step of a house build.
    #
    # Same trade as receipt[0] in script_receipts: a receipt carrying
    # exactly one id can only have meant that id.
    if isinstance(value, dict):
        ids = value.get("ids") or []
        if len(ids) == 1:
            return to_guid(ids[0])
        single = value.get("id") or value.get("guid")
        if single:
            return to_guid(single)
        return None

    if isinstance(value, str):
        text = value.strip()
        if text.startswith("#"):
            from . import tags  # local import: breaks the tags <-> coerce cycle
            return tags.guid_from_tag(text)
        try:
            return System.Guid(text)
        except Exception:
            return None

    # rhinoscriptsyntax will coerce a few other shapes for us
    try:
        return rs.coerceguid(value, False)
    except Exception:
        return None


def to_guids(value):
    """Normalise anything id-shaped into a flat list of Guids, dropping
    entries that cannot be resolved. A single id is treated as a list of
    one, so every tool can just take ``ids`` and not care."""
    if value is None:
        return []
    if isinstance(value, (str, System.Guid)):
        value = [value]
    elif isinstance(value, Rhino.DocObjects.RhinoObject):
        value = [value]

    out = []
    for item in value:
        guid = to_guid(item)
        if guid is not None:
            out.append(guid)
    return out


def missing_note(value):
    """'' or a sentence naming the ids in ``value`` whose objects are gone.

    ``to_guids`` drops what it cannot resolve, so a tool that gets back
    an empty list knows only that nothing resolved - and the message it
    writes, 'No valid object ids supplied', is true but unusable. This
    says which id, and that it USED to exist. Deferred import for the
    same cycle reason as the '#12' branch in ``to_guid``."""
    try:
        from . import tags
        return tags.explain_missing(value)
    except Exception:
        return ""


def only_selected(tool, ids=None, selected=False):
    """``selected=True``: narrow a tool to the objects selected in Rhino right
    now - with ``ids`` too, only those of them that are selected.

    Returns (ids, None), or (None, error receipt) when nothing (of ``ids``)
    is selected: a tool whose ``ids=None`` means "the whole model" must never
    fall back to that when the user asked for the selection only.
    ``selected=False`` passes ``ids`` through untouched."""
    if not selected:
        return ids, None
    chosen = [str(o.Id) for o in sc.doc.Objects.GetSelectedObjects(False, False)]
    if ids:
        wanted = set(str(g) for g in to_guids(ids))
        chosen = [g for g in chosen if g in wanted]
    if chosen:
        return chosen, None
    from . import receipts          # deferred: receipts imports coerce
    return None, receipts.err(
        tool, "selected=True, but nothing {}is selected in Rhino.".format("of ids " if ids else ""),
        hint="Select the objects in Rhino first, or leave selected out (= the whole model).")


def find_object(value):
    """Guid-ish -> RhinoObject, or None if it is not in the document."""
    guid = to_guid(value)
    if guid is None:
        return None
    return sc.doc.Objects.Find(guid)


def geometry_of(value):
    """Guid-ish -> its GeometryBase, or the value itself if it is already
    geometry. This is what lets a pure function accept either an id from
    MCP or a live geometry object from another pure function."""
    if isinstance(value, rg.GeometryBase):
        return value
    rhobj = find_object(value)
    return rhobj.Geometry if rhobj is not None else None


# ---------- Geometry type coercion ----------

def to_brep(value, tol=None):
    """Anything solid-ish -> Brep.

    Handles the Extrusion case explicitly. Rhino stores simple extruded
    profiles (boxes, extruded curves, most imported columns and beams)
    as lightweight ``Extrusion`` objects, NOT Breps - so face iteration,
    ``VolumeMassProperties`` and boolean input all silently fail on them
    unless you convert first. This is exactly the guard the confirmed
    centerline tools open with, lifted here so every tool inherits it.

    ``ToBrep(False)`` means "do not split kinked faces" - we want the
    natural face layout, which is what makes end-cap detection work.
    """
    geom = geometry_of(value)
    if geom is None:
        return None

    if isinstance(geom, rg.Brep):
        return geom
    if isinstance(geom, rg.Extrusion):
        return geom.ToBrep(False)
    if isinstance(geom, rg.SubD):
        return rg.Brep.CreateFromSubD(geom, 0)
    if isinstance(geom, rg.Surface):
        return geom.ToBrep()
    if isinstance(geom, rg.Mesh):
        # Meshes are reference-only per the doctrine, but converting is
        # better than failing outright when someone measures one.
        return rg.Brep.CreateFromMesh(geom, True)
    return None


def to_breps(values, tol=None):
    """List coercion that quietly skips anything unconvertible."""
    out = []
    for v in (values or []):
        brep = to_brep(v, tol)
        if brep is not None:
            out.append(brep)
    return out


def to_curve(value):
    """Anything curve-ish -> Curve. Accepts ids, curve geometry, an edge,
    or a Line/Polyline/Arc/Circle value type."""
    if isinstance(value, rg.Curve):
        return value
    if isinstance(value, rg.Line):
        return rg.LineCurve(value)
    if isinstance(value, rg.Polyline):
        return rg.PolylineCurve(value)
    if isinstance(value, rg.Arc):
        return rg.ArcCurve(value)
    if isinstance(value, rg.Circle):
        return rg.ArcCurve(value)

    geom = geometry_of(value)
    if isinstance(geom, rg.Curve):
        return geom
    if isinstance(geom, rg.BrepEdge):
        return geom.EdgeCurve
    return None


def to_curves(values):
    out = []
    for v in (values or []):
        crv = to_curve(v)
        if crv is not None:
            out.append(crv)
    return out


def to_surface(value):
    """Anything surface-ish -> Surface (the underlying untrimmed one)."""
    if isinstance(value, rg.Surface):
        return value
    geom = geometry_of(value)
    if isinstance(geom, rg.Surface):
        return geom
    if isinstance(geom, rg.Brep) and geom.Faces.Count > 0:
        return geom.Faces[0].UnderlyingSurface()
    if isinstance(geom, rg.Extrusion):
        brep = geom.ToBrep(False)
        if brep is not None and brep.Faces.Count > 0:
            return brep.Faces[0].UnderlyingSurface()
    return None


def to_point3d(value):
    """[x,y,z] / (x,y,z) / Point3d / Point / point id -> Point3d.

    A 2-item sequence is read as [x, y] with z = 0, which is what makes
    the plane-relative 2D workflows in the doctrine ergonomic."""
    if value is None:
        return None
    if isinstance(value, rg.Point3d):
        return value
    if isinstance(value, rg.Point):
        return value.Location
    if isinstance(value, rg.Vector3d):
        return rg.Point3d(value.X, value.Y, value.Z)

    if isinstance(value, (list, tuple)):
        if len(value) == 2:
            return rg.Point3d(float(value[0]), float(value[1]), 0.0)
        if len(value) >= 3:
            return rg.Point3d(float(value[0]), float(value[1]), float(value[2]))
        return None

    geom = geometry_of(value)
    if isinstance(geom, rg.Point):
        return geom.Location
    if isinstance(geom, rg.Point3d):
        return geom
    return None


def to_point3d_list(values):
    out = []
    for v in (values or []):
        pt = to_point3d(v)
        if pt is not None:
            out.append(pt)
    return out


def to_vector3d(value):
    """[x,y,z] / Vector3d / Point3d -> Vector3d."""
    if value is None:
        return None
    if isinstance(value, rg.Vector3d):
        return value
    pt = to_point3d(value)
    return rg.Vector3d(pt.X, pt.Y, pt.Z) if pt is not None else None


# ---------- Serialisation back out to JSON ----------

def xyz(point_or_vector):
    """Point3d/Vector3d -> [x, y, z] floats, JSON-safe."""
    if point_or_vector is None:
        return None
    return [float(point_or_vector.X),
            float(point_or_vector.Y),
            float(point_or_vector.Z)]


def bbox_dict(bbox):
    """BoundingBox -> {'min': [...], 'max': [...]}, or None if invalid."""
    if bbox is None or not bbox.IsValid:
        return None
    return {"min": xyz(bbox.Min), "max": xyz(bbox.Max)}


def bbox_of(values):
    """Union bounding box of any mix of ids/geometry. Returns None when
    nothing usable was supplied - callers treat that as 'no bbox'."""
    box = rg.BoundingBox.Empty
    any_found = False
    items = values if isinstance(values, (list, tuple)) else [values]
    for v in items:
        geom = geometry_of(v)
        if geom is None:
            continue
        gb = geom.GetBoundingBox(True)
        if not gb.IsValid:
            continue
        box = gb if not any_found else rg.BoundingBox.Union(box, gb)
        any_found = True
    return box if any_found else None


# ---------- Object classification ----------

def object_type_name(value):
    """Short, JSON-friendly type name for a document object or geometry.

    Order matters here because of .NET inheritance: Extrusion must be
    tested before the Surface fallback, and Brep before Surface, or
    everything collapses into 'surface'."""
    geom = geometry_of(value)
    if geom is None:
        return "unknown"

    if isinstance(geom, rg.Point):
        return "point"
    if isinstance(geom, rg.PointCloud):
        return "pointcloud"
    if isinstance(geom, rg.Curve):
        return "curve"
    if isinstance(geom, rg.Mesh):
        return "mesh"
    if isinstance(geom, rg.Extrusion):
        return "solid" if geom.IsSolid else "surface"
    if isinstance(geom, rg.Brep):
        if geom.IsSolid:
            return "solid"
        return "surface" if geom.Faces.Count == 1 else "polysurface"
    if isinstance(geom, rg.Surface):
        return "surface"
    if isinstance(geom, rg.SubD):
        return "subd"
    if isinstance(geom, rg.InstanceReferenceGeometry):
        return "block"
    if isinstance(geom, rg.TextDot):
        return "textdot"
    if isinstance(geom, rg.Hatch):
        return "hatch"
    if isinstance(geom, rg.AnnotationBase):
        return "annotation"
    return geom.GetType().Name.lower()


def layer_name_of(value):
    """Full layer path ('Parent::Child') of a document object, or None."""
    rhobj = find_object(value)
    if rhobj is None:
        return None
    layer = sc.doc.Layers[rhobj.Attributes.LayerIndex]
    return layer.FullPath
