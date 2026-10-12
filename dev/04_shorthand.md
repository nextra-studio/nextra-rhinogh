# 04 — GH shorthand in depth

The shorthand is one text language used in **both directions**:
- the LLM writes it to edit the canvas (`gh_edit {ops}`), and
- the engine prints it when describing the canvas (`query {scope:'gh'}`).

Lines printed by `query` can be pasted back into `gh_edit`. Because the language is the same both ways, the LLM learns one grammar, and a 16-node definition costs about 150 tokens instead of 3,000–7,400 as JSON.

The user-facing grammar reference is `skill/nextra/shorthand.md`. This file explains the grammar *and* how the code implements it.

---

## 1. Grammar overview

```
line      := create | modify | wire | delete | group | comment
create    := ID '=' TYPE ['@' CATEGORY['/' SUB]] [inputs] [config] ['->' outputs] [NICK]
modify    := HANDLE [inputs] [config]                      # g12 (…) {…}
wire      := REF '.' PORT ('>' | '!>') REF '.' PORT [flags]  # add / remove one wire
delete    := '-' HANDLE {HANDLE}
group     := '##' [NAME | HANDLE ['"'NAME'"']] ['#'RRGGBB] {'-'HANDLE}   # opens a section
           | '##'                                          # closes the section
inputs    := '(' port {',' port} ')'
port      := PORT [':' ACCESS [':' HINT]] ('=' | '+=' | '-=') value [flags]
           | PORT '=' flags                               # flags only:  C=^f
           | '>' PORT '=' flags                           # output port flags:  (>R=^g)
           | '-' NAME                                     # remove a script input
config    := '{' key ['=' value] {',' key ['=' value]} '}'
outputs   := '(' NAME [flags] {',' NAME} ')'               # script components: exact output set
NICK      := '"' text '"'
comment   := '#' …  (outside quotes/brackets; '#RRGGBB' is kept as a colour)
```

### Names
- `ID` is a local name for this call (letters, digits, `_`). The reply maps it to a handle: `ids: pt=g7`.
- `HANDLE` is `gN`. Rhino objects are `rN` and only appear as values (references).
- `TYPE` is a component name (`Construct Point`), a quoted name for names with odd characters (`"Line (Karamba3D)"`), a GUID, or an alias: `Slider`, `Toggle`, `Py3`/`Python`, `C#`, `Expr`, `Eval`, `Swatch`, `Note`.
- `@Category` narrows the search (`Rectangle @Curve`, `Deconstruct @Vector`).
- `PORT` is a nickname (`R`), a full name (`Radius`) or an index (`0`). On variable-parameter components (Merge, Entwine) a port that does not exist yet (`D3`) is grown.

### Values
| form | kind (`VK`) | meaning |
|---|---|---|
| `g3`, `g3.R`, `pt.1`, `a+b.R` | Ref | wire(s) from nodes. Without a port, the first output that is not a script's `out` |
| `r12`, `r3+r4` | Ref | Rhino object reference (stored as the param's own referenced goo) |
| `5`, `-2.5` | Num | |
| `true` / `false` | Bool | |
| `"text"` | Str | `\n` = newline |
| `{1,2,3}` | Point | point or vector |
| `0..10` | Interval | domain |
| `[1,2,3]` | List | several persistent values |
| `#FF0000` | Color | |
| `XY` `XZ` `YZ` | Ident | world planes |
| `_` | Clear | disconnect and clear the stored value |

### Port flags (after the value)
`^f` flatten · `^g` graft · `^s` simplify · `^r` reverse · `^p` reparameterize · `^i` invert · `^h` hidden wire · `^d` faint wire · `^x` clear all flags · `^e(x*2)` input expression.

### Config keys `{…}`
| key | applies to |
|---|---|
| `v`, `r=a..b`, `min`, `max`, `int`, `even`, `odd`, `d=2` | Number Slider |
| `text`, `ml` | Panel |
| `v` | Boolean Toggle |
| `items="A:0\|B:1"`, `sel`, `mode` | Value List |
| `c` | Colour Swatch |
| `expr` | Expression |
| `graph` | Graph Mapper |
| `set=[…]`, `ref=[r3,r4]` or `ref="layer:X"` | any param: stored values / Rhino references |
| `code="""…"""` or `code=@file` | script components |
| `nick`, `hide`/`show`, `lock`/`unlock` | anything |
| `$Key=value` | **any saved component setting** (dropdowns, modes, on-component check-boxes, right-click toggles); see 05 §7 |
| `menu="Item"`, `"Item:on"`, `"Sub>Item"`, `["A","B"]` | right-click menu items |
| `click="0.73,0.92"` | mouse click at a fraction of the component box |

### Groups
- `## Name #color` opens a section. Nodes **created** after it, and **bare handle lines** (`g12` alone), join group `Name`. An existing group of that name is reused.
- `## g20` addresses an existing group by handle. `## g20 "New name" #ABDBFF -g7` renames it, recolours it and removes member g7.
- A modify line such as `g12 (A=g3)` inside a section does **not** move g12 into the group. Only creation or a bare handle line does.
- Writer output adds `(g31)` after a group name; the parser accepts and uses it.

---

## 2. Parser (`Gh/Shorthand/Parser.cs`)

1. **Logical lines.** Physical lines are joined while brackets or quotes are open, so multi-line `code="""…"""` and long input lists work.
2. **Comments.** A trailing ` # …` is removed if it is outside quotes and brackets and is not a `#RRGGBB` colour.
3. **Classification.**
   - `##…` → `GroupOp`
   - `- …` → `DeleteOp`
   - a top-level `>` without `=` before it → `WireOp`
   - otherwise → `NodeOp`, which is `Create` if it has `id =`.
4. **NodeOp parsing.** The head gives id / type / category. The parser then finds the top-level `( )` for inputs, `{ }` for config, `-> ( )` for outputs and a trailing `"nick"` (which becomes config `nick`).
5. **Values.** `ParseValue` turns text into a `Val`. Flags `^…` and `^e(…)` are peeled off the end of an input value.
6. Each op keeps its line number and raw text, so errors can quote it. Parse errors are per line and do not stop other lines.

AST classes: `NodeOp`, `PortOp`, `OutDecl`, `WireOp`, `DeleteOp`, `GroupOp`, `Val`, `ParseError`.

---

## 3. Builder (`Gh/Shorthand/Builder.cs`)

`Apply(doc, ops)` runs in this fixed order. Each line succeeds or fails on its own.

| step | what |
|---|---|
| 0. deletes | `- g8` lines first, so a replacement can take the old node's place |
| 1. create | `ComponentSearch.Resolve(type, category, portHints)` → `CreateInstance` → `AddObject`. The id → handle map is filled now, so later lines can wire to it |
| 2. script ports | `ScriptParams`: declared inputs `name:list:Curve=…` become the exact input set (new script) or are added (existing); `-> (a,b)` becomes the exact output set. C#: ports follow the `RunScript(...)` signature |
| 3. config | sorted by `ConfigOrder`: ranges/types before values (so `v=12` is not clamped by the default 0..1 range), code last. Then `$state` (StateIO), `menu`, `click` |
| 4. inputs | `ApplyInput`: `=` replaces sources, `+=` adds, `-=` removes; literals become persistent data (cleared first, because GH appends); `r` handles become references; flags and expressions applied. A cheap type-compatibility hint is given; real conversion errors appear after the solve |
| 5. wires | standalone `a.P > b.Q` / `!>` |
| 6. groups | sections → create/reuse groups, add members, rename/recolour/remove |

**Name resolution** (`Resolve(id)`): local id from this call → handle (`g12`) or GUID → unique nickname on the canvas (with a note) → error with a hint.

**Component resolution** (`ComponentSearch.Resolve`):
1. Candidates from every naming strategy at once: name, nickname, name without a plug-in suffix `(Karamba3D)`, and `@Category`.
2. A real name match beats a nickname-only match.
3. Several left: pick the one whose **ports match the ports used on the line**. `Rectangle (P=…, X=…, Y=…)` picks Curve/Primitive Rectangle, not the Surface one.
4. Still several: components before params, visible before hidden (legacy duplicates are usually hidden).
5. Nothing: fuzzy suggestions in the error.

**Error format:** ``L7 `first 50 chars of the line`: reason — alternatives``, e.g. the valid input names of that component. `atomic:true` restores the checkpoint if any line failed.

---

## 4. Writer — the reverse engine (`Gh/Shorthand/Writer.cs`)

`Write(doc, objects, options)` prints the canvas in the same grammar:
- **Grouped by GH group**, groups ordered left → right. Each section header is `## Name #color (gN)`. Loose objects come after.
- **Data-flow order** inside a section (sources before consumers), with ties broken by canvas position. A messy canvas still reads top-down.
- **Each node:** `gN = Type [@Category] (port=source/literal^flags, …) {config} "nick"`.
  - `@Category` is printed only when the name is ambiguous.
  - Persistent values are printed only if they differ from a fresh instance's defaults.
  - `$state` keys only if they differ from a fresh instance (`StateIO`), using the shortest unique suffix of each key.
- **Port references:** `g10.R`. The port is omitted when it is the default output.
- With `data` / `detail`: `=>` output summaries (count, type, branches, sample values), `!` runtime messages, ports, menus, code.

Because Writer and Parser share the grammar, `query` → edit text → `gh_edit` is a valid round trip.

---

## 5. Layout after an edit

`layout:'auto'` (the default) calls `Layout.Place` on the created nodes:
- New nodes that **joined an existing group**: that group is re-laid out in place (top-left kept), and groups or objects it now overlaps are pushed down (`PushAway`).
- **Other new nodes:** placed as a block next to their existing upstream/downstream neighbours, avoiding other boxes.
- Nothing that was not touched moves.

`layout:'tidy'` lays out the whole canvas, and `tidy:group:Name` a subset. `relays:true` adds `_In`/`_Out` params. The algorithm is in 05 §6.

---

## 6. Worked examples

**Create**
```
## Inputs #DBABFF
n = Number Slider {v=12, r=2..40, int} "Floors"
fh = Number Slider {v=3.5, r=2.5..5} "Floor h"
## Floors
ser = Series (S=0, N=fh, C=n)
pt = Construct Point (Z=ser)
pl = XY Plane (O=pt)
rec = Rectangle (P=pl, X=20, Y=20)
## Twist
ang = Range (D=0..1, N=n^e(x-1))
rot = Rotate (G=rec.R, A=ang, P=pl)
## Massing
lft = Loft (C=rot.G^f)
```

**Modify:** replace the rectangle with a circle inside group Floors and rewire the rotate:
```
- g8
## Floors
cir = Circle (P=g7, R=g3^e(x/2))
g12 (G=cir)
```

**Component state** (Karamba support: on-component check-boxes; Bounding Box: right-click toggle):
```
sup = Support @Karamba3D (Pos=g4) {$button[0]=true, $button[1]=true, $button[2]=true}
bb = Bounding Box (C=g9) {menu="Union Box:on"}
```

**Script component** (Python 3, declared ports):
```
py = Python 3 Script (pts:list=g3, n=5) -> (a) {code="""
a = pts[:int(n)]
"""}
```

**Regroup an existing messy canvas:**
```
## Inputs #DBABFF
g1
g2
g6
## g40 "Profile" #ABDBFF -g7
```
followed by `gh_edit {layout:'tidy'}`.

---

## 7. `.ghs` files: a definition stored as shorthand

A `.ghs` file is a whole Grasshopper definition written in this same grammar, as plain UTF-8 text. It is not a new language: anything `gh_edit {ops}` accepts is a valid `.ghs`, and anything `query` prints is close to one. Only the file mode of the Writer adds what a canvas needs to be rebuilt exactly.

```
gh_edit {save:'C:/work/tower.ghs'}    # canvas → text (the canvas keeps its own .gh path)
gh_edit {open:'C:/work/tower.ghs'}    # text → a new canvas, solved
gh_edit {open:'x.gh', save:'x.ghs'}   # convert a .gh
```

**Why.** A `.gh` is a binary archive: it can't be read, diffed or reviewed, and an LLM can't open it. The same definition as `.ghs` is about 10–25 % of the size (house FE model: 28 KB `.gh` → 3.1 KB `.ghs`) and reads like the canvas:

```
# ghs 1 · Grasshopper definition as Nextra shorthand · open: gh_edit {open:'kshell.ghs'} · grammar: query {scope:'docs', filter:'shorthand'}
# 23 objects, 5 groups · saved 2026-10-12 10:02

## Inputs #DBABFF
g1 = Number Slider {v=10, r=4..20, nick="Span m", pos={60,212}}
## Supports on the short edges #ABDBFF
g18 = Support @Karamba3D (0=g17) {hide, $button[0]=true, $button[1]=true, $button[2]=true, pos={2409,217}}
## Logic #ABDBFF
g5 = Python 3 Script (P:list:Point3d=g1, n:item:int=g3) -> (a, count) {code="""
a = [p for p in P] * n
count = len(a)
""", pos={400,372}}
```

**What file mode writes, beyond a normal listing** (`WriteOptions.ForFile()`):

| | listing (`query`) | `.ghs` file |
|---|---|---|
| ids | live handles `g12` | `g1, g2…` in written order, so saving, opening and saving again gives the **identical file** |
| script code | `code=<12 lines py>` | the full code in `code="""…"""`; a `"""` inside it is written `\"""` |
| Python input hints | — | `name:access:Hint` (`P:list:Point3d`); C# hints come from the `RunScript` signature |
| text, values | truncated | full (panel text, strings, all list items, numbers at full precision) |
| `$state` keys | 8 non-default | every non-default key |
| positions | — | `pos={x,y}` on every node (open does not auto-place these) |
| type / port names | object's own | registered names (some files carry renamed components or ports) |
| groups | `## Name #col (g40)` | `## Name #col`; on open **every section is a new group**, so repeated or empty names stay separate groups |
| Rhino references | `ref=[r12,r13]` | object ids `ref=[9ac49892-…]`, valid in the same `.3dm` like a `.gh`; elsewhere open reports the line as failed |

**Not stored** (listed in the header as `# not stored: …` and in the save reply):
- internalised geometry (a param holding a curve that is not a Rhino reference)
- data trees typed into params
- group nesting

Use a `.gh` for those. Anything else that fails to build on open is reported per line. The rest of the canvas is still built.

**Code:**
- `GhsFile.cs`: header plus the open/save entry points.
- `Writer` file mode: `FileIds`, `Quote`, `Storable`, `PortId` with registered names.
- `Builder`:
  - `pos` setting via `Layout.SetPivot`. This moves Karamba's hidden params as well; setting `Pivot` directly stretches the component across the canvas.
  - `freshGroups` and `BuildResult.Placed`.
- `Parser`: `TripleEnd` for the escape.
- Regression **c28** checks that save → open → save is identical, that the results are equal, that references and scripts survive, and that there are no dangling wires or stretched components.

## 8. Changing the grammar

1. `Parser.cs`: add the syntax. Keep it unambiguous with the existing forms (wires vs inputs, `#` comments vs colours).
2. `Builder.cs`: apply it, with a clear per-line error.
3. `Writer.cs`: print it, so the round trip holds.
4. `skill/nextra/shorthand.md`: document it in one line. Every word there costs tokens each session.
5. Test with `nx.py gh_edit` and then `nx.py query '{"scope":"gh"}'`; check the printed line parses back identically.

The LLM never modifies the grammar itself. Memory rules may describe *how* to use it, but `shorthand.md` changes only through the code.
