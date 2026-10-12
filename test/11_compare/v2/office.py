"""Office building: column grid, beams, slabs with core opening, core walls, stair flights, facade mullions.
args: nx (5), ny (3), bay (7200), h (3600), storeys (4), z0 (0), core ((14400,7200)), mull (1800)"""
nx, ny = args.get("nx", 5), args.get("ny", 3)
bay, h = args.get("bay", 7200.0), args.get("h", 3600.0)
storeys, z0 = args.get("storeys", 4), args.get("z0", 0.0)
cx, cy = args.get("core", (14400.0, 7200.0))
mull = args.get("mull", 1800.0)
X, Y = nx * bay, ny * bay


def layer(name, rgb):
    i = doc.Layers.FindByFullPath(name, -1)
    return i if i >= 0 else doc.Layers.Add(name, System.Drawing.Color.FromArgb(*rgb))


L = {n: layer(n, c) for n, c in [("Columns", (200, 60, 60)), ("Beams", (60, 90, 200)), ("Slabs", (150, 150, 150)),
                                  ("Core", (230, 170, 60)), ("Stairs", (120, 70, 160)), ("Mullions", (40, 40, 40))]}


def add(geo, lay, name):
    a = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = L[lay]; a.Name = name
    doc.Objects.Add(geo, a)


def box(x0, x1, y0, y1, z0_, z1_):
    return rg.Box(rg.Plane.WorldXY, rg.Interval(x0, x1), rg.Interval(y0, y1), rg.Interval(z0_, z1_)).ToBrep()


for s in range(storeys):
    zb = z0 + s * h; zt = zb + h; lv = int(round(zb / h)) + 1
    for i in range(nx + 1):
        for j in range(ny + 1):
            add(box(i * bay - 250, i * bay + 250, j * bay - 250, j * bay + 250, zb, zt - 300), "Columns", "C%d%d_L%d" % (i, j, lv))
    for j in range(ny + 1):
        for i in range(nx):
            add(box(i * bay, (i + 1) * bay, j * bay - 200, j * bay + 200, zt - 900, zt - 300), "Beams", "BX%d%d_L%d" % (i, j, lv))
    for i in range(nx + 1):
        for j in range(ny):
            add(box(i * bay - 200, i * bay + 200, j * bay, (j + 1) * bay, zt - 900, zt - 300), "Beams", "BY%d%d_L%d" % (i, j, lv))
    slab = box(-400, X + 400, -400, Y + 400, zt - 300, zt)
    hole = box(cx, cx + bay, cy, cy + bay * 0.5, zt - 400, zt + 100)
    cut = rg.Brep.CreateBooleanDifference(slab, hole, doc.ModelAbsoluteTolerance)
    add(cut[0] if cut else slab, "Slabs", "S_L%d" % lv)
    # core: 4 walls around the opening
    t = 250
    for k, (a0, a1, b0, b1) in enumerate([(cx, cx + bay, cy - t, cy), (cx, cx + bay, cy + bay * 0.5, cy + bay * 0.5 + t),
                                          (cx - t, cx, cy, cy + bay * 0.5), (cx + bay, cx + bay + t, cy, cy + bay * 0.5)]):
        add(box(a0, a1, b0, b1, zb, zt - 300), "Core", "W%d_L%d" % (k, lv))
    # stair: two flights inside the core
    for f in range(2):
        z1 = zb + f * h / 2
        pts = [rg.Point3d(cx + 400, cy + 400 + f * 1600, z1), rg.Point3d(cx + bay - 1600, cy + 400 + f * 1600, z1 + h / 2)]
        add(rg.Box(rg.Plane(pts[0], pts[1] - pts[0], rg.Vector3d.YAxis), rg.Interval(0, pts[0].DistanceTo(pts[1])), rg.Interval(0, 1200), rg.Interval(-200, 0)).ToBrep(),
            "Stairs", "ST%d_L%d" % (f, lv))
    # facade mullions on all four sides
    n_x, n_y = int(X / mull), int(Y / mull)
    for side, (p0, d, n) in enumerate([((0, -500), (mull, 0), n_x), ((0, Y + 500), (mull, 0), n_x), ((-500, 0), (0, mull), n_y), ((X + 500, 0), (0, mull), n_y)]):
        for m in range(n + 1):
            x, y = p0[0] + d[0] * m, p0[1] + d[1] * m
            add(rg.LineCurve(rg.Point3d(x, y, zb), rg.Point3d(x, y, zt)), "Mullions", "M%s%d_L%d" % ("SNWE"[side], m, lv))
print("office storeys %d..%d done" % (z0 / h + 1, z0 / h + storeys))
