"""Input architecture model for case 14 (stands in for a user's house model): solids only, metres.
2 storeys, 8 x 6 m, RC walls on two sides, columns + beams on the open sides, slabs, flat roof slab."""
doc.ModelUnitSystem = Rhino.UnitSystem.Meters
H = 3.0

def layer(n, c):
    i = doc.Layers.FindByFullPath(n, -1)
    return i if i >= 0 else doc.Layers.Add(n, c)

LA = {k: layer("ARCH::" + k, c) for k, c in [("Walls", System.Drawing.Color.LightGray), ("Slabs", System.Drawing.Color.Gray),
                                            ("Columns", System.Drawing.Color.IndianRed), ("Beams", System.Drawing.Color.SteelBlue)]}

def box(x0, x1, y0, y1, z0, z1, lay, name):
    a = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = LA[lay]; a.Name = name
    doc.Objects.Add(rg.Box(rg.Plane.WorldXY, rg.Interval(x0, x1), rg.Interval(y0, y1), rg.Interval(z0, z1)).ToBrep(), a)

for s in range(2):
    zb, zt = s * H, (s + 1) * H
    box(-0.1, 0.1, 0, 6, zb, zt - 0.2, "Walls", "W_west_L%d" % (s + 1))          # wall along y at x=0
    box(0, 8, 5.9, 6.1, zb, zt - 0.2, "Walls", "W_north_L%d" % (s + 1))          # wall along x at y=6
    for (x, y) in [(8, 0), (4, 0), (8, 3)]:
        box(x - 0.15, x + 0.15, y - 0.15, y + 0.15, zb, zt - 0.2, "Columns", "C_%g_%g_L%d" % (x, y, s + 1))
    box(0, 8, -0.1, 0.1, zt - 0.6, zt - 0.2, "Beams", "B_south_L%d" % (s + 1))   # beam along x at y=0
    box(7.9, 8.1, 0, 6, zt - 0.6, zt - 0.2, "Beams", "B_east_L%d" % (s + 1))     # beam along y at x=8
    box(-0.1, 8.1, -0.1, 6.1, zt - 0.2, zt, "Slabs", "S_L%d" % (s + 1))
print("house: walls, columns, beams, slabs on 2 storeys")
