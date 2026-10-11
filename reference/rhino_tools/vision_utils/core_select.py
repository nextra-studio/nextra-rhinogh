"""
Selection core - stackable object filters.

``select`` is the tool that keeps every other tool from needing its own
"which objects?" logic. The plan calls for filters that combine, so this
module is built as a set of independent predicates plus one driver that
ANDs them together:

    type      curve / surface / solid / polysurface / point / mesh / block
    layer     exact full path, or a prefix with '*'
    name      object name, exact or wildcard
    attribute user text key, optionally with a required value
    bbox      inside / overlapping a world box
    plane     on or near a plane, within a distance
    proximity within a radius of a point
    tag       a specific '#12' tag or "anything tagged"
    selected  the current document selection

Every filter is optional. Passing none returns every object in the
document, which is the deliberate default for "census"-style calls.

Nothing in this module prompts the user.
"""

import fnmatch

import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

from .._shared import coerce
from .._shared import planes as plane_utils
from .._shared import tags as tagmod
from .._shared import redraw


# Type aliases, so a caller can say "solid" or "brep" or "polysurface"
# and mean the obvious thing. Values are checked against
# coerce.object_type_name, which already normalises Extrusions.
TYPE_ALIASES = {
    "curve": {"curve"},
    "line": {"curve"},
    "point": {"point"},
    "surface": {"surface"},
    "polysurface": {"polysurface"},
    "brep": {"surface", "polysurface", "solid"},
    "solid": {"solid"},
    "extrusion": {"solid", "surface"},
    "mesh": {"mesh"},
    "block": {"block"},
    "annotation": {"annotation", "textdot"},
    "textdot": {"textdot"},
    "hatch": {"hatch"},
}


def candidate_objects(include_hidden=False, include_locked=False):
    """The pool every filter narrows down.

    Hidden and locked objects are excluded by default because a selection
    the caller cannot then act on is worse than an empty one."""
    out = []
    for rhobj in sc.doc.Objects:
        if rhobj.IsDeleted:
            continue
        if not include_hidden and rhobj.IsHidden:
            continue
        if not include_locked and rhobj.IsLocked:
            continue
        out.append(rhobj)
    return out


# ---------- Individual predicates ----------

def match_type(rhobj, wanted):
    """Type filter. ``wanted`` is a string or list of strings."""
    if not wanted:
        return True
    if isinstance(wanted, str):
        wanted = [wanted]

    actual = coerce.object_type_name(rhobj.Geometry)
    accepted = set()
    for w in wanted:
        key = str(w).strip().lower()
        accepted |= TYPE_ALIASES.get(key, {key})
    return actual in accepted


def match_layer(rhobj, wanted):
    """Layer filter, exact full path or a wildcard pattern.

    'STR_*' matches every layer starting STR_. 'Grid::X::Lines' matches
    exactly that layer. Matching is case-insensitive because Rhino layer
    names are not reliably cased by the people who make them."""
    if not wanted:
        return True
    if isinstance(wanted, str):
        wanted = [wanted]

    layer = sc.doc.Layers[rhobj.Attributes.LayerIndex]
    full = layer.FullPath.lower()
    for pattern in wanted:
        p = str(pattern).lower()
        if fnmatch.fnmatch(full, p) or full == p:
            return True
        # Treat a bare parent name as "this layer or anything under it".
        if full.startswith(p + "::"):
            return True
    return False


def match_name(rhobj, wanted):
    """Object name filter, wildcards allowed."""
    if not wanted:
        return True
    name = rhobj.Attributes.Name or ""
    patterns = [wanted] if isinstance(wanted, str) else wanted
    return any(fnmatch.fnmatch(name.lower(), str(p).lower()) for p in patterns)


def match_attribute(rhobj, key, value=None):
    """User-text filter.

    With only a key, matches any object that has that key at all. With a
    value, the value must match too (wildcards allowed), which is what
    makes user text usable as a lightweight classification system."""
    if not key:
        return True
    stored = rhobj.Attributes.GetUserString(str(key))
    if stored is None:
        return False
    if value is None:
        return True
    return fnmatch.fnmatch(str(stored).lower(), str(value).lower())


def match_bbox(rhobj, box_spec, mode="overlap"):
    """World bounding-box filter.

    ``mode='inside'`` requires full containment; ``mode='overlap'`` (the
    default) accepts any intersection, which is what people usually mean
    by "select what's in this area"."""
    if not box_spec:
        return True

    box_min = coerce.to_point3d(box_spec.get("min"))
    box_max = coerce.to_point3d(box_spec.get("max"))
    if box_min is None or box_max is None:
        return True

    filter_box = rg.BoundingBox(box_min, box_max)
    obj_box = rhobj.Geometry.GetBoundingBox(True)
    if not obj_box.IsValid:
        return False

    if mode == "inside":
        return (filter_box.Contains(obj_box.Min) and filter_box.Contains(obj_box.Max))

    intersection = rg.BoundingBox.Intersection(filter_box, obj_box)
    return intersection.IsValid


def match_plane(rhobj, plane, distance=None, mode="on"):
    """Plane filter - objects lying on, or near, a plane.

    ``mode='on'`` means every corner of the object's bounding box is
    within ``distance`` of the plane, i.e. the object is genuinely flat
    on it. ``mode='near'`` only requires the object's centre to be
    within that distance, which is the looser "in this vicinity" sense.

    ``distance`` defaults to document tolerance for 'on' - the strict
    reading - and must be given explicitly for 'near' to mean anything
    useful."""
    if plane is None:
        return True
    distance = coerce.tol_or_default(distance)

    bbox = rhobj.Geometry.GetBoundingBox(True)
    if not bbox.IsValid:
        return False

    if mode == "near":
        return abs(plane.DistanceTo(bbox.Center)) <= distance

    for corner in bbox.GetCorners():
        if abs(plane.DistanceTo(corner)) > distance:
            return False
    return True


def match_proximity(rhobj, point, radius):
    """Radius filter around a point, measured to the object's bounding
    box centre. Cheap and good enough for "the columns near this core"."""
    if point is None or radius is None:
        return True
    centre = rhobj.Geometry.GetBoundingBox(True).Center
    return centre.DistanceTo(point) <= float(radius)


def match_tag(rhobj, wanted):
    """Tag filter.

    ``wanted=True`` means "anything this session has tagged".
    A string or list of '#12' tags means those specific objects - which
    makes ``select`` a way to turn a tag list back into a live selection.
    """
    if wanted is None or wanted is False:
        return True

    if wanted is True:
        return tagmod.tag_of(rhobj.Id, create=False) is not None

    patterns = [wanted] if isinstance(wanted, str) else wanted
    own = tagmod.tag_of(rhobj.Id, create=False)
    if own is None:
        return False
    return own in {str(p).strip() for p in patterns}


# ---------- Driver ----------

def select(filters=None, include_hidden=False, include_locked=False):
    """Apply every supplied filter and return the matching object ids.

    ``filters`` is a flat dict; unrecognised keys are ignored rather than
    raising, so an over-specified call from a model still returns
    something useful. Recognised keys:

        type, layer, name,
        attribute_key, attribute_value,
        bbox, bbox_mode,
        plane, plane_distance, plane_mode,
        near_point, radius,
        tag, selected

    All conditions are ANDed. Returns a list of Guids in document order,
    which is stable across calls - important when a caller pairs the
    result with a previous one by index."""
    filters = filters or {}

    plane = filters.get("plane")
    if plane is not None:
        plane = plane_utils.resolve_plane(plane)

    near_point = coerce.to_point3d(filters.get("near_point"))

    # An explicit selected=True short-circuits the pool to what the user
    # has highlighted, then still applies the remaining filters to it.
    if filters.get("selected"):
        pool = list(sc.doc.Objects.GetSelectedObjects(False, False))
    else:
        pool = candidate_objects(include_hidden, include_locked)

    out = []
    for rhobj in pool:
        if not match_type(rhobj, filters.get("type")):
            continue
        if not match_layer(rhobj, filters.get("layer")):
            continue
        if not match_name(rhobj, filters.get("name")):
            continue
        if not match_attribute(rhobj, filters.get("attribute_key"),
                               filters.get("attribute_value")):
            continue
        if not match_bbox(rhobj, filters.get("bbox"),
                          filters.get("bbox_mode", "overlap")):
            continue
        if not match_plane(rhobj, plane, filters.get("plane_distance"),
                           filters.get("plane_mode", "on")):
            continue
        if not match_proximity(rhobj, near_point, filters.get("radius")):
            continue
        if not match_tag(rhobj, filters.get("tag")):
            continue
        out.append(rhobj.Id)

    return out


def apply_document_selection(obj_ids, replace=True):
    """Highlight the given ids in the Rhino UI.

    Selection is a side effect ``select`` performs only when asked, so a
    query used purely to feed another tool does not disturb what the user
    had highlighted."""
    if replace:
        sc.doc.Objects.UnselectAll()
    count = 0
    for guid in coerce.to_guids(obj_ids):
        if sc.doc.Objects.Select(guid, True):
            count += 1
    redraw.request()
    return count
