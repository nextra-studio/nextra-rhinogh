"""
View core - viewports, camera, capture and clipping planes.

The distinction this module exists to keep straight is CPlane versus
viewplane. The CPlane is where geometry is drawn; the viewplane is what
the camera sees. They are different objects with different owners, and
conflating them is the failure the workflow guidelines call out.

Everything here touches the CAMERA. Nothing here changes a CPlane -
that is ``plane.set``'s job, and keeping the two apart in code is how
they stay apart in use.

Model space versus sheet space matters too: a layout (``RhinoPageView``)
works in paper millimetres and holds detail viewports that each have
their own model camera. ``view_kind`` reports which kind a view is so a
caller does not try to orbit a sheet.

Nothing in this module prompts the user.
"""

import math
import os

import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

from .._shared import coerce
from .._shared import planes as plane_utils
from .._shared import redraw


# ---------- Viewport basics ----------

def view_kind(view):
    """'model', 'layout' or 'detail'.

    A layout is measured in paper units; a detail is a window into model
    space sitting on a layout. Operations that make sense in one rarely
    make sense in another."""
    if view is None:
        return None
    if isinstance(view, Rhino.Display.RhinoPageView):
        return "layout"
    try:
        if view.ActiveViewport.ParentView is not None:
            return "detail"
    except Exception:
        pass
    return "model"


def list_views(include_layouts=True):
    """Every open view, described."""
    out = []
    for view in sc.doc.Views:
        info = plane_utils.view_info(view)
        if info is None:
            continue
        info["kind"] = view_kind(view)
        if not include_layouts and info["kind"] == "layout":
            continue
        out.append(info)
    return out


def set_active_view(name):
    """Make a viewport active by name. Returns the view, or None.

    Falls back to a case-insensitive match, because "top" and "Top" are
    the same viewport to everyone except a string comparison."""
    if not name:
        return None

    view = None
    try:
        view = sc.doc.Views.Find(str(name), True)
    except Exception:
        view = None

    if view is None:
        for candidate in sc.doc.Views:
            if candidate.ActiveViewport.Name.lower() == str(name).lower():
                view = candidate
                break

    if view is None:
        return None

    sc.doc.Views.ActiveView = view
    return view


def view_names():
    """Names of every open view, for error messages."""
    return [v.ActiveViewport.Name for v in sc.doc.Views]


# ---------- Camera ----------

def camera_state(view):
    """Where the camera is and what it is looking at."""
    if view is None:
        return None
    vp = view.ActiveViewport
    return {
        "location": coerce.xyz(vp.CameraLocation),
        "target": coerce.xyz(vp.CameraTarget),
        "direction": coerce.xyz(vp.CameraDirection),
        "up": coerce.xyz(vp.CameraUp),
        "lens_length": float(vp.Camera35mmLensLength),
        "projection": "parallel" if vp.IsParallelProjection else "perspective",
    }


def orient_camera_to_plane(view, plane, distance=None, bbox=None):
    """Point the camera square at a plane.

    The camera is placed on the plane's normal, looking back down it, and
    the up vector is the plane's Y - so a wall face fills the screen the
    right way up rather than at an arbitrary roll.

    ``distance`` defaults to something that frames the supplied bounding
    box, or the plane's own scale if none is given."""
    if view is None or plane is None:
        return False

    if distance is None:
        if bbox is not None and bbox.IsValid:
            distance = bbox.Diagonal.Length * 1.5
        else:
            distance = 100.0

    target = plane.Origin
    location = target + plane.ZAxis * float(distance)

    vp = view.ActiveViewport
    vp.SetCameraLocations(target, location)
    vp.CameraUp = plane.YAxis
    redraw.request(view)
    return True


def orient_camera_to_face(view, face_ref, distance=None, tol=None):
    """Point the camera square at a planar face.

    Reuses ``planes.plane_from_face``, so 'the plane of this face' means
    the same thing here as it does to ``plane.set`` - including the
    normal-alignment fix, so the camera ends up outside the solid rather
    than inside it."""
    plane = plane_utils.plane_from_face(face_ref, tol)
    if plane is None:
        return False

    bbox = coerce.bbox_of(face_ref)
    return orient_camera_to_plane(view, plane, distance, bbox)


def zoom_to(view, target_ids=None, bbox=None):
    """Zoom to objects, a bounding box, or the whole model.

    The box is inflated by 5% so the subject is not jammed against the
    edge of the frame."""
    if view is None:
        return False

    if bbox is None and target_ids:
        bbox = coerce.bbox_of(target_ids)

    if bbox is None:
        view.ActiveViewport.ZoomExtents()
        redraw.request(view)
        return True

    if not bbox.IsValid:
        return False

    inflated = rg.BoundingBox(bbox.Min, bbox.Max)
    inflated.Inflate(max(bbox.Diagonal.Length * 0.05, 1.0))
    view.ActiveViewport.ZoomBoundingBox(inflated)
    redraw.request(view)
    return True


# ---------- Named views ----------

def save_named_view(name, view):
    """Store the current camera under a name."""
    if not name or view is None:
        return False
    return sc.doc.NamedViews.Add(str(name), view.ActiveViewportID) >= 0


def restore_named_view(name, view):
    """Recall a saved camera into a viewport."""
    if not name or view is None:
        return False

    index = -1
    for i in range(sc.doc.NamedViews.Count):
        if sc.doc.NamedViews[i].Name == str(name):
            index = i
            break
    if index < 0:
        return False

    ok = sc.doc.NamedViews.Restore(index, view.ActiveViewport)
    redraw.request(view)
    return ok


def named_view_names():
    """Every named view in the document."""
    return [sc.doc.NamedViews[i].Name for i in range(sc.doc.NamedViews.Count)]


# ---------- Display mode ----------

DISPLAY_MODES = ("wireframe", "shaded", "ghosted", "rendered", "x-ray",
                 "technical", "pen", "arctic")


def set_display_mode(view, mode):
    """Switch a viewport's display mode by name.

    Mode names vary slightly between Rhino versions and locales, so the
    lookup is a case-insensitive prefix match rather than an exact one."""
    if view is None or not mode:
        return False

    wanted = str(mode).lower().replace("-", "").replace(" ", "")
    for description in Rhino.Display.DisplayModeDescription.GetDisplayModes():
        candidate = description.EnglishName.lower().replace("-", "").replace(" ", "")
        if candidate == wanted or candidate.startswith(wanted):
            view.ActiveViewport.DisplayMode = description
            redraw.request(view)
            return True
    return False


def current_display_mode(view):
    """The viewport's display mode name."""
    if view is None:
        return None
    try:
        return view.ActiveViewport.DisplayMode.EnglishName
    except Exception:
        return None


# ---------- Capture ----------

def capture_to_file(view, path, width=None, height=None, transparent=False):
    """Write a viewport to a PNG (or whatever the extension says).

    Returns ``(path, warning)``. The parent directory is created if
    missing, because a capture failing purely because a folder does not
    exist is a waste of a round trip."""
    if view is None:
        return None, "no such viewport"
    if not path:
        return None, "a file path is required"

    folder = os.path.dirname(os.path.abspath(path))
    if folder and not os.path.isdir(folder):
        try:
            os.makedirs(folder)
        except Exception as exc:
            return None, "could not create the output folder: {}".format(exc)

    # The requested size must keep the viewport's aspect ratio.
    #
    # Two reasons, and the second is the one that bites. A different
    # aspect re-frames the view, so the image is not the picture the
    # viewport was showing. And the tag conduits place their labels in
    # screen space using the LIVE viewport's dimensions - Rhino gives
    # them no other - so any scale change has to be uniform for those
    # positions to survive into the render. A non-uniform one silently
    # puts every label in the wrong place, which is how a carefully
    # de-cluttered L4 capture came out piled up anyway.
    warning = None
    size = None
    native_width = native_height = None
    try:
        native = view.ActiveViewport.Size
        native_width, native_height = int(native.Width), int(native.Height)
    except Exception:
        pass

    if width or height:
        import System.Drawing
        if width and height and native_width and native_height:
            aspect = float(native_width) / float(native_height)
            corrected = int(round(int(width) / aspect))
            if abs(corrected - int(height)) > 1:
                warning = ("height adjusted {} -> {} to keep the viewport's "
                           "aspect ratio; a different aspect re-frames the "
                           "view and displaces every tag label"
                           .format(int(height), corrected))
                height = corrected
        elif width and native_width and native_height:
            height = int(round(int(width) * native_height / float(native_width)))
        elif height and native_width and native_height:
            width = int(round(int(height) * native_width / float(native_height)))
        if width and height:
            size = System.Drawing.Size(int(width), int(height))

    # Tell the conduits how the render is scaled against the viewport
    # they measure in, so their pixel thresholds mean the same thing in
    # the image a reader actually sees.
    try:
        sc.sticky["rg_capture_scale"] = (
            (float(native_width) / float(width)) if size is not None
            and width and native_width else 1.0)
    except Exception:
        sc.sticky["rg_capture_scale"] = 1.0

    # One try/finally around the whole capture, so the scale is reset on
    # EVERY path out.
    #
    # It used to be reset on the exception path and on the save path but
    # not on "bitmap came back None", which is the quiet one. The sticky
    # then stayed at, say, 2.0 for the rest of the session and every
    # later conduit sized its label-collision thresholds against a
    # capture that never happened - so labels bunched up in the live
    # viewport for a reason nothing reported. The old `finally: pass` was
    # dead code sitting exactly where this belonged.
    bitmap = None
    try:
        try:
            if size is not None:
                bitmap = view.CaptureToBitmap(size)
            else:
                bitmap = view.CaptureToBitmap()
        except Exception as exc:
            return None, "capture failed: {}".format(exc)

        if bitmap is None:
            return None, "capture returned no image"

        try:
            bitmap.Save(path)
        except Exception as exc:
            return None, "could not write the image: {}".format(exc)

        return path, warning
    finally:
        sc.sticky["rg_capture_scale"] = 1.0
        # One GDI+ handle per capture otherwise, and a long session takes
        # a lot of captures.
        if bitmap is not None:
            try:
                bitmap.Dispose()
            except Exception:
                pass


# ---------- Clipping planes ----------

def add_clipping_plane(plane, size, view):
    """Add a clipping plane object bound to one viewport.

    Rhino's clipping planes are real document objects sized like a
    surface, so a size has to be chosen; oversizing is safe."""
    if plane is None or view is None:
        return None
    half = float(size) / 2.0
    return sc.doc.Objects.AddClippingPlane(
        plane, half * 2.0, half * 2.0, [view.ActiveViewportID])


def list_clipping_planes():
    """Every clipping plane object in the document."""
    out = []
    for rhobj in sc.doc.Objects:
        if rhobj.IsDeleted:
            continue
        if isinstance(rhobj.Geometry, rg.ClippingPlaneSurface):
            out.append(rhobj.Id)
    return out


def move_clipping_plane(obj_id, plane):
    """Reposition an existing clipping plane."""
    rhobj = coerce.find_object(obj_id)
    if rhobj is None or not isinstance(rhobj.Geometry, rg.ClippingPlaneSurface):
        return False

    surface = rhobj.Geometry.Duplicate()
    surface.Plane = plane
    return sc.doc.Objects.Replace(rhobj.Id, surface)


def remove_clipping_planes(obj_ids=None):
    """Delete clipping planes - all of them when none are named."""
    targets = (coerce.to_guids(obj_ids) if obj_ids
               else list_clipping_planes())
    removed = 0
    for guid in targets:
        if sc.doc.Objects.Delete(guid, True):
            removed += 1
    return removed
