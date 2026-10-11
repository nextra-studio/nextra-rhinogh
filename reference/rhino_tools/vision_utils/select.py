"""
select - stackable object filters.

Filter objects by type, layer, name, user text, bounding box, plane
proximity, radius, or tag. All filters combine with AND, so
"solids on layer STR_Columns within 5m of this point" is one call.

This is how every other tool gets its input when driven over MCP: there
is no ``GetObjects`` on that side, so ``select`` is the substitute, and
its output is a list of '#12' tags that any other tool accepts directly
as ``ids``.

Layer A (pure logic)  - ``core_select``.
Layer B (Rhino entry) - ``run_select``.
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

from rhino_tools._shared import coerce, receipts
from rhino_tools._shared import planes as plane_utils
from rhino_tools._shared import digest
from rhino_tools.vision_utils import core_select


# ---------- Pure logic ----------

def describe_filters(filters):
    """Human-readable echo of what was actually filtered on.

    Worth returning: a filter key the caller misspelled is ignored
    silently by ``core_select``, and seeing it missing from this list is
    how they find out."""
    known = ("type", "layer", "name", "attribute_key", "attribute_value",
             "bbox", "bbox_mode", "plane", "plane_distance", "plane_mode",
             "near_point", "radius", "tag", "selected")
    applied = {k: v for k, v in (filters or {}).items()
               if k in known and v is not None}
    ignored = [k for k in (filters or {}) if k not in known]
    return applied, ignored


def group_by_layer(obj_ids):
    """Breakdown of a result set by layer - context for the receipt.

    Cheap, and it usually answers the follow-up question ("are these all
    on the layer I expected?") without a second call."""
    counts = {}
    for guid in coerce.to_guids(obj_ids):
        layer = coerce.layer_name_of(guid) or "(unknown)"
        counts[layer] = counts.get(layer, 0) + 1
    return counts


def group_by_type(obj_ids):
    """Breakdown of a result set by object type."""
    counts = {}
    for guid in coerce.to_guids(obj_ids):
        kind = coerce.object_type_name(guid)
        counts[kind] = counts.get(kind, 0) + 1
    return counts


# ---------- Tool entry point (MCP-callable) ----------

def tool_select(filters=None, highlight=False, include_hidden=False,
                limit=None, offset=0,
                include_locked=False, count_only=False):
    """Run the filters and return an R5 receipt.

    ``highlight`` is opt-in. A selection used purely to feed another tool
    should not disturb whatever the user had highlighted in the UI, so
    the side effect only happens when asked for."""
    filters = filters or {}
    applied, ignored = describe_filters(filters)

    try:
        found = core_select.select(filters, include_hidden, include_locked)
    except Exception as exc:
        return receipts.err("select", "Filter failed: {}".format(exc))

    warnings = []
    if ignored:
        warnings.append("Ignored unknown filter key(s): {}".format(
            ", ".join(sorted(ignored))))
    if not found:
        warnings.append("No objects matched. Widen the filters or check "
                        "the layer name spelling.")

    highlighted = 0
    if highlight and found:
        highlighted = core_select.apply_document_selection(found)

    receipt = receipts.r5(
        found,
        filters=applied,
        by_layer=group_by_layer(found),
        by_type=group_by_type(found),
        highlighted=highlighted,
        warnings=warnings,
        hint="Pass these ids straight into another tool.")

    # ``count``, ``by_layer`` and ``by_type`` are computed from the FULL
    # result before this, so the summary stays true however much of the
    # id list is shown. That matters: a selection of 1,500 objects is
    # usually being asked "how many, and where" rather than "name them
    # all", and the counts answer that for nothing while the tags cost
    # about eight characters each.
    digest.apply(receipt, "ids", limit=limit or digest.DEFAULT_LIMIT,
                 offset=offset, tool="select", summarise_points=False)
    if count_only:          # "how many, and where" - the counts without the id list
        receipt["ids"] = []
    return receipt


# ---------- Rhino execution wrapper ----------

def run_select():
    """Build a filter interactively from a few common questions.

    Deliberately not exhaustive - the full filter set is meant for MCP.
    This wrapper covers the three filters a person actually types at a
    command line, and leaves the rest to the tool function."""
    type_text = rs.GetString(
        "Type filter (curve/surface/solid/point/mesh/block, Enter for any)", "")
    layer_text = rs.GetString("Layer filter (wildcards allowed, Enter for any)", "")
    name_text = rs.GetString("Name filter (wildcards allowed, Enter for any)", "")

    filters = {}
    if type_text and type_text.strip():
        filters["type"] = [t.strip() for t in type_text.split(",") if t.strip()]
    if layer_text and layer_text.strip():
        filters["layer"] = layer_text.strip()
    if name_text and name_text.strip():
        filters["name"] = name_text.strip()

    return receipts.echo(tool_select(filters, highlight=True))


# From here is only required just to run the above functions in Rhino.

if __name__ == "__main__":
    run_select()
