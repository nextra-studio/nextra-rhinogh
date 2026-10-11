"""
Centerline core - the shared, reusable form of the two confirmed tools.

This module is a refactor, not a rewrite. Every piece of geometry logic
here came out of the working scripts:

    Confirmed_tool_in_Rhino/centerline_endcap.py
        -> planar_faces, face_area, endcap_centerline
    Confirmed_tool_in_Rhino/centerline_slicing.py
        -> principal_frame, oriented_bbox, area_centroid_from_plane,
           sliced_centerline_points

What changed is only the boundary: the originals mixed ``rs.GetObject``
and ``rs.GetInteger`` prompts into the same function that did the maths,
so nothing could be reused from anywhere else. Here the maths takes a
Brep and returns geometry, and the prompting lives in the tool files'
``run_*`` wrappers. That split is what lets ``centerline.linear_brep``
and ``centerline.complex_brep`` be callable over MCP, and it is why
``planes.plane_from_object_axis`` and ``analyse.oriented_bbox`` can
reuse ``principal_frame`` instead of defining "long axis" a second time.

No function in this module prompts the user or touches the document.
"""

import threading

import Rhino
import Rhino.Geometry as rg

from .._shared import coerce


# ---------- Face inspection (from centerline_endcap.py) ----------

def face_area(face):
    """Area of a single Brep face.

    The face has to be duplicated into a standalone Brep first, because
    ``AreaMassProperties.Compute`` measures a Brep, not a face reference.
    ``DuplicateFace(False)`` keeps the trims but drops the rest of the
    solid. Returns +inf on failure so this can be used as a sort key
    without a None check - an unmeasurable face simply sorts last.
    Area only: the moments made it ten times slower."""
    duplicated = face.DuplicateFace(False)
    amp = rg.AreaMassProperties.Compute(duplicated, True, False, False, False)
    return amp.Area if amp else float("inf")


def face_centroid(face):
    """Area centroid of a single Brep face, or None if unmeasurable."""
    duplicated = face.DuplicateFace(False)
    amp = rg.AreaMassProperties.Compute(duplicated)
    return amp.Centroid if amp else None


def planar_faces(brep, tol=0.001):
    """Every face of the Brep that is flat within tolerance.

    The default 0.001 is the value the confirmed end-cap tool used, kept
    deliberately tighter than a typical model tolerance: a face that is
    only "nearly" planar is a fillet or a draft, not an end cap, and
    letting it through is how you get a centerline through the side of
    a member instead of down its length."""
    out = []
    for face in brep.Faces:
        is_planar, _plane = face.TryGetPlane(tol)
        if is_planar:
            out.append(face)
    return out


# ---------- Linear / prismatic centerline (endcap method) ----------

def endcap_faces(brep, tol=0.001):
    """The two smallest planar faces - the presumed end caps.

    For a prismatic beam or column the caps are the smallest flat faces
    by a wide margin, so sorting planar faces by area and taking the
    first two is both cheap and reliable. Returns None when there are
    not two planar faces to choose from."""
    faces = planar_faces(brep, tol)
    if len(faces) < 2:
        return None
    faces.sort(key=face_area)
    return faces[0], faces[1]


def endcap_centerline(brep, tol=0.001):
    """Line joining the centroids of a prismatic member's two end caps.

    This is the exact logic of the confirmed ``get_endcap_centerline``,
    minus the prompt. Returns an ``rg.Line``, or None when the brep has
    no identifiable caps or either centroid cannot be computed."""
    caps = endcap_faces(brep, tol)
    if caps is None:
        return None

    first, second = caps
    centroid_a = face_centroid(first)
    centroid_b = face_centroid(second)
    if centroid_a is None or centroid_b is None:
        return None

    line = rg.Line(centroid_a, centroid_b)
    return line if line.Length > 0 else None


def endcap_section_info(brep, tol=0.001):
    """Cross-section facts that come free with the end-cap method.

    Reported alongside the centerline so a structural take-off has the
    section area and cap planes without a second pass over the solid."""
    caps = endcap_faces(brep, tol)
    if caps is None:
        return None

    first, second = caps
    ok_a, plane_a = first.TryGetPlane(tol)
    ok_b, plane_b = second.TryGetPlane(tol)

    return {
        "section_area": face_area(first),
        "cap_areas": [face_area(first), face_area(second)],
        "cap_plane_a": plane_a if ok_a else None,
        "cap_plane_b": plane_b if ok_b else None,
    }


# ---------- Principal frame (from centerline_slicing.py) ----------

def _same(a, b, rel=0.01):
    """Two moments equal to within 1% - the axes between them are arbitrary."""
    return abs(a - b) <= rel * max(abs(a), abs(b), 1e-12)


def _settle_pair(brep, centroid, normal, u0, v0):
    """Replace an arbitrary axis pair in the plane normal to ``normal`` with
    the pair, taken from the brep's straight edges, whose extents give the
    smallest rectangle. Keeps (u0, v0) when no edge lies in that plane."""
    def span(axis):
        values = [(v.Location - centroid) * axis for v in brep.Vertices]
        return max(values) - min(values)

    best = (span(u0) * span(v0), u0, v0)
    for edge in brep.Edges:
        if not edge.IsLinear(0.001):
            continue
        d = edge.PointAtEnd - edge.PointAtStart
        d = d - normal * (d * normal)          # into the plane
        if d.Length < 1e-9:
            continue
        d.Unitize()
        w = rg.Vector3d.CrossProduct(normal, d)
        w.Unitize()
        area = span(d) * span(w)
        if area < best[0] - 1e-9:
            best = (area, d, w)
    return best[1], best[2]


_FRAMES = []        # [(geometry, frame)] - the last few, by identity
_FRAMES_LOCK = threading.Lock()     # convert(parallel=True): several workers share it
_FRAMES_KEPT = 2000     # a whole model: 32 missed on 168 solids (271 frames, 10 s)


def principal_frame(brep):
    """``_principal_frame``, remembered for the last few geometries: one
    convert asked it of the same solid four times (classify, the layer
    table, the axis, the section) - 0.17 s each on a 127-face beam. A copy
    is handed out (its vectors are reversed in place by some callers)."""
    with _FRAMES_LOCK:
        kept = list(_FRAMES)
    for geometry, frame in kept:
        if geometry is brep:
            return _copy_frame(frame)
    frame = _principal_frame(brep)
    with _FRAMES_LOCK:
        _FRAMES.append((brep, frame))
        del _FRAMES[:-_FRAMES_KEPT]
    return _copy_frame(frame)


def _copy_frame(frame):
    if frame is None:
        return None
    out = dict(frame)
    for key in ("axis1", "axis2", "axis3"):
        out[key] = rg.Vector3d(frame[key])
    out["centroid"] = rg.Point3d(frame["centroid"])
    return out


def _principal_frame(brep):
    """Volume centroid, the three principal axes, and the extent along
    each - the object's own coordinate system.

    ``CentroidCoordinatesPrincipalMoments`` diagonalises the solid's
    second-moment tensor. The eigenvector with the LARGEST eigenvalue is
    the direction of most spread, i.e. the direction the member is
    elongated in, so after sorting the moments ascending:

        axis1, axis2  span the cross-section plane
        axis3         is the long axis

    The extents are the signed min/max distance of the brep's vertices
    from the centroid along each axis, which doubles as the oriented
    bounding box size - ``oriented_bbox`` below builds directly on it.

    Returns a dict rather than a 7-tuple (which is what the original
    returned) because three callers now read it and positional unpacking
    of seven values is where mistakes get made. Returns None if the brep
    has no computable volume - i.e. it is not a closed solid.

    A CLOSED MESH works too (a scanned or exported member): the same
    volume moments, its vertices for the extents.
    """
    open_sheet = (isinstance(brep, rg.Mesh) and not brep.IsClosed) or (
        isinstance(brep, rg.Brep) and not brep.IsSolid)
    # an OPEN polysurface / mesh (a member drawn as its surfaces, a sheet):
    # the same principal axes from its AREA - it has no volume
    vmp = (rg.AreaMassProperties.Compute(brep) if open_sheet
           else rg.VolumeMassProperties.Compute(brep))
    if vmp is None and isinstance(brep, rg.Brep):
        # a brep Rhino cannot measure (a broken face from an import): the
        # same moments from its mesh - the extents still come from the brep
        mesh = rg.Mesh()
        for part in rg.Mesh.CreateFromBrep(brep, rg.MeshingParameters.Default) or []:
            mesh.Append(part)
        mesh.Weld(3.14159)
        if mesh.Faces.Count:
            vmp = (rg.VolumeMassProperties.Compute(mesh) if mesh.IsClosed
                   else rg.AreaMassProperties.Compute(mesh))
            open_sheet = open_sheet or not mesh.IsClosed
    if vmp is None:
        return None

    ok, m1, ax1, m2, ax2, m3, ax3 = vmp.CentroidCoordinatesPrincipalMoments()
    if not ok:
        return None

    moments = [(m1, ax1), (m2, ax2), (m3, ax3)]
    moments.sort(key=lambda m: m[0])  # ascending: smallest, mid, largest

    axis1 = moments[0][1]  # section plane
    axis2 = moments[1][1]  # section plane
    axis3 = moments[2][1]  # long axis
    axis1.Unitize()
    axis2.Unitize()
    axis3.Unitize()

    # Two equal moments - a SQUARE column section, a square slab - mean any
    # direction in that plane is "principal", and Rhino hands back an
    # arbitrary rotated pair. Measured: a 300x300 column came out 334.8 x
    # 334.8 and a 12.3m square slab 17m long. Settle those two axes on the
    # solid's own straight edges instead: the pair giving the smallest box.
    centroid0 = vmp.Centroid
    if isinstance(brep, rg.Mesh):
        pass                    # no straight edges to settle an equal pair on
    elif _same(moments[0][0], moments[1][0]):
        axis1, axis2 = _settle_pair(brep, centroid0, axis3, axis1, axis2)
    elif _same(moments[1][0], moments[2][0]):
        axis2, axis3 = _settle_pair(brep, centroid0, axis1, axis2, axis3)

    # Force a right-handed frame so Plane/Box orientation comes out valid.
    if rg.Vector3d.CrossProduct(axis1, axis2) * axis3 < 0:
        axis2.Reverse()

    centroid = vmp.Centroid

    # The extents come from the TIGHT box in the principal frame, not from
    # the vertices: a pipe or any curved solid has almost no vertices (a
    # curved beam measured 0 thick and was called a plate).
    box = brep.GetBoundingBox(rg.Plane(centroid, axis1, axis2))    # plane coordinates

    return {
        "centroid": centroid,
        "axis1": axis1,
        "axis2": axis2,
        "axis3": axis3,
        "ext1": rg.Interval(box.Min.X, box.Max.X),
        "ext2": rg.Interval(box.Min.Y, box.Max.Y),
        "ext3": rg.Interval(box.Min.Z, box.Max.Z),
        "volume": 0.0 if open_sheet else vmp.Volume,
    }


def oriented_bbox(frame):
    """The oriented bounding box implied by a principal frame.

    Takes the dict from ``principal_frame``. Kept separate from it
    because ``analyse.oriented_bbox`` wants the box while the centerline
    slicer wants the box only as a fallback surface to slice against."""
    if frame is None:
        return None
    plane = rg.Plane(frame["centroid"], frame["axis1"], frame["axis2"])
    return rg.Box(plane, frame["ext1"], frame["ext2"], frame["ext3"])


def frame_plane(frame):
    """Cross-section plane at the centroid, normal along the long axis."""
    if frame is None:
        return None
    return rg.Plane(frame["centroid"], frame["axis1"], frame["axis2"])


# ---------- Slicing (from centerline_slicing.py) ----------

def area_centroid_from_plane(target_brep, plane, tol):
    """Area-weighted centroid of the cross-section cut by a plane.

    Intersecting a Brep with a plane gives loose curves; those have to be
    capped into planar Breps before they can be measured. When the cut
    produces several disjoint regions (a hollow section, a double-web
    profile) each is weighted by its own area, so the result is the true
    section centroid and not the average of region centroids.

    Returns None when the plane misses the solid entirely, which is the
    signal the caller uses to fall back to the bounding box. A mesh is cut
    with MeshPlane; each closed section outline is measured the same way."""
    if isinstance(target_brep, rg.Mesh):
        return _mesh_section_centroid(target_brep, plane)
    success, curves, _pts = rg.Intersect.Intersection.BrepPlane(
        target_brep, plane, tol)
    if not success or not curves:
        return None

    planar_breps = rg.Brep.CreatePlanarBreps(curves, tol)
    if not planar_breps:
        return _curve_centroid(curves)          # an open member: its cut is lines

    total_area = 0.0
    weighted_pt = rg.Point3d(0, 0, 0)
    for pb in planar_breps:
        amp = rg.AreaMassProperties.Compute(pb)
        if amp is None:
            continue
        total_area += amp.Area
        weighted_pt = weighted_pt + amp.Centroid * amp.Area

    if total_area <= 1e-9:
        return None

    return weighted_pt / total_area


def _curve_centroid(curves):
    """The length-weighted centroid of a cut through an OPEN member (a beam
    drawn as its web and flanges, a sheet): its cut is lines, not areas."""
    total, weighted = 0.0, rg.Point3d(0, 0, 0)
    for c in curves or []:
        if c is None:
            continue
        amp = rg.LengthMassProperties.Compute(c)
        if amp is None or amp.Length <= 1e-9:
            continue
        total += amp.Length
        weighted = weighted + amp.Centroid * amp.Length
    return weighted / total if total > 1e-9 else None


def _mesh_section_centroid(mesh, plane):
    return _polylines_centroid(rg.Intersect.Intersection.MeshPlane(mesh, plane), mesh.IsClosed)


def section_centroids(geometry, planes, tol):
    """``area_centroid_from_plane`` for every plane (None where a cut misses).
    A mesh is cut by all the planes in ONE call - 0.05 s for nine cuts
    through a 26 000-face mesh, against 0.5 s one plane at a time - and each
    outline goes back to the plane it lies on."""
    planes = list(planes)
    if isinstance(geometry, rg.Mesh) and len(planes) > 1:
        try:
            polylines = rg.Intersect.Intersection.MeshPlane(geometry, planes)
        except Exception:
            polylines = None
        if polylines is not None:
            groups = [[] for _p in planes]
            for pl in polylines:
                if pl is None or pl.Count < 2:
                    continue
                k = min(range(len(planes)), key=lambda i: abs(planes[i].DistanceTo(pl[0])))
                groups[k].append(pl)
            return [_polylines_centroid(g, geometry.IsClosed) for g in groups]
    return [area_centroid_from_plane(geometry, p, tol) for p in planes]


def _polylines_centroid(polylines, closed):
    if not polylines:
        return None
    if not closed:
        return _curve_centroid([pl.ToPolylineCurve() for pl in polylines])
    total, weighted = 0.0, rg.Point3d(0, 0, 0)
    for pl in polylines:
        if pl.Count < 4 or not pl.IsClosed:
            continue
        amp = rg.AreaMassProperties.Compute(pl.ToPolylineCurve())
        if amp is None or amp.Area <= 1e-9:
            continue
        total += amp.Area
        weighted = weighted + amp.Centroid * amp.Area
    return weighted / total if total > 1e-9 else None


CURVE_TYPES = ("interpolate", "polyline")


DEVIATION_SAMPLES = 50      # points along the trial curve its deviation is measured at


def smooth_centerline(points, control_points=10, straight_tol=None,
                      curve_type="interpolate"):
    """Sliced section centroids -> the member's axis curve.

    First the TRIAL curve: the sliced polyline zig-zags with every uneven
    section, so it is smoothed (rebuilt, degree 3), then ``control_points``
    points are taken evenly along that and a curve is made THROUGH them:
        curve_type='interpolate'  an interpolated curve (degree 3) - its
            defining points lie ON the axis, so they are real positions to
            snap to, and snapping it again keeps it an interpolated curve
        curve_type='polyline'     straight segments between them (degree 1)
            - for FE software that takes only straight elements

    Then the decision, on that curve: ``deviation`` = how far its farthest
    point is from the straight line between its two ends.
      'straight'  deviation <= ``straight_tol`` (document units; None =
                  0.5 % of the length) -> that 2-point straight LineCurve
      'curved'    otherwise -> the trial curve

    Returns ``(curve, shape, deviation)``. ``control_points=2`` always gives
    a straight line."""
    if not points or len(points) < 2:
        return None, None, None
    control_points = max(2, int(control_points or 10))
    chord = rg.Line(points[0], points[-1])
    if control_points == 2 or chord.Length <= 0:
        deviation = max(chord.DistanceTo(p, False) for p in points) if chord.Length > 0 else 0.0
        return rg.LineCurve(chord), "straight", deviation
    polyline = rg.Polyline(points).ToPolylineCurve()
    smooth = polyline.Rebuild(control_points, min(3, control_points - 1), True) or polyline
    trial = through_points(smooth, control_points, curve_type) or smooth
    chord = rg.Line(trial.PointAtStart, trial.PointAtEnd)
    params = trial.DivideByCount(DEVIATION_SAMPLES, True) or []
    deviation = max([chord.DistanceTo(trial.PointAt(t), False) for t in params] or [0.0])
    if straight_tol is None:
        straight_tol = 0.005 * chord.Length
    if deviation <= straight_tol:
        return rg.LineCurve(chord), "straight", deviation
    return trial, "curved", deviation


def through_points(curve, count, curve_type="interpolate"):
    """``count`` points evenly along ``curve`` -> an interpolated curve or a
    polyline through them (None if that fails)."""
    params = curve.DivideByCount(max(1, int(count) - 1), True)
    if not params:
        return None
    pts = [curve.PointAt(t) for t in params]
    return curve_through(pts, curve_type)


def curve_through(pts, curve_type="interpolate"):
    """Interpolated curve (degree 3, or less for few points) or polyline through ``pts``."""
    if len(pts) < 2:
        return None
    if str(curve_type).lower() == "polyline" or len(pts) == 2:
        return rg.Polyline(pts).ToPolylineCurve()
    return rg.Curve.CreateInterpolatedCurve(pts, min(3, len(pts) - 1),
                                            rg.CurveKnotStyle.Chord)


def sliced_centerline_points(brep, segments, tol=None, frame=None):
    """Sample the section centroid at evenly spaced stations along the
    member's long axis.

    This is the confirmed slicing algorithm, prompt removed:

      1. principal frame -> long axis and its extent
      2. walk ``segments + 1`` stations from one end of that extent to
         the other
      3. at each station, cut the solid with a plane normal to the long
         axis and take the area centroid of the section
      4. if the cut returns nothing - which realistically only happens at
         the very ends, where the cap may not be perpendicular to the
         principal axis - fall back to the same station on the oriented
         bounding box, and finally to the station point itself

    That three-level fallback is deliberate: it keeps the returned point
    count equal to ``segments + 2`` no matter what the ends look like, so
    the caller can always build a polyline without checking for holes.

    Returns a list of Point3d, or None if the brep has no principal
    frame (not a closed solid).
    """
    tol = coerce.tol_or_default(tol)
    segments = max(1, int(segments))

    frame = frame or principal_frame(brep)
    if frame is None:
        return None

    axis3 = frame["axis3"]
    ext3 = frame["ext3"]
    centroid = frame["centroid"]

    box_brep = None

    # Stations in the MIDDLE of each of ``segments`` slices, never at the
    # very ends: a cut exactly through an end face returns a broken section
    # (a mesh column's first point came out 70 mm off, an arch's ends
    # 500 mm). The two end points are then extended from the nearest two.
    planes = [rg.Plane(centroid + axis3 * ext3.ParameterAt((i + 0.5) / segments), axis3)
              for i in range(segments)]
    points = []
    for plane, pt in zip(planes, section_centroids(brep, planes, tol)):
        if pt is None:
            if box_brep is None:
                box_brep = oriented_bbox(frame).ToBrep()
            pt = area_centroid_from_plane(box_brep, plane, tol)
        if pt is None:
            pt = plane.Origin    # last resort, keeps the count consistent
        points.append(pt)

    if len(points) >= 2:
        start = points[0] + (points[0] - points[1]) * 0.5
        end = points[-1] + (points[-1] - points[-2]) * 0.5
        points = [start] + points + [end]
    return points


# ---------- Assembling the output curve ----------

def polyline_from_points(points):
    """Point list -> a single PolylineCurve.

    The confirmed script added one Line per segment and then called
    ``rs.JoinCurves`` to stitch them. Building the polyline directly is
    equivalent, produces exactly one curve every time, and avoids leaving
    orphan line segments in the document if the join partially fails."""
    if not points or len(points) < 2:
        return None
    polyline = rg.Polyline(points)
    curve = polyline.ToPolylineCurve()
    return curve if curve is not None and curve.IsValid else None


def centerline_length(curve):
    """Convenience for take-offs: length of a centerline curve."""
    return curve.GetLength() if curve is not None else None
