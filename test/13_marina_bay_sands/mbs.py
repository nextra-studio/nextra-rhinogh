"""Marina Bay Sands massing: 3 towers (straight + curved leg each), skypark ship with cantilever, podium. Units: m.
args: towers (3), spacing (80), height (194), lean (24)"""
import math

doc.ModelUnitSystem = Rhino.UnitSystem.Meters
N = args.get("towers", 3)
S = args.get("spacing", 80.0)
H = args.get("height", 194.0)
LEAN = args.get("lean", 24.0)
LEG_W, LEG_L, GAP = 11.0, 62.0, 4.0
tol = doc.ModelAbsoluteTolerance


def layer(name, rgb):
    i = doc.Layers.FindByFullPath(name, -1)
    return i if i >= 0 else doc.Layers.Add(name, System.Drawing.Color.FromArgb(*rgb))


L = {"Towers": layer("MBS::Towers", (225, 225, 220)), "Skypark": layer("MBS::Skypark", (90, 140, 120)),
     "Podium": layer("MBS::Podium", (180, 170, 150))}


def add(geo, lay, name):
    a = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = L[lay]; a.Name = name
    return doc.Objects.Add(geo, a)


def rect(x0, x1, y0, y1, z):
    return rg.Polyline([rg.Point3d(x0, y0, z), rg.Point3d(x1, y0, z), rg.Point3d(x1, y1, z), rg.Point3d(x0, y1, z), rg.Point3d(x0, y0, z)]).ToNurbsCurve()


for t in range(N):
    cx = t * S
    # straight (east) leg
    straight = rg.Box(rg.Plane.WorldXY, rg.Interval(cx - GAP / 2 - LEG_W, cx - GAP / 2), rg.Interval(-LEG_L / 2, LEG_L / 2), rg.Interval(0, H)).ToBrep()
    add(straight, "Towers", "T%d_leg_straight" % (t + 1))
    # curved (west) leg: sections lofted, leaning onto the straight leg at the top
    secs = []
    for k in range(9):
        z = H * k / 8.0
        off = LEAN * (1 - z / H) ** 2
        secs.append(rect(cx + GAP / 2 + off, cx + GAP / 2 + LEG_W + off * 0.6, -LEG_L / 2, LEG_L / 2, z))
    loft = rg.Brep.CreateFromLoft(secs, rg.Point3d.Unset, rg.Point3d.Unset, rg.LoftType.Normal, False)
    if loft:
        leg = loft[0].CapPlanarHoles(tol) or loft[0]
        add(leg, "Towers", "T%d_leg_curved" % (t + 1))

# skypark: long ship with rounded ends, cantilever past the first tower
x0, x1 = -72.0, (N - 1) * S + 40.0
hull = rect(x0, x1, -19.0, 19.0, H)
hull = rg.Curve.CreateFilletCornersCurve(hull, 15.0, tol, tol) or hull
ship = rg.Extrusion.Create(hull, 9.0, True)
if ship:
    add(ship.ToBrep(), "Skypark", "Skypark")

# podium
pod = rg.Box(rg.Plane.WorldXY, rg.Interval(-40, (N - 1) * S + 40), rg.Interval(-110, -45), rg.Interval(0, 22)).ToBrep()
add(pod, "Podium", "Podium")
print("MBS: %d towers, skypark %.0f m long, cantilever %.0f m" % (N, x1 - x0, -x0 - GAP / 2 - LEG_W))
