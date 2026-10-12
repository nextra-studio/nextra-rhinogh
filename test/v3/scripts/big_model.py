"""Setup for case 19 (not counted): a 6-storey frame plus MEP runs, ~1,500 objects on 4 layers.
Beams on level 4 get one longer span (BX24_L4 is 12 m) so 'find the longest beam on level 4' has one answer."""
nx, ny, bay, h, storeys = 6, 5, 6000.0, 3600.0, 6


def layer(name, rgb):
    i = doc.Layers.FindByFullPath(name, -1)
    return i if i >= 0 else doc.Layers.Add(name, System.Drawing.Color.FromArgb(*rgb))


L = {n: layer(n, c) for n, c in [("Columns", (200, 60, 60)), ("Beams", (60, 90, 200)), ("Slabs", (150, 150, 150)), ("MEP", (40, 160, 90))]}


def add(geo, lay, name):
    a = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = L[lay]; a.Name = name
    doc.Objects.Add(geo, a)


def box(x0, x1, y0, y1, z0, z1):
    return rg.Box(rg.Plane.WorldXY, rg.Interval(x0, x1), rg.Interval(y0, y1), rg.Interval(z0, z1)).ToBrep()


n = 0
for s in range(storeys):
    zb, zt, lv = s * h, (s + 1) * h, s + 1
    for i in range(nx + 1):
        for j in range(ny + 1):
            add(box(i * bay - 200, i * bay + 200, j * bay - 200, j * bay + 200, zb, zt - 250), "Columns", "C%d%d_L%d" % (i, j, lv)); n += 1
    for j in range(ny + 1):
        for i in range(nx):
            x1 = (i + 2) * bay if (lv == 4 and i == 2 and j == 4) else (i + 1) * bay
            add(box(i * bay, x1, j * bay - 150, j * bay + 150, zt - 700, zt - 250), "Beams", "BX%d%d_L%d" % (i, j, lv)); n += 1
    for i in range(nx + 1):
        for j in range(ny):
            add(box(i * bay - 150, i * bay + 150, j * bay, (j + 1) * bay, zt - 700, zt - 250), "Beams", "BY%d%d_L%d" % (i, j, lv)); n += 1
    add(box(-300, nx * bay + 300, -300, ny * bay + 300, zt - 250, zt), "Slabs", "S_L%d" % lv); n += 1
    # MEP: ducts and pipes under each slab, as polylines
    for r in range(ny * 3):
        y = 1000 + r * (ny * bay - 2000) / (ny * 3 - 1)
        for seg in range(nx):
            pts = [rg.Point3d(seg * bay + 300, y, zt - 900), rg.Point3d((seg + 1) * bay - 300, y, zt - 900)]
            add(rg.PolylineCurve(pts), "MEP", "P%d_%d_L%d" % (r, seg, lv)); n += 1
print("big model:", n, "objects")
