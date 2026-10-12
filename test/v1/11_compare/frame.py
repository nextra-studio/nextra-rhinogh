"""Concrete frame: grid of columns, perimeter+grid beams and slabs per storey. args: nx, ny, bay, storeys, h, z0"""
nx, ny = args.get("nx", 3), args.get("ny", 2)
bay, h = args.get("bay", 6000.0), args.get("h", 3500.0)
storeys, z0 = args.get("storeys", 3), args.get("z0", 0.0)

def layer(name, color):
    i = doc.Layers.FindByFullPath(name, -1)
    return i if i >= 0 else doc.Layers.Add(name, color)

L = {k: layer(k, c) for k, c in [("Columns", System.Drawing.Color.Red), ("Beams", System.Drawing.Color.Blue), ("Slabs", System.Drawing.Color.Gray)]}

def add(geo, lay, name):
    a = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = L[lay]; a.Name = name
    doc.Objects.Add(geo, a)

for s in range(storeys):
    zb, zt = z0 + s * h, z0 + (s + 1) * h
    for i in range(nx + 1):
        for j in range(ny + 1):
            box = rg.Box(rg.Plane(rg.Point3d(i * bay, j * bay, zb), rg.Vector3d.ZAxis), rg.Interval(-200, 200), rg.Interval(-200, 200), rg.Interval(0, h - 250))
            add(box.ToBrep(), "Columns", "C%d%d_L%d" % (i, j, s + 1))
    for j in range(ny + 1):
        for i in range(nx):
            b = rg.Box(rg.Plane(rg.Point3d(i * bay, j * bay, zt - 250), rg.Vector3d.ZAxis), rg.Interval(0, bay), rg.Interval(-150, 150), rg.Interval(-450, 0))
            add(b.ToBrep(), "Beams", "BX%d%d_L%d" % (i, j, s + 1))
    for i in range(nx + 1):
        for j in range(ny):
            b = rg.Box(rg.Plane(rg.Point3d(i * bay, j * bay, zt - 250), rg.Vector3d.ZAxis), rg.Interval(-150, 150), rg.Interval(0, bay), rg.Interval(-450, 0))
            add(b.ToBrep(), "Beams", "BY%d%d_L%d" % (i, j, s + 1))
    slab = rg.Box(rg.Plane.WorldXY, rg.Interval(-300, nx * bay + 300), rg.Interval(-300, ny * bay + 300), rg.Interval(zt - 250, zt))
    add(slab.ToBrep(), "Slabs", "S_L%d" % (s + 1))
print("storeys %d..%d done" % (z0 / h + 1, z0 / h + storeys))
