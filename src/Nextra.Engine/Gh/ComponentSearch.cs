using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Grasshopper;
using Grasshopper.Kernel;

namespace Nextra.Engine.Gh
{
    /// Component lookup straight from the loaded ComponentServer (built-ins + every installed plug-in),
    /// with real port names. Replaces the old RAG index: always matches what is actually installed.
    public static class ComponentSearch
    {
        static readonly string[] Builtin = { "Params", "Maths", "Sets", "Vector", "Curve", "Surface", "Mesh", "Intersect", "Transform", "Display", "Rhino", "Script" };

        static readonly Dictionary<string, string> Alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Slider"] = "Number Slider", ["Toggle"] = "Boolean Toggle", ["Bool"] = "Boolean Toggle",
            ["Py"] = "Python 3 Script", ["Py3"] = "Python 3 Script", ["Python"] = "Python 3 Script", ["Python3"] = "Python 3 Script", ["Python 3"] = "Python 3 Script",
            ["CS"] = "C# Script", ["C#"] = "C# Script", ["CSharp"] = "C# Script",
            ["IronPython"] = "IronPython 2 Script", ["GhPython"] = "IronPython 2 Script",
            ["Expr"] = "Expression", ["Eval"] = "Evaluate", ["List"] = "Value List", ["ValueList"] = "Value List",
            ["Swatch"] = "Colour Swatch", ["Color Swatch"] = "Colour Swatch", ["Pt"] = "Point", ["XYZ"] = "Construct Point",
            ["Text"] = "Panel", ["Note"] = "Scribble",
        };

        static Dictionary<Guid, string> _portCache = new Dictionary<Guid, string>();

        public static IEnumerable<IGH_ObjectProxy> All => Instances.ComponentServer.ObjectProxies;

        public static IGH_ObjectProxy Resolve(string name, string cat, out string note, IList<string> portHints = null)
        {
            note = null;
            name = name.Trim();
            if (Alias.TryGetValue(name, out var al)) name = al;
            var all = All.Where(p => p.Kind == GH_ObjectType.CompiledObject).ToList();

            // guid
            if (Guid.TryParse(name, out var g)) return all.FirstOrDefault(p => p.Guid == g) ?? throw new NxError($"no component with guid {g}");

            IEnumerable<IGH_ObjectProxy> Filter(IEnumerable<IGH_ObjectProxy> src)
            {
                if (string.IsNullOrEmpty(cat)) return src;
                var parts = cat.Split('/');
                var r = src.Where(p => p.Desc.Category.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase));
                if (parts.Length > 1) r = r.Where(p => p.Desc.SubCategory.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase));
                return r;
            }

            var exact = Filter(all.Where(p => p.Desc.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (exact.Count == 0) exact = Filter(all.Where(p => p.Desc.NickName.Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (exact.Count == 0) exact = Filter(all.Where(p => Norm(p.Desc.Name) == Norm(name))).ToList();
            var live = exact.Where(p => !p.Obsolete).ToList();
            if (live.Count > 0) exact = live;
            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1 && portHints != null && portHints.Count > 0)
            {
                // pick the candidate whose ports match the ports used on this line
                var fit = exact.Where(p => PortsMatch(p, portHints)).ToList();
                if (fit.Count == 1) { note = $"'{name}' is ambiguous; used {fit[0].Desc.Category}/{fit[0].Desc.SubCategory} because its ports match {string.Join(",", portHints)}"; return fit[0]; }
                if (fit.Count > 1) exact = fit;
            }
            if (exact.Count > 1)
            {
                var bi = exact.Where(p => Builtin.Contains(p.Desc.Category)).ToList();
                if (bi.Count == 1) { note = $"'{name}' is ambiguous; used {bi[0].Desc.Category}/{bi[0].Desc.SubCategory} (others: {string.Join(", ", exact.Except(bi).Select(p => p.Desc.Category).Distinct())})"; return bi[0]; }
                throw new NxError($"'{name}' is ambiguous", "add @Category: " + string.Join("; ", exact.Take(6).Select(p => $"{p.Desc.Name} @{p.Desc.Category}/{p.Desc.SubCategory}")));
            }
            // fuzzy
            var scored = all.Where(p => !p.Obsolete).Select(p => (p, s: Score(p, name))).Where(x => x.s > 0).OrderByDescending(x => x.s).Take(6).ToList();
            var sugg = scored.Select(x => $"{x.p.Desc.Name} @{x.p.Desc.Category}").ToList();
            throw new NxError($"no component named '{name}'" + (cat != null ? " in " + cat : ""),
                sugg.Count > 0 ? "did you mean: " + string.Join("; ", sugg) + " · query {scope:'components', filter:'…'} shows ports" : "query {scope:'components', filter:'keyword'}");
        }

        static readonly Dictionary<Guid, IGH_DocumentObject> Probe = new Dictionary<Guid, IGH_DocumentObject>();

        static bool PortsMatch(IGH_ObjectProxy p, IList<string> hints)
        {
            if (!Probe.TryGetValue(p.Guid, out var o)) { try { o = p.CreateInstance(); } catch { o = null; } Probe[p.Guid] = o; }
            if (!(o is IGH_Component c)) return false;
            foreach (var h in hints)
            {
                if (int.TryParse(h, out var i)) { if (i >= c.Params.Input.Count) return false; continue; }
                if (!c.Params.Input.Any(x => x.NickName.Equals(h, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(h, StringComparison.OrdinalIgnoreCase))) return false;
            }
            return true;
        }

        static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        static int Score(IGH_ObjectProxy p, string q)
        {
            var n = p.Desc.Name; var nn = Norm(n); var nq = Norm(q);
            if (nn.Length == 0 || nq.Length == 0) return 0;
            int s = 0;
            if (nn.Contains(nq)) s += 50 - Math.Abs(nn.Length - nq.Length);
            else if (nq.Contains(nn)) s += 30;
            var words = q.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var w in words) if (n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) s += 10;
            foreach (var w in words) if ((p.Desc.Description ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) s += 2;
            if (Builtin.Contains(p.Desc.Category)) s += 3;
            return s;
        }

        /// "in: B(Base Geometry) D(Direction Vector) | out: E(Extrusion Geometry)"
        public static string Ports(IGH_ObjectProxy p)
        {
            if (_portCache.TryGetValue(p.Guid, out var s)) return s;
            try
            {
                var o = p.CreateInstance();
                if (o is IGH_Component c)
                    s = "in: " + string.Join(" ", c.Params.Input.Select(PortText)) + " | out: " + string.Join(" ", c.Params.Output.Select(PortText));
                else if (o is IGH_Param pa) s = $"param ({pa.TypeName})";
                else s = o?.GetType().Name ?? "";
            }
            catch (Exception e) { s = "(ports unavailable: " + e.Message + ")"; }
            _portCache[p.Guid] = s;
            return s;
        }

        public static string PortText(IGH_Param p)
        {
            var acc = p.Access == GH_ParamAccess.list ? "[]" : p.Access == GH_ParamAccess.tree ? "{}" : "";
            var opt = p.Optional ? "?" : "";
            return p.NickName == p.Name ? $"{p.Name}{acc}{opt}:{Short(p.TypeName)}" : $"{p.NickName}({p.Name}{acc}{opt}:{Short(p.TypeName)})";
        }

        static string Short(string t) => t.Replace("Generic Data", "Any").Replace("Integer", "Int").Replace("Boolean", "Bool");

        public static void Run(Args a, Reply r)
        {
            var q = a.Str("filter", null) ?? a.Str("name", null);
            int limit = a.Int("limit", 8);
            if (string.IsNullOrWhiteSpace(q))
            {
                // plug-in overview
                var cats = All.Where(p => !p.Obsolete).GroupBy(p => p.Desc.Category).OrderBy(g => g.Key).Select(g => $"{g.Key}({g.Count()})");
                r.Line("categories: " + string.Join(", ", cats));
                r.Line("search: query {scope:'components', filter:'extrude'} · filter:'@Karamba3D' lists a category");
                return;
            }
            IEnumerable<IGH_ObjectProxy> hits;
            if (q.StartsWith("@"))
            {
                var parts = q.Substring(1).Split('/');
                hits = All.Where(p => !p.Obsolete && p.Desc.Category.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)
                                     && (parts.Length < 2 || p.Desc.SubCategory.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase)))
                          .OrderBy(p => p.Desc.SubCategory).ThenBy(p => p.Desc.Name);
                var list = hits.ToList();
                r.Line($"{list.Count} in {q}:");
                foreach (var g in list.GroupBy(p => p.Desc.SubCategory))
                    r.Line($"[{g.Key}] " + string.Join(", ", g.Select(p => p.Desc.Name)));
                r.Line("ports: query {scope:'components', filter:'<exact name>'}");
                return;
            }
            var terms = q.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            foreach (var t in terms)
            {
                var exact = All.Where(p => p.Desc.Name.Equals(t, StringComparison.OrdinalIgnoreCase) && !p.Obsolete).ToList();
                var list = exact.Count > 0 ? exact : All.Where(p => !p.Obsolete).Select(p => (p, s: Score(p, t))).Where(x => x.s > 0).OrderByDescending(x => x.s).Take(limit).Select(x => x.p).ToList();
                if (list.Count == 0) { r.Line($"'{t}': nothing found"); continue; }
                foreach (var p in list.Take(limit))
                    r.Line($"{p.Desc.Name} @{p.Desc.Category}/{p.Desc.SubCategory}: {Ports(p)}" + (exact.Count > 0 && terms.Count == 1 ? $"\n  {Fmt.Trunc(p.Desc.Description, 160)}" : ""));
            }
        }

        // ---------- port resolution on live objects ----------

        public static IGH_Param FindPort(IGH_DocumentObject obj, string spec, bool output, out string error, bool grow = false)
        {
            error = null;
            if (obj is IGH_Param single && !(obj is IGH_Component))
            {
                if (string.IsNullOrEmpty(spec) || spec == "0" || spec.Equals(single.NickName, StringComparison.OrdinalIgnoreCase) || spec.Equals(single.Name, StringComparison.OrdinalIgnoreCase)) return single;
                error = $"{obj.Name} is a parameter with one port; drop '.{spec}'";
                return null;
            }
            if (!(obj is IGH_Component c)) { error = $"{obj.Name} has no ports"; return null; }
            var list = output ? c.Params.Output : c.Params.Input;
            if (string.IsNullOrEmpty(spec))
            {
                if (output && list.Count > 0)
                {
                    // default output: first non-"out" (script stdout) param
                    return list.FirstOrDefault(p => p.Name != "out" && p.NickName != "out") ?? list[0];
                }
                if (!output && list.Count == 1) return list[0];
                error = $"{Describe(c, output)} — say which port";
                return null;
            }
            if (int.TryParse(spec, out int idx))
            {
                if (idx >= 0 && idx < list.Count) return list[idx];
                if (grow && TryGrow(c, output, idx + 1)) return (output ? c.Params.Output : c.Params.Input)[idx];
                error = $"port index {idx} out of range; {Describe(c, output)}";
                return null;
            }
            var hit = list.FirstOrDefault(p => p.NickName == spec)
                      ?? list.FirstOrDefault(p => p.NickName.Equals(spec, StringComparison.OrdinalIgnoreCase))
                      ?? list.FirstOrDefault(p => p.Name.Equals(spec, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            var pref = list.Where(p => p.Name.StartsWith(spec, StringComparison.OrdinalIgnoreCase) || p.NickName.StartsWith(spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pref.Count == 1) return pref[0];
            // variable-parameter components: D3 on Merge, etc.
            if (grow && c is IGH_VariableParameterComponent)
            {
                var m = System.Text.RegularExpressions.Regex.Match(spec, @"(\d+)$");
                if (m.Success && list.Count > 0 && System.Text.RegularExpressions.Regex.Replace(list[0].NickName, @"\d+$", "") == spec.Substring(0, m.Index))
                {
                    int want = int.Parse(m.Value);
                    int baseIdx = int.TryParse(System.Text.RegularExpressions.Regex.Match(list[0].NickName, @"\d+$").Value, out var b0) ? b0 : 0;
                    int target = want - baseIdx;
                    if (TryGrow(c, output, target + 1)) return (output ? c.Params.Output : c.Params.Input)[target];
                }
            }
            error = $"no {(output ? "output" : "input")} '{spec}' on {c.Name}; {Describe(c, output)}";
            return null;
        }

        public static string Describe(IGH_Component c, bool output)
        {
            var list = output ? c.Params.Output : c.Params.Input;
            return (output ? "outputs: " : "inputs: ") + (list.Count == 0 ? "(none)" : string.Join(" ", list.Select(PortText)));
        }

        public static bool TryGrow(IGH_Component c, bool output, int count)
        {
            if (!(c is IGH_VariableParameterComponent v)) return false;
            var side = output ? GH_ParameterSide.Output : GH_ParameterSide.Input;
            int safe = 0;
            var list = output ? c.Params.Output : c.Params.Input;
            while (list.Count < count && safe++ < 64)
            {
                int i = list.Count;
                if (!v.CanInsertParameter(side, i)) return false;
                var p = v.CreateParameter(side, i);
                if (p == null) return false;
                if (output) c.Params.RegisterOutputParam(p, i); else c.Params.RegisterInputParam(p, i);
                list = output ? c.Params.Output : c.Params.Input;
            }
            v.VariableParameterMaintenance();
            c.Params.OnParametersChanged();
            return list.Count >= count;
        }
    }
}
