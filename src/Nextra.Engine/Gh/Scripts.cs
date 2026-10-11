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
            if (!string.IsNullOrEmpty(hint))
            {
                // Rhino 8 script params expose a settable type hint via "Converter"/"TypeHint"; best effort
                var th = p.GetType().GetProperty("TypeHint", F) ?? p.GetType().GetProperty("TypeHintName", F);
                try { if (th != null && th.CanWrite && th.PropertyType == typeof(string)) th.SetValue(p, hint); } catch { }
            }
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
