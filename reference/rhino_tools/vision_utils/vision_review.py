"""
vision.review - the several views a building actually needs.

Why one screenshot is not enough
--------------------------------
``vision.capture`` takes one picture of one viewport, which is the right
primitive and the wrong default for checking a building. A column that
has landed a metre off its grid line is invisible in a perspective view
and obvious in plan. A roof floating above its supports is invisible in
plan and obvious in elevation. Asking a model to judge a build from one
perspective render is asking it to guess, and it will.

So this takes the standard set - plan, front, side, axonometric - in one
call, each framed on the same geometry, and returns the paths together
with one shared tag table.

It is still the expensive option
--------------------------------
Four images is four times the image cost, and images are the most
expensive thing in a prompt. The cheap answers come first and this is a
last resort:

    structural.report           do the members actually meet?  ~20 tokens
    session.state (L0)         what got built, and where?    ~80 tokens
    vision.capture(level=1)    what does it look like?      ~1000 tokens
    vision.review              why does it look wrong?      ~4000 tokens

Reach for this when something IS wrong and the measurements have not
explained why - not to confirm a build that already measured clean.

Layer A (pure logic)  - ``core_view``, ``core_vision``.
Layer B (Rhino entry) - ``run_vision_review``.
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

from rhino_tools._shared import coerce, receipts, redraw
from rhino_tools._shared import planes as plane_utils
from rhino_tools.vision_utils import core_view
from rhino_tools.vision_utils import core_vision
from rhino_tools.vision_utils import vision_capture


# The standard set. Named as a drawing set is named, because that is
# what a person reviewing a building asks for.
STANDARD_VIEWS = ["Top", "Front", "Right", "Perspective"]

# What each view is actually good at catching. Shipped in the receipt so
# a model reading four images knows which one answers which question
# rather than studying all four for everything.
VIEW_PURPOSE = {
    "Top": "plan - grid alignment, footprint, anything off its setting-out line",
    "Front": "elevation - levels, heights, a roof floating off its supports",
    "Right": "section-like elevation - depth, the other set of levels",
    "Perspective": "overall form - massing and whether it reads as a building",
}


def tool_vision_review(ids=None, layer=None, views=None, display_mode="shaded",
                       width=900, height=700, folder=None, tag=False):
    """Capture the standard view set in one call. Returns an R3 receipt.

    ``ids`` or ``layer`` frames every view on the same geometry, so the
    four images are comparable. Without either, each is framed on the
    whole model.

    ``tag=True`` labels the objects, which is worth it when the point is
    to identify WHICH element is wrong and costs clutter when it is not.

    Every view is restored afterwards - its camera, its display mode, and
    any tags. A review that permanently reframed the user's viewports
    would be a poor trade for four pictures."""
    wanted = [str(v) for v in (views or STANDARD_VIEWS)]
    available = core_view.view_names()

    resolved = []
    missing = []
    for name in wanted:
        match = next((v for v in available if v.lower() == name.lower()), None)
        (resolved.append(match) if match else missing.append(name))

    if not resolved:
        return receipts.err(
            "vision.review",
            "None of the requested views are open: {}.".format(
                ", ".join(wanted)),
            hint="Open views: {}. Use Rhino's 4-view layout for the standard "
                 "set.".format(", ".join(available)))

    guids = coerce.to_guids(ids) if ids else None
    if not guids and layer:
        from rhino_tools.vision_utils import core_select
        guids = core_select.select({"layer": layer})

    warnings = ["view '{}' is not open".format(name) for name in missing]
    captured = []
    tagged = False

    # One batch around the whole set: four camera moves and four display
    # mode changes would otherwise repaint the document eight times.
    with redraw.batch():
        try:
            if tag and guids:
                core_vision.tag_objects(guids, 2)
                tagged = True

            for name in resolved:
                view = plane_utils.get_view(name)
                if view is None:
                    warnings.append("could not resolve view '{}'".format(name))
                    continue

                previous_mode = core_view.current_display_mode(view)
                previous_camera = core_view.camera_state(view)

                if display_mode and not core_view.set_display_mode(view,
                                                                   display_mode):
                    warnings.append(
                        "display mode '{}' not found - '{}' captured in '{}'"
                        .format(display_mode, name, previous_mode))

                core_view.zoom_to(view, guids)
                path = vision_capture.capture_path(1, folder)
                written, warning = core_view.capture_to_file(
                    view, path, width, height)

                if written is None:
                    warnings.append("{}: {}".format(name, warning))
                else:
                    captured.append({
                        "view": name,
                        "path": written,
                        "shows": VIEW_PURPOSE.get(name, "a view of the model"),
                    })

                # Put this viewport back before moving to the next one.
                if display_mode and previous_mode:
                    core_view.set_display_mode(view, previous_mode)
                _restore_camera(view, previous_camera)

        finally:
            if tagged:
                core_vision.untag_objects(guids)

    if not captured:
        return receipts.err("vision.review", "No views could be captured.",
                            warnings=warnings)

    table = vision_capture.build_sidecar(guids, 2) if guids else None

    return receipts.r3(
        [entry["path"] for entry in captured],
        viewport=[entry["view"] for entry in captured],
        level=1, tag_table=table, warnings=warnings,
        hint="Read the tag table for facts and the images for spatial "
             "relationships. 'shows' says which view answers which question. "
             "If you have not run structural.report yet, do that first "
             "- it answers alignment exactly and costs almost nothing.",
        views=captured)


def _restore_camera(view, state):
    """Put a camera back where it was.

    Skipped silently on failure: a review that could not restore one
    camera has still produced its images, and raising here would lose
    them over a viewport the user can reset themselves."""
    if not state:
        return
    try:
        viewport = view.ActiveViewport
        viewport.SetCameraLocations(
            coerce.to_point3d(state["target"]),
            coerce.to_point3d(state["location"]))
        up = coerce.to_vector3d(state["up"])
        if up is not None:
            viewport.CameraUp = up
        redraw.request(view)
    except Exception:
        pass


# ---------- Rhino execution wrapper ----------

def run_vision_review():
    obj_ids = rs.GetObjects("Select objects to review (Enter for the whole "
                            "model)", preselect=True)
    ids = [str(g) for g in obj_ids] if obj_ids else None
    receipt = tool_vision_review(ids=ids, tag=bool(ids))
    return receipts.echo(receipt)


# From here is only required just to run the above functions in Rhino.

if __name__ == "__main__":
    run_vision_review()
