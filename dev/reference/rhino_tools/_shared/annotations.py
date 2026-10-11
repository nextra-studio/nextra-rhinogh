"""
Annotation primitives shared by the grid generator and the drafting
tools.

``add_dimension`` started life inside ``Line_Srf_tidyup.py``, where it
dimensioned the gaps between grid lines. The drafting category needs the
same primitive for ``dimension(op="linear")``, so rather than have two
slightly different versions drift apart it lives here and both call it.

The awkward part of dimensioning in Rhino is that a dimension carries its
own plane, and the offset that positions the dimension line has to stay
inside that plane. Get it wrong and the dimension either lands edge-on
(invisible) or flips to the far side of the geometry. ``add_dimension``
below handles that by re-orthogonalising the offset direction against the
measurement direction before building the plane.

Nothing in this module prompts the user.
"""

import System
import Rhino
import Rhino.Geometry as rg
import rhinoscriptsyntax as rs
import scriptcontext as sc

from . import coerce
from . import layers as layer_utils
from . import precision


# ---------- Text dots ----------

def add_textdot(text, point, layer_index=None):
    """Place a text dot. Returns the new object id, or None."""
    pt = coerce.to_point3d(point)
    if pt is None:
        return None
    dot = rg.TextDot(str(text), pt)
    obj_id = sc.doc.Objects.AddTextDot(dot, layer_utils.attr_on_layer(layer_index))
    # Rhino returns Guid.Empty (not None) when an Add call fails.
    return obj_id if obj_id != System.Guid.Empty else None


# ---------- Text ----------

def add_text(text, plane, height=None, layer_index=None):
    """Place real (plane-bound, scalable) text, as opposed to a text dot.

    Height defaults to dimension_text_height in
    settings/general_settings.json for the document's units, else the
    document's annotation style height."""
    if plane is None:
        return None
    if height is None:
        height = precision.text_height()
    if height is None:
        try:
            height = sc.doc.DimStyles.Current.TextHeight
        except Exception:
            height = 1.0
    obj_id = sc.doc.Objects.AddText(str(text), plane, float(height),
                                    "", False, False,
                                    layer_utils.attr_on_layer(layer_index))
    return obj_id if obj_id != System.Guid.Empty else None


# ---------- Leaders ----------

def add_leader(points, plane=None, text="", layer_index=None):
    """Leader line, with or without text.

    ``rs.AddLeader`` handles the arrow and the landing shoulder for us.
    An empty string is a legitimate leader - the "arrow without text"
    variant the plan calls for."""
    pts = coerce.to_point3d_list(points)
    if len(pts) < 2:
        return None
    obj_id = rs.AddLeader(pts, plane, str(text) if text else " ")
    if obj_id and layer_index is not None:
        layer_utils.set_object_layer_index(obj_id, layer_index)
    return obj_id


# ---------- Linear dimensions ----------

def dim_style_index():
    """Index of the dimension style tools draw with, or None (= current).

    'nextra 100' in a mm model: the current style, copied, with the text
    height from settings/general_settings.json and arrows / extension
    lines in proportion. Rhino's default style is sized for whatever the
    template was - 3 mm text on a 30 m grid is invisible."""
    height = precision.text_height()
    if height is None:
        return None
    name = "nextra {:g}".format(height)
    style = sc.doc.DimStyles.FindName(name)
    if style is not None:
        return style.Index
    style = sc.doc.DimStyles.Current.Duplicate(name, System.Guid.Empty, System.Guid.Empty)
    style.DimensionScale = 1.0
    style.TextHeight = height
    style.ArrowLength = height
    style.TextGap = height * 0.25
    style.ExtensionLineExtension = height * 0.5
    style.ExtensionLineOffset = height * 0.5
    index = sc.doc.DimStyles.Add(style, False)
    return index if index >= 0 else None


def add_dimension(p1, p2, offset_dir, offset, layer_index=None):
    """Dimension between two points, offset perpendicular to the run.

    The dimension's plane is built with its X axis along p1->p2 and its Y
    axis along the requested offset direction. The offset direction is
    first projected off the X axis (``yaxis - xaxis * (yaxis . xaxis)``)
    so it is exactly perpendicular without leaving the plane the caller
    intended - passing a roughly-right direction is enough.

    Returns the dimension object id, or None if the two points coincide
    or the offset direction is parallel to the run (no valid plane)."""
    a = coerce.to_point3d(p1)
    b = coerce.to_point3d(p2)
    direction = coerce.to_vector3d(offset_dir)
    if a is None or b is None or direction is None:
        return None

    v = b - a
    if v.Length < 1e-9:
        return None

    xaxis = rg.Vector3d(v)
    xaxis.Unitize()

    yaxis = rg.Vector3d(direction)
    yaxis.Unitize()
    yaxis = yaxis - xaxis * (yaxis * xaxis)   # force perpendicularity
    if yaxis.Length < 1e-9:
        return None
    yaxis.Unitize()

    plane = rg.Plane(a, xaxis, yaxis)
    mid = rg.Point3d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, (a.Z + b.Z) / 2.0)
    offset_pt = mid + yaxis * float(offset)

    # rs.AddLinearDimension draws with the CURRENT style: switch to the
    # sized one for this call only, so the user's own setting is untouched.
    previous = sc.doc.DimStyles.CurrentIndex
    index = dim_style_index()
    try:
        if index is not None:
            sc.doc.DimStyles.SetCurrent(index, True)
        dim_id = rs.AddLinearDimension(plane, a, b, offset_pt)
    finally:
        if index is not None:
            sc.doc.DimStyles.SetCurrent(previous, True)
    if dim_id and layer_index is not None:
        layer_utils.set_object_layer_index(dim_id, layer_index)
    return dim_id


def dimension_value(dim_id):
    """Numeric value carried by a dimension object, or None."""
    geo = coerce.geometry_of(dim_id)
    if geo is None:
        return None
    try:
        return float(geo.NumericValue)
    except Exception:
        return None
