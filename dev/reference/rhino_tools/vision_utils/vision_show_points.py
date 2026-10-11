"""
vision.show_points - show control points, and curve direction. [LOW]

Opt-in only, per the plan. Control points on a dense curve are visual
noise, and turning them on across a whole model makes a viewport
unreadable.

The genuinely useful half is curve DIRECTION: a green sphere at the start
and a red one at the end, sized in screen space so they stay visible at
any zoom. Direction decides which side an offset goes and which way a
sweep runs, and it is completely invisible otherwise - so a curve that
offsets the wrong way looks like a broken tool rather than a reversed
curve.

Layer A (pure logic)  - ``core_vision``, plus the conduit below.
Layer B (Rhino entry) - ``run_vision_show_points``.
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

import System.Drawing
import rhinoscriptsyntax as rs
import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

from rhino_tools._shared import coerce, receipts
from rhino_tools._shared import tags as tagmod
from rhino_tools._shared import redraw
from rhino_tools.vision_utils import core_vision


_POINT_CONDUITS = "rg_point_conduits"


# ---------- Pure logic ----------

class ControlPointConduit(Rhino.Display.DisplayConduit):
    """Draws control points and direction markers for one curve.

    Like ``TagConduit`` it re-reads the object each frame, so the markers
    follow edits and vanish with the object. Sphere radius is derived
    from the viewport's pixels-per-unit so markers stay the same size on
    screen however far you zoom - a fixed model-space radius would be
    invisible on a building and enormous on a bolt."""

    def __init__(self, obj_id, show_control_points=True, show_direction=True):
        super(ControlPointConduit, self).__init__()
        self.obj_id = obj_id
        self.show_control_points = show_control_points
        self.show_direction = show_direction

    def _radius(self, e, point):
        try:
            per_unit = e.Viewport.PixelsPerUnit(point)
            if per_unit and per_unit > 0:
                return 4.0 / per_unit
        except Exception:
            pass
        return 1.0

    def DrawForeground(self, e):
        rhobj = sc.doc.Objects.Find(self.obj_id)
        if rhobj is None:
            return

        curve = coerce.to_curve(rhobj.Id)
        if curve is None:
            return

        if self.show_control_points:
            nurbs = curve.ToNurbsCurve()
            if nurbs is not None:
                for i in range(nurbs.Points.Count):
                    location = nurbs.Points[i].Location
                    e.Display.DrawPoint(
                        location, Rhino.Display.PointStyle.ControlPoint,
                        3, System.Drawing.Color.White)

        if self.show_direction:
            start = curve.PointAtStart
            end = curve.PointAtEnd
            e.Display.DrawSphere(rg.Sphere(start, self._radius(e, start)),
                                 core_vision.START_COLOR)
            e.Display.DrawSphere(rg.Sphere(end, self._radius(e, end)),
                                 core_vision.END_COLOR)
            # An arrow in the middle makes the direction unmistakable
            # even when both spheres are far apart on screen.
            mid = curve.PointAt(curve.Domain.Mid)
            tangent = curve.TangentAt(curve.Domain.Mid)
            tangent.Unitize()
            e.Display.DrawArrow(
                rg.Line(mid, mid + tangent * self._radius(e, mid) * 6),
                core_vision.START_COLOR)


def _store():
    if _POINT_CONDUITS not in sc.sticky:
        sc.sticky[_POINT_CONDUITS] = {}
    return sc.sticky[_POINT_CONDUITS]


def show_points(obj_ids, control_points=True, direction=True):
    """Enable a conduit per curve. Returns how many were shown."""
    core_vision.ensure_hooks()
    store = _store()
    count = 0

    for guid in coerce.to_guids(obj_ids):
        if coerce.to_curve(guid) is None:
            continue
        key = str(guid)
        if key in store:
            store[key].Enabled = False

        conduit = ControlPointConduit(guid, control_points, direction)
        conduit.Enabled = True
        store[key] = conduit
        count += 1

    redraw.request()
    return count


def hide_points(obj_ids=None):
    """Turn the conduits off - all of them when none are named."""
    store = _store()
    keys = ([str(g) for g in coerce.to_guids(obj_ids)] if obj_ids
            else list(store.keys()))

    count = 0
    for key in keys:
        conduit = store.pop(key, None)
        if conduit is not None:
            conduit.Enabled = False
            count += 1

    redraw.request()
    return count


# ---------- Tool entry points (MCP-callable) ----------

def tool_vision_show_points(ids, control_points=True, direction=True):
    """Show control points and/or direction markers. R1 receipt."""
    guids = coerce.to_guids(ids)
    if not guids:
        return receipts.err("vision.show_points",
                            "No valid object ids supplied."
                            + coerce.missing_note(ids))

    shown = show_points(guids, control_points, direction)
    if shown == 0:
        return receipts.err("vision.show_points",
                            "None of those objects are curves.", ids=guids)

    warnings = []
    if shown < len(guids):
        warnings.append("{} of {} object(s) were not curves and were "
                        "skipped.".format(len(guids) - shown, len(guids)))
    if control_points and shown > 10:
        warnings.append("Control points on {} curves will be hard to read - "
                        "consider direction markers only.".format(shown))

    return receipts.r1(
        guids, "vision.show_points",
        value={"shown": shown, "control_points": bool(control_points),
               "direction": bool(direction)},
        warnings=warnings,
        hint="Green sphere = curve start, red = end. Direction decides "
             "which way offset and sweep go.")


def tool_vision_hide_points(ids=None):
    """Turn the markers off. R1 receipt."""
    hidden = hide_points(ids)
    if hidden == 0:
        return receipts.err("vision.show_points:hide", "Nothing to hide.")
    return receipts.r1([], "vision.show_points:hide",
                       value={"hidden": hidden})


# ---------- Rhino execution wrapper ----------

def run_vision_show_points():
    go = Rhino.Input.Custom.GetOption()
    go.SetCommandPrompt("Show points")
    idx_both = go.AddOption("Both")
    idx_direction = go.AddOption("DirectionOnly")
    idx_control = go.AddOption("ControlPointsOnly")
    go.AddOption("Hide")
    go.AcceptNothing(True)

    result = go.Get()
    chosen = (go.Option().Index if result == Rhino.Input.GetResult.Option
              else idx_direction)
    if result not in (Rhino.Input.GetResult.Option, Rhino.Input.GetResult.Nothing):
        print("Cancelled.")
        return None

    if chosen not in (idx_both, idx_direction, idx_control):
        return receipts.echo(tool_vision_hide_points())

    obj_ids = rs.GetObjects("Select curves", rs.filter.curve, preselect=True)
    if not obj_ids:
        print("Nothing selected.")
        return None

    control = chosen in (idx_both, idx_control)
    direction = chosen in (idx_both, idx_direction)

    return receipts.echo(tool_vision_show_points(obj_ids, control, direction))


# From here is only required just to run the above functions in Rhino.

if __name__ == "__main__":
    run_vision_show_points()
