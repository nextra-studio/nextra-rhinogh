using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Nextra.Engine.Gh.Shorthand
{
    public class BuildResult
    {
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public readonly Dictionary<string, string> IdMap = new Dictionary<string, string>();
        public readonly List<IGH_DocumentObject> Created = new List<IGH_DocumentObject>();
        public readonly HashSet<IGH_DocumentObject> Touched = new HashSet<IGH_DocumentObject>();
        public int Wires, Deleted, Values, GroupsNew, LinesOk, Lines;
        public readonly HashSet<int> FailedLines = new HashSet<int>();
    }

    /// Applies shorthand ops. Lines are applied independently: a failing line is reported with the
    /// reason and the valid alternatives, the rest still applies (unless atomic). Created nodes get
    /// handles immediately so a fix can reference them.
    public static class Builder
    {
        [ThreadStatic] public static List<string> LastNotes;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly string[] Palette = { "#DBABFF", "#ABDBFF", "#FFD6AB", "#B2FAB4", "#FFABAB", "#E6E6A0", "#C8C8FF" };

        public static BuildResult Apply(GH_Document d, List<Op> ops)
        {
            var res = new BuildResult { Lines = ops.Count(o => !(o is GroupOp)) };
            LastNotes = res.Notes;
            var locals = new Dictionary<string, IGH_DocumentObject>(StringComparer.Ordinal);

            void Fail(Op op, string msg) { res.Errors.Add($"L{op.Line} `{Fmt.Trunc(op.Raw.Split('\n')[0], 50)}`: {msg}"); res.FailedLines.Add(op.Line); }

            IGH_DocumentObject Resolve(string id, Op op)
            {
                if (locals.TryGetValue(id, out var o)) return o;
                if (Handles.IsGhHandle(id) || Guid.TryParse(id, out _))
                {
                    if (Handles.TryGh(d.DocumentID, id, out var g)) { o = d.FindObject(g, true); if (o != null) return o; }
                    throw new NxError($"'{id}' does not exist on the canvas (deleted, or handle from an older document)");
                }
                var byNick = d.Objects.Where(x => x.NickName == id && !(x is GH_Group)).ToList();
                if (byNick.Count == 1) { res.Notes.Add($"'{id}' resolved by nickname to {GhCore.Handle(d, byNick[0])}"); return byNick[0]; }
                throw new NxError($"unknown id '{id}'" + (byNick.Count > 1 ? " (nickname is ambiguous)" : ""), "use a handle (g12) or an id defined with `id = Type`; strings need quotes");
            }

            // 0. deletes first
            foreach (var del in ops.OfType<DeleteOp>())
            {
                var objs = new List<IGH_DocumentObject>();
                foreach (var id in del.Ids)
                {
                    try { objs.Add(Resolve(id, del)); } catch (NxError e) { Fail(del, e.Message); }
                }
                if (objs.Count > 0) { d.RemoveObjects(objs, false); res.Deleted += objs.Count; }
                if (!res.FailedLines.Contains(del.Line)) res.LinesOk++;
            }

            // 1. create
            var nodeOps = ops.OfType<NodeOp>().ToList();
            foreach (var n in nodeOps.Where(x => x.Create))
            {
                if (locals.ContainsKey(n.Id)) { Fail(n, $"id '{n.Id}' defined twice"); continue; }
                // refuse only if gN names a node that existed before this call (in a pasted definition the new
                // nodes get fresh handles that may coincide with ids still to come)
                if (Handles.IsGhHandle(n.Id) && Handles.TryGh(d.DocumentID, n.Id, out var live) && d.FindObject(live, true) is IGH_DocumentObject lo && !res.Created.Contains(lo))
                { Fail(n, $"{n.Id} already exists on this canvas — change it with `{n.Id} (…) {{…}}`, or give a new node another id"); continue; }
                try
                {
                    var proxy = ComponentSearch.Resolve(n.Type, n.Category, out var note, n.Inputs.Select(x => x.Port).Where(x => x != null).ToList());
                    if (note != null) res.Notes.Add($"L{n.Line}: {note}");
                    var obj = proxy.CreateInstance() ?? throw new NxError($"could not instantiate {n.Type}");
                    obj.CreateAttributes();
                    obj.Attributes.Pivot = new PointF(0, 0);
                    d.AddObject(obj, false);
                    locals[n.Id] = obj;
                    res.Created.Add(obj);
                    res.IdMap[n.Id] = GhCore.Handle(d, obj);
                }
                catch (NxError e) { Fail(n, e.Message + (e.Hint != null ? " — " + e.Hint : "")); }
            }

            // 2. params + config, 3. inputs
            foreach (var n in nodeOps)
            {
                if (res.FailedLines.Contains(n.Line) && n.Create) continue;
                IGH_DocumentObject obj;
                try { obj = Resolve(n.Id, n); }
                catch (NxError e) { Fail(n, e.Message + (e.Hint != null ? " — " + e.Hint : "")); continue; }
                if (n.BareMember) { res.LinesOk++; continue; }
                res.Touched.Add(obj);
                bool lineOk = true;
                try { ScriptParams(obj, n); }
                catch (NxError e) { Fail(n, e.Message); lineOk = false; }
                foreach (var kv in n.Config.OrderBy(kv => ConfigOrder(kv.Key)))
                {
                    try { ApplyConfig(d, obj, kv.Key, kv.Value, n); }
                    catch (NxError e) { Fail(n, e.Message + (e.Hint != null ? " — " + e.Hint : "")); lineOk = false; }
                }
                var states = n.Config.Where(kv => kv.Key.StartsWith("$")).Select(kv => new KeyValuePair<string, string>(kv.Key.Substring(1), kv.Value.Kind == VK.Str ? kv.Value.Str : kv.Value.ToString())).ToList();
                if (states.Count > 0)
                {
                    try { StateIO.Write(obj, states); }
                    catch (NxError e) { Fail(n, e.Message + (e.Hint != null ? " — " + e.Hint : "")); lineOk = false; }
                }
                // a new Python script with no declared inputs: drop GH's default x/y unless the code uses them
                if (n.Create && obj is IGH_Component sc && Scripts.IsScript(obj) && Scripts.Lang(sc) != "cs"
                    && !n.Inputs.Any(p => p.Port != null && !int.TryParse(p.Port, out _)))
                {
                    try { Scripts.DropUnusedInputs(sc); } catch { }
                }
                foreach (var p in n.Inputs)
                {
                    try { ApplyInput(d, obj, p, false, Resolve, n, res); }
                    catch (NxError e) { Fail(n, e.Message + (e.Hint != null ? " — " + e.Hint : "")); lineOk = false; }
                }
                foreach (var p in n.OutputMods)
                {
                    try { ApplyInput(d, obj, p, true, Resolve, n, res); }
                    catch (NxError e) { Fail(n, e.Message); lineOk = false; }
                }
                if (lineOk) res.LinesOk++;
            }

            // 4. standalone wires
            foreach (var w in ops.OfType<WireOp>())
            {
                try
                {
                    var src = Resolve(w.From, w); var dst = Resolve(w.To, w);
                    var sp = ComponentSearch.FindPort(src, w.FromPort, true, out var e1) ?? throw new NxError(e1);
                    var dp = ComponentSearch.FindPort(dst, w.ToPort, false, out var e2, grow: true) ?? throw new NxError(e2);
                    if (w.Remove)
                    {
                        if (!dp.Sources.Contains(sp)) throw new NxError("no such wire");
                        dp.RemoveSource(sp);
                    }
                    else { if (!dp.Sources.Contains(sp)) dp.AddSource(sp); res.Wires++; }
                    ApplyFlags(dp, w.Flags);
                    res.Touched.Add(dst);
                    res.LinesOk++;
                }
                catch (NxError e) { Fail(w, e.Message + (e.Hint != null ? " — " + e.Hint : "")); }
            }

            // 5. groups
            Groups(d, ops, locals, res, Resolve, Fail);
            return res;
        }

        // ---------- groups ----------

        static void Groups(GH_Document d, List<Op> ops, Dictionary<string, IGH_DocumentObject> locals, BuildResult res,
            Func<string, Op, IGH_DocumentObject> resolve, Action<Op, string> fail)
        {
            var map = new Dictionary<string, GH_Group>();
            int pal = d.Objects.OfType<GH_Group>().Count();
            foreach (var g in ops.OfType<GroupOp>().Where(g => !g.End))
            {
                string key = g.Handle ?? g.Name;
                if (key == null || map.ContainsKey(key)) continue;
                GH_Group grp = null;
                if (g.Handle != null)
                {
                    grp = (Handles.TryGh(d.DocumentID, g.Handle, out var gid) ? d.FindObject(gid, false) : null) as GH_Group;
                    // "## Name #c (g40)" copied from another canvas: the handle is stale there, the name still works
                    if (grp == null && g.Name == null) { fail(g, $"{g.Handle} is not a group"); continue; }
                }
                if (grp == null)
                {
                    var same = d.Objects.OfType<GH_Group>().Where(x => x.NickName == g.Name).ToList();
                    if (same.Count == 1) grp = same[0];
                    else if (same.Count > 1) { fail(g, $"several groups named '{g.Name}'; use its handle: ## {string.Join(" | ## ", same.Select(x => GhCore.Handle(d, x)))}"); continue; }
                }
                if (grp == null)
                {
                    grp = new GH_Group { NickName = g.Name };
                    grp.CreateAttributes();
                    grp.Colour = ParseColor(g.Color ?? Palette[pal++ % Palette.Length], 150);
                    d.AddObject(grp, false);
                    res.GroupsNew++;
                    res.Created.Add(grp);
                }
                else
                {
                    if (g.Color != null) grp.Colour = ParseColor(g.Color, grp.Colour.A);
                    if (g.Handle != null && g.Name != null && grp.NickName != g.Name && GhCore.Handle(d, grp) == g.Handle) grp.NickName = g.Name;
                }
                foreach (var rm in g.Remove)
                {
                    if (Handles.TryGh(d.DocumentID, rm, out var rid)) grp.RemoveObject(rid);
                }
                map[key] = grp;
                res.IdMap["## " + (g.Name ?? g.Handle)] = GhCore.Handle(d, grp);
            }
            // only created nodes and bare handle lines join the section's group (a modify line does not move a node)
            foreach (var n in ops.OfType<NodeOp>().Where(n => n.Group != null && (n.Create || n.BareMember)))
            {
                if (!map.TryGetValue(n.Group, out var grp)) continue;
                IGH_DocumentObject o;
                try { o = resolve(n.Id, n); } catch { continue; }
                if (!grp.ObjectIDs.Contains(o.InstanceGuid)) grp.AddObject(o.InstanceGuid);
            }
            foreach (var g in map.Values) { g.ExpireCaches(); g.Attributes?.ExpireLayout(); }
        }

        public static Color ParseColor(string hex, int alpha)
        {
            try
            {
                var c = ColorTranslator.FromHtml(hex.Length == 9 ? "#" + hex.Substring(3) : hex);
                if (hex.Length == 9) alpha = int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber);
                return Color.FromArgb(alpha, c);
            }
            catch { throw new NxError($"bad colour '{hex}'", "use #RRGGBB"); }
        }

        // ---------- script params ----------

        static void ScriptParams(IGH_DocumentObject obj, NodeOp n)
        {
            if (!(obj is IGH_Component c) || !Scripts.IsScript(obj)) return;
            var decl = n.Inputs.Where(p => p.Port != null && !int.TryParse(p.Port, out _)).ToList();
            if (n.Create && decl.Count > 0) Scripts.SetInputs(c, decl);
            else
            {
                foreach (var p in decl)
                    if (!c.Params.Input.Any(x => x.NickName == p.Port || x.Name == p.Port)) Scripts.AddInput(c, p);
                    else if (p.Access != null) { var ip = c.Params.Input.First(x => x.NickName == p.Port || x.Name == p.Port); ip.Access = p.Access.StartsWith("l") ? GH_ParamAccess.list : p.Access.StartsWith("t") ? GH_ParamAccess.tree : GH_ParamAccess.item; }
            }
            foreach (var r in n.RemoveInputs) Scripts.RemoveInput(c, r);
            if (n.Outputs != null) Scripts.SetOutputs(c, n.Outputs);
            else if (n.Create && decl.Count > 0) { /* keep default 'a' output */ }
        }

        // ---------- config ----------

        /// limits/types first, then values (so v=12 is not clamped by the default 0..1 range), code last
        static int ConfigOrder(string k) => k switch
        {
            "r" or "range" or "min" or "max" or "int" or "even" or "odd" or "d" or "items" or "mode" => 0,
            "code" => 2,
            _ => 1,
        };

        static string Keys(IGH_DocumentObject o) => o switch
        {
            GH_NumberSlider _ => "v, r=a..b, min, max, d, int|even|odd",
            GH_Panel _ => "text, ml, wrap",
            GH_BooleanToggle _ => "v",
            GH_ValueList _ => "items=\"A:0|B:1\", sel, mode=dropdown|checklist|sequence|cycle",
            GH_ColourSwatch _ => "c=#RRGGBB",
            GH_Scribble _ => "text, size",
            _ when Scripts.IsScript(o) => "code=\"…\" or code=@path",
            _ => "set=…, ref=[r1,…] (params), expr=\"…\" (expression comps), graph=…",
        } + ", nick, hide, show, lock, unlock, menu=\"Item\" (right-click menu), click=\"fx,fy\", $stateKey=value";

        static void ApplyConfig(GH_Document d, IGH_DocumentObject o, string key, Val v, NodeOp n)
        {
            if (key.StartsWith("$")) return; // handled in bulk
            string s = v?.Kind == VK.Str ? v.Str : v?.ToString();
            switch (key)
            {
                case "menu":
                    foreach (var m in (v.Kind == VK.List ? v.Items.Select(x => x.Kind == VK.Str ? x.Str : x.ToString()) : new[] { s }))
                        LastNotes?.Add($"L{n.Line}: " + MenuIO.Click(o, m));
                    return;
                case "click":
                    LastNotes?.Add($"L{n.Line}: " + MenuIO.ClickAt(o, s));
                    return;
                case "nick": o.NickName = s; return;
                case "hide": if (o is IGH_PreviewObject po) po.Hidden = true; return;
                case "show": if (o is IGH_PreviewObject po2) po2.Hidden = false; return;
                case "lock": if (o is IGH_ActiveObject ao) ao.Locked = true; return;
                case "unlock": if (o is IGH_ActiveObject ao2) ao2.Locked = false; return;
            }
            switch (o)
            {
                case GH_NumberSlider sl:
                    {
                        var S = sl.Slider;
                        switch (key)
                        {
                            case "v": case "val": case "value": sl.SetSliderValue((decimal)Num(v, key)); return;
                            case "min": S.Minimum = (decimal)Num(v, key); return;
                            case "max": S.Maximum = (decimal)Num(v, key); return;
                            case "r": case "range":
                                if (v.Kind != VK.Interval) throw new NxError("r must be a..b");
                                S.Minimum = (decimal)Math.Min(v.Xyz[0], v.Xyz[1]); S.Maximum = (decimal)Math.Max(v.Xyz[0], v.Xyz[1]);
                                // re-apply value inside the new range
                                sl.SetSliderValue(Math.Clamp(S.Value, S.Minimum, S.Maximum));
                                return;
                            case "d": S.Type = Grasshopper.GUI.Base.GH_SliderAccuracy.Float; S.DecimalPlaces = (int)Num(v, key); return;
                            case "int": S.Type = Grasshopper.GUI.Base.GH_SliderAccuracy.Integer; S.DecimalPlaces = 0; return;
                            case "even": S.Type = Grasshopper.GUI.Base.GH_SliderAccuracy.Even; return;
                            case "odd": S.Type = Grasshopper.GUI.Base.GH_SliderAccuracy.Odd; return;
                        }
                        break;
                    }
                case GH_Panel pn:
                    switch (key)
                    {
                        case "text": case "v": pn.UserText = s; pn.ExpireSolution(false); return;
                        case "ml": pn.Properties.Multiline = v.Str != "false"; return;
                        case "wrap": pn.Properties.Wrap = v.Str != "false"; return;
                    }
                    break;
                case GH_BooleanToggle t:
                    if (key == "v" || key == "val" || key == "value") { t.Value = s == "true" || s == "1"; t.ExpireSolution(false); return; }
                    break;
                case GH_ValueList vl:
                    switch (key)
                    {
                        case "items":
                            {
                                vl.ListItems.Clear();
                                var items = v.Kind == VK.List ? v.Items.Select(x => x.Kind == VK.Str ? x.Str : x.ToString()) : s.Split('|');
                                int k = 0;
                                foreach (var it in items)
                                {
                                    var parts = it.Split(new[] { ':' }, 2);
                                    var expr = parts.Length > 1 ? parts[1] : (double.TryParse(parts[0], NumberStyles.Float, C, out _) ? parts[0] : k.ToString());
                                    if (parts.Length == 1 && !double.TryParse(parts[0], NumberStyles.Float, C, out _)) expr = "\"" + parts[0] + "\"";
                                    vl.ListItems.Add(new GH_ValueListItem(parts[0].Trim(), expr.Trim()));
                                    k++;
                                }
                                if (vl.ListItems.Count > 0) vl.SelectItem(0);
                                vl.ExpireSolution(false);
                                return;
                            }
                        case "sel": vl.SelectItem((int)Num(v, key)); vl.ExpireSolution(false); return;
                        case "mode":
                            if (Enum.TryParse<GH_ValueListMode>(s, true, out var m)) { vl.ListMode = m; return; }
                            throw new NxError("mode: dropdown | checklist | sequence | cycle");
                    }
                    break;
                case GH_ColourSwatch sw:
                    if (key == "c" || key == "v") { sw.SwatchColour = ParseColor(s, 255); sw.ExpireSolution(false); return; }
                    break;
                case GH_Scribble scr:
                    if (key == "text") { scr.Text = s; return; }
                    if (key == "size") { scr.Font = new Font(scr.Font.FontFamily, (float)Num(v, key)); return; }
                    break;
                case GH_GraphMapper gm:
                    if (key == "graph") { SetGraph(gm, s); return; }
                    break;
            }
            if (o is IGH_Component c)
            {
                if (key == "expr")
                {
                    var p = c.GetType().GetProperty("Expression");
                    if (p == null) throw new NxError($"{c.Name} has no expression");
                    p.SetValue(c, s);
                    c.Params.OnParametersChanged(); c.ExpireSolution(false);
                    return;
                }
                if (key == "code" && Scripts.IsScript(o))
                {
                    var code = s;
                    if (code.StartsWith("@")) { var path = code.Substring(1); if (!File.Exists(path)) throw new NxError("code file not found: " + path); code = File.ReadAllText(path); }
                    var note = Scripts.SetCodeSmart(c, code.Replace("\r\n", "\n"));
                    if (note != null) Builder.LastNotes.Add($"L{n.Line}: {note}");
                    return;
                }
            }
            if (o is IGH_Param pr && !(o is IGH_Component))
            {
                if (key == "set" || key == "v") { SetPersistent(pr, v); return; }
                if (key == "ref") { SetPersistent(pr, v); return; }
            }
            throw new NxError($"unknown setting '{key}' for {o.Name}", "valid: " + Keys(o));
        }

        static void SetGraph(GH_GraphMapper gm, string type)
        {
            Grasshopper.Kernel.Graphs.IGH_Graph g = type?.ToLowerInvariant() switch
            {
                "linear" => new Grasshopper.Kernel.Graphs.GH_LinearGraph(),
                "bezier" => new Grasshopper.Kernel.Graphs.GH_BezierGraph(),
                "conic" => new Grasshopper.Kernel.Graphs.GH_ConicGraph(),
                "sine" => new Grasshopper.Kernel.Graphs.GH_SineGraph(),
                "parabola" => new Grasshopper.Kernel.Graphs.GH_ParabolaGraph(),
                "gaussian" => new Grasshopper.Kernel.Graphs.GH_GaussianGraph(),
                "power" => new Grasshopper.Kernel.Graphs.GH_PowerGraph(),
                "square" => new Grasshopper.Kernel.Graphs.GH_SquareRootGraph(),
                _ => throw new NxError($"graph '{type}' unknown", "linear | bezier | conic | sine | parabola | gaussian | power | square"),
            };
            gm.Container = new Grasshopper.Kernel.Graphs.GH_GraphContainer(g);
            gm.ExpireSolution(false);
        }

        static double Num(Val v, string key)
        {
            if (v == null) throw new NxError($"'{key}' needs a number");
            if (v.Kind == VK.Num) return v.Num;
            if (v.Kind == VK.Bool) return v.Str == "true" ? 1 : 0;
            if ((v.Kind == VK.Ident || v.Kind == VK.Str) && double.TryParse(v.Str, NumberStyles.Float, C, out var x)) return x;
            throw new NxError($"'{key}' needs a number, got {v}");
        }

        // ---------- inputs ----------

        static void ApplyInput(GH_Document d, IGH_DocumentObject obj, PortOp p, bool output, Func<string, Op, IGH_DocumentObject> resolve, NodeOp n, BuildResult res)
        {
            var param = ComponentSearch.FindPort(obj, p.Port, output, out var err, grow: !output);
            if (param == null) throw new NxError(err);
            if (output) { ApplyFlags(param, p.Flags); return; }

            var v = p.Value;
            if (v != null)
            {
                if (v.Kind == VK.Clear)
                {
                    param.RemoveAllSources();
                    var clr = param.GetType().GetMethod("Script_ClearPersistentData");
                    clr?.Invoke(param, null);
                }
                else if (v.Kind == VK.Ref && v.Refs.All(r => Handles.IsRhHandle(r.id) && r.port == null))
                {
                    if (p.Mode == "=") param.RemoveAllSources();
                    SetPersistent(param, v);
                    res.Values++;
                }
                else if (v.Kind == VK.Ref)
                {
                    var srcs = new List<IGH_Param>();
                    foreach (var (id, port) in v.Refs)
                    {
                        var so = resolve(id, n);
                        if (so == obj) throw new NxError("cannot wire a component to itself");
                        var sp = ComponentSearch.FindPort(so, port, true, out var e2);
                        if (sp == null) throw new NxError($"source {id}: {e2}");
                        srcs.Add(sp);
                    }
                    if (p.Mode == "=") param.RemoveAllSources();
                    foreach (var sp in srcs)
                    {
                        if (p.Mode == "-=") { param.RemoveSource(sp); continue; }
                        if (!param.Sources.Contains(sp)) { param.AddSource(sp); res.Wires++; }
                    }
                    TypeCheck(param, srcs, res, n);
                }
                else
                {
                    if (p.Mode == "=") param.RemoveAllSources();
                    SetPersistent(param, v);
                    res.Values++;
                }
            }
            ApplyFlags(param, p.Flags);
            if (p.Expr != null)
            {
                var ep = param.GetType().GetProperty("Expression");
                if (ep == null) throw new NxError($"input {param.NickName} does not support expressions");
                ep.SetValue(param, p.Expr);
            }
        }

        static readonly HashSet<string> Scalars = new HashSet<string> { "Number", "Integer", "Boolean" };
        static readonly HashSet<string> Shapes = new HashSet<string> { "Curve", "Line", "Arc", "Circle", "Rectangle", "Polyline", "Surface", "Brep", "Mesh", "Box", "SubD", "Extrusion" };

        /// Static hint only for wirings that are almost always mistakes (geometry into a number input, or a number
        /// into a geometry input). Grasshopper converts most other pairs (Surface→Domain², Colour→Shader, …), and
        /// real conversion failures come back as runtime errors after the solve.
        static void TypeCheck(IGH_Param target, List<IGH_Param> srcs, BuildResult res, NodeOp n)
        {
            var tt = target.TypeName;
            foreach (var s in srcs)
            {
                var st = s.TypeName;
                bool bad = (Scalars.Contains(tt) && Shapes.Contains(st)) || (Shapes.Contains(tt) && Scalars.Contains(st));
                if (bad) res.Notes.Add($"L{n.Line}: {s.NickName}({st}) → {target.NickName}({tt}) is probably the wrong wire");
            }
        }

        public static void ApplyFlags(IGH_Param p, string flags)
        {
            if (string.IsNullOrEmpty(flags)) return;
            foreach (var ch in flags)
            {
                switch (ch)
                {
                    case 'f': p.DataMapping = GH_DataMapping.Flatten; break;
                    case 'g': p.DataMapping = GH_DataMapping.Graft; break;
                    case 's': p.Simplify = true; break;
                    case 'r': p.Reverse = true; break;
                    case 'p': p.GetType().GetProperty("Reparameterize")?.SetValue(p, true); break;
                    case 'i': p.GetType().GetProperty("Invert")?.SetValue(p, true); break;
                    case 'h': p.WireDisplay = GH_ParamWireDisplay.hidden; break;
                    case 'd': p.WireDisplay = GH_ParamWireDisplay.faint; break;
                    case 'x':
                        p.DataMapping = GH_DataMapping.None; p.Simplify = false; p.Reverse = false; p.WireDisplay = GH_ParamWireDisplay.@default;
                        p.GetType().GetProperty("Reparameterize")?.SetValue(p, false);
                        p.GetType().GetProperty("Invert")?.SetValue(p, false);
                        break;
                    default: throw new NxError($"unknown flag '^{ch}'", "flags: f flatten, g graft, s simplify, r reverse, p reparam, i invert, h hidden wire, d faint wire, x clear");
                }
            }
        }

        // ---------- values ----------

        static IEnumerable<object> Values(Val v, IGH_Param p)
        {
            switch (v.Kind)
            {
                case VK.List: foreach (var x in v.Items) foreach (var y in Values(x, p)) yield return y; break;
                case VK.Num: yield return v.Num; break;
                case VK.Bool: yield return v.Str == "true"; break;
                case VK.Str when IsGeoParam(p) && System.Text.RegularExpressions.Regex.IsMatch(v.Str, @"^(layer|type|name|sel|bbox|inside|cells):|^sel$"):
                    {   // reference every Rhino object matching a filter: {ref="layer:FE::Lines"}
                        var objs = Rh.Filter.Parse(v.Str).Apply(Rhino.RhinoDoc.ActiveDoc);
                        if (objs.Count == 0) throw new NxError($"Rhino filter '{v.Str}' matched nothing");
                        foreach (var ro in objs) yield return Reference(p, ro.Id, Handles.Rh(ro.Id));
                        break;
                    }
                case VK.Str: yield return v.Str; break;
                case VK.Point:
                    if (p.TypeName == "Vector") yield return new Vector3d(v.Xyz[0], v.Xyz[1], v.Xyz[2]);
                    else yield return new Point3d(v.Xyz[0], v.Xyz[1], v.Xyz[2]);
                    break;
                case VK.Interval: yield return new Interval(v.Xyz[0], v.Xyz[1]); break;
                case VK.Color: yield return ParseColor(v.Str, 255); break;
                case VK.Ref:
                    foreach (var (id, _) in v.Refs)
                    {
                        if (!Handles.IsRhHandle(id)) throw new NxError($"'{id}' is not a Rhino handle (r12) — only Rhino objects can be referenced as values");
                        if (!Handles.TryRh(id, out var g) || Rhino.RhinoDoc.ActiveDoc.Objects.FindId(g) == null) throw new NxError($"Rhino object {id} not found");
                        yield return Reference(p, g, id);
                    }
                    break;
                case VK.Ident when Handles.IsRhHandle(v.Str):
                    foreach (var x in Values(new Val { Kind = VK.Ref, Refs = new List<(string, string)> { (v.Str, null) } }, p)) yield return x;
                    break;
                case VK.Ident:
                    switch (v.Str.ToUpperInvariant())
                    {
                        case "XY": yield return Plane.WorldXY; break;
                        case "XZ": yield return Plane.WorldZX; break;
                        case "YZ": yield return Plane.WorldYZ; break;
                        default: yield return v.Str; break;
                    }
                    break;
                default: throw new NxError($"value {v} not supported here");
            }
        }

        static bool IsGeoParam(IGH_Param p)
        {
            for (var t = p.GetType(); t != null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(GH_PersistentParam<>))
                    return typeof(IGH_GeometricGoo).IsAssignableFrom(t.GetGenericArguments()[0]) || t.GetGenericArguments()[0] == typeof(IGH_GeometricGoo);
            return false;
        }

        /// A referenced Rhino object as the param's own goo type (ReferenceID + LoadGeometry), like GH's "Set one…".
        static object Reference(IGH_Param p, Guid id, string handle)
        {
            Type t = p.GetType(), gooType = null;
            while (t != null) { if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(GH_PersistentParam<>)) { gooType = t.GetGenericArguments()[0]; break; } t = t.BaseType; }
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (gooType != null && !gooType.IsAbstract && !gooType.IsInterface && typeof(IGH_GeometricGoo).IsAssignableFrom(gooType))
            {
                var goo = (IGH_GeometricGoo)Activator.CreateInstance(gooType);
                goo.ReferenceID = id;
                if (goo.LoadGeometry(doc)) return goo;
                var obj = doc.Objects.FindId(id);
                throw new NxError($"{handle} ({(obj == null ? "missing" : Rh.Geo.TypeWord(obj.Geometry))}) cannot be referenced as {p.TypeName}", "use a param of the matching type (Curve, Brep, Mesh, Point, Geometry)");
            }
            var any = GH_Convert.ObjRefToGeometry(new Rhino.DocObjects.ObjRef(doc, id));
            if (any == null) throw new NxError($"{handle} cannot be referenced in Grasshopper");
            return any;
        }

        public static void SetPersistent(IGH_Param p, Val v)
        {
            var vals = Values(v, p).ToArray();
            var m = p.GetType().GetMethod("SetPersistentData", new[] { typeof(object[]) });
            if (m == null) throw new NxError($"{p.Name} ({p.TypeName}) cannot store values", "wire a component or param into it instead");
            // SetPersistentData appends to the default value; clear first
            var clear = p.GetType().GetMethod("Script_ClearPersistentData");
            if (clear != null) clear.Invoke(p, null);
            else (p.GetType().GetProperty("PersistentData")?.GetValue(p) as Grasshopper.Kernel.Data.IGH_Structure)?.Clear();
            m.Invoke(p, new object[] { vals });
            int got = (p.GetType().GetProperty("PersistentDataCount")?.GetValue(p) as int?) ?? vals.Length;
            if (got < vals.Length) throw new NxError($"{p.NickName} ({p.TypeName}) accepted {got}/{vals.Length} values from {v}", $"give values of type {p.TypeName}");
            p.ExpireSolution(false);
        }
    }
}
