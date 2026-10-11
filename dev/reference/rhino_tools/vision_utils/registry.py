"""
Vision registry - tool name -> callable.

Imports only the ``tool_*`` functions, never the ``run_*`` wrappers: the
MCP side has no interactive session to prompt against, so it passes ids
and data in directly.

``select`` lives here too: finding objects is part of looking at them.
"""

from . import vision_capture
from . import vision_divide
from . import vision_tag
from . import vision_resolve_tag
from . import vision_isolate
from . import vision_ghost
from . import vision_zoom
from . import vision_show_points
from . import plane_push_to_views
from . import vision_review
from . import select as select_tool


CATEGORY = "vision"

TOOLS = {
    "select": select_tool.tool_select,
    "vision.capture": vision_capture.tool_vision_capture,
    "vision.map": vision_capture.tool_vision_map,
    "vision.divide": vision_divide.tool_vision_divide,
    "vision.parts": vision_divide.tool_vision_parts,
    "vision.review": vision_review.tool_vision_review,
    "vision.tag": vision_tag.tool_vision_tag,
    "vision.tag.clear": vision_tag.tool_vision_untag,
    "vision.tag.level": vision_tag.tool_vision_tag_level,
    "vision.tag.list": vision_tag.tool_vision_tag_list,
    "vision.resolve_tag": vision_resolve_tag.tool_vision_resolve_tag,
    "vision.list_subobjects": vision_resolve_tag.tool_vision_list_subobjects,
    "vision.isolate": vision_isolate.tool_vision_isolate,
    "vision.isolate.restore": vision_isolate.tool_vision_restore,
    "vision.ghost": vision_ghost.tool_vision_ghost,
    "vision.ghost.clear": vision_ghost.tool_vision_unghost,
    "vision.clear_all": vision_ghost.tool_vision_clear_all,
    "vision.zoom": vision_zoom.tool_vision_zoom,
    "vision.show_points": vision_show_points.tool_vision_show_points,
    "vision.show_points.hide": vision_show_points.tool_vision_hide_points,
    "plane.push_to_views": plane_push_to_views.tool_plane_push_to_views,
}
