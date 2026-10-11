"""
vision.capture - screenshot at one of five escalating levels.

The escalation rule from the plan: start at L0 and go up only as far as
needed. Never jump to L4 to answer something L1 could answer - each level
costs more to produce and more to read.

    L0  no image at all. Census, bbox, units, tolerance. Answers most
        questions ("how many columns?", "what units?") for free.
    L1  clean overview of the whole scene. No tags, no clutter. For
        macro orientation.
    L2  filtered objects, isolated or ghosted, with OBJECT tags (#12).
        For identifying specific things in context.
    L3  sub-object detail: curve start/end spheres (green/red), and each
        FACE shaded in its own colour with a matching label. For curve
        direction and face picking. Edge numbers are L4, not L3 - this
        docstring used to say otherwise and a model that asked for L3
        expecting edges got faces and no explanation.
    L4  full topology: each EDGE drawn in its own colour with a matching
        label, plus vertex spheres. For fillet edge picking and boolean
        diagnostics.

The colour pass at L3/L4 is depth-tested, so only components facing the
camera are drawn - which answers "which face am I looking at" without
anyone reading a label. The receipt carries a `legend` mapping each
colour to its component tag.

Every image above L0 ships a SIDECAR tag table, which is the part that
matters: the model reads facts off the table and uses the picture only
for spatial relationships, rather than trying to read pixels.

Layer A (pure logic)  - ``core_vision``, ``core_session``, ``core_view``.
Layer B (Rhino entry) - ``run_vision_capture``.
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
from rhino_tools._shared import tags as tagmod
from rhino_tools._shared import redraw
from rhino_tools._shared import session_store
from rhino_tools.vision_utils import core_vision
from rhino_tools.vision_utils import core_view
from rhino_tools._shared import core_session


# ---------- Pure logic ----------

def sidecar_row(obj_id, level):
    """One row of the tag table that ships with an image.

    Everything here is a FACT about the object - its type, layer, size,
    where it is. The point is that a reader never has to infer any of it
    from the picture."""
    tag = tagmod.tag_of(obj_id)
    bbox = coerce.bbox_of(obj_id)

    row = {
        "tag": tag,
        "guid": str(coerce.to_guid(obj_id)),
        "type": coerce.object_type_name(obj_id),
        "layer": coerce.layer_name_of(obj_id),
        "bbox": coerce.bbox_dict(bbox),
    }

    if bbox is not None and bbox.IsValid:
        size = bbox.Max - bbox.Min
        row["size"] = [float(size.X), float(size.Y), float(size.Z)]
        row["center"] = coerce.xyz(bbox.Center)

    # The colour it is DRAWN in, so "the brown sticks" in a picture can be
    # matched to a layer without numbers.
    rhobj = coerce.find_object(obj_id)
    if rhobj is not None:
        try:
            c = rhobj.Attributes.DrawColor(sc.doc)
            row["color"] = [int(c.R), int(c.G), int(c.B)]
            row["color_name"] = color_name(c.R, c.G, c.B)
        except Exception:
            pass

    curve = coerce.to_curve(obj_id)
    if curve is not None:
        row["length"] = float(curve.GetLength())
        row["closed"] = bool(curve.IsClosed)
        row["start"] = coerce.xyz(curve.PointAtStart)
        row["end"] = coerce.xyz(curve.PointAtEnd)
        return row

    brep = coerce.to_brep(obj_id)
    if brep is not None:
        row["face_count"] = int(brep.Faces.Count)
        row["edge_count"] = int(brep.Edges.Count)
        row["is_solid"] = bool(brep.IsSolid)
        # Which end is up. A bbox cannot say whether a sloped rafter rises
        # toward the ridge or away from it; its lowest and highest corners can.
        points = [v.Location for v in brep.Vertices]
        if points:
            row["low_point"] = coerce.xyz(min(points, key=lambda p: p.Z))
            row["high_point"] = coerce.xyz(max(points, key=lambda p: p.Z))

        if level >= 3:
            row["faces"] = tagmod.face_tags(obj_id)
        if level >= 4:
            row["edges"] = tagmod.edge_tags(obj_id)
            row["vertices"] = tagmod.vertex_tags(obj_id)

    return row


_NAMED_COLOURS = {
    "black": (0, 0, 0), "white": (255, 255, 255), "grey": (128, 128, 128),
    "red": (220, 40, 40), "orange": (240, 140, 30), "yellow": (240, 220, 50),
    "green": (60, 170, 60), "teal": (40, 160, 160), "blue": (50, 110, 220),
    "purple": (140, 70, 190), "pink": (240, 130, 180), "brown": (150, 85, 40),
    "tan": (230, 190, 120), "dark green": (20, 90, 40), "navy": (20, 40, 110),
}


def color_name(r, g, b):
    """The nearest plain colour word - 'brown', 'blue' - for the model to match."""
    return min(_NAMED_COLOURS, key=lambda n: sum(
        (a - c) ** 2 for a, c in zip((r, g, b), _NAMED_COLOURS[n])))


def grid_nodes():
    """[(name, (x, y, z))] - every grid node: where two curves on a grid layer
    cross in plan ('B/2'), at every level found on '<grid>::Z' ('B/2 L2')."""
    from rhino_tools.structural_utils import core_structure, structural_snap
    layers = set()
    for obj in sc.doc.Objects:
        if not obj.IsDeleted and core_structure.on_grid_layer(obj.Id):
            layers.add(sc.doc.Layers[obj.Attributes.LayerIndex].FullPath.split("::")[0])
    out = []
    for layer in sorted(layers):
        curves, levels = structural_snap.read_grid(None, layer)
        for node in structural_snap.curve_nodes(curves, coerce.doc_tol() * 10):
            for k, z in enumerate(levels or [0.0]):
                name = node["name"] + (" L{}".format(k + 1) if levels else "")
                out.append((name, (node["xy"][0], node["xy"][1], z)))
    return out


def location_index():
    """What 'where' is measured against: the grid nodes (read from the grid
    lines themselves - 'B/2 L2', 'C1/R2') and the parts from vision.divide."""
    nodes = []
    try:
        nodes = grid_nodes()
    except Exception:
        pass
    finest = {}
    try:
        from rhino_tools.vision_utils import vision_divide
        for part_id, entry in vision_divide.parts_store().items():
            depth = part_id.count(".")
            for g in entry.get("guids", []):
                if g not in finest or depth > finest[g][0]:
                    finest[g] = (depth, part_id)
    except Exception:
        pass
    return {"nodes": nodes, "parts": dict((g, p) for g, (_d, p) in finest.items())}


def where_of(obj_id, center, index):
    """A location word for an object: the nearest grid node ('near B/2 L2'),
    else the finest part it is in ('in P3.P2'), else None."""
    if index and index["nodes"] and center:
        name, origin = min(index["nodes"], key=lambda n: sum(
            (n[1][i] - center[i]) ** 2 for i in range(3)))
        return "near " + name
    if index and str(coerce.to_guid(obj_id)) in index["parts"]:
        return "in " + index["parts"][str(coerce.to_guid(obj_id))]
    return None


def build_sidecar(obj_ids, level):
    """The full tag table for a capture.

    New numbers are given in SPACE order first, so the objects of one
    region carry a run of close numbers; each row also says WHERE the
    object is, in grid or part terms."""
    guids = coerce.to_guids(obj_ids)
    core_vision.number_in_space_order(guids)
    index = location_index()
    rows = []
    for guid in guids:
        row = sidecar_row(guid, level)
        where = where_of(guid, row.get("center"), index)
        if where:
            row["where"] = where
        rows.append(row)
    return rows


def capture_path(level, folder=None):
    """Where to write the image.

    Numbered per session so successive captures do not overwrite each
    other - comparing capture_0003 with capture_0007 is a normal thing to
    want.

    Default folder is the CONVERSATION's own directory, not the one the
    .3dm sits in. Captures used to pile up beside the model file with
    nothing ever deleting them; now ``session.end`` takes them away with
    the handles they were describing."""
    import tempfile

    counter = sc.sticky.get("rg_capture_counter", 0) + 1
    sc.sticky["rg_capture_counter"] = counter

    if folder is None:
        # The agent's log folder, when the listener was told one.
        folder = sc.sticky.get("nextra_capture_folder")
    if folder:
        try:
            if not os.path.isdir(folder):
                os.makedirs(folder)
        except Exception:
            folder = None
    if folder is None:
        try:
            folder = session_store.ensure_dir()
        except Exception:
            folder = tempfile.gettempdir()

    return os.path.join(folder, "capture_{:04d}_L{}.png".format(counter, level))


MAX_ALL_OBJECTS = 60   # ids="all" numbers at most this many - more is unreadable


def all_visible_ids():
    """Every visible, selectable-type object in the document (for ids="all")."""
    out = []
    for obj in sc.doc.Objects:
        if obj is None or obj.IsDeleted or obj.IsHidden:
            continue
        try:
            if not obj.Visible:
                continue
        except Exception:
            pass
        out.append(obj.Id)
    return out


def write_mapping(image_path, level, viewport, tag_table, legend):
    """The 'json lib' beside every screenshot: number -> guid -> facts.

    capture_0003_L2.png  ->  capture_0003_L2.json

    A model reads '#12' off the picture and looks it up here to get the
    object's guid, type, layer, size and centre - so it never has to guess
    what a label in the image refers to."""
    import json
    mapping_path = os.path.splitext(image_path)[0] + ".json"   # (a .json path stays as is)
    objects = {}
    for row in tag_table or []:
        objects[str(row.get("tag"))] = row
    data = {
        "image": os.path.basename(image_path),
        "level": level,
        "viewport": viewport,
        "how_to_read": "Each label in the image (#12) is a key in 'objects'. "
                       "At level 3/4 faces/edges are coloured - see 'legend'.",
        "objects": objects,
        "legend": legend,
    }
    try:
        with open(mapping_path, "w") as handle:
            json.dump(data, handle, indent=2, default=str)
        return mapping_path
    except Exception:
        return None


def _component_count(guids, level):
    """How many sub-object labels an L3/L4 capture would draw.

    L2 draws one label per OBJECT and no sub-components at all, so it is
    never asked - counting a column's six faces and warning about them
    at a level that does not draw them is a false alarm, and a warning
    that cries wolf gets ignored when it matters."""
    total = 0
    for guid in guids:
        curve = coerce.to_curve(guid)
        if curve is not None:
            total += 2                      # start and end
            continue
        brep = coerce.to_brep(guid)
        if brep is None:
            continue
        total += brep.Faces.Count
        if level >= 4:
            total += brep.Edges.Count + brep.Vertices.Count
    return total


def _part_guids(ids):
    """ids given as part ids from vision.divide ("P3.P2", ["P3.P2", "P3.P4"])
    -> the objects of those parts. None when ids are not part ids.

    This is what NUMBER <part> means: number the objects of that part."""
    import re
    from rhino_tools.vision_utils import vision_divide   # it imports this module
    tokens = ids if isinstance(ids, (list, tuple)) else re.split(r"[\s,]+", str(ids or "").strip())
    tokens = [str(t).strip() for t in tokens if str(t).strip()]
    if not tokens or not all(vision_divide.is_part_id(t) for t in tokens):
        return None
    return coerce.to_guids(vision_divide.part_ids_to_guids(tokens))


# ---------- Tool entry point (MCP-callable) ----------

# ---------- structural scenes ----------

SCENES = ("structure", "structure_solids", "structure_nogrid")
SCENE_DISPLAY = "Ghosted"       # never Shaded: a shaded slab hides the lines under it


def scene_ids(scene):
    """What a structural scene SHOWS, or None (hide nothing):

        structure         member lines + surfaces + the grid (solids hidden)
        structure_nogrid  member lines + surfaces only
        structure_solids  everything - the solids too (ghosted display)"""
    if scene not in SCENES or scene == "structure_solids":
        return None
    from rhino_tools.structural_utils import core_structure
    keep = list(core_structure.members(None)[0])
    if scene == "structure":
        for obj in sc.doc.Objects:
            if obj.IsDeleted:
                continue
            if "grid" in sc.doc.Layers[obj.Attributes.LayerIndex].FullPath.lower():
                keep.append(obj.Id)
    return keep


def tool_vision_capture(level=0, ids=None, viewport="active", mode="isolate",
                        preview=None, path=None, width=None, height=None,
                        zoom=True, folder=None, indices=None,
                        faces=None, edges=None, vertices=None,
                        colour_parts=True, scene=None):
    """Capture at one of the five levels. Returns an R3 receipt.

    ``mode`` decides how non-target objects are treated at L2 and above:
    'isolate' hides them, 'transparent' ghosts them, 'none' leaves the
    scene alone.

    At L3 each requested FACE is shaded in its own colour and at L4 each
    requested EDGE is drawn in its own, with the receipt carrying a
    ``legend`` mapping every colour back to its component tag. Read the
    colour off the image and look it up - that is far more reliable than
    reading a nine-pixel text dot, and because the colour pass is
    depth-tested, only the faces actually facing you get drawn at all.

    ``faces``, ``edges`` and ``vertices`` narrow each kind separately.
    ``indices`` is the older shorthand and still works; it means "these
    components of whichever kind this level draws".

    ``scene`` (structural models): 'structure' shows only the member lines,
    surfaces and grid (the solids they came from hidden), 'structure_nogrid'
    without the grid, 'structure_solids' everything - all in Ghosted
    display unless ``preview`` says otherwise, never Shaded.

    The display state is restored afterwards. A capture that permanently
    hid half the model would be a poor trade for one picture."""
    level = int(level)
    scene = str(scene).lower() if scene else None
    if scene and scene not in SCENES:
        return receipts.err("vision.capture", "scene must be one of {}.".format(", ".join(SCENES)))
    if scene and not preview:
        preview = SCENE_DISPLAY

    # ---- L0: no image at all
    if level <= 0:
        state = core_session.state()
        return receipts.r3(
            None, level=0, tag_table=None,
            object_count=state["object_count"], totals=state["totals"],
            layers=state["layers"],
            hint="Layers and object types only, no image. Escalate to L1 "
                 "for an overview.")

    view = plane_utils.get_view(viewport)
    if view is None and viewport:
        # Names match ignoring case ("perspective"); anything else gets the
        # list of real names, so the next call can pick one.
        for v in sc.doc.Views:
            if v.ActiveViewport.Name.lower() == str(viewport).strip().lower():
                view = v
                break
    if view is None:
        return receipts.err("vision.capture",
                            "No viewport named '{}'.".format(viewport),
                            hint="Viewports in this file: {}.".format(", ".join(
                                sorted(v.ActiveViewport.Name for v in sc.doc.Views))))

    warnings = []
    shown = scene_ids(scene) if scene else None
    if isinstance(ids, str) and ids.lower() == "all":
        # Number EVERY visible object - the whole scene, labelled.
        guids = all_visible_ids()
        if shown is not None:
            keep = set(str(g) for g in shown)
            guids = [g for g in guids if str(g) in keep]
        if len(guids) > MAX_ALL_OBJECTS:
            warnings.append("{} objects - only the first {} are numbered. Pass "
                            "ids=[...] to look at specific ones.".format(
                                len(guids), MAX_ALL_OBJECTS))
            guids = guids[:MAX_ALL_OBJECTS]
        if mode == "isolate":
            mode = "none"        # isolating everything = hiding nothing
    else:
        guids = _part_guids(ids)
        if guids is None:
            guids = coerce.to_guids(ids)

    if level >= 2 and guids:
        # Before tag_objects mints any numbers: new ones go in space order.
        core_vision.number_in_space_order(guids)

    if level >= 2 and not guids:
        return receipts.err(
            "vision.capture",
            "Level {} needs ids - it is a capture OF something.".format(level),
            hint="Pass ids: tags ['#12', '#15'], a part id 'P3.P2' (from vision.divide), or "
                 "ids='all' to number everything visible. level=1 is a whole-scene overview "
                 "without numbers.")

    previous_mode = core_view.current_display_mode(view)
    if preview and not core_view.set_display_mode(view, preview):
        warnings.append("Display mode '{}' not found - captured in '{}'."
                        .format(preview, previous_mode))

    isolated = ghosted = 0
    tag_table = None
    legend = None

    try:
        # ---- the structural scene first: hide what it does not show
        if shown is not None:
            isolated += core_vision.isolate(shown)
        # ---- focus the scene
        if level >= 2:
            if mode == "isolate":
                isolated = core_vision.isolate(guids)
            elif mode in ("transparent", "ghost"):
                ghosted = core_vision.ghost(guids)

            core_vision.tag_objects(
                guids, level, indices=indices, faces=faces, edges=edges,
                vertices=vertices, colour_parts=colour_parts)

            # L3/L4 without a narrowing is the request that produces an
            # unreadable image. A 400x400 column seen whole puts six
            # face, twelve edge and eight vertex labels inside a few
            # dozen pixels; the conduit spreads what it can and drops
            # the rest, but no placement makes twenty-six labels legible
            # in that space. Narrowing is the answer, so the receipt
            # says so with the actual count rather than leaving the
            # caller to wonder why the picture is a pile.
            narrowed = (indices is not None or faces is not None
                        or edges is not None or vertices is not None)
            if not narrowed and level >= 3:
                crowded = _component_count(guids, level)
                if crowded > 12:
                    warnings.append(
                        "{} sub-components labelled across {} object(s) - "
                        "expect overlapping labels. Pass faces=[...] or "
                        "edges=[...] to draw only the ones you are asking "
                        "about; the tag_table below lists them all by name "
                        "either way. The colours stay readable when the "
                        "labels do not.".format(crowded, len(guids)))
            tag_table = build_sidecar(guids, level)
            if colour_parts and level >= 3:
                legend = core_vision.component_legend(
                    guids, level, faces=faces, edges=edges, indices=indices)

            if zoom:
                core_view.zoom_to(view, guids)
        elif zoom:
            core_view.zoom_to(view, shown or None)

        # ---- take the picture
        if path is None:
            path = capture_path(level, folder)

        written, warning = core_view.capture_to_file(view, path, width, height)
        if written is None:
            return receipts.err("vision.capture", warning, warnings=warnings)

    finally:
        # ---- always put the scene back
        if level >= 2:
            core_vision.untag_objects(guids)
            if mode == "isolate":
                core_vision.restore_visibility()
            elif mode in ("transparent", "ghost"):
                core_vision.unghost()
        if shown is not None:
            core_vision.restore_visibility()
        if preview and previous_mode:
            core_view.set_display_mode(view, previous_mode)
        redraw.request()

    if level >= 3:
        warnings.append("L3+ is expensive to produce and to read - drop back "
                        "to L2 once the sub-objects are identified.")

    mapping_ref = None
    if tag_table:
        mapping_ref = write_mapping(written, level, viewport, tag_table, legend)

    return receipts.r3(
        written,
        mapping_ref=mapping_ref,
        viewport=plane_utils.view_info(view),
        preview=core_view.current_display_mode(view),
        level=level,
        focus_ids=guids if level >= 2 else None,
        focus_mode=(mode if level >= 2 else None),
        scene=scene,
        tag_table=tag_table,
        legend=legend,
        warnings=warnings,
        hint=(("Each face is shaded in its own colour - read the colour off "
               "the image and look it up in `legend`. Only faces you can "
               "actually see are drawn, so a colour in the picture is a "
               "face pointing at the camera." if legend else
               "Read the tag table for facts; use the image only for spatial "
               "relationships.") if tag_table else
              "Overview only - no tags. Escalate to L2 to identify objects."))


MAX_MAP_OBJECTS = 20000       # a tower is easily 3-5k members - map them all


def tool_vision_map(ids="all", level=2, limit=MAX_MAP_OBJECTS, path=None):
    """The number -> object table WITHOUT a screenshot. Returns an R2 receipt.

    Every object (or `ids`) gets its '#12' number and a row: guid, type,
    layer, bbox, size, centre (+ faces at level 3, edges at level 4). The
    full table is written to a JSON file (`mapping_ref`) - the agent's
    mapping.py reads that file and lets the model QUERY it (by layer,
    type, number, position...) instead of reading every row.

    Cheap compared to a capture: no view changes, no image."""
    if isinstance(ids, str) and ids.lower() == "all":
        guids = all_visible_ids()
    else:
        guids = coerce.to_guids(ids)
    total = len(guids)
    guids = guids[:int(limit)]
    rows = build_sidecar(guids, int(level))

    if path is None:
        folder = sc.sticky.get("nextra_capture_folder")
        if not folder:
            import tempfile
            folder = tempfile.gettempdir()
        if not os.path.isdir(folder):
            os.makedirs(folder)
        path = os.path.join(folder, "scene_map.json")
    mapping_ref = write_mapping(path, int(level), None, rows, None)

    warnings = []
    if total > len(guids):
        warnings.append("{} objects - only the first {} mapped.".format(total, len(guids)))
    return receipts.r2(
        "map", len(rows), mapping_ref=mapping_ref, count=len(rows),
        warnings=warnings,
        hint="Rows are in the JSON file at mapping_ref, keyed by '#number'.")


# ---------- Rhino execution wrapper ----------

def run_vision_capture():
    """Pick a level, and objects if the level needs them."""
    level = rs.GetInteger("Capture level (0 to 4)", 1, 0, 4)
    if level is None:
        return None

    if level == 0:
        return receipts.echo(tool_vision_capture(0))

    obj_ids = None
    mode = "isolate"

    if level >= 2:
        obj_ids = rs.GetObjects("Select the objects to capture",
                                preselect=True)
        if not obj_ids:
            print("Level {} needs a selection.".format(level))
            return None

        go = Rhino.Input.Custom.GetOption()
        go.SetCommandPrompt("Treatment of everything else")
        idx_isolate = go.AddOption("Isolate")
        idx_ghost = go.AddOption("Transparent")
        go.AddOption("Leave")
        go.AcceptNothing(True)

        result = go.Get()
        if result == Rhino.Input.GetResult.Option:
            chosen = go.Option().Index
            mode = ("isolate" if chosen == idx_isolate else
                    "transparent" if chosen == idx_ghost else "none")

    return receipts.echo(tool_vision_capture(level, obj_ids, mode=mode))


# From here is only required just to run the above functions in Rhino.

if __name__ == "__main__":
    run_vision_capture()
