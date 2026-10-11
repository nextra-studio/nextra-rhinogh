"""
Layer plumbing, shared across categories.

Lifted out of ``Line_Srf_tidyup.py``, where ``ensure_layer_index`` and
friends were doing real work for the grid generator. Three different
categories need them - the grid tool, the whole ``layer_utils`` package,
and every tool that wants to drop its output somewhere other than the
current layer - so they live here rather than being copied three times.

Rhino addresses nested layers by full path with a double-colon
separator: "Grid::X::Lines". Almost every bug in layer handling comes
from assuming that path already exists, so ``ensure_layer_index`` creates
every missing level on the way down.

Nothing in this module prompts the user.
"""

import Rhino
import scriptcontext as sc

from . import coerce


def ensure_layer_index(full_path):
    """Index of the layer at 'A::B::C', creating every missing level.

    Walks the path one segment at a time, parenting each new layer to the
    one above it. Returns None for an empty path so callers can use
    ``None`` to mean "leave objects on their current layer"."""
    if not full_path:
        return None

    index = sc.doc.Layers.FindByFullPath(full_path, True)
    if index >= 0:
        return index

    parent_id = None
    current_path = ""
    index = -1

    for part in full_path.split("::"):
        current_path = part if not current_path else current_path + "::" + part
        idx = sc.doc.Layers.FindByFullPath(current_path, True)
        if idx < 0:
            layer = Rhino.DocObjects.Layer()
            layer.Name = part
            if parent_id is not None:
                layer.ParentLayerId = parent_id
            idx = sc.doc.Layers.Add(layer)
        parent_id = sc.doc.Layers[idx].Id
        index = idx

    return index


def find_layer_index(full_path):
    """Index of an existing layer, or None. Does not create anything."""
    if not full_path:
        return None
    index = sc.doc.Layers.FindByFullPath(full_path, True)
    return index if index >= 0 else None


def layer_at(index):
    """Layer object from an index, guarding against a stale index."""
    if index is None or index < 0 or index >= sc.doc.Layers.Count:
        return None
    return sc.doc.Layers[index]


def set_layer_linetype(layer_index, preferred=("Dots", "Dashed", "DotDash")):
    """Give a layer the first available linetype from ``preferred``.

    Used to make grid lines read as grid lines. Silently does nothing if
    none of the named linetypes exist in the document - a missing
    linetype is a cosmetic problem, not a reason to fail the operation.

    The try/except covers the two different ``Linetypes.Find`` overloads
    across Rhino versions (one takes a name, one takes name + bool)."""
    if layer_index is None:
        return False
    for name in preferred:
        try:
            lt_index = sc.doc.Linetypes.Find(name)
        except TypeError:
            lt_index = sc.doc.Linetypes.Find(name, True)
        if lt_index is not None and lt_index >= 0:
            layer = sc.doc.Layers[layer_index]
            layer.LinetypeIndex = lt_index
            layer.CommitChanges()
            return True
    return False


def set_object_layer_index(obj_id, layer_index):
    """Move one document object onto a layer by index."""
    if layer_index is None:
        return False
    rhobj = coerce.find_object(obj_id)
    if rhobj is None:
        return False
    attr = rhobj.Attributes
    attr.LayerIndex = layer_index
    return sc.doc.Objects.ModifyAttributes(rhobj, attr, True)


def attr_on_layer(layer_index):
    """Fresh ObjectAttributes targeting a layer.

    Passed to ``sc.doc.Objects.Add*`` so newly created geometry lands on
    the right layer in one call, instead of being added and then moved."""
    attr = Rhino.DocObjects.ObjectAttributes()
    if layer_index is not None:
        attr.LayerIndex = layer_index
    return attr


def current_layer_name():
    """Full path of the document's current layer."""
    return sc.doc.Layers.CurrentLayer.FullPath


def layer_names():
    """Full paths of every layer in the document, deleted ones excluded."""
    return [layer.FullPath for layer in sc.doc.Layers if not layer.IsDeleted]
