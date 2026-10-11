"""
Session state and checkpoint/rollback core.

``session.state`` is level 0 of the vision ladder: the cheapest possible
answer to "what am I looking at". It costs no screenshot and no render,
so the doctrine says to call it before escalating to an image.

``session.checkpoint`` / ``session.rollback`` wrap a turn in a Rhino undo
record. The rule from the plan is "roll back rather than repairing
debris afterward" - a failed boolean leaves fragments that are harder to
clean up than to undo.

How the undo bookkeeping works
------------------------------
Each ``checkpoint`` closes any record this module already has open and
opens a new named one. That gives a strict one-checkpoint-one-record
correspondence, which is what makes rollback countable: rolling back to
a label means undoing as many records as were opened after it, plus the
labelled one itself.

The honest caveat: Rhino also creates undo records for anything the user
does by hand in between (drawing a line, deleting an object). Those are
not in this module's stack, so a rollback after manual edits undoes the
scripted records but may leave a manual edit in place. ``rollback``
reports how many records it actually undid so the caller can see this
rather than assume a clean rewind.

Nothing in this module prompts the user.
"""

from collections import OrderedDict

import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

from . import coerce
from . import planes
from . import layers as layer_utils
from . import redraw


_STACK = "rg_checkpoint_stack"   # list of {"label":..., "serial":...}
_OPEN = "rg_checkpoint_open"     # serial of the currently open record


# ---------- Census ----------

def census(include_hidden=True):
    """Count of document objects by short type name.

    Uses this package's own ``object_type_name`` rather than Rhino's raw
    ObjectType enum so the counts line up with the vocabulary every other
    receipt uses - an Extrusion reports as 'solid', not as a fourth kind
    of thing the caller has to learn about.

    Returned as an OrderedDict sorted by descending count, so the most
    numerous thing in the model is the first thing read."""
    counts = {}
    for rhobj in sc.doc.Objects:
        if rhobj.IsDeleted:
            continue
        if not include_hidden and rhobj.IsHidden:
            continue
        name = coerce.object_type_name(rhobj.Geometry)
        counts[name] = counts.get(name, 0) + 1

    ordered = OrderedDict(sorted(counts.items(), key=lambda kv: (-kv[1], kv[0])))
    return ordered


def document_bbox():
    """Bounding box of every visible object, as a dict or None."""
    ids = [rhobj.Id for rhobj in sc.doc.Objects if not rhobj.IsDeleted]
    return coerce.bbox_dict(coerce.bbox_of(ids)) if ids else None


def selected_ids():
    """Ids of the current document selection."""
    return [rhobj.Id for rhobj in sc.doc.Objects.GetSelectedObjects(False, False)]


def tolerance_info():
    """Absolute and angle tolerance, matching the R6 shape.

    Angle is converted to degrees on the way out. Rhino stores it in
    radians internally, but every prompt and dialog in the UI shows
    degrees, so degrees is what a caller will recognise."""
    import math
    return {
        "abs": float(sc.doc.ModelAbsoluteTolerance),
        "angle": float(math.degrees(sc.doc.ModelAngleToleranceRadians)),
    }


def viewport_info(include_all=True):
    """R6's ``active_viewport`` list.

    Returns every open viewport when ``include_all`` is true, otherwise
    just the active one. Each entry carries its own CPlane and viewplane,
    because those differ per viewport and conflating them is the exact
    failure the workflow guidelines warn about."""
    views = planes.all_views() if include_all else [planes.get_view("active")]
    out = []
    for view in views:
        if view is None:
            continue
        info = planes.view_info(view)
        if info is not None:
            out.append(info)
    return out


def geometry_type(rhobj):
    """Rhino's own class name for an object's geometry: 'Extrusion',
    'Brep', 'LineCurve', 'NurbsCurve', 'Mesh', ...

    Deliberately the raw name, not ``coerce.object_type_name`` - that one
    folds an Extrusion into 'solid', and the model needs to know which
    it is (an Extrusion is not a Brep to half of RhinoCommon)."""
    geom = rhobj.Geometry
    return geom.GetType().Name if geom is not None else "Unknown"


def layer_tree():
    """Every layer as a nested tree, each with its object counts by type.

    ``[{"name": "Structure", "objects": {"Extrusion": 12},
        "children": [{"name": "Columns", ...}]}]``

    ``objects`` counts only what sits directly on that layer, not its
    children. Empty keys are left out so an empty layer is one short line;
    ``hidden`` / ``locked`` appear only when true."""
    counts = {}                                  # layer index -> {type: n}
    for rhobj in sc.doc.Objects:
        if rhobj.IsDeleted:
            continue
        per = counts.setdefault(rhobj.Attributes.LayerIndex, {})
        kind = geometry_type(rhobj)
        per[kind] = per.get(kind, 0) + 1

    nodes, roots = {}, []
    layers = [l for l in sc.doc.Layers if not l.IsDeleted]
    for layer in layers:
        node = OrderedDict([("name", layer.Name)])
        if layer.Index in counts:
            node["objects"] = OrderedDict(
                sorted(counts[layer.Index].items(), key=lambda kv: (-kv[1], kv[0])))
        if not layer.IsVisible:
            node["hidden"] = True
        if layer.IsLocked:
            node["locked"] = True
        nodes[layer.Id] = node
    for layer in sorted(layers, key=lambda l: l.SortIndex):
        node = nodes[layer.Id]
        parent = nodes.get(layer.ParentLayerId)
        if parent is None:
            roots.append(node)
        else:
            parent.setdefault("children", []).append(node)
    return roots


def type_totals():
    """Whole-document count by Rhino geometry type, most numerous first."""
    totals = {}
    for rhobj in sc.doc.Objects:
        if rhobj.IsDeleted:
            continue
        kind = geometry_type(rhobj)
        totals[kind] = totals.get(kind, 0) + 1
    return OrderedDict(sorted(totals.items(), key=lambda kv: (-kv[1], kv[0])))


def state(include_all_viewports=True, include_census=True):
    """What is in the document: the layer tree and the objects on it.

    ``layers`` is nested parent -> children, each layer carrying its own
    object counts by Rhino type (Extrusion, Brep, LineCurve, ...);
    ``totals`` is the same count for the whole file. That is all - no
    image, no viewports, so it stays cheap enough to call every turn.

    The two arguments are kept so old callers do not break; they no
    longer change anything."""
    totals = type_totals()
    return OrderedDict([
        ("object_count", sum(totals.values())),
        ("totals", totals),
        ("layers", layer_tree()),
    ])


# ---------- Checkpoint / rollback ----------

def _stack():
    if _STACK not in sc.sticky:
        sc.sticky[_STACK] = []
    return sc.sticky[_STACK]


def _close_open_record():
    """End the undo record this module currently has open, if any."""
    serial = sc.sticky.get(_OPEN)
    if serial:
        try:
            sc.doc.EndUndoRecord(serial)
        except Exception:
            pass
        sc.sticky[_OPEN] = None
        return True
    return False


def checkpoint(label):
    """Open a named undo record and remember it.

    Returns ``{"label":..., "serial":..., "depth":...}``. ``depth`` is
    how many checkpoints are now on the stack, which is a useful sanity
    check that a workflow is not opening one per call and never rolling
    back."""
    label = str(label) if label else "checkpoint"
    _close_open_record()

    serial = sc.doc.BeginUndoRecord(label)
    sc.sticky[_OPEN] = serial

    entry = {"label": label, "serial": int(serial)}
    _stack().append(entry)

    return {"label": label, "serial": int(serial), "depth": len(_stack())}


def list_checkpoints():
    """Labels currently on the checkpoint stack, oldest first."""
    return [entry["label"] for entry in _stack()]


def rollback(label=None, max_steps=64):
    """Undo back to a checkpoint.

    With no label, undoes the most recent checkpoint only. With a label,
    undoes every record back to and including that one.

    ``max_steps`` is a guard against an unbounded undo loop if Rhino
    keeps returning True for records this module did not create.

    Returns ``{"undone":..., "label":..., "remaining_depth":...,
    "warnings":[...]}``. ``undone`` is the number of undo records Rhino
    actually reversed, which can differ from the number of checkpoints
    popped when the user made manual edits in between - the caller
    should compare the two rather than assume they match."""
    stack = _stack()
    warnings = []

    if not stack:
        return {"undone": 0, "label": None, "remaining_depth": 0,
                "warnings": ["No checkpoint has been set in this session."]}

    _close_open_record()

    # Work out how many checkpoints to pop.
    if label is None:
        pop_count = 1
        target_label = stack[-1]["label"]
    else:
        target_label = str(label)
        index = None
        for i in range(len(stack) - 1, -1, -1):
            if stack[i]["label"] == target_label:
                index = i
                break
        if index is None:
            return {"undone": 0, "label": target_label, "remaining_depth": len(stack),
                    "warnings": ["No checkpoint named '{}'.".format(target_label)]}
        pop_count = len(stack) - index

    undone = 0
    for _ in range(min(pop_count, max_steps)):
        try:
            if not sc.doc.Undo():
                warnings.append("Rhino had nothing left to undo.")
                break
        except Exception as exc:
            warnings.append("Undo failed: {}".format(exc))
            break
        undone += 1

    for _ in range(pop_count):
        if stack:
            stack.pop()

    redraw.request()

    if undone < pop_count:
        warnings.append(
            "Popped {} checkpoint(s) but Rhino undid {} record(s) - "
            "manual edits between checkpoints may remain.".format(pop_count, undone))

    return {"undone": undone, "label": target_label,
            "remaining_depth": len(stack), "warnings": warnings}


def clear_checkpoints():
    """Forget the checkpoint stack without undoing anything.

    Used when a workflow completes successfully and the records are no
    longer interesting - keeps ``depth`` meaningful for the next turn."""
    _close_open_record()
    count = len(_stack())
    sc.sticky[_STACK] = []
    return count
