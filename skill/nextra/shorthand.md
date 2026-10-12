---
description: Grasshopper shorthand grammar (gh_edit ops) — create, modify, wire, group, component state
---
# GH shorthand (gh_edit `ops`)

One line = one operation. The same grammar is what `query {scope:'gh'}` prints, so you can copy lines back — even onto a new canvas (`gh_edit {new:true, ops:<printed text>}` copies a definition; printed `gN` ids act as local ids there).

## Create
```
id = Type [@Category[/Sub]] (Port=value, …) {key=value, …} -> (out1, out2) "Nick"
```
- `id`: your local name (letters/digits/_). The reply maps it to a handle: `ids: pt=g7`. Never reuse `g12`/`r3` style names.
- `Type`: exact component name (`Construct Point`, `Number Slider`, `Python 3 Script`). Unknown/ambiguous names come back with suggestions; ambiguity is auto-resolved by the ports you used (`Rectangle (P=…, X=…)` → Curve/Rectangle). Unsure → `query {scope:'components', filter:'rectangle'}`.
- Aliases: Slider, Toggle, Py3/Python, C#, Expr, Eval, Swatch, Note(=Scribble).

## Modify existing (handles)
```
g12 (Z=g3)            # replace inputs of port Z with g3
g12 (Z+=g4, Y-=g5)     # add / remove one source
g12 (Z=_)              # disconnect + clear stored value
g12 {v=7, nick="H"}    # settings
- g12 g13              # delete
g3.R > g12.Z           # add wire      g3.R !> g12.Z  remove wire
```

## Values (inside `( )`)
| form | meaning |
|---|---|
| `g3`, `g3.R`, `pt.1` | wire from a node (default = first output; `.Port` by nickname, name or index) |
| `a+b.R` | several sources |
| `5`, `-2.5`, `true` | number / bool (stored as the input's own value) |
| `"text"` | string (`\n` for newline) |
| `{1,2,3}` | point / vector |
| `0..10` | domain |
| `[1,2,3]` | list of values |
| `#FF0000` | colour |
| `XY` `XZ` `YZ` | world planes |
| `r12`, `r3+r4` | **reference Rhino objects** (geometry inputs) |

Port flags after the value (or alone, `C=^f`): `^f` flatten · `^g` graft · `^s` simplify · `^r` reverse · `^p` reparameterize · `^i` invert · `^h` hidden wire · `^d` faint wire · `^x` clear all · `^e(x*2)` input expression.
Output port flags: `(>R=^g)`.

## Settings `{ }`
| object | keys |
|---|---|
| Number Slider | `v`, `r=a..b` (or `min`,`max`), `int` / `even` / `odd`, `d=2` decimals |
| Panel | `text="a\nb"`, `ml` multiline |
| Boolean Toggle | `v=true` |
| Value List | `items="Low:0|High:1"`, `sel=1`, `mode=dropdown|checklist|sequence|cycle` |
| Colour Swatch | `c=#RRGGBB` |
| Scribble | `text="…"`, `size=20` |
| Expression | `expr="x*y+1"` |
| Graph Mapper | `graph=bezier|linear|sine|…` |
| Param (Point, Curve, Number…) | `set=[…]` stored values, `ref=[r3,r4]` Rhino references |
| script components | `code="""…"""` or `code=@C:/path/file.py` |
| any | `nick="Name"`, `hide`/`show` (preview), `lock`/`unlock` |
| **any component: saved settings** | `$Key=value` — everything the component saves: dropdowns, modes, check-boxes drawn on the component (Karamba Support `$button[2]=true`), right-click toggles (`$UnionBox=true`). Keys may be shortened to any unique suffix. `query level:'detail'` lists all `$` keys; normal listings show only non-default ones. |
| **any component: right-click menu** | `menu="Union Box"` (toggle) · `menu="Union Box:on"` (only if needed) · `menu="Mode>Linear"` (sub-menu) · `menu=["A","B"]`. `level:'detail'` shows `menu: [x] Union Box | …`. Items ending in "…" open dialogs and are refused. |
| **any component: click** | `click="0.73,0.92"` = mouse click at that fraction of the component box (x→right, y→down). Last resort for custom widgets: first `view {target:'gh', focus:'gN'}`, read the position, click, then check with `query`/`view`. |

## Script components
```
py = Python 3 Script (pts:list=g3, n=5, srf:item=g9) -> (a, crv) {code="""
a = [p for p in pts][:int(n)]
crv = None
"""}
```
- Inputs declared as `name[:item|list|tree]=source`. On a new script the declared set becomes the exact input set; on an existing one, unknown names are added; `(-old)` removes an input.
- `-> (a, b)` = exact output set (the `out` stdout port stays).
- C#: write the `RunScript(...)` signature in `code`; inputs/outputs follow it automatically.
- Later edits: `gh_edit {script:{handle:'g7', patch:[{find,replace}]}}` — errors come back with the failing lines.

## Groups (readable canvases are mandatory)
```
## Inputs #DBABFF        # new section: following CREATED nodes join group "Inputs" (existing group of that name is reused)
## g20                   # existing group by handle; bare handles below join it
g12
## g20 "Profile" #ABDBFF -g7   # rename/recolour, remove member g7
##                       # end of section
```
Colours (default palette): inputs `#DBABFF`, logic `#ABDBFF`, geometry `#FFD6AB`, output `#B2FAB4`. Keep 3–12 nodes per group, one purpose per group, left→right data flow. Add a `Scribble` only for non-obvious intent.

## Behaviour you can rely on
- Lines apply independently. A failed line is reported as `L7 \`…\`: reason — valid alternatives`; everything else stays applied. Fix only failed lines (`atomic:true` = all-or-nothing).
- `layout:'tidy', relays:true` adds interface params at every group boundary (`<name>_In` in the left column, `<name>_Out` in the right column, typed like the data they carry; re-running adds none). Use when the user wants groups as movable/replaceable blocks.
- Layout is automatic: new nodes are placed next to their upstream nodes; nodes added to an existing group re-layout that group in place and push neighbours aside. `layout:'tidy'` (whole) or `layout:'tidy:group:Name'`.
- One solve after all lines; runtime errors of affected nodes (and anything newly broken downstream) are listed; `results:` shows what the sinks produced (counts, types, tree paths).

## Examples
```
## Inputs #DBABFF
n = Number Slider {v=10, r=2..50, int} "Count"
r = Number Slider {v=5, r=1..20} "Radius"
## Points #ABDBFF
c = Circle (P=XY, R=r)
dv = Divide Curve (C=c, N=n)
## Out #B2FAB4
ln = Line (A=dv.P, B={0,0,10})
```
Modify: `g4 (N=g1^e(x*2))` · insert: `## g9\nsm = Smooth Curve (C=g5)\ng8 (C=sm)` · replace: `- g6\n## g9\npl = Polyline (V=g5.P, C=true)\ng8 (C=pl)`
