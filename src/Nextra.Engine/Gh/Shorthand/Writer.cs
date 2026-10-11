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
            var name = o.Name.Trim();
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
            var objs = objsIn.Where(o => !(o is GH_Group)).ToList();
            var set = new HashSet<Guid>(objs.Select(o => o.InstanceGuid));
            var groups = d.Objects.OfType<GH_Group>().ToList();
            var sb = new StringBuilder();
            var written = new HashSet<Guid>();
            int count = 0;

            // groups that contain any requested object, left → right
            var gsel = groups.Where(g => g.ObjectIDs.Any(set.Contains))
                             .OrderBy(g => g.Attributes?.Bounds.X ?? 0).ThenBy(g => g.Attributes?.Bounds.Y ?? 0).ToList();
            foreach (var g in gsel)
            {
                var members = g.ObjectIDs.Where(set.Contains).Select(id => d.FindObject(id, false)).Where(x => x != null && !(x is GH_Group)).ToList();
                var nested = g.ObjectIDs.Select(id => d.FindObject(id, false)).OfType<GH_Group>().ToList();
                var c = g.Colour;
                sb.AppendLine($"## {(string.IsNullOrWhiteSpace(g.NickName) || g.NickName == "Group" ? "Group" : g.NickName)} #{c.R:X2}{c.G:X2}{c.B:X2} ({GhCore.Handle(d, g)})");
                if (nested.Count > 0) sb.AppendLine("# contains groups: " + string.Join(" ", nested.Select(x => GhCore.Handle(d, x))));
                foreach (var o in Order(members))
                {
                    if (written.Contains(o.InstanceGuid)) { sb.AppendLine(GhCore.Handle(d, o)); continue; } // member of 2 groups
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

        static IEnumerable<IGH_DocumentObject> Order(IEnumerable<IGH_DocumentObject> objs) =>
            objs.OrderBy(o => Math.Round((o.Attributes?.Pivot.X ?? 0) / 60f)).ThenBy(o => o.Attributes?.Pivot.Y ?? 0);

        public static string Ref(GH_Document d, IGH_Param src)
        {
            var top = src.Attributes?.GetTopLevel?.DocObject;
            if (top == null) return "?";
            var h = GhCore.Handle(d, top);
            if (top is IGH_Component c)
            {
                var outs = c.Params.Output;
                // same default the parser uses: first output that is not the script "out" stream
                var dflt = outs.FirstOrDefault(p => p.Name != "out" && p.NickName != "out") ?? outs.FirstOrDefault();
                if (src != dflt || outs.Count > 1 && !(Scripts.IsScript(c) && outs.Count == 2)) return h + "." + PortId(src, outs);
            }
            return h;
        }

        static string PortId(IGH_Param p, List<IGH_Param> siblings)
        {
            var nk = p.NickName;
            bool unique = siblings.Count(x => x.NickName == nk) == 1;
            if (unique && nk.Length > 0 && !nk.Contains(' ') && !nk.Contains(',') && !nk.Contains('=') && !nk.Contains('.')) return nk;
            return siblings.IndexOf(p).ToString();
        }

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

        public static string Lit(IGH_Goo g)
        {
            switch (g)
            {
                case null: return "null";
                case GH_Number n: return Fmt.N(n.Value);
                case GH_Integer i: return i.Value.ToString();
                case GH_Boolean b: return b.Value ? "true" : "false";
                case GH_String s: return "\"" + Fmt.Trunc(s.Value.Replace("\"", "\\\"").Replace("\n", "\\n"), 60) + "\"";
                case GH_Point p: return $"{{{Fmt.N(p.Value.X)},{Fmt.N(p.Value.Y)},{Fmt.N(p.Value.Z)}}}";
                case GH_Vector v: return $"{{{Fmt.N(v.Value.X)},{Fmt.N(v.Value.Y)},{Fmt.N(v.Value.Z)}}}";
                case GH_Interval iv: return $"{Fmt.N(iv.Value.T0)}..{Fmt.N(iv.Value.T1)}";
                case GH_Colour c: return $"#{c.Value.R:X2}{c.Value.G:X2}{c.Value.B:X2}";
                case GH_Plane pl:
                    if (pl.Value.ZAxis.IsParallelTo(Rhino.Geometry.Vector3d.ZAxis) == 1 && pl.Value.Origin.DistanceTo(Rhino.Geometry.Point3d.Origin) < 1e-9) return "XY";
                    return $"plane@{Fmt.P(pl.Value.Origin)}";
                default:
                    {
                        var rid = g.GetType().GetProperty("ReferenceID")?.GetValue(g);
                        if (rid is Guid gid && gid != Guid.Empty) return Handles.Rh(gid);
                        return "<" + g.TypeName + ">";
                    }
            }
        }

        static string Persistent(IGH_Param p, int max = 6)
        {
            var pd = p.GetType().GetProperty("PersistentData")?.GetValue(p) as Grasshopper.Kernel.Data.IGH_Structure;
            if (pd == null || pd.DataCount == 0) return null;
            var items = pd.AllData(true).ToList();
            if (items.Count == 1) return Lit(items[0]);
            var shown = items.Take(max).Select(Lit).ToList();
            return "[" + string.Join(",", shown) + (items.Count > max ? $",…+{items.Count - max}" : "") + "]";
        }

        static string DefaultPersistent(IGH_DocumentObject o, int inputIndex)
        {
            var f = FreshOf(o) as IGH_Component;
            if (f == null || inputIndex >= f.Params.Input.Count) return null;
            return Persistent(f.Params.Input[inputIndex]);
        }

        public static void Node(GH_Document d, IGH_DocumentObject o, StringBuilder sb, WriteOptions opt)
        {
            var h = GhCore.Handle(d, o);
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
                        var pv = Persistent(p);
                        if (pv != null && (Scripts.IsScript(o) || pv != DefaultPersistent(o, i))) val = pv;
                    }
                    var flags = Flags(p);
                    bool scriptDecl = Scripts.IsScript(o);
                    string acc = scriptDecl ? (p.Access == GH_ParamAccess.list ? ":list" : p.Access == GH_ParamAccess.tree ? ":tree" : "") : "";
                    if (val == null && flags.Length == 0 && !scriptDecl) continue;
                    ins.Add($"{PortId(p, c.Params.Input)}{acc}={val ?? ""}{flags}");
                }
                var outMods = c.Params.Output.Where(p => Flags(p).Length > 0).Select(p => $">{PortId(p, c.Params.Output)}={Flags(p)}").ToList();
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
                        if (pn.SourceCount == 0) cfg.Add("text=\"" + (opt.Compact ? Fmt.Trunc(pn.UserText, 120) : pn.UserText).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"");
                    break;
                case GH_BooleanToggle t: cfg.Add($"v={(t.Value ? "true" : "false")}"); break;
                case GH_ValueList vl:
                    cfg.Add("items=\"" + string.Join("|", vl.ListItems.Select(i => $"{i.Name}:{i.Expression}")) + "\"");
                    var sel = vl.ListItems.FindIndex(i => i.Selected);
                    if (sel > 0) cfg.Add($"sel={sel}");
                    if (vl.ListMode != GH_ValueListMode.DropDown) cfg.Add("mode=" + vl.ListMode.ToString().ToLowerInvariant());
                    break;
                case GH_ColourSwatch sw: cfg.Add($"c=#{sw.SwatchColour.R:X2}{sw.SwatchColour.G:X2}{sw.SwatchColour.B:X2}"); break;
                case GH_Scribble sc: cfg.Add("text=\"" + Fmt.Trunc(sc.Text, 80).Replace("\n", "\\n") + "\""); cfg.Add($"size={Fmt.N(sc.Font.Size)}"); break;
                case GH_ButtonObject _: break;
            }
            if (o is IGH_Component c)
            {
                var ex = c.GetType().GetProperty("Expression");
                if (ex != null && ex.GetValue(c) is string e && !string.IsNullOrEmpty(e)) cfg.Add($"expr=\"{e}\"");
                if (Scripts.IsScript(o))
                {
                    try
                    {
                        var code = Scripts.GetCode(c);
                        int n = code.Split('\n').Length;
                        cfg.Add($"code=<{n} lines {Scripts.Lang(c)}>");
                    }
                    catch { }
                }
            }
            if (o is IGH_Param pr && !(o is IGH_Component) && !(o is GH_NumberSlider) && !(o is GH_Panel) && !(o is GH_BooleanToggle) && !(o is GH_ValueList) && !(o is GH_ColourSwatch) && pr.SourceCount == 0)
            {
                var pv = Persistent(pr);
                if (pv != null) cfg.Add((pv.Contains("r") && System.Text.RegularExpressions.Regex.IsMatch(pv, @"\br\d+") ? "ref=" : "set=") + pv);
            }
            if (o.NickName != o.Name && !(o is GH_Scribble) && !(o is GH_Group) && !string.IsNullOrEmpty(o.NickName))
            {
                var fresh = FreshOf(o);
                if (fresh == null || fresh.NickName != o.NickName) cfg.Add($"nick=\"{o.NickName}\"");
            }
            if (o is IGH_PreviewObject po && po.IsPreviewCapable && po.Hidden) cfg.Add("hide");
            if (o is IGH_ActiveObject ao && ao.Locked) cfg.Add("lock");
            if (!(o is GH_NumberSlider) && !(o is GH_Panel) && !(o is GH_Scribble) && !(o is GH_ValueList) && !(o is GH_BooleanToggle) && !Scripts.IsScript(o))
            {
                var st = opt.FullState ? StateIO.Read(o) : StateIO.Diff(o);
                foreach (var kv in st.Take(opt.FullState ? 40 : 8)) cfg.Add($"${kv.Key}={kv.Value}");
            }
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
