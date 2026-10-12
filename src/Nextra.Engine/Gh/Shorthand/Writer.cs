using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;

namespace Nextra.Engine.Gh.Shorthand
{
    public class WriteOptions
    {
        public bool Messages = true;   // ! error / ? warning lines
        public bool Data;              // => output data summary
        public bool Compact = true;    // truncate long text / code
        public bool FullState;         // all $state keys (not just non-default)
        public int MaxObjects = 200;
        public bool File;              // .ghs: nothing truncated, full code, positions, every non-default state

        public List<string> Lost = new List<string>();   // .ghs: values text cannot carry (internalised geometry, Rhino references)

        public static WriteOptions ForFile() => new WriteOptions { Messages = false, Compact = false, File = true, MaxObjects = int.MaxValue };
    }

    /// Canvas → shorthand, using exactly the grammar the parser reads, grouped by GH groups.
    public static class Writer
    {
        static readonly Dictionary<Guid, IGH_DocumentObject> Fresh = new Dictionary<Guid, IGH_DocumentObject>();

        static IGH_DocumentObject FreshOf(IGH_DocumentObject o)
        {
            if (Fresh.TryGetValue(o.ComponentGuid, out var f)) return f;
            try { f = Instances.ComponentServer.EmitObjectProxy(o.ComponentGuid)?.CreateInstance(); } catch { f = null; }
            Fresh[o.ComponentGuid] = f;
            return f;
        }

        public static string TypeName(IGH_DocumentObject o)
        {
            // the registered name: an object's own Name can be renamed (relay params, some files swap Name/NickName)
            string reg = null;
            try { reg = Instances.ComponentServer.EmitObjectProxy(o.ComponentGuid)?.Desc?.Name; } catch { }
            var name = (string.IsNullOrWhiteSpace(reg) ? o.Name : reg).Trim();
            var clean = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\([^)]*\)\s*$", "").Trim();
            if (clean.Length == 0) clean = name;
            int same = 0;
            try
            {
                string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s*\([^)]*\)\s*$", "").Trim();
                same = Instances.ComponentServer.ObjectProxies.Count(p => !p.Obsolete && (p.Desc.Name.Trim() == clean || Norm(p.Desc.Name) == clean));
            }
            catch { }
            return same > 1 || clean != name ? $"{clean} @{o.Category}" : clean;
        }

        public static string Write(GH_Document d, IEnumerable<IGH_DocumentObject> objsIn, WriteOptions opt)
        {
            FileIds = opt.File ? new Dictionary<Guid, string>() : null;   // .ghs: ids g1, g2... in written order (stable re-saves)
            try { return WriteCore(d, objsIn, opt); }
            finally { FileIds = null; }
        }

        static string WriteCore(GH_Document d, IEnumerable<IGH_DocumentObject> objsIn, WriteOptions opt)
        {
            var objs = objsIn.Where(o => !(o is GH_Group)).ToList();
            var set = new HashSet<Guid>(objs.Select(o => o.InstanceGuid));
            var groups = d.Objects.OfType<GH_Group>().ToList();
            var sb = new StringBuilder();
            var written = new HashSet<Guid>();
            int count = 0;

            // groups that contain any requested object, left → right (files: by member positions, which exist before
            // the canvas has drawn the group)
            PointF MinPivot(GH_Group g)
            {
                var ps = g.ObjectIDs.Select(id => d.FindObject(id, false)).Where(x => x?.Attributes != null && !(x is GH_Group)).Select(x => x.Attributes.Pivot).ToList();
                return ps.Count == 0 ? PointF.Empty : new PointF(ps.Min(p => p.X), ps.Min(p => p.Y));
            }
            var gsel = groups.Where(g => g.ObjectIDs.Any(set.Contains)).ToList();
            gsel = opt.File ? gsel.OrderBy(g => MinPivot(g).X).ThenBy(g => MinPivot(g).Y).ToList()
                            : gsel.OrderBy(g => g.Attributes?.Bounds.X ?? 0).ThenBy(g => g.Attributes?.Bounds.Y ?? 0).ToList();
            foreach (var g in gsel)
            {
                var members = g.ObjectIDs.Where(set.Contains).Select(id => d.FindObject(id, false)).Where(x => x != null && !(x is GH_Group)).ToList();
                var nested = g.ObjectIDs.Select(id => d.FindObject(id, false)).OfType<GH_Group>().ToList();
                var c = g.Colour;
                if (opt.File)
                {   // every section is a new group when the file is opened: no handle needed
                    var nm = string.IsNullOrWhiteSpace(g.NickName) || g.NickName == "Group" ? "" : g.NickName + " ";
                    sb.AppendLine($"## {nm}{(c.A != 150 ? $"#{c.A:X2}" : "#")}{c.R:X2}{c.G:X2}{c.B:X2}");
                    if (nested.Count > 0) opt.Lost.Add($"group nesting in {GhCore.Handle(d, g)} {g.NickName}");
                }
                else
                {
                    sb.AppendLine($"## {(string.IsNullOrWhiteSpace(g.NickName) || g.NickName == "Group" ? "Group" : g.NickName)} #{c.R:X2}{c.G:X2}{c.B:X2} ({Id(d, g)})");
                    if (nested.Count > 0) sb.AppendLine("# contains groups: " + string.Join(" ", nested.Select(x => Id(d, x))));
                }
                foreach (var o in Order(members))
                {
                    if (written.Contains(o.InstanceGuid)) { sb.AppendLine(Id(d, o)); continue; } // member of 2 groups
                    if (count++ >= opt.MaxObjects) break;
                    Node(d, o, sb, opt);
                    written.Add(o.InstanceGuid);
                }
            }
            var rest = Order(objs.Where(o => !written.Contains(o.InstanceGuid))).ToList();
            if (rest.Count > 0)
            {
                if (gsel.Count > 0) sb.AppendLine("##").AppendLine("# ungrouped");
                foreach (var o in rest)
                {
                    if (count++ >= opt.MaxObjects) { sb.AppendLine($"# … {objs.Count - opt.MaxObjects} more (use filter/offset)"); break; }
                    Node(d, o, sb, opt);
                }
            }
            return sb.ToString().TrimEnd();
        }

        [ThreadStatic] static Dictionary<Guid, string> FileIds;

        /// The id written for a node: its live handle, or in a .ghs file the next g1, g2… (first use)
        static string Id(GH_Document d, IGH_DocumentObject o)
        {
            if (FileIds == null) return GhCore.Handle(d, o);
            if (!FileIds.TryGetValue(o.InstanceGuid, out var id)) FileIds[o.InstanceGuid] = id = "g" + (FileIds.Count + 1);
            return id;
        }

        /// Data-flow order (sources first), ties by canvas position — readable even for messy canvases.
        static IEnumerable<IGH_DocumentObject> Order(IEnumerable<IGH_DocumentObject> objsIn)
        {
            var objs = objsIn.ToList();
            var set = new HashSet<Guid>(objs.Select(o => o.InstanceGuid));
            var indeg = objs.ToDictionary(o => o.InstanceGuid, o => Upstream(o).Count(u => set.Contains(u.InstanceGuid) && u != o));
            // whole-pixel positions: what a .ghs stores, so a re-opened file sorts exactly like the original
            var byPos = objs.OrderBy(o => Math.Round(Math.Round(o.Attributes?.Pivot.X ?? 0) / 60f)).ThenBy(o => Math.Round(o.Attributes?.Pivot.Y ?? 0))
                            .ThenBy(o => Math.Round(o.Attributes?.Pivot.X ?? 0)).ToList();
            var outL = new List<IGH_DocumentObject>(); var done = new HashSet<Guid>();
            while (outL.Count < objs.Count)
            {
                var next = byPos.FirstOrDefault(o => !done.Contains(o.InstanceGuid) && indeg[o.InstanceGuid] <= 0) ?? byPos.First(o => !done.Contains(o.InstanceGuid)); // cycle: take next by position
                outL.Add(next); done.Add(next.InstanceGuid);
                foreach (var dn in objs.Where(x => !done.Contains(x.InstanceGuid) && Upstream(x).Contains(next))) indeg[dn.InstanceGuid]--;
            }
            return outL;
        }

        static List<IGH_DocumentObject> Upstream(IGH_DocumentObject o)
        {
            var ps = o is IGH_Component c ? c.Params.Input : o is IGH_Param p ? new List<IGH_Param> { p } : new List<IGH_Param>();
            return ps.SelectMany(p => p.Sources).Select(s => s.Attributes?.GetTopLevel?.DocObject).Where(x => x != null).Distinct().ToList();
        }

        public static string Ref(GH_Document d, IGH_Param src)
        {
            var top = src.Attributes?.GetTopLevel?.DocObject;
            if (top == null) return "?";
            var h = Id(d, top);
            if (top is IGH_Component c)
            {
                var outs = c.Params.Output;
                // same default the parser uses: first output that is not the script "out" stream
                var dflt = outs.FirstOrDefault(p => p.Name != "out" && p.NickName != "out") ?? outs.FirstOrDefault();
                if (src != dflt || outs.Count > 1 && !(Scripts.IsScript(c) && outs.Count == 2)) return h + "." + PortId(src, outs, c, true);
            }
            return h;
        }

        static string PortId(IGH_Param p, List<IGH_Param> siblings, IGH_Component owner = null, bool output = false)
        {
            var nk = p.NickName ?? "";
            // .ghs: the port's registered nickname (files may carry renamed ports, e.g. Karamba "Element" vs "Elem")
            if (FileIds != null && owner != null && !Scripts.IsScript(owner) && FreshOf(owner) is IGH_Component f)
            {
                var fs = output ? f.Params.Output : f.Params.Input;
                int ix = siblings.IndexOf(p);
                if (ix >= 0 && fs.Count == siblings.Count) { siblings = fs; p = fs[ix]; nk = p.NickName ?? ""; }
                else if (fs.Count(x => x.Name == p.Name) == 1)
                {   // a mode changed the port set (Karamba unit dropdown): match by the port's Name instead
                    var fp = fs.First(x => x.Name == p.Name);
                    if (fs.Count(x => x.NickName == fp.NickName) == 1 && !siblings.Any(x => x != p && x.NickName == fp.NickName) && PortWord(fp.NickName)) return fp.NickName;
                }
            }
            bool unique = siblings.Count(x => x.NickName == nk) == 1;
            // only names the parser reads back as a port: words, or a pure symbol like Smaller Than's `<`
            if (unique && PortWord(nk)) return nk;
            return siblings.IndexOf(p).ToString();
        }

        static bool PortWord(string nk) => System.Text.RegularExpressions.Regex.IsMatch(nk ?? "", @"^(?:\w+|[<>!|&*/%~]+[=]?|=)$") && !int.TryParse(nk, out _);

        static string Flags(IGH_Param p)
        {
            var f = "";
            if (p.DataMapping == GH_DataMapping.Flatten) f += "f";
            if (p.DataMapping == GH_DataMapping.Graft) f += "g";
            if (p.Simplify) f += "s";
            if (p.Reverse) f += "r";
            var rp = p.GetType().GetProperty("Reparameterize");
            if (rp != null && rp.GetValue(p) is bool b && b) f += "p";
            var inv = p.GetType().GetProperty("Invert");
            if (inv != null && inv.GetValue(p) is bool b2 && b2) f += "i";
            if (p.WireDisplay == GH_ParamWireDisplay.hidden) f += "h";
            else if (p.WireDisplay == GH_ParamWireDisplay.faint) f += "d";
            var s = f.Length > 0 ? "^" + f : "";
            var ex = p.GetType().GetProperty("Expression");
            if (ex != null && ex.GetValue(p) is string e && e.Length > 0) s += $"^e({e})";
            return s;
        }

        public static string Lit(IGH_Goo g) => Lit(g, false);

        public static string Lit(IGH_Goo g, bool full)
        {
            switch (g)
            {
                case null: return "null";
                case GH_Number n: return full ? n.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : Fmt.N(n.Value);
                case GH_Integer i: return i.Value.ToString();
                case GH_Boolean b: return b.Value ? "true" : "false";
                case GH_String s: return Quote(full ? s.Value : Fmt.Trunc(s.Value, 60));
                case GH_Point p: return $"{{{Fmt.N(p.Value.X)},{Fmt.N(p.Value.Y)},{Fmt.N(p.Value.Z)}}}";
                case GH_Vector v: return $"{{{Fmt.N(v.Value.X)},{Fmt.N(v.Value.Y)},{Fmt.N(v.Value.Z)}}}";
                case GH_Interval iv: return $"{Fmt.N(iv.Value.T0)}..{Fmt.N(iv.Value.T1)}";
                case GH_Colour c: return $"#{c.Value.R:X2}{c.Value.G:X2}{c.Value.B:X2}";
                case GH_Plane pl:
                    {
                        var v = pl.Value;
                        bool atO = v.Origin.DistanceTo(Rhino.Geometry.Point3d.Origin) < 1e-9;
                        bool Same(Rhino.Geometry.Plane w) => v.XAxis.IsParallelTo(w.XAxis) == 1 && v.YAxis.IsParallelTo(w.YAxis) == 1;
                        if (atO && Same(Rhino.Geometry.Plane.WorldXY)) return "XY";
                        if (atO && Same(Rhino.Geometry.Plane.WorldZX)) return "XZ";
                        if (atO && Same(Rhino.Geometry.Plane.WorldYZ)) return "YZ";
                        return $"<plane @{Fmt.P(v.Origin)} n{Fmt.V(v.ZAxis)}>";   // not settable as a literal: wire a Plane component
                    }
                default:
                    {
                        var rid = g.GetType().GetProperty("ReferenceID")?.GetValue(g);
                        // files keep the object id (valid in that .3dm, like a .gh); listings use the short handle
                        if (rid is Guid gid && gid != Guid.Empty) return full ? gid.ToString() : Handles.Rh(gid);
                        return "<" + g.TypeName + ">";
                    }
            }
        }

        /// A persistent value a .ghs file can rebuild: not internalised geometry (`<Curve>`), trees or planes.
        /// (Rhino references are written as object ids.)
        static bool Storable(string v)
        {
            var bare = System.Text.RegularExpressions.Regex.Replace(v, @"""(?:\\.|[^""\\])*""", "\"\"");   // ignore text inside strings
            return !System.Text.RegularExpressions.Regex.IsMatch(bare, @"<[A-Za-z]|(^|[\[,])r\d+\b");
        }

        /// "text" with \ " and newlines escaped — what Parser.ReadString reads back.
        public static string Quote(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r\n", "\n").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

        static string Persistent(IGH_Param p, int max = 6, bool full = false)
        {
            var pd = p.GetType().GetProperty("PersistentData")?.GetValue(p) as Grasshopper.Kernel.Data.IGH_Structure;
            if (pd == null || pd.DataCount == 0) return null;
            var items = pd.AllData(true).ToList();
            if (pd.PathCount > 1) return $"<tree: {pd.PathCount} branches, {items.Count} items>";   // flat set=[…] would hide the paths
            if (full) max = int.MaxValue;
            if (items.Count == 1) return Lit(items[0], full);
            var shown = items.Take(max).Select(x => Lit(x, full)).ToList();
            return "[" + string.Join(",", shown) + (items.Count > max ? $",…+{items.Count - max}" : "") + "]";
        }

        static string DefaultPersistent(IGH_DocumentObject o, int inputIndex, bool full = false)
        {
            var f = FreshOf(o) as IGH_Component;
            if (f == null || inputIndex >= f.Params.Input.Count) return null;
            return Persistent(f.Params.Input[inputIndex], 6, full);
        }

        public static void Node(GH_Document d, IGH_DocumentObject o, StringBuilder sb, WriteOptions opt)
        {
            var h = Id(d, o);
            var line = new StringBuilder();
            line.Append(h).Append(" = ").Append(TypeName(o));

            if (o is IGH_Component c)
            {
                var ins = new List<string>();
                for (int i = 0; i < c.Params.Input.Count; i++)
                {
                    var p = c.Params.Input[i];
                    string val = null;
                    if (p.SourceCount > 0) val = string.Join("+", p.Sources.Select(s => Ref(d, s)));
                    else
                    {
                        var pv = Persistent(p, 6, opt.File);
                        if (pv != null && (Scripts.IsScript(o) || pv != DefaultPersistent(o, i, opt.File))) val = pv;
                        if (val != null && opt.File && !Storable(val)) { opt.Lost.Add($"{GhCore.Handle(d, o)}.{p.NickName}: {Fmt.Trunc(val, 40)}"); val = null; }
                    }
                    var flags = Flags(p);
                    bool scriptDecl = Scripts.IsScript(o);
                    string acc = scriptDecl ? (p.Access == GH_ParamAccess.list ? ":list" : p.Access == GH_ParamAccess.tree ? ":tree" : "") : "";
                    // Python type hints (C# hints come from the RunScript signature in the code)
                    if (opt.File && scriptDecl && Scripts.Lang(c) != "cs" && Scripts.GetHint(p) is string hint)
                        acc = (acc.Length == 0 ? ":item" : acc) + ":" + hint;
                    if (val == null && flags.Length == 0 && !scriptDecl) continue;
                    ins.Add($"{PortId(p, c.Params.Input, c)}{acc}={val ?? ""}{flags}");
                }
                var outMods = c.Params.Output.Where(p => Flags(p).Length > 0).Select(p => $">{PortId(p, c.Params.Output, c, true)}={Flags(p)}").ToList();
                ins.AddRange(outMods);
                if (ins.Count > 0) line.Append(" (").Append(string.Join(", ", ins)).Append(')');
                if (Scripts.IsScript(o))
                {
                    var outs = c.Params.Output.Where(p => p.NickName != "out").Select(p => p.NickName);
                    line.Append(" -> (").Append(string.Join(", ", outs)).Append(')');
                }
            }

            if (!(o is IGH_Component) && o is IGH_Param fp && fp.SourceCount > 0)
                line.Append(" (=").Append(string.Join("+", fp.Sources.Select(s => Ref(d, s)))).Append(Flags(fp)).Append(')');

            var cfg = Config(d, o, opt);
            if (cfg.Count > 0) line.Append(" {").Append(string.Join(", ", cfg)).Append('}');
            sb.AppendLine(line.ToString());

            if (opt.Messages && o is IGH_ActiveObject ao)
            {
                foreach (var m in ao.RuntimeMessages(GH_RuntimeMessageLevel.Error).Take(3)) sb.AppendLine("  ! " + Fmt.Trunc(m.Replace("\n", " "), 160));
                foreach (var m in ao.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Take(2)) sb.AppendLine("  ? " + Fmt.Trunc(m.Replace("\n", " "), 140));
            }
            if (opt.Data)
            {
                if (o is IGH_Component cc) foreach (var p in cc.Params.Output.Take(4)) { var s = DataSummary(p); if (s != null) sb.AppendLine($"  => {p.NickName}: {s}"); }
                else if (o is IGH_Param pp) { var s = DataSummary(pp); if (s != null) sb.AppendLine($"  => {s}"); }
            }
        }

        static List<string> Config(GH_Document d, IGH_DocumentObject o, WriteOptions opt)
        {
            var cfg = new List<string>();
            switch (o)
            {
                case GH_NumberSlider s:
                    {
                        var sl = s.Slider;
                        cfg.Add($"v={Fmt.N((double)sl.Value)}");
                        cfg.Add($"r={Fmt.N((double)sl.Minimum)}..{Fmt.N((double)sl.Maximum)}");
                        if (sl.Type == Grasshopper.GUI.Base.GH_SliderAccuracy.Integer) cfg.Add("int");
                        else if (sl.Type == Grasshopper.GUI.Base.GH_SliderAccuracy.Even) cfg.Add("even");
                        else if (sl.Type == Grasshopper.GUI.Base.GH_SliderAccuracy.Odd) cfg.Add("odd");
                        else if (sl.DecimalPlaces != 3) cfg.Add($"d={sl.DecimalPlaces}");
                        break;
                    }
                case GH_Panel pn:
                    if (pn.SourceCount == 0 || !string.IsNullOrEmpty(pn.UserText))
                        if (pn.SourceCount == 0) cfg.Add("text=" + Quote(opt.Compact ? Fmt.Trunc(pn.UserText, 120) : pn.UserText));
                    break;
                case GH_BooleanToggle t: cfg.Add($"v={(t.Value ? "true" : "false")}"); break;
                case GH_ValueList vl:
                    cfg.Add("items=" + Quote(string.Join("|", vl.ListItems.Select(i => $"{i.Name}:{i.Expression}"))));
                    var sel = vl.ListItems.FindIndex(i => i.Selected);
                    if (sel > 0) cfg.Add($"sel={sel}");
                    if (vl.ListMode != GH_ValueListMode.DropDown) cfg.Add("mode=" + vl.ListMode.ToString().ToLowerInvariant());
                    break;
                case GH_ColourSwatch sw: cfg.Add($"c=#{sw.SwatchColour.R:X2}{sw.SwatchColour.G:X2}{sw.SwatchColour.B:X2}"); break;
                case GH_Scribble sc: cfg.Add("text=" + Quote(opt.Compact ? Fmt.Trunc(sc.Text, 80) : sc.Text)); cfg.Add($"size={Fmt.N(sc.Font.Size)}"); break;
                case GH_ButtonObject _: break;
            }
            if (o is IGH_Component c)
            {
                var ex = c.GetType().GetProperty("Expression");
                if (ex != null && ex.GetValue(c) is string e && !string.IsNullOrEmpty(e)) cfg.Add("expr=" + Quote(e));
                if (Scripts.IsScript(o))
                {
                    try
                    {
                        var code = Scripts.GetCode(c);
                        int n = code.Split('\n').Length;
                        // a """ inside the code is written as \""" (the parser reads it back)
                        if (opt.File) cfg.Add("code=\"\"\"\n" + code.Replace("\r\n", "\n").Replace("\"\"\"", "\\\"\"\"").TrimEnd('\n') + "\n\"\"\"");
                        else cfg.Add($"code=<{n} lines {Scripts.Lang(c)}>");
                    }
                    catch { }
                }
            }
            if (o is IGH_Param pr && !(o is IGH_Component) && !(o is GH_NumberSlider) && !(o is GH_Panel) && !(o is GH_BooleanToggle) && !(o is GH_ValueList) && !(o is GH_ColourSwatch) && pr.SourceCount == 0)
            {
                var pv = Persistent(pr, 6, opt.File);
                if (pv != null && opt.File && !Storable(pv)) { opt.Lost.Add($"{GhCore.Handle(d, o)} {o.Name}: {Fmt.Trunc(pv, 40)}"); pv = null; }
                if (pv != null) cfg.Add((System.Text.RegularExpressions.Regex.IsMatch(pv, @"\br\d+\b|^\[?[0-9a-f]{8}-[0-9a-f]{4}-") ? "ref=" : "set=") + pv);
            }
            if ((opt.File || o.NickName != o.Name) && !(o is GH_Scribble) && !(o is GH_Group) && !string.IsNullOrEmpty(o.NickName))
            {
                var fresh = FreshOf(o);
                if (fresh == null || fresh.NickName != o.NickName) cfg.Add("nick=" + Quote(o.NickName));
            }
            if (o is IGH_PreviewObject po && po.IsPreviewCapable && po.Hidden) cfg.Add("hide");
            if (o is IGH_ActiveObject ao && ao.Locked) cfg.Add("lock");
            if (!(o is GH_NumberSlider) && !(o is GH_Panel) && !(o is GH_Scribble) && !(o is GH_ValueList) && !(o is GH_BooleanToggle) && !(o is GH_ColourSwatch) && !Scripts.IsScript(o))
            {
                var st = opt.FullState ? StateIO.Read(o) : StateIO.Diff(o);
                var all = StateIO.Read(o).Keys.ToList();
                bool hasExpr = cfg.Any(x => x.StartsWith("expr="));
                foreach (var kv in st.Where(kv => !kv.Key.EndsWith("/Count") && !(hasExpr && kv.Key.EndsWith("Expression"))).Take(opt.File ? int.MaxValue : opt.FullState ? 40 : 8))
                    cfg.Add($"${StateIO.ShortKey(kv.Key, all)}={kv.Value}");
            }
            if (opt.File && o.Attributes != null) cfg.Add($"pos={{{Math.Round(o.Attributes.Pivot.X)},{Math.Round(o.Attributes.Pivot.Y)}}}");
            return cfg;
        }

        public static string DataSummary(IGH_Param p)
        {
            var data = p.VolatileData;
            if (data == null || data.DataCount == 0) return "empty";
            var types = data.AllData(true).Where(x => x != null).GroupBy(x => x.TypeName).Select(g => $"{g.Count()} {g.Key}").ToList();
            if (types.Count == 0) return $"{data.DataCount} null";
            var tree = data.PathCount == 1 ? $"{data.get_Path(0)}" : $"{data.PathCount} branches";
            var sample = "";
            var first = data.AllData(true).FirstOrDefault(x => x != null);
            if (first is GH_Number || first is GH_Integer || first is GH_Boolean || first is GH_String)
                sample = " e.g. " + string.Join(",", data.AllData(true).Take(4).Select(Lit));
            return $"{string.Join(", ", types)} {tree}{sample}";
        }
    }
}
