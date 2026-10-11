"""
core_divide - split a region into parts, and say which objects touch which part.

The looking problem behind it: a building is hundreds of objects, and a
picture with #1..#800 on it is unreadable. So the space is cut into up to
2 x 2 x 2 cells, each cell is a PART (P1..P8), and the model picks the part
it cares about - which is cut again (P3 -> P3.P1..P3.P8) until a part is
small enough to number object by object.

Two rules make the parts useful:

    TOUCHING, NOT CENTRE   an object belongs to every part it touches, so a
                           long beam through P1 and P2 is found from either.
                           A bbox test first; for an object whose bbox
                           reaches more than one cell, an exact test (the
                           caller's) drops the cells it never really enters
                           - a diagonal member's bbox covers corners it
                           does not.
    STABLE NUMBERING       a cell's number comes from its POSITION, not from
                           how many cells happen to be non-empty, so P3 is
                           the same place every time the same scope is cut.

Pure Python: a box is ``((x0, y0, z0), (x1, y1, z1))``. Nothing here imports
Rhino, so it runs (and is tested) outside it; ``vision_divide`` feeds it
Rhino geometry.
"""

# An axis shorter than this share of the longest one is not cut: a plan
# (or one storey) gives 2 x 2 x 1 cells instead of four empty ones.
FLAT_RATIO = 0.05
MIN_EXTENT = 1.0


# ---------- boxes ----------

def box_union(boxes):
    boxes = [b for b in boxes if b is not None]
    if not boxes:
        return None
    lo = tuple(min(b[0][i] for b in boxes) for i in range(3))
    hi = tuple(max(b[1][i] for b in boxes) for i in range(3))
    return (lo, hi)


def box_intersection(a, b):
    """The overlap of two boxes, or None. A flat overlap (zero thickness) counts."""
    if a is None or b is None:
        return None
    lo = tuple(max(a[0][i], b[0][i]) for i in range(3))
    hi = tuple(min(a[1][i], b[1][i]) for i in range(3))
    if any(lo[i] > hi[i] for i in range(3)):
        return None
    return (lo, hi)


def box_size(box):
    return tuple(box[1][i] - box[0][i] for i in range(3))


def box_center(box):
    return tuple((box[0][i] + box[1][i]) / 2.0 for i in range(3))


def _axis_overlap(item_lo, item_hi, cell_lo, cell_hi, last, tol):
    """Does an object's extent on one axis reach into a cell's?

    It must go IN by more than ``tol``: a column standing on the cut
    between two storeys touches the upper cell with its end face only,
    and counting that would put every ground-floor column in the upper
    parts too. An object that is flat on this axis (a beam lying exactly
    on the cut) has no depth to go in with, so it takes the half-open
    rule instead - it belongs to the cell whose range [lo, hi) holds it,
    and the last cell also keeps its upper boundary."""
    if item_hi - item_lo <= tol:
        v = (item_lo + item_hi) / 2.0
        if last:
            return cell_lo - tol <= v <= cell_hi + tol
        return cell_lo - tol <= v < cell_hi - tol
    return item_lo < cell_hi - tol and cell_lo < item_hi - tol


def touches_box(item, cell, flags=(True, True, True), tol=0.0):
    """Bbox test of an object against one cell. ``flags[i]`` = the cell is
    the last one along axis i (it keeps its upper boundary)."""
    return all(_axis_overlap(item[0][i], item[1][i], cell[0][i], cell[1][i],
                             flags[i], tol) for i in range(3))


# ---------- cutting the region ----------

def split_counts(region, flat_ratio=FLAT_RATIO):
    """How many cells along each axis: 2, or 1 for a flat axis."""
    size = box_size(region)
    longest = max(size)
    floor = max(MIN_EXTENT, longest * flat_ratio)
    return [1 if s < floor else 2 for s in size]


def _median(values):
    values = sorted(values)
    n = len(values)
    if not n:
        return None
    mid = n // 2
    return values[mid] if n % 2 else (values[mid - 1] + values[mid]) / 2.0


def cut_values(region, counts, centers=None, split="middle"):
    """The cut positions along each axis: [lo, hi] or [lo, cut, hi].

    ``middle``   halves the region - predictable, the default.
    ``balanced`` cuts at the median object centre, so an uneven model
                 (a tower on a wide podium) still gives parts of similar
                 size. Falls back to the middle when the median sits on
                 the region's edge (all centres in one place)."""
    cuts = []
    for i in range(3):
        lo, hi = region[0][i], region[1][i]
        if counts[i] < 2:
            cuts.append([lo, hi])
            continue
        cut = (lo + hi) / 2.0
        if split == "balanced" and centers:
            m = _median([c[i] for c in centers if lo <= c[i] <= hi])
            span = hi - lo
            if m is not None and lo + 0.05 * span < m < hi - 0.05 * span:
                cut = m
        cuts.append([lo, cut, hi])
    return cuts


def cells(cuts):
    """The cells, numbered by position: x first, then y, then z (bottom first).

    Returns ``[(number, box, flags)]`` where ``flags[i]`` says the cell is
    the last one along axis i. With 2 x 2 x 2: P1..P4 are the bottom layer
    (P1 at -x -y, P2 +x, P3 +y, P4 +x +y), P5..P8 the same on top."""
    nx, ny, nz = (len(c) - 1 for c in cuts)
    out = []
    for iz in range(nz):
        for iy in range(ny):
            for ix in range(nx):
                number = ix + nx * (iy + ny * iz) + 1
                lo = (cuts[0][ix], cuts[1][iy], cuts[2][iz])
                hi = (cuts[0][ix + 1], cuts[1][iy + 1], cuts[2][iz + 1])
                flags = (ix == nx - 1, iy == ny - 1, iz == nz - 1)
                out.append((number, (lo, hi), flags))
    return out


# ---------- exact tests ----------

def segment_hits_box(p, q, box, tol=0.0):
    """Does the segment p-q pass through the box? (slab clipping)"""
    t0, t1 = 0.0, 1.0
    for i in range(3):
        lo, hi = box[0][i] - tol, box[1][i] + tol
        d = q[i] - p[i]
        if abs(d) < 1e-12:
            if p[i] < lo or p[i] > hi:
                return False
            continue
        a, b = (lo - p[i]) / d, (hi - p[i]) / d
        if a > b:
            a, b = b, a
        t0, t1 = max(t0, a), min(t1, b)
        if t0 > t1:
            return False
    return True


def polyline_hits_box(points, box, tol=0.0):
    """Does a polyline (a curve's approximation) pass through the box?"""
    points = list(points)
    if len(points) == 1:
        return all(box[0][i] - tol <= points[0][i] <= box[1][i] + tol for i in range(3))
    return any(segment_hits_box(points[k], points[k + 1], box, tol)
               for k in range(len(points) - 1))


# ---------- assigning objects ----------

def assign(items, cell_list, exact=None, tol=0.0):
    """Which objects touch which cell.

    ``items``      [(key, bbox)]
    ``cell_list``  from ``cells()``
    ``exact``      optional ``exact(key, cell_box) -> bool``, asked only for
                   objects whose bbox reaches more than one cell

    Returns ``(members, belongs)``: ``{cell number: [keys]}`` for non-empty
    cells, and ``{key: [cell numbers]}``. If the exact test rejects every
    candidate (it can miss a corner graze), the object falls back to the
    cell holding its bbox centre, so nothing touching the region is lost."""
    members, belongs = {}, {}
    for key, bbox in items:
        if bbox is None:
            continue
        hit = [(n, box) for n, box, flags in cell_list if touches_box(bbox, box, flags, tol)]
        if len(hit) > 1 and exact is not None:
            kept = [(n, box) for n, box in hit if exact(key, box)]
            if not kept:
                c = box_center(bbox)
                kept = [(n, box) for n, box in hit
                        if all(box[0][i] <= c[i] <= box[1][i] for i in range(3))][:1] or hit[:1]
            hit = kept
        if not hit:
            continue
        numbers = [n for n, _ in hit]
        belongs[key] = numbers
        for n in numbers:
            members.setdefault(n, []).append(key)
    return members, belongs


# ---------- numbering in space order ----------

def space_key(center, region, flat, levels=10):
    """Octree (Morton) key of a point: at each level, the cell digit is
    iz*4 + iy*2 + ix - the SAME order as ``cells()`` numbers P1..P8. So
    sorting by this key lists P1's objects first, then P2's, and inside P1
    its P1.P1 before P1.P2, all the way down."""
    size = box_size(region)
    idx = []
    for i in range(3):
        if flat[i] or size[i] <= 0:
            idx.append(0)
        else:
            t = (center[i] - region[0][i]) / size[i]
            idx.append(min(max(int(t * (1 << levels)), 0), (1 << levels) - 1))
    key = 0
    for level in range(levels - 1, -1, -1):
        digit = (((idx[2] >> level) & 1) << 2) | (((idx[1] >> level) & 1) << 1) | ((idx[0] >> level) & 1)
        key = (key << 3) | digit
    return key


def space_order(items, region=None):
    """Keys of ``items`` ([(key, bbox)]) sorted in octree order.

    Numbering objects in this order gives every part a block of
    consecutive numbers, and neighbours close numbers - so '#130' says
    roughly where an object is, and 'around #130' means something."""
    boxes = [b for _, b in items if b is not None]
    if not boxes:
        return [k for k, _ in items]
    region = region or box_union(boxes)
    flat = [c == 1 for c in split_counts(region)]
    keyed = [(space_key(box_center(b), region, flat), k) for k, b in items if b is not None]
    keyed.sort(key=lambda kv: kv[0])
    return [k for _, k in keyed] + [k for k, b in items if b is None]


# ---------- part ids ----------

def child_id(prefix, number):
    """'' + 3 -> 'P3';  'P3' + 2 -> 'P3.P2'."""
    return "P{}".format(number) if not prefix else "{}.P{}".format(prefix, number)


def parent_of(part_id):
    return part_id.rsplit(".", 1)[0] if "." in part_id else ""


def scope_prefix(part_ids):
    """The id prefix for the children of a scope made of several parts.

    One part: its own id (P3 -> P3.P1...). Siblings: the parent plus the
    joined last steps (P3.P2 + P3.P4 -> 'P3.P2+P4', children
    'P3.P2+P4.P1'...). Unrelated parts: all ids joined with '+'."""
    part_ids = list(dict.fromkeys(part_ids))
    if not part_ids:
        return ""
    if len(part_ids) == 1:
        return part_ids[0]
    parents = {parent_of(p) for p in part_ids}
    if len(parents) == 1:
        parent = parents.pop()
        lasts = "+".join(p.rsplit(".", 1)[-1] for p in part_ids)
        return "{}.{}".format(parent, lasts) if parent else lasts
    return "+".join(part_ids)
