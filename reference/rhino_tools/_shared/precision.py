"""
Reporting precision and annotation size - one setting for every tool.

settings/general_settings.json says, per document unit (mm / cm / m):

    report_decimals        decimals in the numbers a tool reports
    dimension_text_height  text height of the dimensions a tool draws

Tools call ``length(v)`` / ``point(p)`` where they used to hard-code
``round(v, 1)``. That was millimetre thinking: in a METRE document 0.1 is
100 mm, and a column at x = 4.25 m came back as 4.2.

``tidy(value)`` is the last pass the agent puts every receipt through
before the model sees it, so a tool that reports 15 decimals still costs
only the digits the setting allows. Values below 1 keep 2 significant
digits (unit vectors, tolerances), and angles / ratios keep 2 decimals -
rounding those to whole numbers would make them wrong, not shorter.

No Rhino import at module level: the agent (plain CPython) uses this too.
The file is re-read when it changes, so an edit applies without a restart.
"""

import json
import math
import os

try:
    _HERE = os.path.dirname(os.path.abspath(__file__))
except NameError:
    _HERE = os.getcwd()
SETTINGS_PATH = os.path.join(os.path.dirname(os.path.dirname(_HERE)),
                             "settings", "general_settings.json")

UNIT_KEYS = {"millimeters": "mm", "centimeters": "cm", "meters": "m",
             "mm": "mm", "cm": "cm", "m": "m"}
DEFAULT_DECIMALS = {"mm": 0, "cm": 2, "m": 3}
DEFAULT_TEXT_HEIGHT = {"mm": 100, "cm": 10, "m": 1}
OTHER_UNIT_DECIMALS = 2

# Keys whose numbers are not lengths - rounding them like millimetres would
# turn 30.5 degrees into 30 and a slenderness of 1.45 into 1.
NOT_LENGTH_WORDS = ("angle", "ratio", "slenderness", "flatness", "scale", "seconds")
NOT_LENGTH_DECIMALS = 2
NOISE = 1e-9            # smaller than this is 0 (a model tolerance is 1e-3 or more)

_cache = {"mtime": None, "data": {}}
_state = {"units": None}      # set by the agent (it knows the units from __ping__)


# ---------- settings ----------

def settings():
    """general_settings.json as a dict ({} if missing or broken)."""
    try:
        mtime = os.path.getmtime(SETTINGS_PATH)
    except OSError:
        return {}
    if mtime != _cache["mtime"]:
        try:
            with open(SETTINGS_PATH, encoding="utf-8") as f:
                _cache["data"] = json.load(f)
        except (OSError, ValueError):
            _cache["data"] = {}
        _cache["mtime"] = mtime
    return _cache["data"]


STRUCTURAL_DEFAULTS = {"curve_tolerance_mm": 100.0,
                       "curve_points": 10, "curve_type": "polyline",
                       "connect_passes": 10, "surface_tidy_percent": 5.0,
                       "grid_angle_tolerance_deg": 3.0,
                       # a member with no section in its user text joins as if it had this
                       "default_section_mm": {"column": "300 x 300", "beam": "200 x 400",
                                              "brace": "150 x 150", "wall": "t = 200",
                                              "slab": "t = 200", "shell": "t = 200"},
                       # a section text with a larger side than this is a mistake
                       # (a wall read as 't = 4500', its height) - the default is used
                       "max_section_mm": 1000.0,
                       # walls / slabs (core_plates, structural.snap): a void with both
                       # sides longer than this is an opening, smaller ones are filled
                       "opening_min_mm": 300.0,
                       # an edge void larger than this percent of the plate's rectangle
                       # stays in its outline (an L-shaped wall / slab)
                       "corner_void_percent": 25.0,
                       # no panel cut closer than this to another cut or to the edge
                       "min_panel_mm": 500.0,
                       # structural.clean: two wall / slab boundaries in one plane
                       # overlapping by this percent of the smaller = a duplicate
                       "duplicate_overlap_percent": 50.0}

# Per-axis tolerances (mm): {"x", "y", "z"} in the settings file.
STRUCTURAL_XYZ_DEFAULTS = {"base_tolerance_mm": (10.0, 10.0, 10.0),      # structural.connect
                           "flat_tolerance_mm": (100.0, 100.0, 50.0),    # structural.connect
                           "grid_tolerance_mm": (100.0, 100.0, 100.0)}   # structural.grid / snap


def structural(key):
    """A value from the "structural" block of general_settings.json (lengths
    in mm - the caller converts them to document units), else the default."""
    value = (settings().get("structural") or {}).get(key)
    default = STRUCTURAL_DEFAULTS[key]
    if value is None:
        return default
    try:
        return type(default)(value) if not isinstance(default, str) else str(value).lower()
    except (TypeError, ValueError):
        return default


def structural_xyz(key):
    """A per-axis tolerance from the "structural" block, as (x, y, z) in mm.
    Written as {"x": 10, "y": 10, "z": 50}, [10, 10, 50] or one number for all
    three; a missing or unreadable axis keeps its default."""
    default = STRUCTURAL_XYZ_DEFAULTS[key]
    value = (settings().get("structural") or {}).get(key)
    if value is None:
        return default
    if isinstance(value, dict):
        value = [value.get(a, value.get(a.upper())) for a in ("x", "y", "z")]
    elif not isinstance(value, (list, tuple)):
        value = [value] * 3
    out = []
    for i in range(3):
        try:
            out.append(abs(float(value[i])))
        except (TypeError, ValueError, IndexError):
            out.append(default[i])
    return tuple(out)


def set_units(units):
    """The agent's side: the document units, e.g. 'Millimeters'."""
    _state["units"] = units


def unit_key(units=None):
    """'mm' / 'cm' / 'm', or None for any other unit."""
    if units is None:
        units = _state["units"]
    if units is None:
        try:
            import scriptcontext as sc
            units = str(sc.doc.ModelUnitSystem)
        except Exception:
            return None
    return UNIT_KEYS.get(str(units).strip().lower())


def decimals(units=None):
    key = unit_key(units)
    if key is None:
        return OTHER_UNIT_DECIMALS
    value = (settings().get("report_decimals") or {}).get(key, DEFAULT_DECIMALS[key])
    try:
        return max(0, int(value))
    except (TypeError, ValueError):
        return DEFAULT_DECIMALS[key]


def text_height(units=None):
    """Dimension text height in document units, or None (other units:
    leave the document's dimension style alone)."""
    key = unit_key(units)
    if key is None:
        return None
    value = (settings().get("dimension_text_height") or {}).get(key, DEFAULT_TEXT_HEIGHT[key])
    try:
        return float(value) if float(value) > 0 else None
    except (TypeError, ValueError):
        return None


# ---------- rounding ----------

def _whole(value):
    """12.0 -> 12: the '.0' is a token that says nothing."""
    return int(value) if value == int(value) and abs(value) < 1e15 else value


def number(value, digits):
    """Round to ``digits``, but never round a small non-zero value away:
    below 1 it keeps 2 significant digits (0.7071 -> 0.71, 0.001 stays)."""
    value = float(value)
    if value != value or value in (float("inf"), float("-inf")):
        return value
    if abs(value) < NOISE:
        return 0        # -1.3e-13 is floating point noise, not a value
    if abs(value) < 1:
        digits = max(digits, 1 - int(math.floor(math.log10(abs(value)))))
    return _whole(round(value, digits))


def length(value, units=None):
    """A length / coordinate / area for a report."""
    return None if value is None else number(value, decimals(units))


def point(p, units=None):
    """Point3d (or x, y, z sequence) -> [x, y, z] at report precision."""
    xyz = (p.X, p.Y, p.Z) if hasattr(p, "X") else tuple(p)
    d = decimals(units)
    return [number(v, d) for v in xyz]


def text(value, units=None):
    """A length for a label: '300', '0.25' ('{:g}' of the rounded value)."""
    return "{:g}".format(length(value, units))


def _not_length(key):
    key = str(key).lower()
    return any(word in key for word in NOT_LENGTH_WORDS)


def tidy(value, units=None, _digits=None):
    """Every float in a receipt at report precision - dicts and lists too.
    Integers, strings and booleans pass through unchanged."""
    if _digits is None:
        _digits = decimals(units)
    if isinstance(value, bool) or value is None:
        return value
    if isinstance(value, float):
        return number(value, _digits)
    if isinstance(value, dict):
        return dict((k, tidy(v, units, max(_digits, NOT_LENGTH_DECIMALS) if _not_length(k)
                             else _digits)) for k, v in value.items())
    if isinstance(value, (list, tuple)):
        return [tidy(v, units, _digits) for v in value]
    return value
