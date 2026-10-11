# Rhino Vision and Structural Tools: Internals

This guide describes the implementation in `test_v02/rhino_tools`. The
separate top-level `test py rhino/rhino_tools` tree may not have the same
implementation. Tool names are registered in `vision_utils/registry.py` and
`structural_utils/registry.py`; the MCP listener calls their `tool_*`
functions. Interactive `run_*` functions are Rhino-side prompt wrappers and
are not used by MCP.

## 1. How a tool call reaches Rhino

In tool-calling mode, `../agent/tool_calling.py` turns entries from
`catalog_index.json` into provider function schemas. The agent sends the
selected call over `../agent/bridge.py`; the listener dispatches it through a
category registry to a Python function in Rhino. In code mode, the listener
executes a script inside Rhino with only the allowed functions exposed.

Vision tools return receipts containing image paths and structured data.
Paths alone are not visible to a model. `../agent/vision.py` reads the image,
base64-encodes it as an image content block, and sends it with the next model
message. For models that cannot see images, the configured `eyes` model can
describe the image and return text to the main model. Image records and
sidecars can be copied into the session's `logs/<session>/images/` folder.

```text
model -> bridge -> Rhino listener -> category registry -> tool function
                                                       -> Rhino document
model <- receipt / image block <- agent vision <- image + JSON sidecar
```

## 2. Registered tool inventory and syntax

The call forms below use the dotted names exposed by the registries/catalog,
not the internal Python `tool_*` function names. Arguments are ordinary JSON
values: strings, numbers, arrays, objects, or `null`. `ids` accepts object
tags such as `"#12"`, tag arrays such as `["#12", "#15"]`, or GUIDs where
applicable. Tools return a receipt; they do not return a RhinoCommon object
directly to the caller.

### Session utilities

These five tools are in `session_utils/registry.py`, but are deliberately not
in `catalog_index.json`: the agent/checkpoint code uses them internally and
does not offer them as model-callable tools. Snapshots are held in Rhino
session state. `full=True` writes a `.3dm` backup needed for file-mode restore.

| Call syntax | What it does |
|---|---|
| `session.snapshot(label=None, full=False)` | Record current object IDs and a document census. With `full=True`, also write a full `.3dm` backup. Reusing a label replaces the prior snapshot. |
| `session.restore(label=None, mode="delta")` | Restore the named or latest snapshot. `delta` deletes objects added since the snapshot but cannot recover deleted/edited objects. `file` replaces the entire document from a backup created with `full=True`. |
| `session.snapshots()` | List snapshots, oldest first, with age, object count, backup path, and restore capability. |
| `session.diff(label=None)` | Compare the current object set and object-type counts with a snapshot; reports added and removed IDs. |
| `session.clear_snapshots(delete_backups=False)` | Forget all snapshot records. Set `delete_backups=True` to also delete their `.3dm` backup files. |

Example: `session.snapshot(label="before-roof", full=True)` then
`session.restore(label="before-roof", mode="file")`. File restore discards
everything done after that snapshot.

### Structural utilities

These are the 10 entries in `structural_utils/registry.py`. There are no
check modes: every tool does its job and returns what it did;
`structural.report` is the one non-mutating check (and a turn can be undone). There is no separately registered `structural.centerline` in this
tree: use `structural.convert(make="line", ids=...)` to force centerline
creation.

| Call syntax | What it does |
|---|---|
| `object.inspect(ids, detail="summary", max_points=200)` | Read object facts. `summary` is the compact geometry/classification view; `topology` adds Brep topology and oriented bounds; `full` adds bounded vertex/edge coordinates. Start with summary and request detail only for selected objects. |
| `object.delete(ids)` | Delete the given objects and return the tags deleted. Destructive; tags are read before deletion. |
| `structural.convert(ids=None, layers=None, make=None, control_points=10, method="auto", segments=None)` | Classify closed solids and closed meshes and create axis curves for linear members and mid-surfaces for plates, each with user text `structural_section` (`"300 x 500"` / `"t = 200"`). `ids=None` means every visible one. Without `layers` it only lists the layers (count, typical size, suggested kind); with `layers={...}` it converts. `make="line"` or `"surface"` forces conversion for explicit IDs. `method` selects `auto`, end-cap, or sliced; a sliced axis is a straight line when within 0.5 % of one, else a curve rebuilt with `control_points`. |
| `structural.grid(grid="auto", ids=None, tolerance=None, x_spacing=None, y_spacing=None, z_spacing=None, origin=None, angle=0.0, axes=("X", "Y", "Z"), min_support=2, round_to=None, base_layer="Grid", labels=True, dimensions=True)` | Derive a grid from structural points (`auto`) or describe/draw one from spacing (`spacing`). It draws the grid curves (each named after its grid line) and nothing else - no named planes, no snapping. A rotated grid can use `angle` and `origin`. Follow with `structural.snap`. |
| `structural.snap(ids=None, tolerance=None, grid_layer="Grid", grid_ids=None, grid_points=None, levels=None, min_support=2, nonplanar="split")` | Snap member endpoints and eligible surface corners onto grid curves/points. Uses node, then curve, then level priority; moves each point within grid tolerance + half its member's section (the widest at a joint). `nonplanar` is `split`, `mesh`, `error`, or `project`. |
| `structural.clean(ids=None, tolerance=None, remove_coincident=True)` | Delete overlapping or coincident duplicate members. |
| `structural.intersect(ids=None, tolerance=None)` | Split members crossing away from their ends into connected pieces (`structural.report` lists the crossings). |
| `structural.connect(ids=None, tolerance=None, nonplanar="split")` | Join near-miss member endpoints: two members join when their solids would touch (base + half of each section, per axis - `core_members.pair_gap`). |
| `structural.report(ids=None, tolerance=None, max_findings=25)` | Non-mutating node-graph/connectivity report for members and eligible surfaces. Lists near misses, crossings, duplicates, isolated members, surface intersections/gaps, and free ends. |

Example sequence: `structural.convert()`, `structural.convert(layers={...})`,
`structural.connect()`, `structural.grid()`,
`structural.snap()`, then `structural.report()`.

### Vision and view utilities

These are the 21 entries in `vision_utils/registry.py`. A common call chain is
`select(...)` -> pass its `ids` to another tool -> optionally
`vision.capture(...)` to inspect the result. Selection filters combine with
AND. Supported keys are `type`, `layer`, `name`, `attribute_key`,
`attribute_value`, `bbox`, `bbox_mode`, `plane`, `plane_distance`,
`plane_mode`, `near_point`, `radius`, `tag`, and `selected`.

| Call syntax | What it does |
|---|---|
| `select(filters=None, highlight=False, include_hidden=False, limit=None, offset=0, include_locked=False)` | Find objects by one or more filters; returns IDs/tags and counts by layer/type. Example: `select(filters={"type": ["curve"], "layer": "Beams*"})`. Use `highlight=True` only when changing Rhino's UI selection is intended. |
| `vision.capture(level=0, ids=None, viewport="active", mode="isolate", preview=None, path=None, width=None, height=None, zoom=True, folder=None, indices=None, faces=None, edges=None, vertices=None, colour_parts=True)` | Capture L0 census, L1 overview, L2 object tags, L3 face detail, or L4 edge/vertex detail. L2+ needs IDs. Writes an image and, when tagged, a JSON sidecar. |
| `vision.map(ids="all", level=2, limit=20000, path=None)` | Number/map objects and write their facts to JSON without taking a screenshot or changing the view. |
| `vision.divide(scope="all", by="space", split="middle", part_limit=20, views=["Perspective", "Top", "Front"], exact=True, rest="fade", width=None, height=None, folder=None, fade_rest=None)` | Partition a scope and capture labelled region boxes. `scope` may be `all`, part IDs, `layer:Name`, or object IDs; `by` is `space` or `layer`; `split` is `middle` or `balanced`; `rest` is `fade`, `hide`, or `show`. |
| `vision.parts(scope=None)` | List regions created by `vision.divide`, optionally limited to a part or its children. No image is taken. |
| `vision.review(ids=None, layer=None, views=None, display_mode="shaded", width=900, height=700, folder=None, tag=False)` | Capture a consistent multi-view set (standard views by default), restoring each camera/display mode afterwards. `ids` or `layer` frames the same objects in each view; `tag=True` adds labels. |
| `vision.tag(ids, level=2, persist=False)` | Add live object tags/colours. Persisted tags are written to object user text and survive reopening; temporary tags do not. |
| `vision.tag.clear(ids=None)` | Remove tags and restore original object colour settings; no IDs clears all live tags. |
| `vision.tag.level(level)` | Change the display detail of all currently tagged objects. |
| `vision.tag.list()` | List currently live tagged objects. |
| `vision.resolve_tag(tag, highlight=False)` | Resolve an object/component tag such as `#12` or `#12.F3` to live document identity and facts. `tag` may be a string or list. |
| `vision.list_subobjects(ids, kind="face")` | List component tags without a screenshot. `kind` is `face`, `edge`, or `vertex`. |
| `vision.isolate(ids, zoom=True)` | Hide everything except the supplied objects. Restoration only unhides objects this call hid. |
| `vision.isolate.restore()` | Restore visibility changed by `vision.isolate`. |
| `vision.ghost(ids, zoom=False)` | Keep the supplied objects opaque and ghost the rest, preserving context. |
| `vision.ghost.clear()` | Undo ghost display overrides. |
| `vision.clear_all()` | Clear live tags, restore isolated objects, and clear ghost overrides. |
| `vision.zoom(target="selection", ids=None, layer=None, plane=None, viewport="active", orient=False)` | Frame a selection, layer, plane, or all geometry. `orient=True` points the camera at the plane before zooming. |
| `vision.show_points(ids, control_points=True, direction=True)` | Draw curve control points and/or direction markers (green start, red end, arrow). |
| `vision.show_points.hide(ids=None)` | Remove point/direction markers for given curves, or all when omitted. |
| `plane.push_to_views(plane=None, source="active", switch_to=None, skip_layouts=True)` | Copy one construction plane (CPlane) to all model viewports. A supplied named/explicit plane takes precedence over `source`; layouts are skipped by default. |

Examples: `vision.capture(level=2, ids=["#12", "#15"], mode="transparent")`,
`vision.capture(level=3, ids="#12", faces=[0, 2])`,
`vision.divide(scope="layer:Beams", by="space")`, and
`vision.resolve_tag(tag="#12.F3")`.

## 3. Vision tools

### Capture levels

`vision.capture` is implemented in `vision_utils/vision_capture.py`. It has
five levels; L0 deliberately does not create an image.

| Level | What is captured |
|---|---|
| L0 | Document census only: units, tolerance, views, counts and related state. |
| L1 | Whole-scene image without object labels. |
| L2 | Target objects with `#12`-style object labels. |
| L3 | L2 plus curve start/end markers, or individually coloured and labelled Brep faces (`#12.F3`). |
| L4 | L3 plus individually coloured and labelled Brep edges (`#12.E3`) and vertex markers. |

For L2-L4, `ids` is required. `ids="all"` numbers visible objects, capped
at 60 to avoid an unreadable image. New tags are assigned in spatial order;
existing tag numbers remain stable. `ids` may also refer to regions created
by `vision.divide`.

### Screenshot production: RhinoCommon calls

The capture path is:

1. Resolve the requested `RhinoView` by viewport name. Optionally change its
   display mode using `Rhino.Display.DisplayModeDescription`.
2. For L2+, optionally isolate the targets or ghost the other objects, then
   attach a `Rhino.Display.DisplayConduit` (`TagConduit`) to each target.
3. Optionally frame the targets using their bounding box. `core_view.zoom_to`
   calls `RhinoViewport.ZoomBoundingBox` (or `ZoomExtents` for the whole scene).
4. Capture the viewport using `RhinoView.CaptureToBitmap`, optionally with a
   `System.Drawing.Size`, then write it with `Bitmap.Save(path)` and dispose
   the bitmap.
5. Write a JSON sidecar next to the image. The receipt returns both paths,
   viewport information, tags, warnings and (at L3/L4) a colour legend.

`CaptureToBitmap` captures Rhino's viewport drawing. It is not a screenshot
made by an external screen-grabbing package and it does not itself ask an AI
to interpret the model. Image interpretation happens later in the agent.
Requested dimensions are adjusted to preserve the live viewport aspect ratio;
the conduit also receives a temporary scale factor so its label-spacing rules
match the rendered image.

### Labels, face colours and the JSON sidecar

`vision_utils/core_vision.py` implements the conduit and state handling.
Object labels are drawn with `DisplayPipeline.DrawDot`; leaders use
`DrawLine`. At L3, curve endpoints use `DrawSphere`, with green for start and
red for end. Brep face centroids are calculated from duplicated faces using
`AreaMassProperties.Compute`.

For face colour overlays, the conduit duplicates each Brep face, converts it
to meshes with `Mesh.CreateFromBrep`, and assigns a stable flat vertex colour.
The meshes are drawn with `DrawMeshFalseColors` in `PostDrawObjects`, which
uses the depth buffer: nearer geometry occludes faces behind it. The mesh is
lifted slightly along its vertex normals to avoid z-fighting with the source
face. At L4, Brep edge curves are duplicated and nudged away from adjacent
face normals before `DrawCurve`; vertices are marked with spheres. Labels are
screen-space placed to reduce collisions, using `WorldToClient`,
`PixelsPerUnit`, and `CameraX`/`CameraY`; labels that cannot fit are omitted
rather than drawn on top of each other.

The overlay colour is an identifier, not a material or analysis result.
`component_legend` returns the exact RGB-to-component mapping, such as
`"#12.F3": [r, g, b]`. Component indices are Brep topology indices, not
permanent semantic face names; edits to a Brep can change them.

The adjacent JSON sidecar maps each object tag to its GUID, type, layer,
bounding box, size, centre, colour, and geometry-specific facts. Breps also
include solid status and face/edge counts; higher levels include face/edge/
vertex tags. The image helps the model understand spatial relationships; the
sidecar supplies exact identity and numeric facts. The image itself is not a
reliable source for exact dimensions or GUIDs.

### Temporary document changes and limits

Capture uses `try/finally` to remove conduits and restore original object
colour sources, unhide only objects it hid, restore previous display-mode
overrides for ghosted objects, and restore the preview display mode. The
tagging system hooks Rhino document add/delete/replace events so edits do not
leave stale conduit references or duplicate tags on copies.

The camera framing is not restored: with `zoom=True`, the viewport remains
zoomed to the target (or extents). A requested `preview` display mode is
restored. Dense L3/L4 captures can still have crowded labels; narrowing with
`faces=[...]`, `edges=[...]`, `vertices=[...]`, or `indices=[...]` is the
intended remedy. `vision.clear_all` clears state left by explicit tag,
isolate, or ghost tool calls.

The automated checker in `../agent/vision.py` typically starts at L2 for a
small build, or `vision.divide` for a large one. It can request a deeper part,
number a small part, or ask for L3/L4 detail, then returns `OK` or a reported
problem. This checker is an agent workflow, not part of `CaptureToBitmap`.

## 4. Structural representation and conversion

Structural tools turn architectural solids into analysis-oriented generating
geometry. Their convention is:

- A MEMBER is any curve or open surface not on a grid layer (a layer whose
  name contains "grid") - `core_structure.members`. Solids and meshes are
  never members. No layer names are required.
- Convert puts axes on `<source layer>::axis` and mid-surfaces on
  `<source layer>::base`, only to keep them apart from the solids.
- The original solid remains in the document. Generated objects store user
  strings `structural_source` (the solid / mesh; older files `nextra_source`)
  and `structural_section` - `"300 x 500"` on a line, `"t = 200"` on a surface.

The `structural.*` entry points live in `structural_utils/registry.py`.
`structural.convert` classifies visible closed Breps/extrusions and closed
meshes. Previously converted sources are skipped. The
call without `layers` reports classifications without creating geometry;
with `layers` it creates axes and mid-surfaces. Block-like objects are reported
but intentionally left for an engineer to decide.

### Shape classification

`structural_utils/core_fe.py` reuses `core_centerline.principal_frame` to get
three oriented extents. After sorting them as `thin <= mid <= long`, it
computes `slenderness = long / mid` and `flatness = mid / thin`:

- `linear`: slenderness is at least 3 and is no smaller than flatness.
- `planar`: flatness is at least 3 and is greater than slenderness.
- `block`: neither rule wins.

Layer/object names and selected user strings are hints and disagreement
between a stated role and the geometric classification is reportable; names
do not decide the class. The ratio is a heuristic, not a structural design
standard.

### Centerlines

There are two algorithms because the end-cap shortcut is exact for constant
sections but does not describe a taper.

**Linear/prismatic: `structural.convert` (end-cap route)**

1. Coerce the selected Brep or extrusion to a `Rhino.Geometry.Brep`.
2. For every face, call `BrepFace.TryGetPlane(planar_tol)`; keep planar faces.
3. Duplicate each candidate face with `DuplicateFace(false)` and measure area
   with `AreaMassProperties.Compute`.
4. Treat the two smallest planar faces as end caps. Compute each face's area
   centroid and create an `Rhino.Geometry.Line` between the centroids.
5. Add the result to the document (`AddLine` in the direct tool; a
   `LineCurve` when the structural conversion pipeline needs a curve). The
   receipt can include line length and section area.

This is fast and appropriate for prismatic beams and columns. It assumes the
two smallest planar faces are the two ends; unusual solids, extra small planar
faces, or non-prismatic members can violate that assumption. `planar_tol`
defaults to `0.001` model units.

**Complex/tapered/curved/mesh: the sliced route, the auto fallback**

The end-cap line is only accepted when the section centroid half way along
lies on it (within 0.5 % of the length) - a curved beam has flat end caps
too, and the line between them is only its chord.

1. Require a closed solid or a closed mesh. `VolumeMassProperties.Compute` supplies volume,
   centroid, and principal axes/moments via
   `CentroidCoordinatesPrincipalMoments()`.
2. Sort the returned moments; the current implementation treats the axis
   associated with the largest moment as the member's long axis. When two
   moments are nearly equal, `_settle_pair` uses the Brep's straight edges to
   choose a less arbitrary cross-section orientation. The extents are the
   tight box in that frame (`GetBoundingBox(plane)`) - vertices alone gave a
   pipe zero thickness.
3. Place `segments` stations in the MIDDLE of equal slices along the
   long-axis extent (never at the ends: a cut through an end face gives a
   broken section), and extend the two end points from the nearest two. At
   each station, cut with a plane normal to that axis
   (`Intersection.BrepPlane`, or `MeshPlane` for a mesh).
4. Cap the resulting section curves using `Brep.CreatePlanarBreps`. Measure
   each region with `AreaMassProperties.Compute` and take the area-weighted
   centroid, so multiple disjoint regions are weighted by their areas.
5. If a station misses the solid, try the oriented bounding-box Brep; if
   that also misses, use the station point.
6. Smooth (`core_centerline.smooth_centerline`): if every point is within
   0.5 % of the length from the line through the two ends, the axis is that
   straight line; otherwise the polyline is rebuilt (`Curve.Rebuild`) with
   `control_points` control points (default 10, degree 3). The number of
   stations is at least `2 x control_points`. The section of a sliced member
   is measured on a cut square to the new axis at its middle (the smallest
   box over the cut) - the principal box of an arch includes its rise.

More segments follow curvature/taper more closely but require more section
intersections. This is a sampled polyline approximation, not an analytic
medial axis. The axis selection depends on Rhino's returned principal-moment
convention; unusual or nearly symmetric shapes should be visually checked.

For `structural.convert(method="auto")`, `core_fe.centreline` first tries
the end-cap algorithm; if it cannot identify caps, the tool tries the sliced
algorithm. `method="endcap"` and `method="sliced"` request one route.
`make="line"` / `make="surface"` force an axis or a mid-surface for the ids
given, whatever the classification says.

### Mid-surfaces

For a plate-like solid, `core_fe.mid_surface` finds the two largest planar
faces and checks that their plane normals are parallel or antiparallel
(`abs(dot) >= 0.99`). It measures the separation, duplicates one face, and
translates it half-way toward the opposite face. This yields the mid-surface
and thickness. If no suitable planar pair exists, it offsets the largest
face inward by half the estimated thickness using
`UnderlyingSurface().Offset(...)`. That curved-plate route is approximate;
offset failures are reported rather than silently replaced.

## 5. Grid and connectivity tools

### `structural.grid` - makes a grid, moves nothing

The tool derives a rectilinear grid from member points (`grid="auto"`) or
builds one from explicit spacing (`grid="spacing"`). Auto mode uses vertical
members as X/Y supports when available and member points for levels;
otherwise it uses all selected member points. It clusters coordinates within
the tolerance (median of each cluster) and reports candidate lines and
distances. `angle=` works in a turned frame: points are rotated by `-angle`
about `origin`, the orthogonal logic runs unchanged, and the drawn lines,
labels and dimensions are rotated back.

It draws the lines on
`Grid::X/Y/Z::Lines` - each line's object name is its grid name (`1`, `A`,
`L1`). Nothing else: no named planes - the nodes are where the lines
cross, and `structural.snap` (and the `where` of `vision.capture`) find
them from the lines. It does not move geometry.

### `structural.snap` - snaps onto any grid

It reads every curve on the grid layer (or `grid_ids`), and/or node
positions given as `grid_points` (their Z values count as levels). Curves on a
`<grid>::Z` sub-layer are level lines (their start height is the level);
vertical curves are ignored; the others are projected to world XY, and plan
lines repeated at several levels are merged. Nodes are the plan
intersections of pairs of grid curves (`Intersection.CurveCurve`), named
`<name>/<name>` after the curves' object names. Per point the priority is
node, then the closest point on a grid curve in plan (`Curve.ClosestPoint`),
then Z to the nearest level (`levels=` > level lines > the Z of
`grid_points` > clusters of the model's heights). Only points within
tolerance move. It moves the members, never the grid itself. For surfaces,
moving corners can make a face non-planar; the `nonplanar` policy (`split`,
`mesh`, `error`, `project`) decides, and the receipt includes notes; the check
mode previews faces that would leave their plane. Joining points to EACH
OTHER without a grid is `structural.connect`.

### Cleanup, joining, and intersections

- Moving points (snap, connect): polygonal Brep face loops are rebuilt after
  moving corners. Curved-edge Breps are not silently flattened; they are
  skipped and noted.
- `structural.clean` identifies coincident curves and shorter collinear
  members covered by longer ones and deletes the duplicates.
- `structural.intersect` finds curve intersections with
  `Intersection.CurveCurve`, excludes overlaps and intersections at both
  member ends, and splits curves at interior parameters with `Curve.Split`.
  It adds valid pieces and deletes the original only after a successful
  split.
- `structural.connect` plans where each line end goes (`core_connect`):
  along its own axis, onto a crossing member's span (split there), else the
  point that turns it least - within each pair's gap, base + half of each
  section per axis (`core_members.pair_gap`). Rounds repeat until every end
  within reach shares its point.

### `structural.report`

The report inspects the generating geometry; it does not create a finite
element mesh, assign releases/materials/loads/supports, or solve the
structure. It clusters curve endpoints into nodes with per-axis tolerances,
then reports:

- near misses: separate nodes within a limited multiple of the joining
  tolerance;
- crossings: interior curve intersections that are not already endpoint
  joints;
- duplicate/overlapping members and members with both ends free;
- ordinary free ends, which may be valid supports/cantilevers and are only
  listed;
- for open surfaces, member pierces/touches, edges inside other surfaces,
  and near-gap pairs.

For larger selections, pairwise crossing and duplicate checks are skipped
above 400 members and the receipt warns about the skipped checks. A clean
report means the tested geometric node graph passed these checks, not that
the model is ready to solve without engineering properties, loads, supports,
or FE-mesh-specific intersection handling.

## 6. Source map

| Concern | Implementation |
|---|---|
| Vision tool registration | `vision_utils/registry.py` |
| Capture orchestration and sidecar | `vision_utils/vision_capture.py` |
| Conduit, labels, colours, isolate/ghost | `vision_utils/core_vision.py` |
| Viewport camera, display mode, bitmap capture | `vision_utils/core_view.py` |
| Agent image attachment and checker | `../agent/vision.py` |
| Session registry and snapshots | `session_utils/registry.py`, `session_utils/session_snapshot.py`, `session_utils/core_snapshot.py` |
| Structural tool registration | `structural_utils/registry.py` |
| Conversion, classifier handoff, output layers | `structural_utils/structural_convert.py` |
| Shape classification and plate geometry | `structural_utils/core_fe.py` |
| Both centerline algorithms | `structural_utils/core_centerline.py` |
| Centerline tool entry point, sections, smoothing | `structural_utils/structural_convert.py`, `core_centerline.smooth_centerline` |
| Grid making | `structural_utils/structural_grid.py`, `structural_utils/core_grid.py`, `structural_utils/core_grid_auto.py` |
| Snapping and point movement | `structural_utils/structural_snap.py`, `structural_utils/core_tidy.py` |
| Oriented bounding box | `structural_utils/analyse_oriented_bbox.py` (maths: `core_centerline.oriented_bbox`), reported by `object.inspect` |
| Connectivity checks and curve splitting | `structural_utils/structure.py`, `structural_utils/core_structure.py` |
| Structural tidy wrappers | `structural_utils/structural_tidy.py` |