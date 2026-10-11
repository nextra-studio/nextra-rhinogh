"""Karamba-style radio buttons drawn on a GH Python 3 component (custom attributes, pure Python).
Inputs: none required. Output: choice (selected option text), index.
Click a button on the component to change the choice; it persists in the component's Message.
"""
import System
import System.Drawing as sd
import Grasshopper
import scriptcontext as sc
from Grasshopper.Kernel.Attributes import GH_ComponentAttributes
from Grasshopper.GUI.Canvas import GH_CanvasChannel, GH_Capsule, GH_Palette, GH_ObjectResponse

OPTIONS = ["Steel", "Timber", "Concrete"]
ROW = 18          # height of one radio row

comp = ghenv.Component

# cache the attribute class per script version: editing this code makes a new class automatically
import hashlib
try:
    _ok, _src = comp.TryGetSource()
except Exception:
    _src = ""
VERSION = hashlib.md5((_src or "").encode("utf-8")).hexdigest()[:8]
KEY = "nextra.radio." + VERSION


def attr_bases():
    """Rhino 8 script components require their attributes to implement IScriptAttribute
    (cast during script build). GH_ComponentAttributes + that interface satisfies it."""
    try:
        from RhinoCodePlatform.GH.Context import IScriptAttribute
        return (GH_ComponentAttributes, IScriptAttribute)
    except ImportError:                      # legacy GhPython / Rhino 7
        return (GH_ComponentAttributes,)


def make_class(bases, ns):
    base = bases[0]

    class RadioAttributes(*bases):
        __namespace__ = ns   # pythonnet (CPython) needs a unique .NET namespace per class version

        def __init__(self, owner):
            base.__init__(self, owner)
            self.rows = []

        def Layout(self):
            base.Layout(self)
            b = self.Bounds
            n = len(OPTIONS)
            self.rows = [sd.RectangleF(b.X + 4, b.Bottom + 2 + i * ROW, b.Width - 8, ROW - 2) for i in range(n)]
            self.Bounds = sd.RectangleF(b.X, b.Y, b.Width, b.Height + n * ROW + 6)

        def Render(self, canvas, graphics, channel):
            base.Render(self, canvas, graphics, channel)
            if channel != GH_CanvasChannel.Objects:
                return
            sel = selected(self.Owner)
            for i, r in enumerate(self.rows):
                cap = GH_Capsule.CreateCapsule(r, GH_Palette.Black if i == sel else GH_Palette.White, 2, 0)
                cap.Render(graphics, self.Selected, self.Owner.Locked, False)
                cap.Dispose()
                font = Grasshopper.Kernel.GH_FontServer.StandardAdjusted
                brush = sd.Brushes.White if i == sel else sd.Brushes.Black
                graphics.DrawString(OPTIONS[i], font, brush, r.X + 15, r.Y + (r.Height - font.Height) / 2.0)
                # radio dot
                cx, cy = r.X + 8, r.Y + r.Height / 2
                graphics.DrawEllipse(sd.Pens.DimGray, cx - 4, cy - 4, 8, 8)
                if i == sel:
                    graphics.FillEllipse(sd.Brushes.White, cx - 2.5, cy - 2.5, 5, 5)

        def ShowProgress(self, progress, message):    # IScriptAttribute
            return False

        def RespondToMouseDown(self, sender, e):
            if e.Button == System.Windows.Forms.MouseButtons.Left:
                for i, r in enumerate(self.rows):
                    if r.Contains(e.CanvasLocation):
                        set_selected(self.Owner, i)
                        return GH_ObjectResponse.Handled
            return base.RespondToMouseDown(self, sender, e)

    return RadioAttributes


def selected(c):
    try:
        return OPTIONS.index(c.Message)
    except ValueError:
        return 0


def set_selected(c, i):
    c.Message = OPTIONS[i]
    c.ExpireSolution(True)


if KEY not in sc.sticky:
    sc.sticky[KEY] = make_class(attr_bases(), "NextraRadio_" + VERSION)
cls = sc.sticky[KEY]

if type(comp.Attributes).__name__ != cls.__name__:
    pivot = comp.Attributes.Pivot
    comp.Attributes = cls(comp)
    comp.Attributes.Pivot = pivot
    comp.Attributes.ExpireLayout()
    comp.Attributes.PerformLayout()

if not comp.Message:
    comp.Message = OPTIONS[0]
index = selected(comp)
choice = OPTIONS[index]
