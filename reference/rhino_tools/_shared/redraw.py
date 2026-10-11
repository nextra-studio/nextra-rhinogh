"""
Redraw discipline - one place that decides when Rhino repaints.

What this does and does not fix
-------------------------------
Almost every tool used to end with ``sc.doc.Views.Redraw()``. The
obvious theory is that this is what makes a long build freeze Rhino, and
that theory is WRONG - it was measured, and it does not hold:

    200 solids, redraws batched     7.67s
    200 solids, redraws live        7.68s

``Views.Redraw`` only marks viewports dirty; the paint happens when the
message loop next runs, and during a script the message loop never runs.
So there was never a repaint per call to eliminate.

The actual freeze is that a generated script runs on Rhino's MAIN
thread, which it must, so no window message is processed for the
duration and Windows reports the app as hung. That is handled in
``rhino_listener._RunLog`` by pumping the queue periodically, not here.

What batching IS worth
----------------------
Correctness and flicker, not speed. Setting ``RedrawEnabled = False``
stops Rhino repainting on document-change events between the tool calls
we do not control, so a half-built model is never presented, and one
repaint at the end shows the finished state. It also gives every tool a
single place to ask for a repaint instead of fifty-three files deciding
for themselves.

So redraws are now *requested*, not performed:

    from . import redraw
    ...
    redraw.request()

Outside a batch that repaints immediately, exactly as before - a human
running ``run_curve_create`` still sees their curve appear. Inside a
batch it only sets a dirty flag, and the batch repaints once at the end.

    with redraw.batch():
        for i in range(400):
            curve.create(...)      # 400 requests, 0 repaints
    # one repaint here, showing the finished model rather than
    # forty intermediate states

Nesting is counted, not boolean, so a batched tool calling another
batched tool does not release the batch early.

``RedrawEnabled`` is also driven from here. Setting it False is what
actually stops Rhino repainting on document change events we do not
control (object add, layer change), which a dirty flag alone cannot.
The original value is restored on exit, including when the body raises -
leaving a document with redraw permanently disabled is the one failure
mode worse than a slow build.

This module must stay importable when the batch machinery is unavailable
(an odd Rhino version, a unit test outside Rhino); every Rhino call in it
is guarded, and a failure degrades to "repaint normally" rather than
raising into a tool's success path.
"""

import scriptcontext as sc


# sticky, not module state: Rhino reloads modules between script runs but
# sc.sticky survives, so a batch cannot be orphaned by a reload.
_DEPTH = "rg_redraw_depth"
_DIRTY = "rg_redraw_dirty"
_SAVED = "rg_redraw_saved_enabled"


# ---------- state ----------

def depth():
    """How many batches are currently open. 0 means redraws are live."""
    try:
        return int(sc.sticky.get(_DEPTH, 0))
    except Exception:
        return 0


def is_batched():
    """True while inside at least one batch."""
    return depth() > 0


def _set_enabled(value):
    """Drive ``Views.RedrawEnabled``, tolerating its absence."""
    try:
        sc.doc.Views.RedrawEnabled = bool(value)
        return True
    except Exception:
        return False


def _repaint():
    """The actual repaint. Never raises."""
    try:
        sc.doc.Views.Redraw()
        return True
    except Exception:
        return False


# ---------- the call sites use these two ----------

def request(view=None):
    """Ask for a repaint.

    Immediate outside a batch, deferred inside one. ``view`` repaints a
    single RhinoView when given - used by the plane and camera tools,
    which change one viewport rather than the document.

    Returns True when it actually repainted, so a caller that cares (a
    ``run_*`` wrapper wanting to be sure the user saw something) can
    tell. Nothing in the tool layer needs to check it."""
    if is_batched():
        sc.sticky[_DIRTY] = True
        return False

    if view is not None:
        try:
            view.Redraw()
            return True
        except Exception:
            pass
    return _repaint()


def flush():
    """Repaint now even inside a batch, and clear the dirty flag.

    For the rare tool that genuinely must show intermediate state - a
    progressive capture, an interactive pick mid-script. Use sparingly:
    a repaint only actually happens when the message loop runs, so in a
    tight loop this either does nothing or, if paired with a message
    pump, costs a full frame each time."""
    was = _set_enabled(True)
    painted = _repaint()
    sc.sticky[_DIRTY] = False
    if was and is_batched():
        _set_enabled(False)
    return painted


# ---------- batching ----------

def begin():
    """Open a batch. Pair with ``end()``, or use the ``batch`` context
    manager, which pairs them for you even when the body raises."""
    current = depth()
    if current == 0:
        try:
            sc.sticky[_SAVED] = bool(sc.doc.Views.RedrawEnabled)
        except Exception:
            sc.sticky[_SAVED] = True
        sc.sticky[_DIRTY] = False
        _set_enabled(False)
    sc.sticky[_DEPTH] = current + 1
    return sc.sticky[_DEPTH]


def end(force_redraw=True):
    """Close a batch. The outermost one restores ``RedrawEnabled`` and
    repaints if anything asked for it.

    ``force_redraw=False`` suppresses even that final repaint, for a
    batch whose caller is about to open another one."""
    current = depth()
    if current <= 0:
        return 0

    remaining = current - 1
    sc.sticky[_DEPTH] = remaining
    if remaining > 0:
        return remaining

    restored = sc.sticky.get(_SAVED, True)
    _set_enabled(restored)

    if force_redraw and restored and sc.sticky.get(_DIRTY, False):
        _repaint()
    sc.sticky[_DIRTY] = False
    return 0


class batch(object):
    """Context manager form. ``with redraw.batch():``

    The ``finally`` is the point: a boolean that fails in a loop must not
    leave the document with redraw disabled, so the batch closes on the
    way out of an exception just as it does on success."""

    def __init__(self, force_redraw=True):
        self.force_redraw = force_redraw

    def __enter__(self):
        begin()
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        end(force_redraw=self.force_redraw)
        return False  # never swallow the exception


def reset():
    """Force redraw back on and forget any open batch.

    The escape hatch for a session left in a bad state by a script that
    was interrupted mid-batch (Esc in Rhino, a killed bridge). Safe to
    call at any time; the listener calls it before each script."""
    sc.sticky[_DEPTH] = 0
    sc.sticky[_DIRTY] = False
    _set_enabled(True)
    return _repaint()


def status():
    """What the redraw layer thinks is going on - for session.state."""
    try:
        enabled = bool(sc.doc.Views.RedrawEnabled)
    except Exception:
        enabled = None
    return {"batch_depth": depth(), "dirty": bool(sc.sticky.get(_DIRTY, False)),
            "redraw_enabled": enabled}
