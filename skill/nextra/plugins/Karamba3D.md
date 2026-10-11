---
plugin: Karamba3D
description: Karamba3D 3 in shorthand — units, component names, ports, dropdown/toggle state, typical chain, checks
---
# Karamba3D profile

## Rules
- **Karamba works in metres, kN, cm results — whatever the Rhino unit is.** If the doc is in mm, scale geometry ×0.001 before Karamba (`Scale (G=…, F=0.001)`) or model the analysis geometry in metres. Check: `Assemble.Mass` must be plausible (10 m CHS 114.3×4 ≈ 109 kg).
- **Loads need nodes.** A point load at a point that is not a node gives `Info: "Could not find node at …"` and zero results. Split lines first (`Shatter`, `Divide`→`Polyline`→`Explode`, or connect members at intersections).
- Use current components: `Analyse @Karamba3D` (not `AnalyzeThI` — Karamba itself warns "newer version available").
- Write plug-in components without the "(Karamba3D)" suffix and add `@Karamba3D`: `Support @Karamba3D`, `Assemble Model @Karamba3D`. Two `Assemble Model` versions exist; the engine picks the visible one.

## On-component controls
| control | how |
|---|---|
| Support check-boxes Tx…Rz | input `Dofs=[0,1,2]` = **indices** of fixed DOFs (0 Tx, 1 Ty, 2 Tz, 3 Rx, 4 Ry, 5 Rz); added to the check-boxes. Pinned: `[0,1,2]`, fixed: `[0,1,2,3,4,5]`, roller: `[1,2]` (+ Rx `3` once per model to stop torsion modes) |
| Loads dropdown (type of load) | `{$ActiveUnit/unitname="Point"}` — units: Gravity, Point, PointDisplacement, InitialStrain, Temperature, MeshLoad -Const., MeshLoad -Variable. Inputs change with the unit (Point: Force, Moment, Local?, Pos\|Ind, LCase) |
| any other dropdown | `query {scope:'gh', filter:'g10', level:'detail'}` lists `$` keys; set with `{$Key=value}` |

## Minimal beam (copy and adapt)
```
## Geometry #DBABFF
a = Point {set={0,0,0}}
b = Point {set={10,0,0}}
ln = Line (A=a, B=b)
sh = Shatter (C=ln^p, t=0.5)
## Karamba #ABDBFF
el = Create Linear Element @Karamba3D (Line=sh)
sup = Support @Karamba3D (Pos|Ind=[{0,0,0},{10,0,0}], Dofs=[0,1,2,3])
ld = Loads @Karamba3D (Pos|Ind={5,0,0}, Force={0,0,-10}) {$ActiveUnit/unitname="Point"}
asm = Assemble Model @Karamba3D (Elem=el.Elem, Support=sup, Load=ld)
an = Analyse @Karamba3D (Model=asm.Model)
## Results #B2FAB4
d = Panel (=an.MaxDisp)
```
Expected: MaxDisp ≈ 47.0 cm (= PL³/48EI with default CHS 114.3×4, S235).

## Ports (Karamba3D 3)
- `Create Linear Element`: Line[] Colour Id CroSec … Pts → Elem Pts Info
- `Create Surface Element`: Mesh[] … → Elem
- `Support`: Pos|Ind Plane Ct Cr Dofs → Supp
- `Loads`: (per unit, see above) → Load
- `Assemble Model`: Elem Support Load Joint Set LDist Pts CroSec Mat → Model Info Mass COG LCNames
- `Analyse`: Model LCase → Model MaxDisp[cm] Force Energy
- `Cross Section Selector`: CroSec Name|Ind Elems|Ids → CroSec · `Material Selection`: Mat Name|Ind → Mat
- Results: `Beam View`, `Model View` (Model LCase …), `Nodal Displacements`, `Utilization of Elements` (Util …), `Beam Forces`, `Reaction Forces`
- `Mesh Breps` (Brep → Mesh for shells)
Ports of anything else: `query {scope:'components', filter:'Beam Forces (Karamba3D)'}` or `@Karamba3D`.

## Checks after every change
`results:` line of gh_edit → `query {filter:'g13 g14', level:'data'}` for `Info` (warnings live there), `Mass`, `MaxDisp`. "rigid body modes" warning = missing support DOFs.
