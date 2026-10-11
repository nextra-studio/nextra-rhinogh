"""
core_fe - turning built geometry into the thing an analysis reads.

A finite element model is not a pile of solids. A column is a LINE with a
section; a wall is a SURFACE with a thickness. The solid is what the
architect drew; these are what the engineer analyses, and getting from
one to the other is the whole job of the structural pipeline.

Two questions, in order
-----------------------
    WHAT IS IT?     linear, planar, or neither. Decided from the solid's
                    own proportions, not from its name - a thing called
                    'Column_01' on a layer called 'Structure' might be a
                    footing, and the geometry cannot lie about its shape.
    WHAT REPLACES IT?
                    linear -> a centreline plus a section (w x d)
                    planar -> a mid-surface plus a thickness

The classification mirrors what ``core_centerline`` already does for
linear members, and deliberately reuses its ``principal_frame``: the
three principal extents ARE the answer. Sort them and the shape falls
out.

    column  300 x  300 x 4000   ->  one extent dominates      -> LINEAR
    wall    200 x 3000 x 6000   ->  one extent is tiny        -> PLANAR
    footing 600 x  800 x  800   ->  none dominates            -> BLOCK

A deep wall - 200 x 3000 x 20000 - satisfies both tests, so the tie is
broken by which ratio is LARGER. Being fifteen times as long as it is
thick makes it a plate more than being seven times as long as it is deep
makes it a beam. Guessing that wrong turns a shear wall into a beam and
the analysis is nonsense, so the rule is written down rather than left to
whichever test ran first.

What the architect wrote down
-----------------------------
Layer names and object attributes are the only place intent survives -
'A-WALL-EXT', 'Structural Columns', a user string saying 'C30/37'. They
are read as a HINT that can confirm or contradict the geometry, never as
the answer: the geometry decides, and a disagreement is reported rather
than silently resolved. Only a short, filtered digest ever travels, since
a model with four thousand objects would otherwise put four thousand
layer names into a prompt.

Layer A. Nothing here prompts, draws, or touches the document.
"""

import math

import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

from .._shared import coerce, precision
from . import core_centerline


# How many times one extent must exceed another before the shape is
# called. Three is deliberate and low: a 1:3 member is already clearly
# one thing or the other, and anything below it is genuinely a block.
SHAPE_RATIO = 3.0

# Faces flatter than this are treated as planar. Same value and same
# reason as core_centerline: a "nearly" flat face is a fillet or a draft,
# and letting one through puts the mid-surface through the side of a wall.
PLANAR_TOL = 0.001

LINEAR = "linear"
PLANAR = "planar"
BLOCK = "block"


# ---------- what is it ----------

def _extents(info):
    """The three principal extents as plain lengths, ascending."""
    return sorted([abs(info["ext1"].Length),
                   abs(info["ext2"].Length),
                   abs(info["ext3"].Length)])


def classify(brep, ratio=SHAPE_RATIO):
    """``(kind, facts)`` for one solid. None if it has no volume.

    ``facts`` carries the numbers the decision was made on, so a caller
    that disagrees can see why rather than arguing with a verdict.
    """
    info = core_centerline.principal_frame(brep)
    if info is None:
        return None, None

    thin, mid, long_ = _extents(info)
    if thin <= 0 or mid <= 0:
        return None, None

    slenderness = long_ / mid          # how beam-like
    flatness = mid / thin              # how plate-like

    if slenderness >= ratio and slenderness >= flatness:
        kind = LINEAR
    elif flatness >= ratio and flatness > slenderness:
        kind = PLANAR
    else:
        kind = BLOCK

    return kind, {
        "thickness": float(thin),
        "width": float(mid),
        "length": float(long_),
        "slenderness": round(float(slenderness), 2),
        "flatness": round(float(flatness), 2),
        "volume": float(info.get("volume") or 0.0),
        "frame": info,
    }


# ---------- what replaces it ----------

def section_of(facts):
    """The two smaller extents, as the section a centreline carries.

    Reported width-then-depth with the smaller first, so '50 x 200'
    always reads the same way round for the same joist however the solid
    happened to be oriented when it was drawn."""
    if not facts:
        return None
    return {"width": precision.length(facts["thickness"]),
            "depth": precision.length(facts["width"])}


def plate_faces(brep, tol=PLANAR_TOL):
    """The two LARGEST planar faces - the two sides of a plate.

    The mirror of ``core_centerline.endcap_faces``, which takes the two
    SMALLEST for a linear member. Same reasoning, opposite end of the
    list: on a wall or a slab the two faces are larger than every edge
    face by a wide margin, so sorting by area and taking the last two is
    both cheap and right.

    Returns None unless the two are parallel and face each other - on a
    wedge or a tapered slab they are not, and there is no single
    mid-surface to find."""
    faces = core_centerline.planar_faces(brep, tol)
    if len(faces) < 2:
        return None

    ranked = sorted(faces, key=core_centerline.face_area)
    first, second = ranked[-1], ranked[-2]

    ok_a, plane_a = first.TryGetPlane(tol)
    ok_b, plane_b = second.TryGetPlane(tol)
    if not (ok_a and ok_b):
        return None

    # Opposite faces of a plate point away from each other, so the dot
    # product of their normals is near -1. Near +1 means the same side
    # twice; near 0 means they meet at a corner.
    alignment = plane_a.ZAxis * plane_b.ZAxis
    if abs(alignment) < 0.99:
        return None

    return first, second, plane_a, plane_b


def mid_surface(brep, tol=PLANAR_TOL, force_curved=False):
    """``(surface, thickness)`` for a plate-like solid, or ``(None, why)``.

    The surface an analysis meshes as a shell, and the thickness it
    assigns to it. Built by moving the larger face half way to the other
    one, which is exact for the flat case that covers almost every wall
    and slab.

    A curved plate has no pair of planar faces, so it falls back to
    offsetting the largest face inward by half the measured thickness.
    That is what a curved wall needs and it is approximate - the offset
    of a doubly-curved surface is not its mid-surface - so the caller is
    told which route was taken rather than being handed a surface that
    silently means something slightly different."""
    pair = None if force_curved else plate_faces(brep, tol)

    if pair is not None:
        first, _second, plane_a, plane_b = pair
        thickness = abs(plane_a.Origin.DistanceTo(
            plane_b.ClosestPoint(plane_a.Origin)))
        if thickness <= 0:
            return None, "the two faces are coincident"

        surface = first.DuplicateFace(False)
        # Move half way, TOWARDS the other face - along the face's NORMAL. The
        # line between the two planes' origins runs mostly ALONG a wall when
        # they sit far apart (an IFC wall with windows: its mid-surface came
        # out 108 mm off, 75 mm along and 118 mm up)
        direction = rg.Vector3d(plane_a.ZAxis)
        if (plane_b.Origin - plane_a.Origin) * direction < 0:
            direction.Reverse()
        surface.Translate(direction * (thickness / 2.0))
        return surface, float(thickness)

    # ---- curved plate
    faces = list(brep.Faces)
    if not faces:
        return None, "no faces"
    curved = sorted([f for f in faces if not f.IsPlanar(tol)], key=core_centerline.face_area,
                    reverse=True)
    largest = (curved or sorted(faces, key=core_centerline.face_area, reverse=True))[0]
    vmp = rg.VolumeMassProperties.Compute(brep)
    if vmp is None or vmp.Volume <= 0:
        return None, "no computable volume"
    # thickness = volume / the mean of its two big (curved) sides - the box's
    # smallest side is a vault's RISE, not its thickness (1820 for a 400 shell)
    sides = [core_centerline.face_area(f) for f in (curved[:2] if len(curved) >= 2 else [largest])]
    thickness = vmp.Volume / (sum(sides) / len(sides))

    # the trimmed face offset half the thickness INTO the solid: try both ways,
    # keep the one whose middle lies inside (a face's normal may point in or out)
    best = None
    for sign in (-1.0, 1.0):
        try:
            made = rg.Brep.CreateFromOffsetFace(largest, sign * thickness / 2.0,
                                                coerce.doc_tol(), False, False)
        except Exception:
            made = None
        if made is None or made.Faces.Count == 0:
            continue
        f = made.Faces[0]
        mid = f.PointAt(f.Domain(0).Mid, f.Domain(1).Mid)
        inside = brep.IsPointInside(mid, coerce.doc_tol(), False)
        if best is None or (inside and not best[0]):
            best = (inside, made)
    if best is None:
        return None, "could not offset the face to the middle"
    return best[1], float(thickness)


# A flat plate is as thick as its box across it, give or take pockets. A
# curved one is much thicker across its box than volume / area says.
CURVED_PLATE_RATIO = 1.5


def close_open(geometry):
    """An OPEN polysurface / mesh that is a solid with faces missing (an IFC
    wall without its bottom face, a tube without end caps) CLOSED: its planar
    holes capped, a mesh's holes filled. None when that does not close it."""
    tol = coerce.doc_tol()
    if isinstance(geometry, rg.Mesh):
        if geometry.IsClosed:
            return geometry
        m = geometry.DuplicateMesh()
        try:
            m.FillHoles()
        except Exception:
            return None
        return m if m.IsClosed and _small_cap(geometry, m) else None
    if isinstance(geometry, rg.Brep):
        if geometry.IsSolid:
            return geometry
        capped = geometry.CapPlanarHoles(tol)
        if capped is not None and capped.IsSolid and _small_cap(geometry, capped):
            return capped
        return nearly_solid(geometry)
    return None


def healed(brep):
    """An INVALID brep (an import with a broken face: 'm_L[41] loop is not
    valid', IsSolid still True) as something Rhino can measure - it gave no
    volume, so no frame, and convert made nothing. Repaired when that gives
    a measurable solid, else its mesh, holes filled. None when neither."""
    tol = coerce.doc_tol()
    fixed = brep.DuplicateBrep()
    try:
        fixed.Repair(tol)
    except Exception:
        fixed = None
    if fixed is not None and fixed.IsValid:
        if not fixed.IsSolid:
            fixed = close_open(fixed) or fixed
        if isinstance(fixed, rg.Brep) and fixed.IsSolid and \
                rg.VolumeMassProperties.Compute(fixed, True, False, False, False) is not None:
            return fixed
    return as_mesh(brep)


def as_mesh(brep):
    """The brep as one welded mesh, its holes filled where they close it
    (a face that does not mesh leaves one), or None."""
    mesh = rg.Mesh()
    for part in rg.Mesh.CreateFromBrep(brep, rg.MeshingParameters.QualityRenderMesh) or []:
        mesh.Append(part)
    if mesh.Faces.Count == 0:
        return None
    mesh.Weld(math.radians(22.5))
    if not mesh.IsClosed:
        filled = mesh.DuplicateMesh()
        try:
            filled.FillHoles()
            if filled.IsClosed:
                mesh = filled
        except Exception:
            pass
    mesh.UnifyNormals()
    return mesh


CAP_SHARE = 0.5            # a hole filled with more than this share of the area was no hole


def _area_of(geometry):
    amp = rg.AreaMassProperties.Compute(geometry, True, False, False, False)
    return amp.Area if amp is not None else 0.0


def _small_cap(before, after):
    """Did closing ADD little (a wall's missing bottom: 3 % of it, a pipe's
    ends: 2.5 %)? A SHEET closed over its whole rim gains as much as it has -
    an open roof mesh became a closed half-barrel: it is a sheet, not a solid."""
    a, b = _area_of(before), _area_of(after)
    return a > 0 and (b - a) <= CAP_SHARE * a


SHEET_FLAT_SHARE = 0.01     # a sheet within this share of its size of a plane is flat


def sheet_surface(geometry):
    """``(surface, how)`` for a SHEET - an open polysurface or mesh that is one
    layer of surface (a roof modelled as a single surface), not a solid: it is
    its own mid-surface. A single face as it is; flat faces merged into one
    planar surface; a curved polysurface or mesh a smooth surface fitted
    through it (fit_through, one side), trimmed to its outline - a curved open
    mesh was flattened. ``(None, why)`` when it is not a sheet."""
    tol = coerce.doc_tol()
    if isinstance(geometry, rg.Extrusion):
        geometry = geometry.ToBrep()
    if isinstance(geometry, rg.Brep):
        if geometry.Faces.Count == 1:
            return geometry, "sheet"
        merged = geometry.DuplicateBrep()
        merged.MergeCoplanarFaces(tol * 10)
        if merged.Faces.Count == 1:
            return merged, "sheet (faces merged)"
        mesh = rg.Mesh()
        for part in rg.Mesh.CreateFromBrep(geometry, rg.MeshingParameters.QualityRenderMesh) or []:
            mesh.Append(part)
        mesh.Weld(math.radians(22.5))
        geometry = mesh
    if not isinstance(geometry, rg.Mesh) or geometry.Faces.Count == 0:
        return None, "not a sheet"
    pts = [rg.Point3d(v) for v in geometry.Vertices]
    rc, plane = rg.Plane.FitPlaneToPoints(pts)
    if rc == rg.PlaneFitResult.Failure:
        return None, "no plane through the sheet"
    outlines = sorted(geometry.GetNakedEdges() or [], key=lambda pl: pl.Length, reverse=True)
    if not outlines:
        return None, "the sheet has no outline"
    rim = list(outlines[0])
    if rim[0].DistanceTo(rim[-1]) > tol:
        rim.append(rim[0])
    size = geometry.GetBoundingBox(True).Diagonal.Length
    if max(abs(plane.DistanceTo(p)) for p in pts) <= size * SHEET_FLAT_SHARE:
        flat = rg.PolylineCurve([plane.ClosestPoint(p) for p in rim])
        holes = [rg.PolylineCurve([plane.ClosestPoint(p) for p in list(o) + [o[0]]])
                 for o in outlines[1:]]
        made = rg.Brep.CreatePlanarBreps([flat] + holes, tol) or rg.Brep.CreatePlanarBreps([flat], tol)
        return (made[0], "sheet (flat)") if made else (None, "no flat surface from the sheet")
    made, why = fit_through([geometry], plane, rim, tol)
    return (made, "sheet (curved, fitted)") if made is not None else (None, why)


def nearly_solid(brep):
    """An IFC solid that is not quite closed (a wall with window cut-outs: 79
    faces, ONE naked edge no join tolerance closes) as a closed welded mesh -
    or None when it really is open (a surface stays a surface)."""
    naked = brep.DuplicateNakedEdgeCurves(True, False) or []
    if not naked or len(naked) > max(4, brep.Faces.Count // 10):
        return None
    mesh = rg.Mesh()
    for part in rg.Mesh.CreateFromBrep(brep, rg.MeshingParameters.QualityRenderMesh) or []:
        mesh.Append(part)
    mesh.Weld(math.radians(22.5))
    mesh.UnifyNormals()
    return mesh if mesh.IsClosed else None


MESH_CREASE_DEG = 30.0      # a mesh splits into its sides at creases sharper than this
MESH_CURVED_SHARE = 0.01    # a side bending more than this share of its size is curved


def _mesh_area(mesh):
    amp = rg.AreaMassProperties.Compute(mesh)
    return amp.Area if amp is not None else 0.0


def mesh_mid_surface(mesh):
    """``(surface, thickness)`` for a CURVED closed mesh plate (an IFC vault or
    barrel roof comes as triangles), or ``(None, why)`` - a flat one is left
    to ``mid_surface_sliced``.

    Slicing a vault through its thinnest box direction cut it ACROSS (a flat
    strip, 't = 3604'). Instead: the mesh welded at its smooth edges and
    split at its creases - the two largest pieces are its two sides; the
    thickness is how far apart they are (median), and the mid-surface is a
    smooth surface through a regular grid of points half way between them,
    trimmed to the outline half way between them."""
    tol = coerce.doc_tol()
    m = mesh.DuplicateMesh()
    m.Weld(math.radians(MESH_CREASE_DEG))
    pieces = sorted(m.ExplodeAtUnweldedEdges() or [], key=_mesh_area, reverse=True)
    if len(pieces) < 2:
        return None, "the mesh has no two sides"
    top, bottom = pieces[0], pieces[1]
    pts = [rg.Point3d(v) for v in top.Vertices]
    rc, plane = rg.Plane.FitPlaneToPoints(pts)
    size = top.GetBoundingBox(True).Diagonal.Length
    if rc == rg.PlaneFitResult.Failure or max(abs(plane.DistanceTo(p)) for p in pts) <= size * MESH_CURVED_SHARE:
        return None, "flat"
    gaps = sorted(p.DistanceTo(bottom.ClosestPoint(p)) for p in pts)
    thickness = gaps[len(gaps) // 2]
    outlines = sorted(top.GetNakedEdges() or [], key=lambda pl: pl.Length, reverse=True)
    if not outlines:
        return None, "the mesh side has no outline"
    rim = [(p + bottom.ClosestPoint(p)) / 2.0 for p in outlines[0]]
    if rim[0].DistanceTo(rim[-1]) > tol:
        rim.append(rim[0])
    made, why = fit_through([top, bottom], plane, rim, tol)
    return (made, float(thickness)) if made is not None else (None, why)


def fit_through(sides, plane, rim, tol):
    """``(brep, why)`` - a smooth surface through the MIDDLE of ``sides`` (two
    meshes: half way between them; one: the sheet itself), trimmed to the
    closed ``rim`` (points). A patch through a mesh's own vertices came out
    WAVY (they bunch along the facet strips): a regular grid over ``plane``,
    each point where the plane's normal meets the sides, and a smooth surface
    interpolated through the grid."""
    box = rg.BoundingBox.Empty
    for side in sides:
        box.Union(side.GetBoundingBox(plane))
    size = max(side.GetBoundingBox(True).Diagonal.Length for side in sides)
    n = MESH_GRID
    # a hair INSIDE the edge: a ray exactly on the mesh's rim missed it, and a
    # steep eave was carried on from further in (138 mm off at the corner);
    # the host reaches past its edges (core_host), so the rim still trims it
    ex, ey = (box.Max.X - box.Min.X) * EDGE_INSET, (box.Max.Y - box.Min.Y) * EDGE_INSET
    x0, x1, y0, y1 = box.Min.X + ex, box.Max.X - ex, box.Min.Y + ey, box.Max.Y - ey
    grid, heights = [], []
    for i in range(n):
        for j in range(n):
            x = x0 + (x1 - x0) * i / float(n - 1)
            y = y0 + (y1 - y0) * j / float(n - 1)
            grid.append((x, y))
            heights.append(_through(sides, plane, plane.PointAt(x, y), size))
    # a grid point off the mesh (past a curved or cut outline) takes the
    # height of the nearest one on it - snapping it onto the mesh edge piled
    # points up there and folded the surface
    known = [(g, h) for g, h in zip(grid, heights) if h is not None]
    if len(known) < 4:
        return None, "too few points through the mesh's middle"
    pts3 = []
    for k, ((x, y), h) in enumerate(zip(grid, heights)):
        if h is None:
            h = _extrapolated(heights, k // n, k % n, n)
        if h is None:
            h = min(known, key=lambda q: (q[0][0] - x) ** 2 + (q[0][1] - y) ** 2)[1]
        pts3.append(plane.PointAt(x, y, h))
    try:
        srf = rg.NurbsSurface.CreateThroughPoints(pts3, n, n, 3, 3, False, False)
    except Exception:
        srf = None
    if srf is None:
        return None, "no smooth surface through the mesh's middle"
    from . import core_host
    host = core_host.Host(srf, tol)
    made, why = core_host.trimmed(host, rg.PolylineCurve(rim), [], tol)
    if made is None:
        return None, "the mid-surface could not be trimmed to its outline ({})".format(why)
    return made, None


MESH_GRID = 24              # points per direction through a curved mesh's middle
EDGE_INSET = 0.002          # the fitting grid stays this share of the size inside the edge


def _extrapolated(heights, i, j, n):
    """A grid point off the mesh: its height carried on from the two nearest
    known points along its row and its column (the SLOPE continues) - their
    mean - or None. The nearest known height alone made a flat step where a
    roof keeps dropping at its eave: 216 mm off at the corner."""
    guesses = []
    for di, dj in ((0, 1), (0, -1), (1, 0), (-1, 0)):
        a = b = None
        step = 1
        while 0 <= i + di * step < n and 0 <= j + dj * step < n:
            h = heights[(i + di * step) * n + (j + dj * step)]
            if h is not None:
                if a is None:
                    a = (step, h)
                else:
                    b = (step, h)
                    break
            elif a is not None:
                break
            step += 1
        if a is not None and b is not None and b[0] == a[0] + 1:
            guesses.append(a[1] - (b[1] - a[1]) * a[0])     # on along the slope
    return sum(guesses) / len(guesses) if guesses else None


def _through(sides, plane, at, size):
    """How far above ``plane`` the middle of ``sides`` lies along the normal
    through ``at`` (the hits nearest the plane, averaged) - None when the line
    misses a side (off the mesh)."""
    ray = rg.Line(at - plane.ZAxis * size, at + plane.ZAxis * size)
    hits = []
    for side in sides:
        found = [p for p in (rg.Intersect.Intersection.MeshLine(side, ray) or [])]
        if not found:
            return None
        hits.append(plane.DistanceTo(min(found, key=lambda p: abs(plane.DistanceTo(p)))))
    return sum(hits) / float(len(hits))


def mid_surface_sliced(geometry):
    """``(surface, thickness)`` for a FLAT plate of any make, or ``(None, why)``.

    For what ``mid_surface`` cannot take: a closed mesh, or a brep without
    two parallel planar faces (a chamfer, a pilaster, an imported solid).
    Same idea as the sliced centreline: the principal frame gives the plate's
    thinnest direction, the solid is cut half way through along it, and the
    cut outline IS the mid-surface. Thickness = volume / that area, so a
    beam pocket through the wall does not make it read thicker."""
    frame = core_centerline.principal_frame(geometry)
    if frame is None:
        return None, "no computable volume - is it closed?"
    tol = coerce.doc_tol()
    normal = rg.Vector3d(frame["axis1"])
    centre = frame["centroid"] + normal * frame["ext1"].Mid
    plane = rg.Plane(centre, normal)

    if isinstance(geometry, rg.Mesh):
        cuts = [pl.ToPolylineCurve() for pl in
                (rg.Intersect.Intersection.MeshPlane(geometry, plane) or []) if pl.IsClosed]
    else:
        ok, cuts, _pts = rg.Intersect.Intersection.BrepPlane(geometry, plane, tol)
        cuts = list(rg.Curve.JoinCurves(list(cuts or []), tol * 10) or []) if ok else []
        cuts = [c for c in cuts if c.IsClosed]
    if not cuts:
        return None, "the cut through the middle gave no closed outline"
    breps = rg.Brep.CreatePlanarBreps(cuts, tol) or []
    if not breps:
        return None, "could not make a surface from the cut outline"
    surface = max(breps, key=lambda b: rg.AreaMassProperties.Compute(b).Area)
    area = rg.AreaMassProperties.Compute(surface).Area
    if area <= 0:
        return None, "the cut outline has no area"
    thickness = float(frame["volume"]) / area
    if abs(frame["ext1"].Length) > CURVED_PLATE_RATIO * thickness:
        return None, "a curved plate - no flat mid-surface"
    return surface, thickness


def _min_rectangle(curve, plane):
    """The smallest-area rectangle around ``curve`` in ``plane`` (1-degree steps)."""
    best = None
    for deg in range(0, 90):
        p = rg.Plane(plane)
        p.Rotate(math.radians(deg), p.ZAxis)
        box = curve.GetBoundingBox(p)
        dx, dy = box.Max.X - box.Min.X, box.Max.Y - box.Min.Y
        if best is None or dx * dy < best[0] - 1e-9:
            best = (dx * dy, rg.Rectangle3d(p, rg.Interval(box.Min.X, box.Max.X),
                                            rg.Interval(box.Min.Y, box.Max.Y)))
    return best[1]


def _straight(curve, tol):
    """The loop with no vertices on straight runs, when it is a polyline."""
    ok, pl = curve.TryGetPolyline()
    if not ok:
        simple = curve.Simplify(rg.CurveSimplifyOptions.All, tol, math.radians(0.1))
        return simple or curve
    pl.MergeColinearSegments(math.radians(0.1), True)
    return pl.ToPolylineCurve()


def tidy_planar(brep, percent):
    """``(surface, info)`` - a flat surface with its small voids filled.

    The surface is laid in its smallest bounding rectangle; the rectangle is
    split by the surface's own outline and holes into regions. A region that
    is not the surface is a void - a beam pocket, a cut corner, an opening.
    Voids smaller than ``percent`` of the surface's area are filled, bigger
    ones (a lift, an L-shaped corner) stay. The result is one planar surface
    in the SAME plane - nothing is rebuilt, only the boundary changes.

    info: {"voids_filled", "voids_kept"}. Anything failing returns the
    surface unchanged."""
    info = {"voids_filled": 0, "voids_kept": 0}
    if brep is None or percent is None or percent <= 0 or brep.Faces.Count != 1:
        return brep, info
    tol = coerce.doc_tol()
    face = brep.Faces[0]
    ok, plane = face.TryGetPlane(tol * 10)
    if not ok:
        return brep, info
    try:
        amp = rg.AreaMassProperties.Compute(brep)
        area = amp.Area if amp else 0.0
        if area <= 0:
            return brep, info
        outer = face.OuterLoop.To3dCurve()
        holes = [loop.To3dCurve() for loop in face.Loops
                 if loop.LoopType == rg.BrepLoopType.Inner]
        rect = _min_rectangle(outer, plane).ToNurbsCurve()
        regions = rg.Curve.CreateBooleanRegions([rect, outer] + holes, plane, False, tol)
        if regions is None or regions.RegionCount < 1:
            return brep, info
        keep = []
        for r in range(regions.RegionCount):
            pieces = rg.Brep.CreatePlanarBreps(list(regions.RegionCurves(r)), tol) or []
            for piece in pieces:
                piece_area = rg.AreaMassProperties.Compute(piece).Area
                mesh = (rg.Mesh.CreateFromBrep(piece, rg.MeshingParameters.Coarse) or [None])[0]
                if mesh is None or mesh.Faces.Count == 0:
                    return brep, info
                inside = mesh.Faces.GetFaceCenter(0)
                on_surface = brep.ClosestPoint(inside).DistanceTo(inside) <= tol * 10
                if on_surface:
                    keep.append(piece)                      # the surface itself
                elif piece_area < area * percent / 100.0:
                    keep.append(piece)                      # a small void: fill it
                    info["voids_filled"] += 1
                else:
                    info["voids_kept"] += 1                 # a real opening / shape
        if not info["voids_filled"]:
            return brep, info
        joined = rg.Brep.JoinBreps(keep, tol * 10) or []
        if len(joined) != 1:
            return brep, dict(info, voids_filled=0)
        merged = joined[0]
        merged.MergeCoplanarFaces(tol * 10)
        if merged.Faces.Count != 1:
            return brep, dict(info, voids_filled=0)
        loops = [_straight(loop.To3dCurve(), tol) for loop in merged.Faces[0].Loops]
        rebuilt = rg.Brep.CreatePlanarBreps(loops, tol) or []
        result = rebuilt[0] if len(rebuilt) == 1 else merged
        new_area = rg.AreaMassProperties.Compute(result).Area
        if new_area < area - tol or not result.IsValid:
            return brep, dict(info, voids_filled=0)
        return result, info
    except Exception:
        return brep, dict(info, voids_filled=0)


def centreline(brep, tol=PLANAR_TOL):
    """``(curve, length)`` for a linear member, or ``(None, why)``.

    Straight through to the confirmed end-cap method; this exists so a
    caller extracting a whole model asks one module rather than two."""
    line = core_centerline.endcap_centerline(brep, tol)
    if line is None:
        return None, "could not find two end caps"

    # endcap_centerline returns an rg.Line - a STRUCT, not a Curve. It has
    # .Length, not .GetLength(), and nothing can be added to the document
    # until it is wrapped. Every caller wants a curve, so the wrapping
    # happens here rather than in each of them.
    if isinstance(line, rg.Line):
        return rg.LineCurve(line), float(line.Length)
    return line, float(line.GetLength())


# ---------- what the architect wrote down ----------

# Words that appear in layer names and mean something structural. Matched
# case-insensitively as substrings, longest first so 'retaining wall'
# beats 'wall'.
ROLE_WORDS = (
    ("retaining", "retaining wall"), ("shear", "shear wall"),
    ("column", "column"), ("stanchion", "column"), ("pier", "column"),
    ("post", "column"), ("stud", "stud"), ("mullion", "mullion"),
    ("beam", "beam"), ("joist", "joist"), ("rafter", "rafter"),
    ("purlin", "purlin"), ("truss", "truss"), ("brace", "brace"),
    ("lintel", "lintel"), ("girder", "beam"),
    ("slab", "slab"), ("deck", "slab"), ("floor", "slab"),
    ("wall", "wall"), ("roof", "roof"), ("core", "core"),
    ("found", "foundation"), ("footing", "foundation"), ("pile", "pile"),
    ("stair", "stair"), ("ramp", "ramp"),
)

# Roles that are 1D, and roles that are 2D. Used only to notice when the
# NAME and the SHAPE disagree.
LINEAR_ROLES = {"column", "beam", "joist", "rafter", "purlin", "stud",
                "mullion", "brace", "lintel", "truss", "pile"}
PLANAR_ROLES = {"wall", "shear wall", "retaining wall", "slab", "roof",
                "core"}


def role_from_text(text):
    """A structural role from a layer name or object name, or None."""
    if not text:
        return None
    lowered = str(text).lower()
    for word, role in ROLE_WORDS:
        if word in lowered:
            return role
    return None


def layer_path(obj_id):
    try:
        rhobj = coerce.find_object(obj_id)
        if rhobj is None:
            return ""
        return sc.doc.Layers[rhobj.Attributes.LayerIndex].FullPath or ""
    except Exception:
        return ""


def stated_role(obj_id):
    """What the model SAYS this is - layer name first, then object name.

    Layer first on purpose: an architect names a layer once and an object
    rarely, so the layer is the more reliable of the two and the object
    name is usually 'Extrusion' or a GUID."""
    role = role_from_text(layer_path(obj_id))
    if role:
        return role, "layer"
    rhobj = coerce.find_object(obj_id)
    if rhobj is not None:
        role = role_from_text(rhobj.Attributes.Name)
        if role:
            return role, "name"
    return None, None


def disagreement(kind, role):
    """Does the NAME contradict the SHAPE? Returns a sentence, or None.

    Reported, never resolved. A 'column' that measures like a plate is
    either a mislabelled wall or a very short fat column, and which one
    it is changes the analysis - so it goes to a human rather than being
    quietly filed as whichever the code happened to prefer."""
    if not role or not kind:
        return None
    if role in LINEAR_ROLES and kind == PLANAR:
        return ("named '{}' but measures like a plate - check whether it "
                "is a mislabelled wall".format(role))
    if role in PLANAR_ROLES and kind == LINEAR:
        return ("named '{}' but measures like a member - check whether it "
                "is a mislabelled beam".format(role))
    return None


# Attribute keys worth carrying through to the FE model. Anything else an
# architect attached is ignored: a model can have dozens of user strings
# per object and none of the rest describe structure.
CARRIED_KEYS = ("Material", "MATERIAL", "Grade", "GRADE", "Section",
                "SECTION", "Profile", "PROFILE", "Thickness", "THICKNESS",
                "LoadBearing", "Structural", "IfcType", "IFC_TYPE")


def carried_attributes(obj_id):
    """The few user strings worth keeping, as a small dict.

    Filtered hard and deliberately. Reading everything an architect
    attached would put hundreds of strings per object into a receipt and
    from there into a prompt, for information that is nearly all about
    cost codes and phasing."""
    rhobj = coerce.find_object(obj_id)
    if rhobj is None:
        return {}
    out = {}
    for key in CARRIED_KEYS:
        try:
            value = rhobj.Attributes.GetUserString(key)
        except Exception:
            continue
        if value:
            out[key.lower()] = value
    return out


def describe(obj_id, brep=None, ratio=SHAPE_RATIO, tol=PLANAR_TOL):
    """Everything known about one solid, before anything is built from it.

    One dict per object, deliberately small - this is what a caller
    summarises over a whole model, so a fat one here is a fat prompt
    there."""
    brep = brep if brep is not None else coerce.to_brep(obj_id)
    if brep is None:
        return None

    kind, facts = classify(brep, ratio)
    if kind is None:
        return None

    role, source = stated_role(obj_id)
    record = {
        "kind": kind,
        "role": role,
        "role_from": source,
        "layer": layer_path(obj_id),
    }
    if facts:
        record["size"] = [precision.length(facts["thickness"]),
                          precision.length(facts["width"]),
                          precision.length(facts["length"])]
    attributes = carried_attributes(obj_id)
    if attributes:
        record["attributes"] = attributes
    clash = disagreement(kind, role)
    if clash:
        record["disagreement"] = clash
    return record
