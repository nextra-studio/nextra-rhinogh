"""
Plane plumbing - the backbone of the 2D-first doctrine.

The whole tool set is built on the idea that you set a working plane
first and then draw flat on it, instead of manipulating things in 3D.
That only works if "the plane" is an explicit, serialisable object that
survives a round trip through JSON, so this module defines exactly one
wire format:

    {"origin": [x,y,z], "xaxis": [x,y,z], "yaxis": [x,y,z],
     "zaxis": [x,y,z], "name": "world_xy"}

``zaxis`` is redundant (it is xaxis cross yaxis) but is emitted anyway so
a reader never has to compute a cross product to know which way is up.
On input it is ignored - the frame is always rebuilt from origin/x/y so
a hand-written plane cannot be internally inconsistent.

CPlane is per-viewport
----------------------
Rhino gives every viewport its own construction plane. Top is world XY,
Front is world ZX, Right is world YZ, and Perspective carries whatever
it was last given. Every function here that touches a CPlane therefore
takes a ``viewport`` argument, defaulting to "active".

Nothing in this module prompts the user.
"""

import math

import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

from . import coerce
from . import redraw
from . import session_store


# Conversation-scoped bucket for named planes saved with plane(op="save").
# Grid nodes ("A1", "B3") land here too - see grid_create.
_NAMED = "named_planes"

# The three world planes, addressable by name from any tool.
WORLD_PLANES = {
    "world_xy": rg.Plane.WorldXY,
    "world_zx": rg.Plane.WorldZX,
    "world_yz": rg.Plane.WorldYZ,
}


# ---------- Wire format ----------

def plane_to_dict(plane, name=None):
    """rg.Plane -> the explicit plane dict used by every receipt."""
    if plane is None or not plane.IsValid:
        return None
    out = {
        "origin": coerce.xyz(plane.Origin),
        "xaxis": coerce.xyz(plane.XAxis),
        "yaxis": coerce.xyz(plane.YAxis),
        "zaxis": coerce.xyz(plane.ZAxis),
    }
    if name:
        out["name"] = name
    return out


def dict_to_plane(data):
    """Plane dict -> rg.Plane.

    Rebuilds the frame from origin/xaxis/yaxis and lets Rhino derive the
    z axis, so a caller that sends a slightly non-orthogonal or stale
    ``zaxis`` still gets a valid right-handed plane back."""
    if data is None:
        return None
    if isinstance(data, rg.Plane):
        return data

    origin = coerce.to_point3d(data.get("origin", [0, 0, 0]))
    xaxis = coerce.to_vector3d(data.get("xaxis", [1, 0, 0]))
    yaxis = coerce.to_vector3d(data.get("yaxis", [0, 1, 0]))
    if origin is None or xaxis is None or yaxis is None:
        return None

    plane = rg.Plane(origin, xaxis, yaxis)
    return plane if plane.IsValid else None


# ---------- Named plane store ----------

def _named_store():
    return session_store.bucket(_NAMED)


def save_named(name, plane):
    """Remember a plane under a name for this conversation."""
    if not name or plane is None:
        return False
    _named_store()[str(name)] = plane_to_dict(plane, name)
    session_store.mark_dirty()
    return True


def would_overwrite(name):
    """Is this name already taken by a DIFFERENT plane?

    Two grid.create calls both name their nodes "A1", and the second
    used to overwrite the first in silence - so ``plane="A1"`` placed a
    column on whichever grid happened to be built last. Callers that
    save in bulk check this and report the clash rather than guessing
    which one the caller meant."""
    if not name:
        return False
    return str(name) in _named_store()


def get_named(name):
    """Look up a saved plane, or one of the three world planes."""
    if not name:
        return None
    key = str(name).strip().lower()
    if key in WORLD_PLANES:
        return WORLD_PLANES[key]
    store = _named_store()
    data = store.get(str(name)) or store.get(key)
    return dict_to_plane(data) if data else None


def list_named():
    """Every plane name this session knows about, world planes included."""
    return sorted(list(WORLD_PLANES.keys()) + list(_named_store().keys()))


# ---------- Viewports ----------

def get_view(viewport="active"):
    """Resolve a viewport argument to a RhinoView.

    Accepts "active" (or None), a viewport name, or a RhinoView. Returns
    None when the name does not match any open viewport, which callers
    surface as a warning rather than crashing."""
    if isinstance(viewport, Rhino.Display.RhinoView):
        return viewport
    if viewport is None or str(viewport).lower() in ("active", "", "current"):
        return sc.doc.Views.ActiveView
    try:
        return sc.doc.Views.Find(str(viewport), True)
    except Exception:
        return None


def all_views():
    """Every open model viewport, as a list."""
    return list(sc.doc.Views)


def resolve_views(viewport="active"):
    """'all' -> every viewport; anything else -> the single match.

    Returns a list either way so callers can write one loop instead of
    branching on the argument."""
    if isinstance(viewport, str) and viewport.lower() == "all":
        return all_views()
    view = get_view(viewport)
    return [view] if view is not None else []


def get_cplane(viewport="active"):
    """The construction plane of one viewport."""
    view = get_view(viewport)
    if view is None:
        return rg.Plane.WorldXY
    return view.ActiveViewport.ConstructionPlane()


def set_cplane(plane, viewport="active"):
    """Write a construction plane onto one or many viewports.

    Returns the list of viewport names that actually changed, which the
    receipt reports so the caller can tell 'all' really meant four."""
    if plane is None:
        return []
    changed = []
    for view in resolve_views(viewport):
        vp = view.ActiveViewport
        vp.SetConstructionPlane(plane)
        redraw.request(view)
        changed.append(vp.Name)
    return changed


def view_plane(viewport="active"):
    """The CAMERA plane (viewplane) of a viewport - the projection plane
    perpendicular to the line of sight.

    This is NOT the CPlane. Drawing tools work on the CPlane; capture and
    make2d work on this. Keeping them separate is the whole point of the
    distinction drawn in the workflow guidelines."""
    view = get_view(viewport)
    if view is None:
        return None
    vp = view.ActiveViewport
    # CameraX/Y/Z form the camera frame; CameraZ points from the target
    # back toward the camera, so the frame is already right-handed.
    return rg.Plane(vp.CameraLocation, vp.CameraX, vp.CameraY)


def view_info(viewport="active"):
    """Everything the R3/R6 receipts need to say about one viewport."""
    view = get_view(viewport)
    if view is None:
        return None
    vp = view.ActiveViewport
    return {
        "name": vp.Name,
        "projection": "parallel" if vp.IsParallelProjection else "perspective",
        "active_cplane": plane_to_dict(vp.ConstructionPlane()),
        "active_viewplane": plane_to_dict(
            rg.Plane(vp.CameraLocation, vp.CameraX, vp.CameraY)),
    }


# ---------- The default-resolution rule ----------

def resolve_plane(spec, viewport="active", strict=False):
    """Turn any plane argument into an rg.Plane, following the doctrine's
    fallback chain:

        explicit dict  ->  use it
        rg.Plane       ->  use it
        a name string  ->  saved plane, or one of the world planes
        "active"/None  ->  the target viewport's current CPlane

    Session start leaves every viewport on its stock CPlane, so the
    None case bottoms out at world XY exactly as the doc specifies.

    ``strict`` governs the one case the chain cannot express: a string
    that names nothing. Lenient (the default) ends at the CPlane, which
    is what the drafting tools have always done. Strict returns None, so
    the caller can refuse - see the note at the tail of the string
    branch for why the design layer asks for that."""
    if spec is None:
        return get_cplane(viewport)
    if isinstance(spec, rg.Plane):
        return spec
    if isinstance(spec, dict):
        return dict_to_plane(spec)
    if isinstance(spec, str):
        text = spec.strip()
        if text.lower() in ("active", "cplane", "current", ""):
            return get_cplane(viewport)
        if text.lower() in ("view", "viewplane", "camera"):
            return view_plane(viewport)
        named = get_named(text)
        if named is not None:
            return named
        # A string that names NOTHING. Falling through to the CPlane
        # here answers every typo, and every accidental argument, with
        # a valid world-origin plane - and a wrong answer that looks
        # like a right one is the worst thing this function can return.
        #
        # Measured: a script iterated a receipt (a dict, so it yielded
        # its KEYS) into a tool taking a plane. Each key - 'ok', 'hint',
        # 'warnings' - arrived here and left as world XY, so sixteen
        # storeys of geometry all landed at the same point. The tools
        # reported ok, built sixty-four braces stacked at the origin,
        # and the build only failed two steps later, where nothing
        # could diagnose it.
        #
        # Lenient stays the default because the drafting tools have
        # always leant on it. A caller that would rather refuse than
        # place geometry somewhere it did not mean passes strict.
        if strict:
            return None
    return get_cplane(viewport)


# ---------- Plane construction from geometry ----------

def plane_from_3points(pt_a, pt_b, pt_c):
    """Origin at A, x axis toward B, y axis toward C's side.

    Rhino's 3-point constructor already orthogonalises for us, so a
    sloppily-picked third point still yields a clean frame."""
    a = coerce.to_point3d(pt_a)
    b = coerce.to_point3d(pt_b)
    c = coerce.to_point3d(pt_c)
    if None in (a, b, c):
        return None
    plane = rg.Plane(a, b, c)
    return plane if plane.IsValid else None


def plane_from_face(face_ref, tol=None):
    """Plane of a planar Brep face, oriented with the face's normal.

    ``face_ref`` may be a '#12.F3' tag, a BrepFace, or an object id whose
    first face is used. Returns None for a non-planar face - the caller
    should fall back rather than silently drawing on a fitted plane."""
    tol = coerce.tol_or_default(tol)

    face = None
    if isinstance(face_ref, rg.BrepFace):
        face = face_ref
    elif isinstance(face_ref, str) and face_ref.startswith("#"):
        from . import tags
        resolved = tags.resolve(face_ref)
        if resolved and resolved["kind"] == "face":
            face = resolved["geometry"]
    if face is None:
        brep = coerce.to_brep(face_ref)
        if brep is not None and brep.Faces.Count > 0:
            face = brep.Faces[0]
    if face is None:
        return None

    ok, plane = face.TryGetPlane(tol)
    if not ok:
        return None

    # TryGetPlane can hand back a plane pointing into the solid; align it
    # with the face's own normal so "extrude along plane z" goes outward.
    normal = face.NormalAt(face.Domain(0).Mid, face.Domain(1).Mid)
    if plane.ZAxis * normal < 0:
        plane = rg.Plane(plane.Origin, -plane.XAxis, plane.YAxis)
    return plane


def plane_from_planar_curve(curve_ref, tol=None):
    """Plane a planar curve lies in, origin moved to the curve's start.

    Putting the origin on the curve start (rather than wherever Rhino's
    fitted plane landed) makes plane coordinates readable - a rectangle's
    first corner becomes roughly [0, 0]."""
    tol = coerce.tol_or_default(tol)
    curve = coerce.to_curve(curve_ref)
    if curve is None:
        return None

    ok, plane = curve.TryGetPlane(tol)
    if not ok:
        return None
    return rg.Plane(curve.PointAtStart, plane.XAxis, plane.YAxis)


def plane_from_object_axis(obj_ref, tol=None):
    """Plane built on an object's own long axis.

    For a curve: origin at the start, x axis along the start tangent.
    For a brep/extrusion: the principal-axis frame, so the plane's z runs
    down the member. This is the "object_axis" source in ``plane.set``
    and it reuses the same principal-moment maths as the centerline
    tools rather than inventing a second definition of 'long axis'."""
    curve = coerce.to_curve(obj_ref)
    if curve is not None:
        tangent = curve.TangentAtStart
        frame_ok, frame = curve.FrameAt(curve.Domain.T0)
        if frame_ok:
            return rg.Plane(curve.PointAtStart, frame.XAxis, frame.YAxis)
        return rg.Plane(curve.PointAtStart, tangent)

    brep = coerce.to_brep(obj_ref)
    if brep is None:
        return None

    # Deferred import: core_centerline pulls in this module for its own
    # plane helpers, so importing it at module scope would cycle.
    from ..structural_utils import core_centerline
    frame = core_centerline.principal_frame(brep)
    if frame is None:
        return None
    return rg.Plane(frame["centroid"], frame["axis1"], frame["axis2"])


# ---------- Coordinate conversion ----------

def world_to_plane(points, plane):
    """World Point3d list -> [u, v, w] coordinates in the plane's frame.

    ``w`` is the signed distance off the plane, so a caller can check
    'is this really flat on my working plane?' by looking at how close
    the third number is to zero."""
    if plane is None:
        return []
    out = []
    for pt in coerce.to_point3d_list(points):
        vec = pt - plane.Origin
        out.append([float(vec * plane.XAxis),
                    float(vec * plane.YAxis),
                    float(vec * plane.ZAxis)])
    return out


def plane_to_world(coords, plane):
    """[u, v] or [u, v, w] in plane coordinates -> world [x, y, z]."""
    if plane is None:
        return []
    out = []
    for c in (coords or []):
        u = float(c[0])
        v = float(c[1]) if len(c) > 1 else 0.0
        w = float(c[2]) if len(c) > 2 else 0.0
        pt = plane.Origin + plane.XAxis * u + plane.YAxis * v + plane.ZAxis * w
        out.append(coerce.xyz(pt))
    return out


def point_on_plane(point, plane, tol=None):
    """True when a point lies on the plane within tolerance."""
    tol = coerce.tol_or_default(tol)
    pt = coerce.to_point3d(point)
    if pt is None or plane is None:
        return False
    return abs(plane.DistanceTo(pt)) <= tol


def angle_between(vec_a, vec_b):
    """Unsigned angle in degrees between two vectors. Used by the
    parallel/colinear cluster rules and by several query tools."""
    a = coerce.to_vector3d(vec_a)
    b = coerce.to_vector3d(vec_b)
    if a is None or b is None or a.Length == 0 or b.Length == 0:
        return None
    cosine = (a * b) / (a.Length * b.Length)
    cosine = max(-1.0, min(1.0, cosine))
    return math.degrees(math.acos(cosine))
