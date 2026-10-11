"""
progress - a long tool that keeps Rhino alive: a progress meter in the status
bar, the step it is on in the command prompt (with a spinner and a running
clock while a step works in the background), and Esc to stop.

Why not everything on a background thread: every step edits the document
(adds, moves, splits, deletes objects), and RhinoCommon's document may only
be touched from Rhino's main thread. The pure geometry maths can run on a
worker (_shared/background); the main thread then only pumps Rhino
(``RhinoApp.Wait``) - it repaints and reads the keyboard, so Windows no
longer marks it 'Not responding' and Esc is seen.

    with progress.Meter("structural.from_3d", steps=12) as meter:
        meter.step("convert")          # Esc since the last step: Stopped
        ...
        meter.pump("#12 4 s")          # while a worker runs

Meters NEST: structural.convert inside structural.from_3d shows
'structural.from_3d: convert (2/19) > structural.convert: axis #12 (5/103)'
on the one status-bar meter of the outermost.

Layer A helper - no tool logic here.
"""

import time

import Rhino


class Stopped(Exception):
    """Esc was pressed - the tool stops after the step it was on."""


_OPEN = []          # the meters open now, outermost first
SPIN = "|/-\\"
PUMP_EVERY = 0.15   # seconds between prompt updates while a worker runs


class Meter(object):

    def __init__(self, title, steps):
        self.title = title
        self.steps = max(1, int(steps))
        self.done = 0
        self.label = ""
        self.escaped = False
        self._spin = 0
        self._shown = 0.0

    def _on_escape(self, _sender, _args):
        self.escaped = True

    def __enter__(self):
        # Rhino's Esc EVENT - fired while RhinoApp.Wait reads the keyboard.
        # scriptcontext.escape_test cost 0.1 s a call, and its flag latches:
        # an Esc pressed any time before stopped the next build at step 1.
        try:
            Rhino.RhinoApp.EscapeKeyPressed += self._on_escape
        except Exception:
            pass
        if not _OPEN:
            try:
                Rhino.UI.StatusBar.ShowProgressMeter(0, self.steps, self.title, True, True)
            except Exception:
                pass
        _OPEN.append(self)
        self._show()
        return self

    def __exit__(self, *_exc):
        try:
            Rhino.RhinoApp.EscapeKeyPressed -= self._on_escape
        except Exception:
            pass
        if self in _OPEN:
            _OPEN.remove(self)
        if not _OPEN:
            try:
                Rhino.UI.StatusBar.HideProgressMeter()
            except Exception:
                pass
            try:
                Rhino.RhinoApp.SetCommandPrompt("Command")
            except Exception:
                pass
        return False

    def more(self, steps):
        """A loop turned out longer: room for ``steps`` more."""
        self.steps += max(0, int(steps))

    def step(self, label):
        """The next step starts: shown, Rhino repaints, Esc is checked."""
        self.done = min(self.done + 1, self.steps)
        self.label = label
        self._show()
        self._check()

    def pump(self, detail=""):
        """While a worker runs: the spinner turns, Rhino repaints, Esc is checked."""
        now = time.time()
        if now - self._shown >= PUMP_EVERY:
            self._spin += 1
            self._show(detail)
        else:
            _wait()
        self._check()

    def _check(self):
        if any(m.escaped for m in _OPEN) or self.escaped:
            raise Stopped(self.label)

    def _text(self, detail=""):
        return "{}: {} ({}/{})".format(self.title, self.label or "starting", self.done,
                                       self.steps) + (" {} {}".format(SPIN[self._spin % 4], detail)
                                                      if detail else "")

    def _show(self, detail=""):
        self._shown = time.time()
        chain = [m for m in _OPEN if m is not self] + [self]
        text = " > ".join(m._text(detail if m is self else "") for m in chain) + " - Esc stops"
        try:
            Rhino.RhinoApp.SetCommandPrompt(text)
        except Exception:
            pass
        top = _OPEN[0] if _OPEN else self
        try:
            Rhino.UI.StatusBar.UpdateProgressMeter(text, top.done, True)
        except Exception:
            try:
                Rhino.UI.StatusBar.UpdateProgressMeter(top.done, True)
            except Exception:
                pass
        _wait()


def _wait():
    try:
        Rhino.RhinoApp.Wait()           # repaint the window, read the keyboard
    except Exception:
        pass
