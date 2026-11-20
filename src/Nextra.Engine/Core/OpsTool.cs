using System;
using System.Collections.Generic;
using Color = System.Drawing.Color;
using System.Linq;
using System.Text.Json.Nodes;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Nextra.Engine.Gh;
using Nextra.Engine.Rh;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Nextra.Engine
{
    /// `ops`: a menu of small typed Rhino / Grasshopper functions (and a few workflows) for the decision-API assistant.
    /// Every function says what it does, what it acts on, and its values as typed slots (the decision chain's Slot shape), so a
    /// decision model can choose them; every run is checked, made as ONE Rhino undo step and one Nextra checkpoint.
    ///   {action:'list'}                      the menu (functions, workflows, the model's units, layers and GH inputs)
    ///   {action:'elements', limit?}          the objects the assistant may target: handle, type, layer, name, selected
    ///   {action:'run', fn, args, targets[]}  check and run one function (targets = r-handles; [] for none)
    /// Also usable by any MCP client (it is a normal engine tool).
    public static class OpsTool
    {
        class Op
        {
            public string Name, Group, Description;
            public bool Workflow, Answer, Gh, SelfManaged;   // SelfManaged: gh_edit makes its own checkpoint
            public string Target;            // null = acts on nothing existing; "objects" | "sliders" | "toggles" | "panels"
            public bool TargetOptional;
            public Func<JsonArray> Slots;
            public Action<Ctx> Run;
        }

        class Ctx
        {
            public RhinoDoc Doc; public JsonObject Args; public List<RhinoObject> Objs = new List<RhinoObject>();
            public List<IGH_DocumentObjectLike> GhObjs = new List<IGH_DocumentObjectLike>();
            public Reply R; public double Scale = 1;   // model units per slot unit (slots in the model's own unit: 1)
            public double Num(string k, double d = double.NaN) => Args[k] is JsonValue v && v.TryGetValue<double>(out var x) ? x : d;
            public string Str(string k, string d = null) => Args[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : d;
            public bool Bool(string k, bool d = false) => Args[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
            public Vector3d Vec(string k)
            {
                if (!(Args[k] is JsonArray a) || a.Count != 3) throw new NxError($"'{k}' must be [x, y, z]");
                return new Vector3d((double)a[0], (double)a[1], (double)a[2]);
            }
            public Point3d Pt(string k, Point3d d)
            {
                if (!(Args[k] is JsonArray a) || a.Count != 3) return d;
                return new Point3d((double)a[0], (double)a[1], (double)a[2]);
            }
            public Point3d Center()
            {
                var bb = BoundingBox.Empty;
                foreach (var o in Objs) bb.Union(o.Geometry.GetBoundingBox(true));
                return bb.IsValid ? bb.Center : Point3d.Origin;
            }
            public void Need(int n = 1) { if (Objs.Count < n) throw new NxError("no objects to act on", "name them (r12), select them, or say which (layer, type)"); }
        }

        /// a Grasshopper object, without referencing GH types in the menu's signatures
        class IGH_DocumentObjectLike { public object O; public string Handle; }

        // ---- slot helpers (the decision chain's Slot shape) -------------------------------------------------------------------
        static JsonObject S(string kind, string name, string desc, bool optional = false, string unit = null, string[] values = null, bool integer = false)
        {
            var o = new JsonObject { ["kind"] = kind, ["name"] = name, ["description"] = desc };
            if (optional) o["optional"] = true;
            if (unit != null) o["unit"] = unit;
            if (values != null) o["values"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray());
            if (integer) o["integer"] = true;
            return o;
        }
        static JsonArray Slots(params JsonObject[] s) => new JsonArray(s);
        static string _unit = "";     // the model's length unit as a slot unit ('mm' | 'm' | '')
        static string[] _layers = new string[0];

        static readonly string[] Colours = { "red", "green", "blue", "yellow", "cyan", "magenta", "orange", "black", "white", "grey", "by layer" };
        static readonly string[] About = { "count", "length", "area", "volume", "size", "layer", "type" };

        static List<Op> _menu;
        static List<Op> Menu => _menu ??= Build();

        static List<Op> Build() => new List<Op>
        {
            // ---- questions (nothing changes) ------------------------------------------------------------------------------------
            new Op { Name = "ask_model", Group = "query", Answer = true, Description = "answer a question about the whole model: how many objects (of a type, on a layer), which layers, the units, the overall size. Not about one object (ask_object).",
                Slots = () => Slots(S("choice", "about", "what is asked", values: new[] { "counts", "layers", "units", "size", "selection" })), Run = AskModel },
            new Op { Name = "ask_object", Group = "query", Answer = true, Target = "objects", Description = "answer a question about the named or selected objects: length, area, volume, size, layer, type. Changes nothing.",
                Slots = () => Slots(S("choice", "about", "what is asked about the objects", values: About)), Run = AskObject },
            // ---- selection --------------------------------------------------------------------------------------------------------
            new Op { Name = "select", Group = "selection", Target = "objects", Description = "select objects (by layer, type, name or handle); replaces the selection unless add is true. Not for changing them.",
                Slots = () => Slots(S("boolean", "add", "add to the current selection instead of replacing it (default: replace)", optional: true)), Run = Select },
            new Op { Name = "unselect_all", Group = "selection", Description = "clear the selection", Slots = () => Slots(), Run = c => { c.Doc.Objects.UnselectAll(); c.R.Line("selection cleared"); } },
            // ---- transforms ---------------------------------------------------------------------------------------------------------
            new Op { Name = "move", Group = "transform", Target = "objects", Description = "move objects by a distance along a direction (not a copy)",
                Slots = () => Slots(S("vector", "by", "the move: a distance along a direction", unit: _unit)), Run = c => Xform(c, Transform.Translation(c.Vec("by") * c.Scale), false, "moved") },
            new Op { Name = "copy", Group = "transform", Target = "objects", Description = "copy objects, the copy moved by a distance along a direction (the originals stay)",
                Slots = () => Slots(S("vector", "by", "where the copy goes: a distance along a direction", unit: _unit)), Run = c => Xform(c, Transform.Translation(c.Vec("by") * c.Scale), true, "copied") },
            new Op { Name = "rotate", Group = "transform", Target = "objects", Description = "rotate objects by an angle about a vertical (or x / y) axis through their centre or the origin",
                Slots = () => Slots(S("number", "angle", "the angle, counter-clockwise positive", unit: "deg"), S("choice", "axis", "the rotation axis (default z, vertical)", true, values: new[] { "z", "x", "y" }), S("choice", "about", "the point it turns about (default: their centre)", true, values: new[] { "centre", "origin" })),
                Run = c => { var ax = c.Str("axis", "z"); var v = ax == "x" ? Vector3d.XAxis : ax == "y" ? Vector3d.YAxis : Vector3d.ZAxis; Xform(c, Transform.Rotation(RhinoMath.ToRadians(c.Num("angle")), v, c.Str("about", "centre") == "origin" ? Point3d.Origin : c.Center()), false, "rotated"); } },
            new Op { Name = "scale", Group = "transform", Target = "objects", Description = "scale objects uniformly by a factor (2 = twice as big) about their centre or the origin",
                Slots = () => Slots(S("number", "factor", "the scale factor"), S("choice", "about", "the point it scales about (default: their centre)", true, values: new[] { "centre", "origin" })),
                Run = c => Xform(c, Transform.Scale(c.Str("about", "centre") == "origin" ? Point3d.Origin : c.Center(), c.Num("factor")), false, "scaled") },
            new Op { Name = "mirror", Group = "transform", Target = "objects", Description = "mirror objects across a plane through their centre (x = flip left-right); keeps the originals unless replace is true",
                Slots = () => Slots(S("choice", "across", "flip along this axis", values: new[] { "x", "y", "z" }), S("boolean", "replace", "remove the originals (default: keep them)", true)),
                Run = c => { var a = c.Str("across", "x"); var n = a == "y" ? Vector3d.YAxis : a == "z" ? Vector3d.ZAxis : Vector3d.XAxis; Xform(c, Transform.Mirror(new Plane(c.Center(), n)), !c.Bool("replace"), "mirrored"); } },
            new Op { Name = "array_linear", Group = "transform", Target = "objects", Description = "make copies in a row, each one step further than the one before",
                Slots = () => Slots(S("number", "count", "how many copies (the original not counted)", integer: true), S("vector", "step", "the distance between neighbours along a direction", unit: _unit)), Run = ArrayLinear },
            new Op { Name = "array_polar", Group = "transform", Target = "objects", Description = "make copies around a vertical axis (a polar array) through the origin or the objects' centre",
                Slots = () => Slots(S("number", "count", "how many copies (the original not counted)", integer: true), S("number", "angle", "the total angle they fill (default 360)", true, unit: "deg"), S("choice", "about", "the axis position (default: the origin)", true, values: new[] { "origin", "centre" })), Run = ArrayPolar },
            // ---- edit ----------------------------------------------------------------------------------------------------------------
            new Op { Name = "delete", Group = "edit", Target = "objects", Description = "delete objects", Slots = () => Slots(),
                Run = c => { c.Need(); int n = c.Objs.Count(o => c.Doc.Objects.Delete(o, true)); c.R.Line($"deleted {n}"); } },
            new Op { Name = "join", Group = "edit", Target = "objects", Description = "join curves (end to end) or surfaces into one", Slots = () => Slots(), Run = Join },
            new Op { Name = "explode", Group = "edit", Target = "objects", Description = "explode polycurves into segments and polysurfaces into faces", Slots = () => Slots(), Run = Explode },
            new Op { Name = "offset_curve", Group = "edit", Target = "objects", Description = "offset planar curves by a distance, outwards or inwards (in their plane)",
                Slots = () => Slots(S("number", "distance", "the offset distance", unit: _unit), S("choice", "side", "outwards or inwards (default outwards)", true, values: new[] { "out", "in" })), Run = Offset },
            new Op { Name = "extrude", Group = "edit", Target = "objects", Description = "extrude curves straight up (z) by a height into surfaces; closed planar curves become capped solids",
                Slots = () => Slots(S("number", "height", "the extrusion height (negative = down)", unit: _unit)), Run = Extrude },
            new Op { Name = "boolean_union", Group = "edit", Target = "objects", Description = "merge overlapping closed solids into one", Slots = () => Slots(), Run = Union },
            // ---- attributes ------------------------------------------------------------------------------------------------------------
            new Op { Name = "set_layer", Group = "attributes", Target = "objects", Description = "move objects to a layer (made if it does not exist)",
                Slots = () => Slots(S("catalogue", "layer", "the layer", values: _layers)), Run = SetLayer },
            new Op { Name = "set_colour", Group = "attributes", Target = "objects", Description = "give chosen objects a display colour (or back to their layer's colour). Not the colour of a layer itself (layer_colour)",
                Slots = () => Slots(S("choice", "colour", "the colour", values: Colours)), Run = SetColour },
            new Op { Name = "rename", Group = "attributes", Target = "objects", Description = "give objects a name",
                Slots = () => Slots(new JsonObject { ["kind"] = "free", ["name"] = "name", ["description"] = "the new name", ["schema"] = new JsonObject { ["type"] = "string" } }),
                Run = c => { c.Need(); var nm = c.Str("name") ?? throw new NxError("no name given"); foreach (var o in c.Objs) { o.Attributes.Name = nm; o.CommitChanges(); } c.R.Line($"named {c.Objs.Count} '{nm}'"); } },
            new Op { Name = "hide", Group = "attributes", Target = "objects", Description = "hide chosen objects (show_all brings them back). Not for turning a whole layer off (layer_visibility)", Slots = () => Slots(),
                Run = c => { c.Need(); int n = c.Objs.Count(o => c.Doc.Objects.Hide(o, true)); c.R.Line($"hid {n}"); } },
            new Op { Name = "show_all", Group = "attributes", Description = "show all hidden objects", Slots = () => Slots(), Run = c => { var hid = c.Doc.Objects.GetObjectList(new ObjectEnumeratorSettings { HiddenObjects = true, NormalObjects = false, LockedObjects = false }).ToList(); int n = hid.Count(o => c.Doc.Objects.Show(o, true)); c.R.Line($"showed {n}"); } },
            new Op { Name = "lock", Group = "attributes", Target = "objects", Description = "lock chosen objects (unlock_all frees them). Not a whole layer (lock_layer)", Slots = () => Slots(),
                Run = c => { c.Need(); int n = c.Objs.Count(o => c.Doc.Objects.Lock(o, true)); c.R.Line($"locked {n}"); } },
            new Op { Name = "unlock_all", Group = "attributes", Description = "unlock all locked objects", Slots = () => Slots(), Run = c => { var lk = c.Doc.Objects.GetObjectList(new ObjectEnumeratorSettings { LockedObjects = true, NormalObjects = false, HiddenObjects = false }).Where(o => o.IsLocked).ToList(); int n = lk.Count(o => c.Doc.Objects.Unlock(o, true)); c.R.Line($"unlocked {n}"); } },
            new Op { Name = "group", Group = "attributes", Target = "objects", Description = "group objects together", Slots = () => Slots(),
                Run = c => { c.Need(2); var gi = c.Doc.Groups.Add(c.Objs.Select(o => o.Id)); c.R.Line($"grouped {c.Objs.Count} (group {gi})"); } },
            new Op { Name = "layer_visibility", Group = "attributes", Description = "turn a whole layer on or off (the layer's own setting). Not for hiding chosen objects (hide)",
                Slots = () => Slots(S("catalogue", "layer", "the layer", values: _layers), S("boolean", "visible", "on (true) or off (false)")), Run = LayerVisibility },
            // ---- tidy (layers and clean-up) ----------------------------------------------------------------------------------------------
            new Op { Name = "sort_by_type", Group = "tidy", Target = "objects", TargetOptional = true, Description = "tidy: put objects on layers by their type (Curves, Surfaces, Solids, Meshes, Points, Annotations, Blocks) under a parent layer; all objects when none are named or selected",
                Slots = () => Slots(new JsonObject { ["kind"] = "free", ["name"] = "parent", ["description"] = "the parent layer (default Sorted)", ["optional"] = true, ["schema"] = new JsonObject { ["type"] = "string" } }), Run = SortByType },
            new Op { Name = "delete_empty_layers", Group = "tidy", Description = "tidy: delete every layer that has no objects (and no sub-layers with objects); never the current layer", Slots = () => Slots(), Run = DeleteEmptyLayers },
            new Op { Name = "merge_layers", Group = "tidy", Description = "move every object of one layer to another and delete the emptied layer",
                Slots = () => Slots(S("catalogue", "from", "the layer to empty and remove", values: _layers), S("catalogue", "into", "the layer that receives the objects", values: _layers)), Run = MergeLayers },
            new Op { Name = "rename_layer", Group = "tidy", Description = "rename a layer",
                Slots = () => Slots(S("catalogue", "layer", "the layer", values: _layers), new JsonObject { ["kind"] = "free", ["name"] = "name", ["description"] = "the new name", ["schema"] = new JsonObject { ["type"] = "string" } }), Run = RenameLayer },
            new Op { Name = "layer_colour", Group = "tidy", Description = "set the colour of a layer itself (the layer's own setting, named by the layer). Not for colouring chosen objects (set_colour)",
                Slots = () => Slots(S("catalogue", "layer", "the layer", values: _layers), S("choice", "colour", "the colour", values: Colours.Where(x => x != "by layer").ToArray())), Run = LayerColour },
            new Op { Name = "lock_layer", Group = "tidy", Description = "lock or unlock a whole layer (the layer's own setting). Not for locking chosen objects (lock)",
                Slots = () => Slots(S("catalogue", "layer", "the layer", values: _layers), S("boolean", "locked", "locked (true) or unlocked (false)")), Run = LockLayer },
            new Op { Name = "isolate_layer", Group = "tidy", Description = "show only this layer (all other layers off); show_all_layers undoes it",
                Slots = () => Slots(S("catalogue", "layer", "the layer to keep on", values: _layers)), Run = IsolateLayer },
            new Op { Name = "show_all_layers", Group = "tidy", Description = "turn every layer on", Slots = () => Slots(), Run = ShowAllLayers },
            new Op { Name = "set_current_layer", Group = "tidy", Description = "make a layer the current layer (new objects go there)",
                Slots = () => Slots(S("catalogue", "layer", "the layer", values: _layers)), Run = c => { int li = LayerIndex(c.Doc, c.Str("layer"), false); if (li < 0) throw new NxError($"no layer '{c.Str("layer")}'"); c.Doc.Layers.SetCurrentLayerIndex(li, true); c.R.Line("current layer " + c.Doc.Layers[li].FullPath); } },
            new Op { Name = "delete_duplicates", Group = "tidy", Target = "objects", TargetOptional = true, Description = "tidy: delete exact duplicate objects (same geometry on top of each other), keeping one; all objects when none are named or selected", Slots = () => Slots(), Run = DeleteDuplicates },
            new Op { Name = "delete_small", Group = "tidy", Target = "objects", TargetOptional = true, Description = "tidy: delete tiny leftover objects smaller than a size (their overall size); all objects when none are named or selected",
                Slots = () => Slots(S("number", "size", "the size below which an object is deleted", unit: _unit)), Run = DeleteSmall },
            new Op { Name = "align", Group = "transform", Target = "objects", Description = "align objects to each other along one side (left = smallest x, front = smallest y, bottom = smallest z) or their centres",
                Slots = () => Slots(S("choice", "to", "the side or centre they line up on", values: new[] { "left", "right", "centre_x", "front", "back", "centre_y", "bottom", "top", "centre_z" })), Run = Align },
            new Op { Name = "move_to_origin", Group = "transform", Target = "objects", Description = "move objects together so their lower corner (or their centre) sits at the origin",
                Slots = () => Slots(S("choice", "by", "which point goes to the origin (default: the lower corner)", true, values: new[] { "corner", "centre" })), Run = MoveToOrigin },
            // ---- create ------------------------------------------------------------------------------------------------------------------
            new Op { Name = "add_box", Group = "create", Description = "make a box of a width (x), depth (y) and height (z) with its lower corner at a point (default the origin)",
                Slots = () => Slots(S("number", "width", "size in x", unit: _unit), S("number", "depth", "size in y", unit: _unit), S("number", "height", "size in z", unit: _unit), S("point", "at", "the lower corner (default: the origin)", true)), Run = AddBox },
            new Op { Name = "add_circle", Group = "create", Description = "make a circle of a radius, flat (xy), centred at a point (default the origin)",
                Slots = () => Slots(S("number", "radius", "the radius", unit: _unit), S("point", "at", "the centre (default: the origin)", true)), Run = AddCircle },
            new Op { Name = "add_line", Group = "create", Description = "make a line or a polyline through points in order",
                Slots = () => Slots(S("points", "points", "the points in order")), Run = AddLine },
            new Op { Name = "add_rectangle", Group = "create", Description = "make a flat rectangle of a width (x) and depth (y) with its lower-left corner at a point",
                Slots = () => Slots(S("number", "width", "size in x", unit: _unit), S("number", "depth", "size in y", unit: _unit), S("point", "at", "the lower-left corner (default: the origin)", true)), Run = AddRect },
            new Op { Name = "add_layer", Group = "create", Description = "make a new layer (and make it current)",
                Slots = () => Slots(new JsonObject { ["kind"] = "free", ["name"] = "name", ["description"] = "the layer name", ["schema"] = new JsonObject { ["type"] = "string" } }, S("choice", "colour", "its colour (default black)", true, values: Colours)), Run = AddLayer },
            // ---- Grasshopper inputs ---------------------------------------------------------------------------------------------------
            new Op { Name = "gh_set_slider", Group = "grasshopper", Gh = true, Target = "sliders", Description = "set a Grasshopper number slider to a value (the definition recomputes)",
                Slots = () => Slots(S("number", "value", "the new value")), Run = GhSlider },
            new Op { Name = "gh_set_toggle", Group = "grasshopper", Gh = true, Target = "toggles", Description = "switch a Grasshopper boolean toggle on or off",
                Slots = () => Slots(S("boolean", "value", "on (true) or off (false)")), Run = GhToggle },
            new Op { Name = "gh_recompute", Group = "grasshopper", Gh = true, Description = "recompute the open Grasshopper definition", Slots = () => Slots(),
                Run = c => { var d = GhCore.Doc(false) ?? throw new NxError("no Grasshopper definition open"); d.NewSolution(true); c.R.Line("recomputed"); } },
            new Op { Name = "gh_bake", Group = "grasshopper", Gh = true, Description = "bake the preview geometry of the open Grasshopper definition to a layer",
                Slots = () => Slots(S("catalogue", "layer", "the layer to bake to (default: Grasshopper bake)", true, values: _layers)), Run = GhBake },
            // ---- Grasshopper canvas (the shorthand engine underneath: placement, layout and checkpoints as gh_edit) -------------------
            new Op { Name = "gh_add_component", Group = "grasshopper", SelfManaged = true, Description = "drop ONE component onto the Grasshopper canvas (chosen by category, sub-category and name), placed automatically. Not for several components at once (say them all: the chat model builds them in one go)",
                Slots = () => Slots(Cat("component", "the component (category > sub-category > name)", _components, _componentPaths), new JsonObject { ["kind"] = "free", ["name"] = "nick", ["description"] = "its nickname (default none)", ["optional"] = true, ["schema"] = new JsonObject { ["type"] = "string" } }),
                Run = GhAdd },
            new Op { Name = "gh_connect", Group = "grasshopper", SelfManaged = true, Description = "wire an output of one Grasshopper object to an input of another (adds the wire; existing wires stay)",
                Slots = () => Slots(S("catalogue", "from", "the output the wire starts at", values: _ghOuts), S("catalogue", "to", "the input it goes into", values: _ghIns)), Run = GhConnect },
            new Op { Name = "gh_disconnect", Group = "grasshopper", SelfManaged = true, Description = "remove every wire going into one Grasshopper input",
                Slots = () => Slots(S("catalogue", "to", "the input to clear", values: _ghIns)), Run = c => { var pt = PortOf(c.Str("to")); int dot = pt.IndexOf('.'); if (dot < 0) throw new NxError("pick an input of a component (handle.port)"); GhEdit(c, $"{pt.Substring(0, dot)} ({pt.Substring(dot + 1)}=_)"); } },
            new Op { Name = "gh_tidy", Group = "grasshopper", SelfManaged = true, Description = "tidy the Grasshopper canvas: lay the whole definition out left to right by data flow, groups kept together", Slots = () => Slots(),
                Run = c => GhEdit(c, null, "tidy") },
            new Op { Name = "gh_group", Group = "grasshopper", Gh = true, Target = "gh_objects", Description = "put Grasshopper objects in a named, coloured group",
                Slots = () => Slots(new JsonObject { ["kind"] = "free", ["name"] = "name", ["description"] = "the group's name (default Group)", ["optional"] = true, ["schema"] = new JsonObject { ["type"] = "string" } }, S("choice", "colour", "its colour (default: by purpose)", true, values: new[] { "inputs (violet)", "logic (blue)", "geometry (orange)", "output (green)", "grey" })), Run = GhGroup },
            new Op { Name = "gh_select", Group = "grasshopper", Target = "gh_objects", Description = "select objects on the Grasshopper canvas (by category, type or name)", Slots = () => Slots(), Run = GhSelect },
            new Op { Name = "gh_delete", Group = "grasshopper", SelfManaged = true, Target = "gh_objects", Description = "delete objects from the Grasshopper canvas", Slots = () => Slots(),
                Run = c => GhEdit(c, "- " + string.Join(" ", c.GhObjs.Select(g => g.Handle))) },
            new Op { Name = "gh_preview", Group = "grasshopper", SelfManaged = true, Target = "gh_objects", Description = "show or hide the Rhino-viewport preview of Grasshopper components",
                Slots = () => Slots(S("boolean", "show", "show (true) or hide (false)")), Run = c => GhEdit(c, string.Join("\n", c.GhObjs.Select(g => $"{g.Handle} {{{(c.Bool("show", true) ? "show" : "hide")}}}"))) },
            new Op { Name = "gh_enable", Group = "grasshopper", SelfManaged = true, Target = "gh_objects", Description = "enable or disable (lock) Grasshopper components so they compute or not",
                Slots = () => Slots(S("boolean", "enabled", "enabled (true) or disabled (false)")), Run = c => GhEdit(c, string.Join("\n", c.GhObjs.Select(g => $"{g.Handle} {{{(c.Bool("enabled", true) ? "unlock" : "lock")}}}"))) },
            new Op { Name = "gh_new", Group = "grasshopper", SelfManaged = true, Description = "start a new, empty Grasshopper definition", Slots = () => Slots(),
                Run = c => GhEdit(c, null, null, true) },
            // ---- workflows (several steps, one undo) -----------------------------------------------------------------------------------
            new Op { Name = "column_grid", Group = "workflows", Workflow = true, Description = "a grid of columns (square boxes): columns in x and y, spacing in x and y, height, column size; on layer Columns",
                Slots = () => Slots(S("number", "nx", "columns in x", integer: true), S("number", "ny", "columns in y", integer: true), S("number", "sx", "spacing in x", unit: _unit), S("number", "sy", "spacing in y", true, unit: _unit),
                    S("number", "height", "column height", unit: _unit), S("number", "size", "column width", true, unit: _unit)), Run = ColumnGrid },
            new Op { Name = "floor_slabs", Group = "workflows", Workflow = true, Description = "stacked floor slabs: floors, storey height, width (x), depth (y), slab thickness; on layer Slabs",
                Slots = () => Slots(S("number", "floors", "how many floors", integer: true), S("number", "storey", "storey height", unit: _unit), S("number", "width", "slab size in x", unit: _unit), S("number", "depth", "slab size in y", unit: _unit), S("number", "thickness", "slab thickness", true, unit: _unit)), Run = FloorSlabs },
            new Op { Name = "frame_building", Group = "workflows", Workflow = true, Description = "a simple building massing: a column grid with floor slabs on every level (columns, slabs, one undo)",
                Slots = () => Slots(S("number", "nx", "bays + 1 in x (columns)", integer: true), S("number", "ny", "columns in y", integer: true), S("number", "sx", "spacing in x", unit: _unit), S("number", "sy", "spacing in y", true, unit: _unit), S("number", "floors", "floors", integer: true), S("number", "storey", "storey height", unit: _unit)), Run = FrameBuilding },
        };

        // ---- entry ------------------------------------------------------------------------------------------------------------------
        public static void Run(Args a, Reply r)
        {
            var doc = RhinoDoc.ActiveDoc ?? throw new NxError("no active Rhino document");
            Refresh(doc);
            var action = a.Str("action", "list");
            if (action == "list") { r.Line(ListJson(doc).ToJsonString()); return; }
            if (action == "elements") { r.Line(Elements(doc, a.Int("limit", 3000)).ToJsonString()); return; }
            if (action != "run") throw new NxError($"unknown action '{action}'", "action: list | elements | run");
            var fn = a.Str("fn") ?? throw new NxError("fn missing", "ops {action:'list'} shows the functions");
            var op = Menu.FirstOrDefault(o => o.Name == fn) ?? throw new NxError($"unknown function '{fn}'", "ops {action:'list'}");
            var args = a.Raw("args") as JsonObject ?? new JsonObject();
            var targets = (a.Raw("targets") as JsonArray)?.Select(x => (string)x).ToList() ?? new List<string>();
            var c = new Ctx { Doc = doc, Args = args, R = r };
            ResolveTargets(c, op, targets);

            if (op.Answer || op.SelfManaged) { op.Run(c); return; }
            var ghBefore = op.Gh ? GhCore.Snapshot() : null;
            var ck = Checkpoints.Begin("ops: " + fn, op.Gh);
            uint sn = doc.BeginUndoRecord("Nextra " + fn);
            var delta = new DeltaTracker(doc);
            Exception fail = null;
            try { op.Run(c); }
            catch (Exception e) { fail = e; }
            finally { delta.Dispose(); doc.EndUndoRecord(sn); doc.Views.Redraw(); }
            Checkpoints.Complete(ck, delta);
            bool changed = !delta.Empty || (ghBefore != null && GhCore.Changed(ghBefore));
            if (!changed) Checkpoints.Discard(ck);
            if (fail != null)
            {
                if (changed) DeltaTracker.Revert(doc, delta.Added, delta.Before);    // all or nothing
                throw fail is NxError ? fail : new NxError($"{fn} failed: {fail.Message}");
            }
            if (changed) r.Line($"checkpoint {ck["id"]} (one undo step)");
            delta.Report(r);
        }

        static void Refresh(RhinoDoc doc)
        {
            _unit = doc.ModelUnitSystem == UnitSystem.Millimeters ? "mm" : doc.ModelUnitSystem == UnitSystem.Meters ? "m" : "";
            _layers = doc.Layers.Where(l => !l.IsDeleted).Select(l => l.FullPath).ToArray();
            RefreshGh();
        }

        static JsonObject ListJson(RhinoDoc doc)
        {
            JsonObject Fn(Op o) => new JsonObject
            {
                ["name"] = o.Name, ["group"] = o.Group, ["description"] = o.Description,
                ["target"] = o.Target == null ? null : new JsonObject { ["kind"] = o.Target, ["what"] = o.Target == "objects" ? "the Rhino objects it acts on" : "the Grasshopper " + o.Target, ["optional"] = o.TargetOptional },
                ["slots"] = o.Slots(), ["answer"] = o.Answer,
            };
            var gh = GhCore.Doc(false);
            var inputs = new JsonArray();
            if (gh != null)
                foreach (var o in gh.Objects)
                    if (o is GH_NumberSlider || o is GH_BooleanToggle || o is GH_Panel)
                        inputs.Add(new JsonObject { ["h"] = GhCore.Handle(gh, o), ["kind"] = o is GH_NumberSlider ? "sliders" : o is GH_BooleanToggle ? "toggles" : "panels", ["name"] = o.NickName,
                            ["value"] = o is GH_NumberSlider s ? s.CurrentValue.ToString() : o is GH_BooleanToggle t ? t.Value.ToString().ToLowerInvariant() : Fmt.Trunc(((GH_Panel)o).UserText, 30) });
            return new JsonObject
            {
                ["units"] = doc.ModelUnitSystem.ToString().ToLowerInvariant(), ["unit"] = _unit,
                ["layers"] = new JsonArray(_layers.Select(l => (JsonNode)l).ToArray()),
                ["functions"] = new JsonArray(Menu.Where(o => !o.Workflow).Select(Fn).ToArray()),
                ["workflows"] = new JsonArray(Menu.Where(o => o.Workflow).Select(Fn).ToArray()),
                ["gh_inputs"] = inputs,
            };
        }

        static JsonObject Elements(RhinoDoc doc, int limit)
        {
            var arr = new JsonArray();
            int n = 0, total = 0;
            var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false, IncludeLights = false };
            foreach (var o in doc.Objects.GetObjectList(settings))
            {
                total++;
                if (n >= limit) continue;
                n++;
                var e = new JsonObject { ["h"] = Handles.Rh(o.Id), ["type"] = ShapeWord(o.Geometry), ["layer"] = doc.Layers[o.Attributes.LayerIndex].FullPath };
                if (!string.IsNullOrEmpty(o.Attributes.Name)) e["name"] = o.Attributes.Name;
                if (o.IsSelected(false) > 0) e["sel"] = true;
                arr.Add(e);
            }
            var ghArr = new JsonArray();
            var gd = GhCore.Doc(false);
            if (gd != null)
                foreach (var o in gd.Objects)
                {
                    var e = new JsonObject { ["h"] = GhCore.Handle(gd, o), ["name"] = o.Name, ["cat"] = o.Category, ["sub"] = o.SubCategory,
                        ["kind"] = o is Grasshopper.Kernel.IGH_Component ? "component" : o is Grasshopper.Kernel.Special.GH_Group ? "group" : o is Grasshopper.Kernel.IGH_Param ? "param" : "other" };
                    if (!string.IsNullOrEmpty(o.NickName) && o.NickName != o.Name) e["nick"] = o.NickName;
                    if (o.Attributes.Selected) e["sel"] = true;
                    ghArr.Add(e);
                }
            return new JsonObject { ["total"] = total, ["elements"] = arr, ["gh"] = ghArr };
        }

        /// the type as a person says it: rectangle, closed polyline, solid box ... (Geo.TypeWord with the shape where it is clear)
        static string ShapeWord(GeometryBase g)
        {
            var w = Geo.TypeWord(g);
            if (g is Curve c && c.IsClosed && !(g is ArcCurve))
            {
                if (c.TryGetPolyline(out var pl) && pl.Count == 5)
                {
                    bool square = true;
                    var seg = new Vector3d[4];
                    for (int i = 0; i < 4; i++) seg[i] = pl[i + 1] - pl[i];
                    for (int i = 0; i < 4; i++) { var a = seg[i]; var b = seg[(i + 1) % 4]; if (Math.Abs(a * b) > 1e-6 * a.Length * b.Length) square = false; }
                    if (square) return "rectangle";
                }
                return "closed " + w;
            }
            if (g is Extrusion ex && ex.IsSolid) return "solid " + w;
            if (g is Brep br && br.IsSolid) return "solid " + w;
            return w;
        }

        static void ResolveTargets(Ctx c, Op op, List<string> targets)
        {
            if (op.Target == null) return;
            if (op.Target == "objects")
            {
                foreach (var h in targets)
                {
                    if (!Handles.TryRh(h, out var g)) throw new NxError($"unknown object '{h}'", "ops {action:'elements'} lists the handles");
                    var o = c.Doc.Objects.FindId(g);
                    if (o != null && !o.IsDeleted) c.Objs.Add(o);
                }
                if (targets.Count == 0) c.Objs.AddRange(c.Doc.Objects.GetSelectedObjects(false, false));
                if (c.Objs.Count == 0 && op.TargetOptional)    // "all objects" functions (tidy): every normal object
                    c.Objs.AddRange(c.Doc.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = false, HiddenObjects = false }));
                return;
            }
            var gh = GhCore.Doc(false) ?? throw new NxError("no Grasshopper definition open");
            foreach (var h in targets)
            {
                var o = GhCore.Find(gh, h) ?? throw new NxError($"unknown Grasshopper object '{h}'");
                c.GhObjs.Add(new IGH_DocumentObjectLike { O = o, Handle = h });
            }
            if (c.GhObjs.Count == 0 && op.Target == "gh_objects")
                foreach (var o in gh.Objects.Where(o => o.Attributes.Selected)) c.GhObjs.Add(new IGH_DocumentObjectLike { O = o, Handle = GhCore.Handle(gh, o) });
            if (c.GhObjs.Count == 0 && !op.TargetOptional) throw new NxError($"which {op.Target}?", "name them or select them on the canvas");
        }


        // ---- Grasshopper canvas ------------------------------------------------------------------------------------------------------
        static string[] _components = new string[0];
        static Dictionary<string, string[]> _componentPaths = new Dictionary<string, string[]>();
        static string[] _ghOuts = new string[0], _ghIns = new string[0];

        static JsonObject Cat(string name, string desc, string[] values, Dictionary<string, string[]> paths)
        {
            var o = S("catalogue", name, desc, values: values);
            var pj = new JsonObject();
            foreach (var kv in paths) pj[kv.Key] = new JsonArray(kv.Value.Select(x => (JsonNode)x).ToArray());
            o["paths"] = pj;
            return o;
        }

        /// the component catalogue (once): every installed, visible component; "Name" or, when names repeat, "Name @Category/Sub"
        static void LoadComponents()
        {
            if (_components.Length > 0) return;
            var proxies = Grasshopper.Instances.ComponentServer.ObjectProxies
                .Where(p => !p.Obsolete && !p.Exposure.HasFlag(Grasshopper.Kernel.GH_Exposure.hidden) && !string.IsNullOrEmpty(p.Desc.Category))
                .ToList();
            var dup = new HashSet<string>(proxies.GroupBy(p => p.Desc.Name).Where(g => g.Count() > 1).Select(g => g.Key));
            var paths = new Dictionary<string, string[]>();
            foreach (var p in proxies)
            {
                var v = dup.Contains(p.Desc.Name) ? $"{p.Desc.Name} @{p.Desc.Category}/{p.Desc.SubCategory}" : p.Desc.Name;
                if (!paths.ContainsKey(v)) paths[v] = new[] { p.Desc.Category, string.IsNullOrEmpty(p.Desc.SubCategory) ? "-" : p.Desc.SubCategory, v };
            }
            _componentPaths = paths;
            _components = paths.Keys.OrderBy(x => x).ToArray();
        }

        // no quotes: OpenAI's strict schemas refuse them inside allowed values
        static string Label(IGH_DocumentObject o) => o.Name + (string.IsNullOrEmpty(o.NickName) || o.NickName == o.Name ? "" : $" ({o.NickName.Replace("\"", "'")})");

        /// the open definition's outputs and inputs as choices: "g7.R  Circle: Radius" (handle.port first, read back by PortOf)
        static void RefreshGh()
        {
            try { LoadComponents(); } catch { }
            var outs = new List<string>(); var ins = new List<string>();
            var d = GhCore.Doc(false);
            if (d != null)
                foreach (var o in d.Objects)
                {
                    var h = GhCore.Handle(d, o);
                    if (o is Grasshopper.Kernel.IGH_Component comp)
                    {
                        foreach (var p in comp.Params.Output) outs.Add($"{h}.{p.NickName}  {Label(o)}: {p.Name}");
                        foreach (var p in comp.Params.Input) ins.Add($"{h}.{p.NickName}  {Label(o)}: {p.Name}");
                    }
                    else if (o is Grasshopper.Kernel.IGH_Param prm)
                    {
                        outs.Add($"{h}  {Label(o)}");
                        if (!(o is GH_NumberSlider || o is GH_BooleanToggle || o is GH_Panel)) ins.Add($"{h}  {Label(o)}");
                    }
                }
            _ghOuts = outs.Take(250).ToArray();
            _ghIns = ins.Take(250).ToArray();
        }

        static string PortOf(string choice)
        {
            var v = (choice ?? "").Trim();
            int sp = v.IndexOf(' ');
            var port = sp > 0 ? v.Substring(0, sp) : v;
            if (!Handles.IsGhHandle(port.Split('.')[0])) throw new NxError($"'{choice}' is not a Grasshopper port", "pick one of the listed outputs / inputs");
            return port;
        }

        static void GhEdit(Ctx c, string ops, string layout = null, bool newDoc = false)
        {
            var o = new JsonObject();
            if (ops != null) o["ops"] = ops;
            if (layout != null) o["layout"] = layout;
            if (newDoc) o["new"] = true;
            GhEditTool.Run(Args.From(o), c.R);
        }

        static void GhAdd(Ctx c)
        {
            var comp = c.Str("component") ?? throw new NxError("which component?");
            var nick = c.Str("nick");
            if (GhCore.Doc(false) == null) GhEdit(c, null, null, true);
            GhEdit(c, $"n1 = {comp}" + (string.IsNullOrWhiteSpace(nick) ? "" : $" \"{nick.Replace("\"", "'")}\""));
        }

        static void GhConnect(Ctx c) => GhEdit(c, $"{PortOf(c.Str("from"))} > {PortOf(c.Str("to"))}");

        static void GhGroup(Ctx c)
        {
            var d = GhCore.Doc(false) ?? throw new NxError("no Grasshopper definition open");
            var g = new Grasshopper.Kernel.Special.GH_Group { NickName = c.Str("name") ?? "Group" };
            var col = c.Str("colour", "");
            g.Colour = col.StartsWith("inputs") ? Color.FromArgb(150, 0xDB, 0xAB, 0xFF) : col.StartsWith("logic") ? Color.FromArgb(150, 0xAB, 0xDB, 0xFF)
                : col.StartsWith("geometry") ? Color.FromArgb(150, 0xFF, 0xD6, 0xAB) : col.StartsWith("output") ? Color.FromArgb(150, 0xB2, 0xFA, 0xB4) : Color.FromArgb(150, 170, 170, 170);
            foreach (var x in c.GhObjs) g.AddObject(((IGH_DocumentObject)x.O).InstanceGuid);
            d.AddObject(g, false);
            g.ExpireCaches();
            Grasshopper.Instances.ActiveCanvas?.Refresh();
            c.R.Line($"group \"{g.NickName}\" ({c.GhObjs.Count} objects) = {GhCore.Handle(d, g)}");
        }

        static void GhSelect(Ctx c)
        {
            var d = GhCore.Doc(false) ?? throw new NxError("no Grasshopper definition open");
            foreach (var o in d.Objects) o.Attributes.Selected = false;
            foreach (var x in c.GhObjs) ((IGH_DocumentObject)x.O).Attributes.Selected = true;
            Grasshopper.Instances.ActiveCanvas?.Refresh();
            c.R.Line($"selected {c.GhObjs.Count} on the canvas");
        }

        // ---- functions --------------------------------------------------------------------------------------------------------------
        static void AskModel(Ctx c)
        {
            var objs = c.Doc.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true }).ToList();
            switch (c.Str("about", "counts"))
            {
                case "layers":
                    c.R.Line(string.Join("; ", c.Doc.Layers.Where(l => !l.IsDeleted).Select(l => $"{l.FullPath}: {objs.Count(o => o.Attributes.LayerIndex == l.Index)}{(l.IsVisible ? "" : " (off)")}")));
                    break;
                case "units": c.R.Line($"the model is in {c.Doc.ModelUnitSystem.ToString().ToLowerInvariant()}, tolerance {c.Doc.ModelAbsoluteTolerance}"); break;
                case "size":
                    var bb = BoundingBox.Empty; foreach (var o in objs) bb.Union(o.Geometry.GetBoundingBox(true));
                    c.R.Line(bb.IsValid ? $"overall size {Fmt.Dims(bb.Diagonal.X, bb.Diagonal.Y, bb.Diagonal.Z)} {c.Doc.ModelUnitSystem.ToString().ToLowerInvariant()}, from {Fmt.P(bb.Min)} to {Fmt.P(bb.Max)}" : "the model is empty");
                    break;
                case "selection":
                    var sel = c.Doc.Objects.GetSelectedObjects(false, false).ToList();
                    c.R.Line(sel.Count == 0 ? "nothing is selected" : $"{sel.Count} selected: " + string.Join(", ", sel.GroupBy(o => Geo.TypeWord(o.Geometry)).Select(g => $"{g.Count()} {g.Key}")));
                    break;
                default:
                    c.R.Line($"{objs.Count} objects: " + (objs.Count == 0 ? "none" : string.Join(", ", objs.GroupBy(o => Geo.TypeWord(o.Geometry)).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))));
                    break;
            }
        }

        static void AskObject(Ctx c)
        {
            c.Need();
            var u = c.Doc.ModelUnitSystem.ToString().ToLowerInvariant();
            var about = c.Str("about", "size");
            foreach (var o in c.Objs.Take(30))
            {
                var g = o.Geometry; string v;
                switch (about)
                {
                    case "length": v = g is Curve cv ? $"length {Fmt.N(cv.GetLength())} {u}" : "not a curve"; break;
                    case "area": { var m = g is Brep b ? AreaMassProperties.Compute(b) : g is Extrusion e ? AreaMassProperties.Compute(e.ToBrep()) : g is Curve cc && cc.IsClosed ? AreaMassProperties.Compute(cc) : g is Mesh me ? AreaMassProperties.Compute(me) : null; v = m != null ? $"area {Fmt.N(m.Area)} {u}²" : "no area"; break; }
                    case "volume": { var b = g as Brep ?? (g as Extrusion)?.ToBrep(); v = b != null && b.IsSolid ? $"volume {Fmt.N(b.GetVolume())} {u}³" : "not a closed solid"; break; }
                    case "layer": v = "layer " + c.Doc.Layers[o.Attributes.LayerIndex].FullPath; break;
                    case "type": v = Geo.TypeWord(g); break;
                    case "count": v = ""; break;
                    default: { var bb = g.GetBoundingBox(true); v = $"size {Fmt.Dims(bb.Diagonal.X, bb.Diagonal.Y, bb.Diagonal.Z)} {u}"; break; }
                }
                if (about != "count") c.R.Line($"{Handles.Rh(o.Id)}{(string.IsNullOrEmpty(o.Attributes.Name) ? "" : " " + o.Attributes.Name)}: {v}");
            }
            if (about == "count" || c.Objs.Count > 30) c.R.Line($"{c.Objs.Count} objects");
        }

        static void Select(Ctx c)
        {
            c.Need();
            if (!c.Bool("add")) c.Doc.Objects.UnselectAll();
            int n = c.Doc.Objects.Select(c.Objs.Select(o => o.Id));
            c.R.Line($"selected {n}");
        }

        static void Xform(Ctx c, Transform t, bool copy, string verb)
        {
            c.Need();
            if (!t.IsValid) throw new NxError("the values give no valid transform");
            int n = 0;
            foreach (var o in c.Objs)
            {
                if (copy) { var dup = o.Geometry.Duplicate(); dup.Transform(t); if (c.Doc.Objects.Add(dup, o.Attributes.Duplicate()) != Guid.Empty) n++; }
                else if (c.Doc.Objects.Transform(o, t, true) != Guid.Empty) n++;
            }
            c.R.Line($"{verb} {n}");
        }

        static void ArrayLinear(Ctx c)
        {
            c.Need();
            int copies = (int)c.Num("count");
            if (copies < 1 || copies > 1000) throw new NxError("count (copies) must be 1..1000");
            var step = c.Vec("step") * c.Scale;
            int count = copies + 1;
            for (int i = 1; i < count; i++) foreach (var o in c.Objs) { var d = o.Geometry.Duplicate(); d.Transform(Transform.Translation(step * i)); c.Doc.Objects.Add(d, o.Attributes.Duplicate()); }
            c.R.Line($"{count - 1} copies of {c.Objs.Count}");
        }

        static void ArrayPolar(Ctx c)
        {
            c.Need();
            int copies = (int)c.Num("count");
            if (copies < 1 || copies > 1000) throw new NxError("count (copies) must be 1..1000");
            int count = copies + 1;
            double total = c.Num("angle", 360);
            var centre = c.Str("about", "origin") == "centre" ? c.Center() : Point3d.Origin;
            double step = Math.Abs(total - 360) < 1e-6 ? 360.0 / count : total / (count - 1);
            for (int i = 1; i < count; i++) foreach (var o in c.Objs) { var d = o.Geometry.Duplicate(); d.Transform(Transform.Rotation(RhinoMath.ToRadians(step * i), Vector3d.ZAxis, centre)); c.Doc.Objects.Add(d, o.Attributes.Duplicate()); }
            c.R.Line($"{count - 1} copies of {c.Objs.Count}, {Fmt.N(step)}° apart");
        }

        static void Join(Ctx c)
        {
            c.Need(2);
            var tol = c.Doc.ModelAbsoluteTolerance;
            var curves = c.Objs.Where(o => o.Geometry is Curve).ToList();
            var breps = c.Objs.Where(o => o.Geometry is Brep || o.Geometry is Extrusion).ToList();
            int made = 0;
            if (curves.Count > 1)
                foreach (var j in Curve.JoinCurves(curves.Select(o => (Curve)o.Geometry), tol)) { c.Doc.Objects.AddCurve(j, curves[0].Attributes.Duplicate()); made++; }
            if (curves.Count > 1) foreach (var o in curves) c.Doc.Objects.Delete(o, true);
            if (breps.Count > 1)
            {
                var res = Brep.JoinBreps(breps.Select(o => o.Geometry as Brep ?? ((Extrusion)o.Geometry).ToBrep()), tol);
                if (res != null) { foreach (var b in res) { c.Doc.Objects.AddBrep(b, breps[0].Attributes.Duplicate()); made++; } foreach (var o in breps) c.Doc.Objects.Delete(o, true); }
            }
            if (made == 0) throw new NxError("nothing could be joined (two or more curves or surfaces that touch)");
            c.R.Line($"joined into {made}");
        }

        static void Explode(Ctx c)
        {
            c.Need();
            int made = 0;
            foreach (var o in c.Objs)
            {
                GeometryBase[] parts = o.Geometry is PolyCurve pc ? pc.Explode() : o.Geometry is PolylineCurve pl ? pl.ToPolyline().GetSegments().Select(s => (GeometryBase)new LineCurve(s)).ToArray()
                    : o.Geometry is Brep b && b.Faces.Count > 1 ? b.Faces.Select(f => (GeometryBase)f.DuplicateFace(false)).ToArray()
                    : o.Geometry is Extrusion e ? e.ToBrep().Faces.Select(f => (GeometryBase)f.DuplicateFace(false)).ToArray() : null;
                if (parts == null || parts.Length < 2) continue;
                foreach (var p in parts) { c.Doc.Objects.Add(p, o.Attributes.Duplicate()); made++; }
                c.Doc.Objects.Delete(o, true);
            }
            if (made == 0) throw new NxError("nothing to explode (polycurves, polylines or polysurfaces)");
            c.R.Line($"exploded into {made} parts");
        }

        static void Offset(Ctx c)
        {
            c.Need();
            double d = c.Num("distance") * c.Scale;
            bool inward = c.Str("side", "out") == "in";
            int made = 0;
            foreach (var o in c.Objs.Where(o => o.Geometry is Curve))
            {
                var cv = (Curve)o.Geometry;
                if (!cv.TryGetPlane(out var pl, c.Doc.ModelAbsoluteTolerance)) pl = Plane.WorldXY;
                var res = cv.Offset(pl, d, c.Doc.ModelAbsoluteTolerance, CurveOffsetCornerStyle.Sharp);
                // closed curves: offset both ways and keep the side asked for (outwards = the longer result)
                if (cv.IsClosed)
                {
                    var alt = cv.Offset(pl, -d, c.Doc.ModelAbsoluteTolerance, CurveOffsetCornerStyle.Sharp);
                    double l1 = res?.Sum(x => x.GetLength()) ?? -1, l2 = alt?.Sum(x => x.GetLength()) ?? -1;
                    res = (l1 >= l2) == !inward ? res : alt;
                }
                if (res == null) continue;
                foreach (var r in res) { c.Doc.Objects.AddCurve(r, o.Attributes.Duplicate()); made++; }
            }
            if (made == 0) throw new NxError("no curve could be offset");
            c.R.Line($"offset {made}");
        }

        static void Extrude(Ctx c)
        {
            c.Need();
            double h = c.Num("height") * c.Scale;
            int made = 0;
            foreach (var o in c.Objs.Where(o => o.Geometry is Curve))
            {
                var cv = (Curve)o.Geometry;
                var e = Extrusion.Create(cv, h, cv.IsClosed && cv.IsPlanar());
                if (e != null) { c.Doc.Objects.AddExtrusion(e, o.Attributes.Duplicate()); made++; continue; }
                var s = Surface.CreateExtrusion(cv, new Vector3d(0, 0, h));
                if (s != null) { c.Doc.Objects.AddSurface(s, o.Attributes.Duplicate()); made++; }
            }
            if (made == 0) throw new NxError("nothing extruded (curves needed)");
            c.R.Line($"extruded {made}");
        }

        static void Union(Ctx c)
        {
            c.Need(2);
            var breps = c.Objs.Select(o => o.Geometry as Brep ?? (o.Geometry as Extrusion)?.ToBrep()).Where(b => b != null).ToList();
            var res = Brep.CreateBooleanUnion(breps, c.Doc.ModelAbsoluteTolerance);
            if (res == null || res.Length == 0) throw new NxError("the union failed (closed solids that overlap are needed)");
            foreach (var b in res) c.Doc.Objects.AddBrep(b, c.Objs[0].Attributes.Duplicate());
            foreach (var o in c.Objs) c.Doc.Objects.Delete(o, true);
            c.R.Line($"{c.Objs.Count} solids into {res.Length}");
        }

        static int LayerIndex(RhinoDoc doc, string path, bool create, Color? colour = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new NxError("no layer given");
            int i = doc.Layers.FindByFullPath(path, -1);
            if (i < 0) { var l = doc.Layers.FirstOrDefault(x => !x.IsDeleted && string.Equals(x.Name, path, StringComparison.OrdinalIgnoreCase)); if (l != null) i = l.Index; }
            if (i >= 0 || !create) return i;
            return doc.Layers.Add(new Layer { Name = path, Color = colour ?? Color.Black });
        }

        static void SetLayer(Ctx c)
        {
            c.Need();
            int li = LayerIndex(c.Doc, c.Str("layer"), true);
            foreach (var o in c.Objs) { o.Attributes.LayerIndex = li; o.CommitChanges(); }
            c.R.Line($"{c.Objs.Count} to layer {c.Doc.Layers[li].FullPath}");
        }

        static Color? ColourOf(string name) => name switch
        {
            "red" => Color.FromArgb(220, 50, 50), "green" => Color.FromArgb(40, 170, 70), "blue" => Color.FromArgb(40, 90, 220), "yellow" => Color.FromArgb(240, 200, 30),
            "cyan" => Color.FromArgb(30, 190, 210), "magenta" => Color.FromArgb(200, 50, 190), "orange" => Color.FromArgb(240, 140, 30), "black" => Color.Black,
            "white" => Color.White, "grey" => Color.Gray, "gray" => Color.Gray, _ => null,
        };

        static void SetColour(Ctx c)
        {
            c.Need();
            var name = c.Str("colour", "by layer");
            var col = ColourOf(name);
            if (col == null && name != "by layer") throw new NxError($"unknown colour '{name}'");
            foreach (var o in c.Objs)
            {
                if (col == null) o.Attributes.ColorSource = ObjectColorSource.ColorFromLayer;
                else { o.Attributes.ColorSource = ObjectColorSource.ColorFromObject; o.Attributes.ObjectColor = col.Value; }
                o.CommitChanges();
            }
            c.R.Line($"{c.Objs.Count} {name}");
        }

        static void LayerVisibility(Ctx c)
        {
            int li = LayerIndex(c.Doc, c.Str("layer"), false);
            if (li < 0) throw new NxError($"no layer '{c.Str("layer")}'");
            var l = c.Doc.Layers[li];
            l.IsVisible = c.Bool("visible", true);
            l.CommitChanges();
            c.R.Line($"layer {l.FullPath} {(l.IsVisible ? "on" : "off")}");
        }

        static string TypeLayer(GeometryBase g) =>
            g is Curve ? "Curves" : g is Extrusion || (g is Brep b && b.IsSolid) ? "Solids" : g is Brep || g is Surface ? "Surfaces" : g is Mesh || g is SubD ? "Meshes"
            : g is Point || g is PointCloud ? "Points" : g is AnnotationBase || g is TextDot ? "Annotations" : g is InstanceReferenceGeometry ? "Blocks" : "Other";

        static void SortByType(Ctx c)
        {
            c.Need();
            var parent = c.Str("parent", "Sorted");
            int pi = LayerIndex(c.Doc, parent, true);
            var pid = c.Doc.Layers[pi].Id;
            var made = new Dictionary<string, int>();
            foreach (var o in c.Objs)
            {
                var name = TypeLayer(o.Geometry);
                if (!made.TryGetValue(name, out int li))
                {
                    li = c.Doc.Layers.FindByFullPath(c.Doc.Layers[pi].FullPath + "::" + name, -1);
                    if (li < 0) li = c.Doc.Layers.Add(new Layer { Name = name, ParentLayerId = pid });
                    made[name] = li;
                }
                o.Attributes.LayerIndex = li; o.CommitChanges();
            }
            c.R.Line($"{c.Objs.Count} objects sorted under {parent}: " + string.Join(", ", made.Keys.Select(k => $"{k} {c.Objs.Count(o => TypeLayer(o.Geometry) == k)}")));
        }

        static void DeleteEmptyLayers(Ctx c)
        {
            var used = new HashSet<int>(c.Doc.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true }).Select(o => o.Attributes.LayerIndex));
            var cur = c.Doc.Layers.CurrentLayerIndex;
            bool Busy(Layer l) => used.Contains(l.Index) || l.Index == cur || (l.GetChildren()?.Any(Busy) ?? false);
            var names = new List<string>();
            // deepest first, so a parent emptied of its sub-layers goes too
            foreach (var l in c.Doc.Layers.Where(l => !l.IsDeleted).OrderByDescending(l => l.FullPath.Count(ch => ch == ':')).ToList())
                { var path = l.FullPath; if (!Busy(l) && c.Doc.Layers.Delete(l.Index, true)) names.Add(path); }
            c.R.Line(names.Count == 0 ? "no empty layers" : $"deleted {names.Count} empty layers: " + Fmt.Trunc(string.Join(", ", names), 400));
        }

        static void MergeLayers(Ctx c)
        {
            int from = LayerIndex(c.Doc, c.Str("from"), false), into = LayerIndex(c.Doc, c.Str("into"), true);
            if (from < 0) throw new NxError($"no layer '{c.Str("from")}'");
            if (from == into) throw new NxError("the two layers are the same");
            var objs = c.Doc.Objects.FindByLayer(c.Doc.Layers[from]) ?? new RhinoObject[0];
            foreach (var o in objs) { o.Attributes.LayerIndex = into; o.CommitChanges(); }
            if (c.Doc.Layers.CurrentLayerIndex == from) c.Doc.Layers.SetCurrentLayerIndex(into, true);
            var name = c.Doc.Layers[from].FullPath;
            bool gone = c.Doc.Layers.Delete(from, true);
            c.R.Line($"{objs.Length} objects from {name} to {c.Doc.Layers[into].FullPath}{(gone ? ", layer removed" : " (layer kept: it has sub-layers)")}");
        }

        static Layer NeedLayer(Ctx c, string key = "layer")
        {
            int li = LayerIndex(c.Doc, c.Str(key), false);
            if (li < 0) throw new NxError($"no layer '{c.Str(key)}'");
            return c.Doc.Layers[li];
        }

        static void RenameLayer(Ctx c)
        {
            var l = NeedLayer(c); var old = l.FullPath;
            var name = c.Str("name") ?? throw new NxError("no new name given");
            if (!Layer.IsValidName(name)) throw new NxError($"'{name}' is not a valid layer name");
            l.Name = name; l.CommitChanges();
            c.R.Line($"layer {old} renamed to {l.FullPath}");
        }

        static void LayerColour(Ctx c)
        {
            var l = NeedLayer(c);
            l.Color = ColourOf(c.Str("colour")) ?? throw new NxError($"unknown colour '{c.Str("colour")}'");
            l.CommitChanges();
            c.R.Line($"layer {l.FullPath} {c.Str("colour")}");
        }

        static void LockLayer(Ctx c)
        {
            var l = NeedLayer(c);
            l.IsLocked = c.Bool("locked", true); l.CommitChanges();
            c.R.Line($"layer {l.FullPath} {(l.IsLocked ? "locked" : "unlocked")}");
        }

        static void IsolateLayer(Ctx c)
        {
            var keep = NeedLayer(c);
            c.Doc.Layers.SetCurrentLayerIndex(keep.Index, true);
            var keepIds = new HashSet<Guid> { keep.Id };
            for (var p = keep.ParentLayerId; p != Guid.Empty; p = c.Doc.Layers.FindId(p)?.ParentLayerId ?? Guid.Empty) keepIds.Add(p);
            int off = 0;
            foreach (var l in c.Doc.Layers.Where(l => !l.IsDeleted))
            {
                bool on = keepIds.Contains(l.Id) || l.IsChildOf(keep);
                if (l.IsVisible != on) { l.IsVisible = on; l.CommitChanges(); if (!on) off++; }
            }
            c.R.Line($"only {keep.FullPath} on ({off} layers off)");
        }

        static void ShowAllLayers(Ctx c)
        {
            int n = 0;
            foreach (var l in c.Doc.Layers.Where(l => !l.IsDeleted && !l.IsVisible)) { l.IsVisible = true; l.CommitChanges(); n++; }
            c.R.Line(n == 0 ? "all layers were on" : $"{n} layers on");
        }

        static void DeleteDuplicates(Ctx c)
        {
            c.Need();
            var kept = new List<RhinoObject>();
            int n = 0;
            foreach (var o in c.Objs)
            {
                var bb = o.Geometry.GetBoundingBox(true);
                bool dup = kept.Any(k => k.ObjectType == o.ObjectType && k.Geometry.GetBoundingBox(true).Min.DistanceTo(bb.Min) < c.Doc.ModelAbsoluteTolerance
                    && k.Geometry.GetBoundingBox(true).Max.DistanceTo(bb.Max) < c.Doc.ModelAbsoluteTolerance && GeometryBase.GeometryEquals(k.Geometry, o.Geometry));
                if (dup) { if (c.Doc.Objects.Delete(o, true)) n++; }
                else kept.Add(o);
            }
            c.R.Line(n == 0 ? "no duplicates" : $"deleted {n} duplicates");
        }

        static void DeleteSmall(Ctx c)
        {
            c.Need();
            double size = c.Num("size") * c.Scale;
            if (!(size > 0)) throw new NxError("the size must be positive");
            int n = 0;
            foreach (var o in c.Objs.Where(o => o.Geometry.GetBoundingBox(true).Diagonal.Length < size)) if (c.Doc.Objects.Delete(o, true)) n++;
            c.R.Line(n == 0 ? $"nothing smaller than {Fmt.N(size)}" : $"deleted {n} objects smaller than {Fmt.N(size)}");
        }

        static void Align(Ctx c)
        {
            c.Need(2);
            var to = c.Str("to", "left");
            int axis = to.EndsWith("x") || to == "left" || to == "right" ? 0 : to.EndsWith("y") || to == "front" || to == "back" ? 1 : 2;
            var boxes = c.Objs.Select(o => o.Geometry.GetBoundingBox(true)).ToList();
            double Coord(BoundingBox b) => to.StartsWith("centre") ? b.Center[axis] : to == "left" || to == "front" || to == "bottom" ? b.Min[axis] : b.Max[axis];
            double goal = to.StartsWith("centre") ? boxes.Average(Coord) : to == "left" || to == "front" || to == "bottom" ? boxes.Min(Coord) : boxes.Max(Coord);
            for (int i = 0; i < c.Objs.Count; i++)
            {
                var v = Vector3d.Zero; v[axis] = goal - Coord(boxes[i]);
                if (v.Length > 1e-12) c.Doc.Objects.Transform(c.Objs[i], Transform.Translation(v), true);
            }
            c.R.Line($"aligned {c.Objs.Count} to {to}");
        }

        static void MoveToOrigin(Ctx c)
        {
            c.Need();
            var bb = BoundingBox.Empty; foreach (var o in c.Objs) bb.Union(o.Geometry.GetBoundingBox(true));
            var from = c.Str("by", "corner") == "centre" ? bb.Center : bb.Min;
            Xform(c, Transform.Translation(Point3d.Origin - from), false, "moved to the origin:");
        }

        static void AddBox(Ctx c)
        {
            double w = c.Num("width") * c.Scale, d = c.Num("depth") * c.Scale, h = c.Num("height") * c.Scale;
            if (!(w > 0 && d > 0 && h > 0)) throw new NxError("width, depth and height must be positive");
            var p = c.Pt("at", Point3d.Origin);
            var box = new Box(Plane.WorldXY, new Interval(p.X, p.X + w), new Interval(p.Y, p.Y + d), new Interval(p.Z, p.Z + h));
            c.Doc.Objects.AddBox(box);
            c.R.Line($"box {Fmt.Dims(w, d, h)} at {Fmt.P(p)}");
        }

        static void AddCircle(Ctx c)
        {
            double r = c.Num("radius") * c.Scale;
            if (!(r > 0)) throw new NxError("the radius must be positive");
            var p = c.Pt("at", Point3d.Origin);
            c.Doc.Objects.AddCircle(new Circle(new Plane(p, Vector3d.ZAxis), r));
            c.R.Line($"circle r {Fmt.N(r)} at {Fmt.P(p)}");
        }

        static void AddLine(Ctx c)
        {
            if (!(c.Args["points"] is JsonArray a) || a.Count < 2) throw new NxError("two or more points are needed");
            var pts = a.Select(x => x as JsonArray).Where(x => x != null && x.Count == 3).Select(x => new Point3d((double)x[0], (double)x[1], (double)x[2])).ToList();
            if (pts.Count < 2) throw new NxError("points must be [x, y, z]");
            if (pts.Count == 2) c.Doc.Objects.AddLine(pts[0], pts[1]); else c.Doc.Objects.AddPolyline(pts);
            c.R.Line(pts.Count == 2 ? "line" : $"polyline through {pts.Count} points");
        }

        static void AddRect(Ctx c)
        {
            double w = c.Num("width") * c.Scale, d = c.Num("depth") * c.Scale;
            if (!(w > 0 && d > 0)) throw new NxError("width and depth must be positive");
            var p = c.Pt("at", Point3d.Origin);
            c.Doc.Objects.AddRectangle(new Rectangle3d(new Plane(p, Vector3d.ZAxis), w, d));
            c.R.Line($"rectangle {Fmt.N(w)}x{Fmt.N(d)} at {Fmt.P(p)}");
        }

        static void AddLayer(Ctx c)
        {
            var name = c.Str("name") ?? throw new NxError("no layer name given");
            int li = LayerIndex(c.Doc, name, true, ColourOf(c.Str("colour", "black")));
            c.Doc.Layers.SetCurrentLayerIndex(li, true);
            c.R.Line($"layer {c.Doc.Layers[li].FullPath} (current)");
        }

        static void GhSlider(Ctx c)
        {
            foreach (var g in c.GhObjs)
            {
                if (!(g.O is GH_NumberSlider s)) throw new NxError($"{g.Handle} is not a number slider");
                var v = (decimal)c.Num("value");
                if (v < s.Slider.Minimum || v > s.Slider.Maximum) { if (v < s.Slider.Minimum) s.Slider.Minimum = v; else s.Slider.Maximum = v; c.R.Line($"(range widened to {s.Slider.Minimum}..{s.Slider.Maximum})"); }
                s.SetSliderValue(v);
                c.R.Line($"{s.NickName} = {s.CurrentValue}");
            }
            GhCore.Doc(false)?.NewSolution(false);
        }

        static void GhToggle(Ctx c)
        {
            foreach (var g in c.GhObjs)
            {
                if (!(g.O is GH_BooleanToggle t)) throw new NxError($"{g.Handle} is not a toggle");
                t.Value = c.Bool("value", !t.Value);
                t.ExpireSolution(false);
                c.R.Line($"{t.NickName} = {t.Value.ToString().ToLowerInvariant()}");
            }
            GhCore.Doc(false)?.NewSolution(false);
        }

        static void GhBake(Ctx c)
        {
            var d = GhCore.Doc(false) ?? throw new NxError("no Grasshopper definition open");
            var layer = c.Str("layer", "Grasshopper bake");
            if (d.Objects.OfType<Grasshopper.Kernel.IGH_Component>().Any(x => x.Params.Output.Any(p => p.VolatileDataCount == 0)))
                d.NewSolution(false);      // a definition never computed (editor closed, just built) has no output yet
            int li = LayerIndex(c.Doc, layer, true);
            int n = 0;
            foreach (var o in d.Objects.OfType<Grasshopper.Kernel.IGH_PreviewObject>())
            {
                if (o.Hidden || !(o is Grasshopper.Kernel.IGH_Component comp)) continue;
                foreach (var p in comp.Params.Output)
                    foreach (var goo in p.VolatileData.AllData(true))
                    {
                        if (!(goo is Grasshopper.Kernel.Types.IGH_GeometricGoo)) continue;
                        // GH's own conversion: circles, lines, arcs, boxes ... (a GH_Circle does not cast to Curve)
                        GeometryBase geo = Grasshopper.Kernel.GH_Convert.ToGeometryBase(goo);
                        if (geo == null) continue;
                        c.Doc.Objects.Add(geo, new ObjectAttributes { LayerIndex = li });
                        n++;
                    }
            }
            if (n == 0) throw new NxError("nothing to bake (no visible component output with geometry)");
            c.R.Line($"baked {n} objects to {c.Doc.Layers[li].FullPath}");
        }

        static double DefaultSize(Ctx c, double mm) => c.Doc.ModelUnitSystem == UnitSystem.Meters ? mm / 1000 : c.Doc.ModelUnitSystem == UnitSystem.Centimeters ? mm / 10 : mm;

        static int Columns(Ctx c, int nx, int ny, double sx, double sy, double h, double size, int li)
        {
            if (nx < 1 || ny < 1 || nx * ny > 5000) throw new NxError("columns in x and y must be 1.. (at most 5000 in all)");
            if (!(h > 0 && size > 0)) throw new NxError("height and column size must be positive");
            int n = 0;
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    double x = i * sx, y = j * sy;
                    var box = new Box(Plane.WorldXY, new Interval(x - size / 2, x + size / 2), new Interval(y - size / 2, y + size / 2), new Interval(0, h));
                    c.Doc.Objects.AddBox(box, new ObjectAttributes { LayerIndex = li });
                    n++;
                }
            return n;
        }

        static void ColumnGrid(Ctx c)
        {
            int nx = (int)c.Num("nx"), ny = (int)c.Num("ny", 1);
            double sx = c.Num("sx") * c.Scale, sy = c.Num("sy", c.Num("sx")) * c.Scale, h = c.Num("height") * c.Scale, size = c.Num("size", double.NaN);
            size = double.IsNaN(size) ? DefaultSize(c, 300) : size * c.Scale;
            int li = LayerIndex(c.Doc, "Columns", true, Color.FromArgb(40, 90, 220));
            int n = Columns(c, nx, ny, sx, sy, h, size, li);
            c.R.Line($"{n} columns ({nx} x {ny}, spacing {Fmt.N(sx)} x {Fmt.N(sy)}, height {Fmt.N(h)}) on layer Columns");
        }

        static int Slabs(Ctx c, int floors, double storey, double w, double d, double t, double x0, double y0, int li)
        {
            if (floors < 1 || floors > 300) throw new NxError("floors must be 1..300");
            if (!(storey > 0 && w > 0 && d > 0 && t > 0)) throw new NxError("storey height, width, depth and thickness must be positive");
            for (int k = 1; k <= floors; k++)
                c.Doc.Objects.AddBox(new Box(Plane.WorldXY, new Interval(x0, x0 + w), new Interval(y0, y0 + d), new Interval(k * storey - t, k * storey)), new ObjectAttributes { LayerIndex = li });
            return floors;
        }

        static void FloorSlabs(Ctx c)
        {
            int floors = (int)c.Num("floors");
            double t = c.Num("thickness", double.NaN);
            t = double.IsNaN(t) ? DefaultSize(c, 250) : t * c.Scale;
            int li = LayerIndex(c.Doc, "Slabs", true, Color.Gray);
            Slabs(c, floors, c.Num("storey") * c.Scale, c.Num("width") * c.Scale, c.Num("depth") * c.Scale, t, 0, 0, li);
            c.R.Line($"{floors} slabs on layer Slabs");
        }

        static void FrameBuilding(Ctx c)
        {
            int nx = (int)c.Num("nx"), ny = (int)c.Num("ny", 2), floors = (int)c.Num("floors");
            double sx = c.Num("sx") * c.Scale, sy = c.Num("sy", c.Num("sx")) * c.Scale, storey = c.Num("storey") * c.Scale;
            double col = DefaultSize(c, 300), t = DefaultSize(c, 250);
            int lc = LayerIndex(c.Doc, "Columns", true, Color.FromArgb(40, 90, 220)), ls = LayerIndex(c.Doc, "Slabs", true, Color.Gray);
            int n = Columns(c, nx, ny, sx, sy, floors * storey, col, lc);
            Slabs(c, floors, storey, (nx - 1) * sx + col, (ny - 1) * sy + col, t, -col / 2, -col / 2, ls);
            c.R.Line($"{n} columns and {floors} slabs ({nx - 1} x {ny - 1} bays, {floors} floors of {Fmt.N(storey)})");
        }
    }
}
