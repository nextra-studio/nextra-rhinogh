"""
vision.divide - cut a scope into parts, draw them, take the pictures.

The first step of looking at a big model. One picture with #1..#800 is
unreadable; one picture with eight coloured boxes is not:

    vision.divide("all")        -> P1..P8, a picture per view
    vision.divide("P3")         -> P3.P1..P3.P8
    vision.divide("P3.P2 P3.P4")-> P3.P2+P4.P1 ...  (two parts together)

until a part holds about ``part_limit`` objects, when the receipt says
NUMBER - and ``vision.capture`` level 2 numbers the objects one by one.

What the picture shows
----------------------
Each part is a SEE-THROUGH coloured box with one label ('P3.P2 · 45').
The objects are NOT recoloured: an object belongs to every part it
touches (see ``core_divide``), so it cannot wear one part's colour.
Everything outside the scope is, by ``rest``:
    "fade" (default) ghosted - still there for context, and nothing looks
           "missing" (a hidden house once was reported as missing)
    "hide" hidden - only the focused part(s), when the rest is clutter
    "show" left as it is - when the rest matters as much as the part

What it leaves behind
---------------------
The part map, in sticky (so the next call can say scope="P3") and in a
JSON file beside the first picture (``mapping_ref``): part -> the tags
and guids of its objects, its box, its parent. Each object row there
also says which parts it is in, so the agent's object map can answer
``map_query(part="P3.P2")``.

by="layer" makes one part per layer instead of cutting space - useful
when the layers mean something, useless on an import where they do not.

Layer A (pure logic)  - ``core_divide``.
Layer B (Rhino entry) - ``run_vision_divide``.
"""

import json
import os
import re
import sys

try:
    _HERE = os.path.dirname(os.path.abspath(__file__))
except NameError:
    _HERE = os.getcwd()
_PARENT = os.path.dirname(os.path.dirname(_HERE))
if _PARENT not in sys.path:
    sys.path.insert(0, _PARENT)

import System.Drawing
import rhinoscriptsyntax as rs
import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

from rhino_tools._shared import coerce, precision, receipts, redraw
from rhino_tools._shared import planes as plane_utils
from rhino_tools._shared import tags as tagmod
from rhino_tools.vision_utils import core_divide
from rhino_tools.vision_utils import core_vision
from rhino_tools.vision_utils import vision_capture
from rhino_tools.vision_utils import core_view


_PARTS = "rg_vision_parts"            # sticky: part id -> {"guids", "box", "parent", "layer"}
_CONDUIT = "rg_vision_part_conduit"   # sticky: the live PartConduit, so a crash can be cleaned up

DEFAULT_VIEWS = ("Perspective", "Top", "Front")
PART_LIMIT = 20
REST_MODES = ("fade", "hide", "show")  # what happens to everything outside the scope
EXACT_SAMPLES = 48                    # points per curve for the exact touching test

# Part ids: P3, P3.P2, P3.P2+P4.P1, [Beams].P1
PART_ID = re.compile(r"^(\[[^\]]*\]\.)?P\d+([.+]P\d+)*$")

# Fixed, nameable colours: the model (or the eyes) can say "the blue box".
PALETTE = [
    ("red", (220, 50, 50)), ("blue", (50, 110, 220)), ("green", (50, 160, 70)),
    ("orange", (240, 140, 30)), ("purple", (140, 70, 190)), ("teal", (30, 160, 160)),
    ("pink", (235, 110, 170)), ("brown", (150, 90, 40)),
]


def colour_for(index):
    """(name, System colour) for the index-th part (0-based)."""
    if index < len(PALETTE):
        name, (r, g, b) = PALETTE[index]
        return name, System.Drawing.Color.FromArgb(r, g, b)
    c = core_vision.generate_color(index)
    return vision_capture.color_name(c.R, c.G, c.B), c


# ---------- boxes: Rhino <-> core_divide tuples ----------

def to_tuple(bbox):
    if bbox is None or not bbox.IsValid:
        return None
    return ((bbox.Min.X, bbox.Min.Y, bbox.Min.Z), (bbox.Max.X, bbox.Max.Y, bbox.Max.Z))


def to_bbox(box):
    return rg.BoundingBox(rg.Point3d(*box[0]), rg.Point3d(*box[1]))


def _round(values):
    """At report precision (settings/general_settings.json) - whole mm in a
    mm model, but not whole METRES in a metre model."""
    return [precision.length(v) for v in values]


# ---------- the part store ----------

def parts_store():
    if _PARTS not in sc.sticky:
        sc.sticky[_PARTS] = {}
    return sc.sticky[_PARTS]


def is_part_id(text):
    return bool(PART_ID.match(str(text).strip()))


# ---------- what to divide ----------

def _layer_objects(name):
    """Objects on a layer (full path or last name) and its sublayers."""
    wanted = name.strip().lower()
    out = []
    for obj in vision_capture.all_visible_ids():
        path = (coerce.layer_name_of(obj) or "").lower()
        if (path == wanted or path.endswith("::" + wanted)
                or path.startswith(wanted + "::") or ("::" + wanted + "::") in path):
            out.append(obj)
    return out


def resolve_scope(scope):
    """scope -> (guids, region box or None, child-id prefix, label, error).

    ``region`` is set for a part scope: the next cut stays INSIDE the
    part's box, even though a long beam in it reaches far beyond."""
    if scope is None:
        scope = "all"
    tokens = scope if isinstance(scope, (list, tuple)) else re.split(r"[\s,]+", str(scope).strip())
    tokens = [str(t).strip() for t in tokens if str(t).strip()]

    if not tokens or (len(tokens) == 1 and tokens[0].lower() == "all"):
        return vision_capture.all_visible_ids(), None, "", "all", None

    if len(tokens) == 1 and tokens[0].lower().startswith("layer:"):
        name = tokens[0].split(":", 1)[1]
        guids = _layer_objects(name)
        if not guids:
            return [], None, "", name, "No visible objects on layer '{}'.".format(name)
        return guids, None, "[{}]".format(name), "layer " + name, None

    part_ids = [t for t in tokens if is_part_id(t)]
    if part_ids:
        if len(part_ids) != len(tokens):
            return [], None, "", "", "Mix of part ids and other ids - pass one kind."
        store = parts_store()
        unknown = [p for p in part_ids if p not in store]
        if unknown:
            known = sorted(store.keys())[:12]
            return [], None, "", "", ("Unknown part(s): {}. Known: {}".format(
                ", ".join(unknown), ", ".join(known) or "none - run vision.divide('all') first"))
        guids, seen = [], set()
        for p in part_ids:
            for g in store[p]["guids"]:
                if g not in seen and coerce.find_object(g) is not None:
                    seen.add(g)
                    guids.append(coerce.to_guid(g))
        region = core_divide.box_union([tuple(map(tuple, store[p]["box"])) for p in part_ids])
        return guids, region, core_divide.scope_prefix(part_ids), " + ".join(part_ids), None

    guids = coerce.to_guids(tokens)
    if not guids:
        return [], None, "", "", "No objects resolved from {}.".format(
            coerce.describe_input(scope))
    return guids, None, "[sel]", "{} object(s)".format(len(guids)), None


# ---------- the exact touching test ----------

def _curve_points(curve):
    ok, polyline = curve.TryGetPolyline()
    if ok:
        return [(p.X, p.Y, p.Z) for p in polyline]
    params = curve.DivideByCount(EXACT_SAMPLES, True) or []
    points = [curve.PointAt(t) for t in params] or [curve.PointAtStart, curve.PointAtEnd]
    return [(p.X, p.Y, p.Z) for p in points]


def _inside(point, box, tol):
    return all(box[0][i] - tol <= point[i] <= box[1][i] + tol for i in range(3))


def make_exact(tol):
    """exact(guid, cell box) -> does the geometry itself enter the cell?

    Curves: their polyline against the box. Breps / meshes: the closest
    point to the cell centre lies in the cell, or a vertex does, or an
    edge crosses it - that catches a slab passing through the middle of a
    cell with no edge or corner in it. Anything else keeps the bbox answer."""
    cache = {}

    def exact(guid, box):
        key = str(guid)
        if key not in cache:
            geometry = coerce.geometry_of(guid)
            entry = None
            if isinstance(geometry, rg.Point):
                entry = ("points", [(geometry.Location.X, geometry.Location.Y, geometry.Location.Z)])
            elif isinstance(geometry, rg.Curve):
                entry = ("polyline", _curve_points(geometry))
            elif isinstance(geometry, rg.Mesh):
                entry = ("mesh", geometry)
            else:
                brep = coerce.to_brep(guid)
                if brep is not None:
                    edges = [_curve_points(e.DuplicateCurve()) for e in brep.Edges]
                    entry = ("brep", (brep, edges))
            cache[key] = entry
        entry = cache[key]
        if entry is None:
            return True
        kind, data = entry
        if kind == "points":
            return any(_inside(p, box, tol) for p in data)
        if kind == "polyline":
            return core_divide.polyline_hits_box(data, box, tol)

        centre = rg.Point3d(*core_divide.box_center(box))
        if kind == "mesh":
            cp = data.ClosestPoint(centre)
            if cp.IsValid and _inside((cp.X, cp.Y, cp.Z), box, tol):
                return True
            return any(_inside((v.X, v.Y, v.Z), box, tol) for v in data.Vertices)

        brep, edges = data
        cp = brep.ClosestPoint(centre)
        if cp.IsValid and _inside((cp.X, cp.Y, cp.Z), box, tol):
            return True
        if any(_inside((v.Location.X, v.Location.Y, v.Location.Z), box, tol)
               for v in brep.Vertices):
            return True
        return any(core_divide.polyline_hits_box(pts, box, tol) for pts in edges)

    return exact


# ---------- the display ----------

class PartConduit(Rhino.Display.DisplayConduit):
    """See-through coloured boxes, one label each. Objects are left alone.

    Boxes in PostDrawObjects (depth-tested, drawn after the objects so they
    tint rather than hide); labels in DrawForeground, placed in SCREEN
    space and nudged apart - in the Top view the bottom and top cells of a
    column of parts fall on the same spot, and two labels on one spot
    read as one."""

    TRANSPARENCY = 0.82
    MIN_GAP_PX = 26

    def __init__(self, parts):
        super(PartConduit, self).__init__()
        self.parts = []
        boxes = []
        for p in parts:
            bbox = to_bbox(p["box"])
            boxes.append(bbox)
            brep = rg.Brep.CreateFromBox(bbox) if _has_volume(bbox) else None
            self.parts.append({
                "bbox": bbox, "brep": brep, "colour": p["colour"],
                "material": Rhino.Display.DisplayMaterial(p["colour"], self.TRANSPARENCY),
                "label": p["label"], "anchor": bbox.Center,
            })
        # The static Union: the instance one mutates a .NET struct in place,
        # which from Python can land on a copy and change nothing.
        self.extent = boxes[0] if boxes else rg.BoundingBox.Empty
        for b in boxes[1:]:
            self.extent = rg.BoundingBox.Union(self.extent, b)

    def CalculateBoundingBox(self, e):
        # Without this the boxes are clipped by the view's near/far planes.
        if self.extent.IsValid:
            e.IncludeBoundingBox(self.extent)

    def PostDrawObjects(self, e):
        for p in self.parts:
            if p["brep"] is not None:
                e.Display.DrawBrepShaded(p["brep"], p["material"])
            e.Display.DrawBox(p["bbox"], p["colour"], 2)

    def DrawForeground(self, e):
        placed = []
        for p in self.parts:
            screen = e.Viewport.WorldToClient(p["anchor"])
            x, y = float(screen.X), float(screen.Y)
            for _ in range(12):
                if all(abs(x - px) > 70 or abs(y - py) > self.MIN_GAP_PX for px, py in placed):
                    break
                y += self.MIN_GAP_PX
            placed.append((x, y))
            e.Display.DrawDot(x, y, p["label"], p["colour"], System.Drawing.Color.White)


def _has_volume(bbox):
    d = bbox.Diagonal
    return d.X > 0 and d.Y > 0 and d.Z > 0


def _clear_conduit():
    conduit = sc.sticky.pop(_CONDUIT, None)
    if conduit is not None:
        try:
            conduit.Enabled = False
        except Exception:
            pass


def resolve_views(views, warnings):
    """[(name, view)] for the views asked for.

    A model passed views="Perspective" - a string - and it was iterated
    letter by letter ("No viewport named 'P'"), and "perspective" in lower
    case matched nothing. So: a string is split, names match ignoring case,
    unknown names are skipped with a warning, and if nothing is left the
    default views are used rather than failing."""
    if isinstance(views, str):
        views = [v for v in re.split(r"[\s,;]+", views) if v]
    wanted = list(views or DEFAULT_VIEWS)
    existing = dict((v.ActiveViewport.Name.lower(), v) for v in sc.doc.Views)
    out = []
    for name in wanted:
        view = existing.get(str(name).lower())
        if view is None:
            warnings.append("No viewport named '{}' (there are: {}) - skipped.".format(
                name, ", ".join(sorted(v.ActiveViewport.Name for v in sc.doc.Views))))
        elif view.ActiveViewport.Name not in [n for n, _v in out]:
            out.append((view.ActiveViewport.Name, view))
    if not out:
        warnings.append("None of the views asked for exist - used {}.".format(", ".join(DEFAULT_VIEWS)))
        out = [(n, existing[n.lower()]) for n in DEFAULT_VIEWS if n.lower() in existing]
    return out


def _image_path(view_name, folder=None):
    """capture_0007_parts_Top.png - the 'capture_' prefix lets the agent's
    image log pick up pictures a script took."""
    base = vision_capture.capture_path(0, folder)
    return base[:-len("_L0.png")] + "_parts_{}.png".format(view_name)


# ---------- building the parts ----------

def number_range(guids):
    """'#120-#135' - the span of '#' numbers in a part. With space-ordered
    numbering most of a part sits in one run, so the model can say 'around
    #130' and mean this part."""
    numbers = sorted(int(t.lstrip("#")) for t in tagmod.tags_of(guids, create=True))
    if not numbers:
        return None
    return "#{}".format(numbers[0]) if len(numbers) == 1 else "#{}-#{}".format(numbers[0], numbers[-1])


def _layer_counts(guids, top=3):
    counts = {}
    for g in guids:
        name = coerce.layer_name_of(g) or "?"
        counts[name] = counts.get(name, 0) + 1
    ranked = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
    return [[name, n] for name, n in ranked[:top]]


def by_space(items, region, split, tol, exact_on):
    """Cut the region into up to 8 cells -> [(number, box, [guid strings])], belongs."""
    counts = core_divide.split_counts(region)
    centers = [core_divide.box_center(b) for _, b in items]
    cuts = core_divide.cut_values(region, counts, centers, split)
    cell_list = core_divide.cells(cuts)
    exact = make_exact(tol) if exact_on else None
    members, belongs = core_divide.assign(items, cell_list, exact, tol)
    cell_box = dict((n, box) for n, box, _ in cell_list)
    out = [(n, cell_box[n], members[n]) for n in sorted(members)]
    return out, belongs, counts


def by_layer(items):
    """One part per layer, biggest first."""
    groups = {}
    for key, box in items:
        groups.setdefault(coerce.layer_name_of(key) or "?", []).append((key, box))
    ranked = sorted(groups.items(), key=lambda kv: (-len(kv[1]), kv[0]))
    out, belongs = [], {}
    for i, (layer, rows) in enumerate(ranked):
        n = i + 1
        box = core_divide.box_union([b for _, b in rows])
        out.append((n, box, [k for k, _ in rows], layer))
        for k, _ in rows:
            belongs[k] = [n]
    return out, belongs


# ---------- Tool entry point (MCP-callable) ----------

def tool_vision_divide(scope="all", by="space", split="middle", part_limit=PART_LIMIT,
                       views=DEFAULT_VIEWS, exact=True, rest="fade",
                       width=None, height=None, folder=None, fade_rest=None):
    """Divide a scope into parts and picture them. Returns an R3 receipt.

    ``scope``      "all", part ids ("P3" or "P3.P2 P3.P4"), "layer:Beams",
                   or object ids ('#12', guids).
    ``by``         "space" (cells, default) or "layer" (one part per layer).
    ``split``      "middle" (halve) or "balanced" (cut at the median centre).
    ``part_limit`` a part with this many objects or fewer is marked NUMBER.
    ``views``      one picture per view.
    ``rest``       what happens to everything OUTSIDE the scope while the
                   pictures are taken: "fade" (default), "hide" or "show".
                   ``fade_rest=False`` is the old spelling of rest="show".
    """
    if fade_rest is not None and rest == "fade":
        rest = "fade" if fade_rest else "show"
    rest = str(rest or "fade").lower()
    if rest not in REST_MODES:
        return receipts.err("vision.divide", "rest must be one of {}.".format(", ".join(REST_MODES)))
    part_limit = int(part_limit or PART_LIMIT)
    by = str(by or "space").lower()
    guids, region, prefix, label, error = resolve_scope(scope)
    if error:
        return receipts.err("vision.divide", error,
                            hint="scope='all', 'P3', 'P3.P2 P3.P4', 'layer:Beams' or ['#12', ...]")
    if not guids:
        return receipts.err("vision.divide", "Nothing to divide - the scope is empty.")

    items = []
    for g in guids:
        box = to_tuple(coerce.bbox_of(g))
        if box is not None:
            items.append((str(g), box))
    if not items:
        return receipts.err("vision.divide", "None of the objects has a usable bounding box.")
    # Number new objects in space order BEFORE anything mints a tag, so each
    # part below holds a run of close numbers (its 'numbers' column).
    core_vision.number_in_space_order(guids)

    union = core_divide.box_union([b for _, b in items])
    region = core_divide.box_intersection(region, union) if region else union
    if region is None:
        region = union
    tol = max(coerce.doc_tol(), 1e-6)

    warnings = []
    layers_of = {}
    if by == "layer":
        cut, belongs = by_layer(items)
        counts = None
        for n, _box, _keys, layer in cut:
            layers_of[n] = layer
        cut = [(n, box, keys) for n, box, keys, _ in cut]
        if prefix == "":
            prefix = "[layers]"
    else:
        cut, belongs, counts = by_space(items, region, split, tol, exact)
        if len(cut) == 1 and len(items) > part_limit:
            warnings.append("Everything fell into one part - the scope may be one big object "
                            "or a tight cluster. Try split='balanced' or by='layer'.")

    # ---- the parts, stored for the next call
    store = parts_store()
    parts, table = [], []
    for index, (n, box, keys) in enumerate(cut):
        part_id = core_divide.child_id(prefix, n)
        colour_name, colour = colour_for(index)
        shared = sum(1 for k in keys if len(belongs.get(k, [])) > 1)
        store[part_id] = {"guids": list(keys), "box": [list(box[0]), list(box[1])],
                          "parent": prefix, "layer": layers_of.get(n)}
        count = len(keys)
        parts.append({"box": box, "colour": colour,
                      "label": "{} ({})".format(part_id, count)})
        row = {
            "part": part_id,
            "colour": colour_name,
            "objects": count,
            # The part's OWN objects: shared ones (a slab over four parts)
            # carry numbers from wherever they were first numbered and
            # would stretch the range over half the model.
            "numbers": number_range([k for k in keys if len(belongs.get(k, [])) == 1] or keys),
            "shared": shared,
            "layers": _layer_counts(keys),
            "size": _round(core_divide.box_size(box)),
            "center": _round(core_divide.box_center(box)),
            "next": "NUMBER" if count <= part_limit else "FOCUS",
        }
        if n in layers_of:
            row["layer"] = layers_of[n]
        table.append(row)

    # ---- the pictures
    images = []
    conduit = None
    _clear_conduit()
    try:
        if rest == "fade":
            core_vision.ghost(guids)
        elif rest == "hide":
            core_vision.isolate(guids)
        conduit = PartConduit(parts)
        conduit.Enabled = True
        sc.sticky[_CONDUIT] = conduit
        zoom_box = to_bbox(core_divide.box_union([p["box"] for p in parts]) or region)
        for name, view in resolve_views(views, warnings):
            core_view.zoom_to(view, bbox=zoom_box)
            sc.doc.Views.Redraw()
            written, warning = core_view.capture_to_file(view, _image_path(name, folder),
                                                         width, height)
            if written is None:
                warnings.append("{}: {}".format(name, warning))
                continue
            images.append({"view": name, "path": written})
    finally:
        _clear_conduit()
        if rest == "fade":
            core_vision.unghost()
        elif rest == "hide":
            core_vision.restore_visibility()
        redraw.request()

    if not images:
        return receipts.err("vision.divide", "No picture could be taken.", warnings=warnings)

    # ---- the part map file (the agent reads it into its object map)
    mapping_ref = write_parts_file(images[0]["path"], label, prefix, table, belongs, store)

    small = [r["part"] for r in table if r["next"] == "NUMBER"]
    hint = ("Pick the part(s) that matter: FOCUS <part ids> divides again, NUMBER <part id> "
            "numbers its objects (vision.capture level 2 with ids=the part's objects). "
            "Outside the part: rest='fade' (default, kept faint), 'hide' (only the part - when "
            "the rest is clutter) or 'show' (when the rest matters too). ")
    if small:
        hint += "Small enough to number now: {}.".format(", ".join(small))
    if len(items) <= part_limit:
        hint = ("The whole scope is only {} object(s) - number it directly (NUMBER) instead of "
                "dividing further.".format(len(items)))

    return receipts.r3(
        images[0]["path"],
        level="parts",
        images=images,
        scope=label,
        rest=rest,
        by=by,
        cells=counts,
        total_objects=len(items),
        parts=table,
        part_limit=part_limit,
        mapping_ref=mapping_ref,
        warnings=warnings,
        hint=hint)


def write_parts_file(image_path, label, prefix, table, belongs, store):
    """capture_0007_parts_Perspective.png -> capture_0007_parts.json

    Holds the parts (with the TAGS of their objects, which is what the model
    uses) and one row per object - the same rows as vision.map - with the
    parts it belongs to, so the agent's object map can filter by part."""
    path = re.sub(r"_parts_[^_\\/]+\.png$", "_parts.json", image_path)
    if path == image_path:
        path = os.path.splitext(image_path)[0] + ".json"
    parts = {}
    for row in table:
        stored = store[row["part"]]
        parts[row["part"]] = dict(row, parent=stored["parent"],
                                  box=stored["box"],
                                  tags=tagmod.tags_of(stored["guids"], create=True),
                                  guids=stored["guids"])
    objects = {}
    index = vision_capture.location_index()
    for key, numbers in belongs.items():
        sidecar = vision_capture.sidecar_row(key, 2)
        sidecar["parts"] = [core_divide.child_id(prefix, n) for n in numbers]
        where = vision_capture.where_of(key, sidecar.get("center"), index)
        if where:
            sidecar["where"] = where
        objects[str(sidecar.get("tag"))] = sidecar
    data = {"image": os.path.basename(image_path), "level": "parts", "scope": label,
            "prefix": prefix, "parts": parts, "objects": objects,
            "how_to_read": "Each coloured box in the pictures is a part (label 'P3.P2 (45)'). "
                           "'parts' lists each part's objects by tag; an object touching "
                           "several boxes is in each of them."}
    try:
        with open(path, "w") as handle:
            json.dump(data, handle, indent=1, default=str)
        return path
    except Exception:
        return None


def tool_vision_parts(scope=None):
    """List the parts made so far (or the children of one), no pictures."""
    store = parts_store()
    rows = []
    for part_id in sorted(store.keys()):
        entry = store[part_id]
        if scope and entry["parent"] != scope and part_id != scope:
            continue
        rows.append({"part": part_id, "parent": entry["parent"], "objects": len(entry["guids"]),
                     "layer": entry.get("layer")})
    return receipts.r2("parts", len(rows), parts=rows,
                       hint="Pass a part id as scope to vision.divide, or its objects to "
                            "vision.capture.")


def part_ids_to_guids(part_ids):
    """The objects of one or more parts (for vision.capture ids=...)."""
    store = parts_store()
    out, seen = [], set()
    for p in part_ids:
        for g in store.get(p, {}).get("guids", []):
            if g not in seen:
                seen.add(g)
                out.append(g)
    return out


# ---------- Rhino execution wrapper ----------

def run_vision_divide():
    scope = rs.GetString("Scope (all, a part id like P3, or layer:Name)", "all")
    if scope is None:
        return None
    return receipts.echo(tool_vision_divide(scope))


if __name__ == "__main__":
    run_vision_divide()
