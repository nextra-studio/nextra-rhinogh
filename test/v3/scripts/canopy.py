"""Freeform canopy: 5 arched sections lofted into a shell, offset to a solid of thickness t, columns under the low edges.
args: L (30000) length, W (14000) width, h_mid (7000) crown height at the middle section, h_end (4500) crown at the ends,
      t (250) shell thickness, n_cols (4) columns per long edge"""
L, W = args.get("L", 30000.0), args.get("W", 14000.0)
h_mid, h_end = args.get("h_mid", 7000.0), args.get("h_end", 4500.0)
t, n_cols = args.get("t", 250.0), args.get("n_cols", 4)
edge_z = 3000.0


def layer(name, rgb):
    i = doc.Layers.FindByFullPath(name, -1)
    return i if i >= 0 else doc.Layers.Add(name, System.Drawing.Color.FromArgb(*rgb))


LC, LP = layer("Canopy", (90, 150, 200)), layer("Canopy Columns", (200, 80, 60))


def add(geo, li, name):
    a = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = li; a.Name = name
    return doc.Objects.Add(geo, a)


import math
secs = []
for k in range(5):
    x = L * k / 4.0
    crown = h_end + (h_mid - h_end) * math.sin(math.pi * k / 4.0)          # higher in the middle
    secs.append(rg.Arc(rg.Point3d(x, 0, edge_z), rg.Point3d(x, W / 2, crown), rg.Point3d(x, W, edge_z)).ToNurbsCurve())
loft = rg.Brep.CreateFromLoft(secs, rg.Point3d.Unset, rg.Point3d.Unset, rg.LoftType.Normal, False)
srf = loft[0]
solid = rg.Brep.CreateFromOffsetFace(srf.Faces[0], t, doc.ModelAbsoluteTolerance, True, True)
add(solid or srf, LC, "CANOPY")
for side, y in [("S", 0.0), ("N", W)]:
    for i in range(n_cols):
        x = L * (i + 0.5) / n_cols
        col = rg.Cylinder(rg.Circle(rg.Plane(rg.Point3d(x, y, 0), rg.Vector3d.ZAxis), 200), edge_z).ToBrep(True, True)
        add(col, LP, "COL_%s%d" % (side, i + 1))
amp = rg.AreaMassProperties.Compute(srf)
print("canopy %.0f x %.0f, crown %.0f/%.0f, area %.1f m2, %d columns" % (L, W, h_mid, h_end, amp.Area / 1e6, 2 * n_cols))
