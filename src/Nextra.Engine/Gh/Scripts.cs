using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using Nextra.Engine.Gh.Shorthand;

namespace Nextra.Engine.Gh
{
    /// Rhino 8 script components (Python 3 / C# / IronPython 2) plus legacy GhPython / C# Script.
    /// Code goes through the component's own SetSource/TryGetSource (no UI), params are rebuilt from
    /// shorthand declarations, and code can be mirrored to .nextra/scripts/<handle>.py for line-range edits.
    public static class Scripts
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        public static bool IsScript(IGH_DocumentObject o) => o is IGH_Component c && Lang(c) != null;

        /// "py" | "cs" | "ipy" | null
        public static string Lang(IGH_Component c)
        {
            var t = c.GetType().Name;
            if (t.Contains("Python3")) return "py";
            if (t.Contains("IronPython") || t.Contains("ZuiPython") || t.Contains("PythonComponent")) return "ipy";
            if (t.Contains("CSharp") || t.Contains("ScriptComponent") && c.Name.Contains("C#")) return "cs";
            if (c.ComponentGuid == new Guid("b6ba1144-02d6-4a2d-b53c-ec62e290eeb7")) return "cs";
            return null;
        }

        public static string GetCode(IGH_Component c)
        {
            var m = c.GetType().GetMethod("TryGetSource", F);
            if (m != null)
            {
                var args = new object[] { null };
                if ((bool)m.Invoke(c, args)) return ((string)args[0] ?? "").Replace("\r\n", "\n");
            }
            var p = c.GetType().GetProperty("Code", F) ?? c.GetType().GetProperty("ScriptSource", F);
            var v = p?.GetValue(c);
            if (v is string s) return s.Replace("\r\n", "\n");
            if (v != null) { var sc = v.GetType().GetProperty("ScriptCode"); if (sc != null) return ((string)sc.GetValue(v) ?? "").Replace("\r\n", "\n"); }
            throw new NxError($"cannot read code of {c.Name}");
        }

        public static void SetCode(IGH_Component c, string code)
        {
            var m = c.GetType().GetMethod("SetSource", F, null, new[] { typeof(string) }, null);
            if (m != null) { m.Invoke(c, new object[] { code }); }
            else
            {
                var p = c.GetType().GetProperty("Code", F);
                if (p != null && p.CanWrite) p.SetValue(c, code);
                else
                {
                    var ss = c.GetType().GetProperty("ScriptSource", F)?.GetValue(c);
                    var sc = ss?.GetType().GetProperty("ScriptCode");
                    if (sc == null) throw new NxError($"cannot set code on {c.Name}");
                    sc.SetValue(ss, code);
                }
            }
            c.ExpireSolution(false);
        }

        /// C# (and Python SDK-mode) scripts declare params in RunScript(...): let the component parse them.
        public static bool ParamsFromScript(IGH_Component c)
        {
            var m = c.GetType().GetMethod("SetParametersFromScript", F, null, Type.EmptyTypes, null);
            if (m == null) return false;
            try { m.Invoke(c, null); c.Params.OnParametersChanged(); return true; } catch { return false; }
        }

        static bool IsStdOut(IGH_Param p) => p.Name == "out" || p.NickName == "out";

        /// Make the input set exactly `decl` (names in order), keeping wires of params that stay.
        public static void SetInputs(IGH_Component c, List<PortOp> decl)
        {
            if (!(c is IGH_VariableParameterComponent v)) throw new NxError($"{c.Name} inputs cannot be changed");
            var keep = decl.Select(d => d.Port).ToList();
            foreach (var p in c.Params.Input.ToList())
                if (!keep.Contains(p.NickName)) { c.Params.UnregisterInputParameter(p, true); }
            for (int i = 0; i < decl.Count; i++)
            {
                var name = decl[i].Port;
                var p = c.Params.Input.FirstOrDefault(x => x.NickName == name);
                if (p == null)
                {
                    int at = Math.Min(i, c.Params.Input.Count);
                    p = v.CreateParameter(GH_ParameterSide.Input, at);
                    if (p == null) throw new NxError($"{c.Name} refused a new input '{name}'");
                    p.Name = name; p.NickName = name; p.Description = name;
                    c.Params.RegisterInputParam(p, at);
                }
                ApplyAccess(p, decl[i].Access, decl[i].TypeHint);
            }
            v.VariableParameterMaintenance();
            c.Params.OnParametersChanged();
        }

        public static void AddInput(IGH_Component c, PortOp d)
        {
            if (!(c is IGH_VariableParameterComponent v)) throw new NxError($"{c.Name} inputs cannot be added");
            int at = c.Params.Input.Count;
            var p = v.CreateParameter(GH_ParameterSide.Input, at) ?? throw new NxError($"{c.Name} refused a new input '{d.Port}'");
            p.Name = d.Port; p.NickName = d.Port; p.Description = d.Port;
            c.Params.RegisterInputParam(p, at);
            ApplyAccess(p, d.Access, d.TypeHint);
            v.VariableParameterMaintenance();
            c.Params.OnParametersChanged();
        }

        /// Remove unwired inputs whose name the code never mentions (the x/y defaults of a new script).
        public static void DropUnusedInputs(IGH_Component c)
        {
            var code = GetCode(c) ?? "";
            if (code.Trim().Length == 0) return;
            foreach (var p in c.Params.Input.ToList())
                if (p.SourceCount == 0 && !System.Text.RegularExpressions.Regex.IsMatch(code, @"\b" + System.Text.RegularExpressions.Regex.Escape(p.NickName) + @"\b"))
                    RemoveInput(c, p.NickName);
        }

        public static void RemoveInput(IGH_Component c, string name)
        {
            var p = c.Params.Input.FirstOrDefault(x => x.NickName == name || x.Name == name) ?? throw new NxError($"no input '{name}' on {c.Name}");
            c.Params.UnregisterInputParameter(p, true);
            (c as IGH_VariableParameterComponent)?.VariableParameterMaintenance();
            c.Params.OnParametersChanged();
        }

        public static void SetOutputs(IGH_Component c, List<OutDecl> decl)
        {
            if (!(c is IGH_VariableParameterComponent v)) throw new NxError($"{c.Name} outputs cannot be changed");
            var keep = decl.Select(d => d.Name).ToList();
            foreach (var p in c.Params.Output.ToList())
                if (!IsStdOut(p) && !keep.Contains(p.NickName)) c.Params.UnregisterOutputParameter(p, true);
            int offset = c.Params.Output.Count(IsStdOut);
            for (int i = 0; i < decl.Count; i++)
            {
                var name = decl[i].Name;
                if (c.Params.Output.Any(x => x.NickName == name)) continue;
                int at = Math.Min(i + offset, c.Params.Output.Count);
                var p = v.CreateParameter(GH_ParameterSide.Output, at) ?? throw new NxError($"{c.Name} refused output '{name}'");
                p.Name = name; p.NickName = name; p.Description = name;
                c.Params.RegisterOutputParam(p, at);
            }
            v.VariableParameterMaintenance();
            c.Params.OnParametersChanged();
        }

        static void ApplyAccess(IGH_Param p, string access, string hint)
        {
            if (!string.IsNullOrEmpty(access))
            {
                p.Access = access.StartsWith("l") ? GH_ParamAccess.list : access.StartsWith("t") ? GH_ParamAccess.tree : GH_ParamAccess.item;
            }
            if (!string.IsNullOrEmpty(hint)) SetHint(p, hint);
        }

        static readonly Dictionary<string, string> HintAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Int32"] = "int", ["Integer"] = "int", ["Double"] = "double", ["Number"] = "double", ["float"] = "double", ["Boolean"] = "bool",
            ["String"] = "string", ["str"] = "string", ["Point"] = "Point3d", ["Vector"] = "Vector3d", ["Rectangle"] = "Rectangle3d",
            ["object"] = "No Type Hint", ["Any"] = "No Type Hint", ["var"] = "No Type Hint", ["dynamic"] = "No Type Hint",
            ["NurbsCurve"] = "Curve", ["PolylineCurve"] = "Curve", ["LineCurve"] = "Curve", ["ArcCurve"] = "Curve", ["Color"] = "Color",
        };

        /// Rhino 8 script params: TypeHints.Select("Curve" | "int" | …). Returns false if unknown.
        public static bool SetHint(IGH_Param p, string hint)
        {
            var hs = p.GetType().GetProperty("TypeHints", F)?.GetValue(p);
            if (hs == null) return false;
            if (HintAlias.TryGetValue(hint, out var al)) hint = al;
            var sel = hs.GetType().GetMethod("Select", new[] { typeof(string) });
            try { var r = sel?.Invoke(hs, new object[] { hint }); return r != null; } catch { return false; }
        }

        /// C# `RunScript(Curve crv, List<Point3d> pts, ref object a)` → inputs (name, access, hint) and outputs.
        public static bool ParseRunScript(string code, out List<PortOp> inputs, out List<OutDecl> outputs)
        {
            inputs = new List<PortOp>(); outputs = new List<OutDecl>();
            var m = System.Text.RegularExpressions.Regex.Match(code ?? "", @"void\s+RunScript\s*\(([^)]*)\)", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) return false;
            foreach (var raw in SplitArgs(m.Groups[1].Value))
            {
                var a = raw.Trim();
                if (a.Length == 0) continue;
                bool isOut = a.StartsWith("ref ") || a.StartsWith("out ");
                if (isOut) a = a.Substring(4).Trim();
                int sp = a.LastIndexOf(' ');
                if (sp < 0) continue;
                var type = a.Substring(0, sp).Trim(); var name = a.Substring(sp + 1).Trim();
                if (isOut) { outputs.Add(new OutDecl { Name = name }); continue; }
                string access = "item", hint = type;
                var g = System.Text.RegularExpressions.Regex.Match(type, @"^(List|IList|IEnumerable|DataTree)<(.+)>$");
                if (g.Success) { access = g.Groups[1].Value == "DataTree" ? "tree" : "list"; hint = g.Groups[2].Value; }
                inputs.Add(new PortOp { Port = name, Access = access, TypeHint = hint });
            }
            return true;
        }

        static IEnumerable<string> SplitArgs(string s)
        {
            int depth = 0, last = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '<') depth++; else if (s[i] == '>') depth--;
                else if (s[i] == ',' && depth == 0) { yield return s.Substring(last, i - last); last = i + 1; }
            }
            yield return s.Substring(last);
        }

        /// Set code; for C# first make the params match the RunScript signature (Rhino 8 regenerates the
        /// signature from params, so params must be right before the source is applied).
        public static string SetCodeSmart(IGH_Component c, string code)
        {
            string note = null;
            if (Lang(c) == "cs" && ParseRunScript(code, out var ins, out var outs))
            {
                SetInputs(c, ins);
                if (outs.Count > 0) SetOutputs(c, outs);
                var bad = ins.Where(i => i.TypeHint != null && c.Params.Input.FirstOrDefault(x => x.NickName == i.Port) is IGH_Param pp && !SetHint(pp, i.TypeHint)).Select(i => i.TypeHint).ToList();
                note = $"ports from RunScript: in {string.Join(",", ins.Select(i => i.Port))} out {string.Join(",", outs.Select(o => o.Name))}" + (bad.Count > 0 ? $"; unknown type hints {string.Join(",", bad)} (left as object)" : "");
            }
            SetCode(c, code);
            return note;
        }

        // ---------- file mirror ----------

        public static string ScriptDir(GH_Document d)
        {
            string baseDir = d.IsFilePathDefined ? Path.GetDirectoryName(d.FilePath) : Path.Combine(Memory.Dir, "unsaved_gh");
            var dir = Path.Combine(baseDir, ".nextra", "scripts");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string Ext(IGH_Component c) => Lang(c) == "cs" ? ".cs" : ".py";

        public static string Mirror(GH_Document d, IGH_Component c)
        {
            var path = Path.Combine(ScriptDir(d), GhCore.Handle(d, c) + Ext(c));
            File.WriteAllText(path, GetCode(c));
            return path;
        }

        public static string Numbered(string code, int from = 1, int to = int.MaxValue)
        {
            var lines = code.Split('\n');
            to = Math.Min(to, lines.Length);
            return string.Join("\n", Enumerable.Range(from, Math.Max(0, to - from + 1)).Select(i => $"{i,4}| {lines[i - 1]}"));
        }
    }
}
