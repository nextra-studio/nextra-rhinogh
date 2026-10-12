"""Solids -> FE geometry: linear members -> centre lines, planar members -> mid-surfaces, snapped to an analytical grid; beams split at nodes.
args: filter ('layer:ARCH::*'), out ('FE'), snap (0.35 = max snap distance, model units)"""
src = q(args.get("filter", "layer:ARCH::*"))
OUT = args.get("out", "FE")
SNAP = args.get("snap", 0.35)


def layer(name, c):
    i = doc.Layers.FindByFullPath(name, -1)
    if i >= 0:
        return i
    parent = doc.Layers.FindByFullPath(OUT, -1)
    if parent < 0:
        parent = doc.Layers.Add(OUT, System.Drawing.Color.Black)
    ly = Rhino.DocObjects.Layer(); ly.Name = name.split("::")[-1]; ly.Color = c; ly.ParentLayerId = doc.Layers[parent].Id
    return doc.Layers.Add(ly)


LB = layer(OUT + "::Lines", System.Drawing.Color.Blue)
LS = layer(OUT + "::Shells", System.Drawing.Color.Orange)

items = [(o, obb(o)) for o in src]
items = [(o, b) for o, b in items if b]

# analytical grid: levels from slab mid-planes (+ ground), axes from vertical members and wall planes
levels, gx, gy = {0.0}, set(), set()
for o, b in items:
    c = b["center"]
    if b["shape"] == "planar" and abs(b["short"].Z) > 0.9:
        levels.add(round(c.Z, 4))
    elif b["shape"] == "linear" and abs(b["axis"].Z) > 0.9:
        gx.add(round(c.X, 4)); gy.add(round(c.Y, 4))
    elif b["shape"] == "planar":                      # wall: its plane gives one axis
        n = b["short"]
        if abs(n.X) > 0.9: gx.add(round(c.X, 4))
        if abs(n.Y) > 0.9: gy.add(round(c.Y, 4))


def snap1(v, grid):
    if not grid: return v
    g = min(grid, key=lambda x: abs(x - v))
    return g if abs(g - v) <= SNAP else v


def snap(p):
    return rg.Point3d(snap1(p.X, gx), snap1(p.Y, gy), snap1(p.Z, levels))


lines, shells = [], []
for o, b in items:
    c, a, L, W = b["center"], b["axis"], b["L"], b["W"]
    if b["shape"] == "linear":
        p0, p1 = c - a * (L / 2), c + a * (L / 2)
        if abs(a.Z) < 0.1:                             # beam: to the nearest storey level
            z = snap1(c.Z, levels); p0.Z = z; p1.Z = z
        lines.append([snap(p0), snap(p1), o])
    elif b["shape"] == "planar":
        m = rg.Vector3d.CrossProduct(b["short"], a); m.Unitize()
        pts = [snap(c + a * (sx * L / 2) + m * (sy * W / 2)) for sx, sy in [(-1, -1), (1, -1), (1, 1), (-1, 1)]]
        shells.append((pts, o))

# split lines where any other node lies on them (columns meeting beams, beams meeting beams)
nodes = [p for l in lines for p in l[:2]] + [p for s in shells for p in s[0]]
out_lines = []
for p0, p1, o in lines:
    seg = rg.Line(p0, p1)
    ts = sorted({0.0, 1.0} | {round(seg.ClosestParameter(n), 6) for n in nodes
                              if seg.DistanceTo(n, True) < 1e-3 and 1e-6 < seg.ClosestParameter(n) < 1 - 1e-6})
    for t0, t1 in zip(ts[:-1], ts[1:]):
        out_lines.append((rg.Line(seg.PointAt(t0), seg.PointAt(t1)), o))

def add(geo, lay, o):
    at = Rhino.DocObjects.ObjectAttributes(); at.LayerIndex = lay; at.Name = o.Attributes.Name
    at.SetUserString("src", handle(o))
    doc.Objects.Add(geo, at)

for ln, o in out_lines:
    add(rg.LineCurve(ln), LB, o)
for pts, o in shells:
    add(rg.Brep.CreateFromCornerPoints(pts[0], pts[1], pts[2], pts[3], doc.ModelAbsoluteTolerance), LS, o)
print("levels %s, grid x %s y %s; %d lines (from %d members), %d shells" % (sorted(levels), sorted(gx), sorted(gy), len(out_lines), len(lines), len(shells)))
