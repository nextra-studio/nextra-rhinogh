using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nextra.Engine
{
    /// One documented API item: a type, member or rhinoscriptsyntax function, already cleaned for an LLM.
    public class ApiItem
    {
        public string Src { get; set; }      // rc = RhinoCommon, gh = Grasshopper SDK (+GH_IO), rs = rhinoscriptsyntax
        public string Kind { get; set; }     // T type, C ctor, M method, P property, F field, E event, O operator, S rs function
        public string Ns { get; set; }       // Rhino.Geometry | rs.surface
        public string Type { get; set; }     // Brep (nested: BrepFace.Something)
        public string Name { get; set; }     // CreateFromLoft
        public string Sig { get; set; }      // CreateFromLoft(IEnumerable<Curve> curves, …) / class Brep : GeometryBase
        public string Ret { get; set; }      // Brep[]
        public bool Static { get; set; }
        public string Obsolete { get; set; } // null, or the obsolete message
        public string Sum { get; set; }      // first sentence
        public string Doc { get; set; }      // full cleaned doc (params, returns, remarks, example)
    }

    /// Cleaned, searchable API reference for RhinoCommon, the Grasshopper SDK and rhinoscriptsyntax.
    /// Built once per installed Rhino/GH version from the assemblies (reflection: real signatures and return
    /// types) and the XML docs that ship with Rhino; rhinoscriptsyntax from its Python sources. Cached as JSON in
    /// %APPDATA%/Nextra/api/, with tidy per-namespace Markdown next to it. Served by query {scope:'api'}.
    public static class ApiDocs
    {
        const int Format = 3;
        static List<ApiItem> _items;
        static string _stamp;

        public static string Dir => Path.Combine(Entry.AppDir, "api");

        static string Stamp()
        {
            var gh = typeof(Grasshopper.Kernel.GH_Document).Assembly.GetName().Version;
            return $"rhino {Rhino.RhinoApp.Version} gh {gh} f{Format}";
        }

        // ------------------------------------------------------------------ load / build

        public static List<ApiItem> Items(Reply r = null)
        {
            var stamp = Stamp();
            if (_items != null && _stamp == stamp) return _items;
            var cache = Path.Combine(Dir, "index.json");
            var stampFile = Path.Combine(Dir, "stamp.txt");
            try
            {
                if (File.Exists(cache) && File.Exists(stampFile) && File.ReadAllText(stampFile).Trim() == stamp)
                {
                    _items = JsonSerializer.Deserialize<List<ApiItem>>(File.ReadAllBytes(cache));
                    _stamp = stamp;
                    return _items;
                }
            }
            catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var items = Build(out var notes);
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(cache, JsonSerializer.SerializeToUtf8Bytes(items));
            File.WriteAllText(stampFile, stamp);
            try { WriteMarkdown(items, Path.Combine(Dir, "md")); } catch (Exception e) { notes.Add("markdown export failed: " + e.Message); }
            r?.Note($"API reference built once for this Rhino version: {items.Count} items in {sw.ElapsedMilliseconds / 1000.0:0.0}s" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : ""));
            _items = items; _stamp = stamp;
            return items;
        }

        static List<ApiItem> Build(out List<string> notes)
        {
            notes = new List<string>();
            var res = new List<ApiItem>();
            var rc = typeof(Rhino.RhinoDoc).Assembly;
            var gh = typeof(Grasshopper.Kernel.GH_Document).Assembly;
            var io = typeof(GH_IO.Serialization.GH_Archive).Assembly;
            foreach (var (asm, src) in new[] { (rc, "rc"), (gh, "gh"), (io, "gh") })
            {
                try { res.AddRange(Reflect(asm, src)); }
                catch (Exception e) { notes.Add($"{asm.GetName().Name}: {e.Message}"); }
            }
            try { res.AddRange(RhinoScript(out var where)); if (where == null) notes.Add("rhinoscriptsyntax sources not found"); }
            catch (Exception e) { notes.Add("rhinoscriptsyntax: " + e.Message); }
            return res;
        }

        // ------------------------------------------------------------------ XML docs

        class XDoc { public string Summary, Returns, Remarks, Example, Value; public List<(string name, string text)> Params = new List<(string, string)>(); }

        static Dictionary<string, XDoc> LoadXml(Assembly asm)
        {
            var res = new Dictionary<string, XDoc>(StringComparer.Ordinal);
            var loc = asm.Location;
            if (string.IsNullOrEmpty(loc)) return res;
            var xml = new[] { ".xml", ".XML" }.Select(e => Path.ChangeExtension(loc, e)).FirstOrDefault(File.Exists);
            if (xml == null) return res;
            var x = XDocument.Load(xml);
            foreach (var m in x.Descendants("member"))
            {
                var id = (string)m.Attribute("name");
                if (id == null) continue;
                var d = new XDoc
                {
                    Summary = Clean(m.Element("summary")),
                    Returns = Clean(m.Element("returns")),
                    Remarks = Clean(m.Element("remarks")),
                    Value = Clean(m.Element("value")),
                    Example = m.Element("example") == null ? null : CleanCode(m.Element("example")),
                };
                foreach (var p in m.Elements("param")) d.Params.Add(((string)p.Attribute("name"), Clean(p)));
                res[id] = d;
            }
            return res;
        }

        /// XML doc text → plain text: <see cref="T:Rhino.Geometry.Point3d"/> → Point3d, <c>x</c> → x, paragraphs kept.
        static string Clean(XElement e)
        {
            if (e == null) return null;
            var sb = new StringBuilder();
            void Walk(XNode n)
            {
                switch (n)
                {
                    case XText t: sb.Append(t.Value); break;
                    case XElement el:
                        var name = el.Name.LocalName;
                        if (name == "see" || name == "seealso")
                        {
                            var cref = (string)el.Attribute("cref") ?? (string)el.Attribute("langword") ?? (string)el.Attribute("href");
                            sb.Append(el.Value.Length > 0 ? el.Value : CrefShort(cref));
                        }
                        else if (name == "paramref" || name == "typeparamref") sb.Append((string)el.Attribute("name"));
                        else if (name == "para" || name == "br") { sb.Append('\n'); foreach (var c in el.Nodes()) Walk(c); sb.Append('\n'); }
                        else if (name == "code") sb.Append(' ').Append(el.Value.Trim()).Append(' ');
                        else foreach (var c in el.Nodes()) Walk(c);
                        break;
                }
            }
            foreach (var n in e.Nodes()) Walk(n);
            var s = Regex.Replace(sb.ToString(), @"[ \t\r]*\n[ \t\r\n]*\n[ \t\r]*", "\n");   // blank lines → one break
            s = Regex.Replace(s, @"[ \t\r]*\n[ \t\r]*(?=\S)", m => " ");                       // wrapped lines → one line
            s = Regex.Replace(s, @"[ \t]{2,}", " ").Trim();
            s = Regex.Replace(s, @"^(///\s*)+", "");                                            // stray doc-comment markers
            return s.Length == 0 ? null : s;
        }

        static string CleanCode(XElement ex)
        {
            var code = ex.Descendants("code").FirstOrDefault(c => ((string)c.Attribute("language") ?? "cs").StartsWith("c", StringComparison.OrdinalIgnoreCase))
                       ?? ex.Descendants("code").FirstOrDefault();
            if (code == null) return Clean(ex);
            var lines = code.Value.Replace("\r", "").Split('\n').ToList();
            while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            return string.Join("\n", lines.Select(l => l.Length >= indent ? l.Substring(indent) : l.TrimStart()));
        }

        static string CrefShort(string cref)
        {
            if (string.IsNullOrEmpty(cref)) return "";
            var s = cref.Length > 2 && cref[1] == ':' ? cref.Substring(2) : cref;
            var paren = s.IndexOf('(');
            if (paren >= 0) s = s.Substring(0, paren);
            var parts = s.Split('.');
            // Type.Member keeps the type (Curve.Reverse), a bare type just its name
            return cref.StartsWith("T:") || parts.Length < 2 ? parts[^1] : parts[^2] + "." + parts[^1];
        }

        static string FirstSentence(string s, int max = 110)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\n', ' ');
            var m = Regex.Match(s, @"^.*?[.!?](?=\s+[A-Z(]|\s*$)");
            var f = m.Success ? m.Value : s;
            return f.Length <= max ? f : f.Substring(0, max - 1).TrimEnd() + "…";
        }

        // ------------------------------------------------------------------ reflection

        static IEnumerable<Type> Exported(Assembly asm)
        {
            try { return asm.GetExportedTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null && t.IsPublic); }
        }

        static bool Hidden(MemberInfo m)
        {
            try
            {
                var eb = m.GetCustomAttributes(typeof(System.ComponentModel.EditorBrowsableAttribute), false).FirstOrDefault() as System.ComponentModel.EditorBrowsableAttribute;
                return eb != null && eb.State == System.ComponentModel.EditorBrowsableState.Never;
            }
            catch { return false; }
        }

        static string ObsoleteOf(MemberInfo m)
        {
            try
            {
                var o = m.GetCustomAttributes(typeof(ObsoleteAttribute), false).FirstOrDefault() as ObsoleteAttribute;
                return o == null ? null : string.IsNullOrWhiteSpace(o.Message) ? "obsolete" : FirstSentence(o.Message, 120);
            }
            catch { return null; }
        }

        static List<ApiItem> Reflect(Assembly asm, string src)
        {
            var xml = LoadXml(asm);
            var res = new List<ApiItem>();
            const BindingFlags BF = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var t in Exported(asm))
            {
                if (t.Name.Contains('<') || Hidden(t)) continue;
                string ns = t.Namespace ?? "";
                string tname = TypeDisplay(t, false);
                xml.TryGetValue("T:" + TypeId(t), out var td);
                string kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : typeof(Delegate).IsAssignableFrom(t) ? "delegate" : t.IsAbstract && t.IsSealed ? "static class" : "class";
                var bases = new List<string>();
                if (t.BaseType != null && t.BaseType != typeof(object) && t.BaseType != typeof(ValueType) && t.BaseType != typeof(Enum) && !typeof(Delegate).IsAssignableFrom(t))
                    bases.Add(TypeDisplay(t.BaseType));
                string tsig = $"{kind} {tname}" + (bases.Count > 0 ? " : " + string.Join(", ", bases) : "");
                var tdoc = new StringBuilder();
                if (td?.Summary != null) tdoc.Append(td.Summary);
                if (t.IsEnum)
                {
                    var vals = new List<string>();
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (Hidden(f) || ObsoleteOf(f) != null) continue;
                        object v = null; try { v = Convert.ToInt64(f.GetRawConstantValue()); } catch { }
                        xml.TryGetValue("F:" + TypeId(t) + "." + f.Name, out var fd);
                        vals.Add($"{f.Name}={v}" + (fd?.Summary != null ? " (" + FirstSentence(fd.Summary, 60) + ")" : ""));
                    }
                    tsig += " { " + string.Join(", ", vals.Select(v => v.Split(' ')[0])) + " }";
                    if (vals.Count > 0) tdoc.Append("\nvalues: ").Append(string.Join("; ", vals));
                }
                if (typeof(Delegate).IsAssignableFrom(t))
                {
                    var inv = t.GetMethod("Invoke");
                    if (inv != null) tsig = $"delegate {TypeDisplay(inv.ReturnType)} {tname}({Params(inv.GetParameters())})";
                }
                if (td?.Remarks != null) tdoc.Append("\nremarks: ").Append(td.Remarks);
                res.Add(new ApiItem { Src = src, Kind = "T", Ns = ns, Type = tname, Name = tname.Split('.').Last(), Sig = tsig, Obsolete = ObsoleteOf(t), Sum = FirstSentence(td?.Summary), Doc = tdoc.ToString() });
                if (t.IsEnum || typeof(Delegate).IsAssignableFrom(t)) continue;

                foreach (var m in t.GetMembers(BF))
                {
                    if (Hidden(m)) continue;
                    ApiItem it = null;
                    XDoc d = null;
                    switch (m)
                    {
                        case ConstructorInfo c:
                            if (c.IsStatic) continue;
                            xml.TryGetValue(MethodId(c), out d);
                            it = new ApiItem { Kind = "C", Name = tname.Split('.').Last(), Sig = $"new {tname.Split('.').Last()}({Params(c.GetParameters())})", Ret = tname };
                            break;
                        case MethodInfo mi:
                            if (mi.IsSpecialName && !mi.Name.StartsWith("op_")) continue;          // property/event accessors
                            if (mi.DeclaringType != t) continue;
                            xml.TryGetValue(MethodId(mi), out d);
                            var nm = mi.Name.StartsWith("op_") ? OperatorName(mi) : mi.Name + (mi.IsGenericMethodDefinition ? "<" + string.Join(",", mi.GetGenericArguments().Select(g => g.Name)) + ">" : "");
                            it = new ApiItem { Kind = mi.Name.StartsWith("op_") ? "O" : "M", Name = nm, Sig = $"{nm}({Params(mi.GetParameters())})", Ret = TypeDisplay(mi.ReturnType), Static = mi.IsStatic };
                            break;
                        case PropertyInfo pi:
                            var idx = pi.GetIndexParameters();
                            string pid = "P:" + TypeId(t) + "." + pi.Name + (idx.Length > 0 ? "(" + string.Join(",", idx.Select(p => ParamId(p.ParameterType))) + ")" : "");
                            xml.TryGetValue(pid, out d);
                            var getter = pi.GetGetMethod(); var setter = pi.GetSetMethod();
                            if (getter == null && setter == null) continue;
                            it = new ApiItem { Kind = "P", Name = idx.Length > 0 ? "this[]" : pi.Name, Sig = (idx.Length > 0 ? $"this[{Params(idx)}]" : pi.Name) + (setter == null ? " {get}" : getter == null ? " {set}" : " {get;set}"), Ret = TypeDisplay(pi.PropertyType), Static = (getter ?? setter).IsStatic };
                            break;
                        case FieldInfo fi:
                            xml.TryGetValue("F:" + TypeId(t) + "." + fi.Name, out d);
                            it = new ApiItem { Kind = "F", Name = fi.Name, Sig = fi.Name + (fi.IsLiteral ? " (const)" : fi.IsInitOnly ? " {get}" : ""), Ret = TypeDisplay(fi.FieldType), Static = fi.IsStatic };
                            break;
                        case EventInfo ei:
                            xml.TryGetValue("E:" + TypeId(t) + "." + ei.Name, out d);
                            it = new ApiItem { Kind = "E", Name = ei.Name, Sig = "event " + ei.Name, Ret = TypeDisplay(ei.EventHandlerType), Static = ei.GetAddMethod()?.IsStatic ?? false };
                            break;
                    }
                    if (it == null) continue;
                    it.Src = src; it.Ns = ns; it.Type = tname; it.Obsolete = ObsoleteOf(m);
                    it.Sum = FirstSentence(d?.Summary ?? d?.Value);
                    it.Doc = MemberDoc(d, m);
                    res.Add(it);
                }
            }
            return res;
        }

        static string MemberDoc(XDoc d, MemberInfo m)
        {
            var sb = new StringBuilder();
            if (d?.Summary != null) sb.Append(d.Summary);
            if (d?.Value != null && d.Value != d.Summary) sb.Append("\nvalue: ").Append(d.Value);
            var ps = m is MethodBase mb ? mb.GetParameters() : Array.Empty<ParameterInfo>();
            foreach (var p in ps)
            {
                var pd = d?.Params.FirstOrDefault(x => x.name == p.Name).text;
                if (pd != null) sb.Append("\n  ").Append(p.Name).Append(": ").Append(pd);
            }
            if (d?.Returns != null) sb.Append("\nreturns: ").Append(d.Returns);
            if (ps.Any(p => p.ParameterType.IsByRef && p.IsOut)) sb.Append("\npython: out parameters come back as extra return values, e.g. ok, x = obj.").Append(m.Name).Append("(…)");
            if (d?.Remarks != null) sb.Append("\nremarks: ").Append(d.Remarks);
            if (!string.IsNullOrWhiteSpace(d?.Example)) sb.Append("\nexample:\n").Append(d.Example);
            return sb.ToString();
        }

        static string OperatorName(MethodInfo m)
        {
            var map = new Dictionary<string, string> {
                ["op_Addition"] = "+", ["op_Subtraction"] = "-", ["op_Multiply"] = "*", ["op_Division"] = "/", ["op_Equality"] = "==", ["op_Inequality"] = "!=",
                ["op_UnaryNegation"] = "-x", ["op_LessThan"] = "<", ["op_GreaterThan"] = ">", ["op_LessThanOrEqual"] = "<=", ["op_GreaterThanOrEqual"] = ">=",
                ["op_Implicit"] = "implicit", ["op_Explicit"] = "explicit", ["op_Modulus"] = "%", ["op_BitwiseOr"] = "|", ["op_BitwiseAnd"] = "&" };
            return "operator " + (map.TryGetValue(m.Name, out var s) ? s : m.Name.Substring(3));
        }

        static string Params(ParameterInfo[] ps) => string.Join(", ", ps.Select(p =>
        {
            var t = p.ParameterType;
            string pre = "";
            if (t.IsByRef) { pre = p.IsOut ? "out " : "ref "; t = t.GetElementType(); }
            if (p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0) pre = "params ";
            string def = "";
            if (p.IsOptional && p.HasDefaultValue)
            {
                var v = p.DefaultValue;
                def = " = " + (v == null ? (t.IsValueType ? "default" : "null") : v is bool b ? (b ? "true" : "false") : v is string s ? "\"" + s + "\"" : t.IsEnum ? t.Name + "." + Enum.GetName(t, v) : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
            }
            return $"{pre}{TypeDisplay(t)} {p.Name}{def}";
        }));

        static readonly Dictionary<Type, string> Alias = new Dictionary<Type, string> {
            [typeof(int)] = "int", [typeof(double)] = "double", [typeof(bool)] = "bool", [typeof(string)] = "string", [typeof(object)] = "object",
            [typeof(void)] = "void", [typeof(float)] = "float", [typeof(long)] = "long", [typeof(uint)] = "uint", [typeof(byte)] = "byte",
            [typeof(short)] = "short", [typeof(ulong)] = "ulong", [typeof(char)] = "char", [typeof(decimal)] = "decimal", [typeof(ushort)] = "ushort" };

        /// Short readable type: IEnumerable<Curve>, Point3d[], int?, BrepFace.Something
        static string TypeDisplay(Type t, bool alias = true)
        {
            if (t == null) return "?";
            if (t.IsByRef) return TypeDisplay(t.GetElementType());
            if (t.IsArray) return TypeDisplay(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (t.IsPointer) return TypeDisplay(t.GetElementType()) + "*";
            if (alias && Alias.TryGetValue(t, out var a)) return a;
            if (t.IsGenericParameter) return t.Name;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>)) return TypeDisplay(t.GetGenericArguments()[0]) + "?";
            string name = t.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            if (t.IsNested && !t.IsGenericParameter) name = TypeDisplay(t.DeclaringType, false).Split('<')[0] + "." + name;
            if (t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                if (t.IsNested) args = args.Skip(t.DeclaringType.GetGenericArguments().Length).ToArray();
                if (args.Length > 0) name += "<" + string.Join(", ", args.Select(x => TypeDisplay(x))) + ">";
            }
            return name;
        }

        // XML documentation IDs (ECMA-334 annex D)
        static string TypeId(Type t) => (t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t).FullName?.Replace('+', '.') ?? t.Name;

        static string MethodId(MethodBase m)
        {
            var name = m is ConstructorInfo ? "#ctor" : m.Name;
            if (m is MethodInfo mi && mi.IsGenericMethodDefinition) name += "``" + mi.GetGenericArguments().Length;
            var ps = m.GetParameters();
            var id = "M:" + TypeId(m.DeclaringType) + "." + name + (ps.Length > 0 ? "(" + string.Join(",", ps.Select(p => ParamId(p.ParameterType))) + ")" : "");
            if (m is MethodInfo op && (m.Name == "op_Implicit" || m.Name == "op_Explicit")) id += "~" + ParamId(op.ReturnType);
            return id;
        }

        static string ParamId(Type t)
        {
            if (t.IsByRef) return ParamId(t.GetElementType()) + "@";
            if (t.IsPointer) return ParamId(t.GetElementType()) + "*";
            if (t.IsArray) return ParamId(t.GetElementType()) + (t.GetArrayRank() == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", t.GetArrayRank())) + "]");
            if (t.IsGenericParameter) return (t.DeclaringMethod != null ? "``" : "`") + t.GenericParameterPosition;
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition().FullName ?? t.Name;
                def = def.Substring(0, def.IndexOf('`') < 0 ? def.Length : def.IndexOf('`')).Replace('+', '.');
                return def + "{" + string.Join(",", t.GetGenericArguments().Select(ParamId)) + "}";
            }
            return (t.FullName ?? t.Name).Replace('+', '.');
        }

        // ------------------------------------------------------------------ rhinoscriptsyntax

        static IEnumerable<ApiItem> RhinoScript(out string where)
        {
            where = null;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root = Path.Combine(home, ".rhinocode");
            if (!Directory.Exists(root)) return Enumerable.Empty<ApiItem>();
            where = Directory.GetDirectories(root, "py3*-rh8").Concat(Directory.GetDirectories(root, "py*"))
                .Select(d => Path.Combine(d, "site-rhinopython", "rhinoscript")).FirstOrDefault(Directory.Exists);
            if (where == null) return Enumerable.Empty<ApiItem>();
            var res = new List<ApiItem>();
            var defRx = new Regex(@"^def ([A-Za-z]\w*)\((.*?)\):[ \t]*\r?\n[ \t]+(?:r?""""""|''')(.*?)(?:""""""|''')", RegexOptions.Multiline | RegexOptions.Singleline);
            foreach (var f in Directory.GetFiles(where, "*.py").OrderBy(x => x))
            {
                var mod = Path.GetFileNameWithoutExtension(f);
                if (mod.StartsWith("_") || mod == "compat") continue;
                var text = File.ReadAllText(f).Replace("\r\n", "\n");
                foreach (Match m in defRx.Matches(text))
                {
                    var name = m.Groups[1].Value;
                    var args = Regex.Replace(m.Groups[2].Value, @"\s+", " ").Trim();
                    var doc = Dedent(m.Groups[3].Value);
                    var sections = Regex.Split(doc, @"^(Parameters|Returns|Example|See Also):\s*$", RegexOptions.Multiline);
                    string desc = sections[0].Trim(), pars = null, rets = null, ex = null, see = null;
                    for (int i = 1; i + 1 < sections.Length; i += 2)
                    {
                        var body = sections[i + 1].Trim('\n');
                        switch (sections[i]) { case "Parameters": pars = body; break; case "Returns": rets = body; break; case "Example": ex = body; break; case "See Also": see = body; break; }
                    }
                    string ret = rets == null ? null : Regex.Match(rets.Trim(), @"^([^:\n]+?)\s*:").Groups[1].Value.Trim();
                    var full = new StringBuilder(desc);
                    // returns before the (often long) parameter list, so a truncated reply still has it
                    if (rets != null) full.Append("\nreturns:\n").Append(Indent(rets));
                    if (pars != null) full.Append("\nparameters:\n").Append(Indent(pars));
                    if (ex != null) full.Append("\nexample:\n").Append(Dedent(ex));
                    if (see != null) full.Append("\nsee also: ").Append(Regex.Replace(see.Trim(), @"\s+", " "));
                    res.Add(new ApiItem
                    {
                        Src = "rs", Kind = "S", Ns = "rs." + mod, Type = "rs", Name = name,
                        Sig = $"{name}({args})", Ret = string.IsNullOrEmpty(ret) ? null : ret,
                        Static = true, Sum = FirstSentence(desc), Doc = full.ToString()
                    });
                }
            }
            return res;
        }

        static string Dedent(string s)
        {
            var lines = s.Replace("\r", "").Split('\n').ToList();
            int ind = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            return string.Join("\n", lines.Select((l, i) => i == 0 ? l.Trim() : (l.Length >= ind ? l.Substring(ind) : l.TrimStart()))).Trim();
        }

        static string Indent(string s) => string.Join("\n", Dedent(s).Split('\n').Select(l => "  " + l));

        // ------------------------------------------------------------------ tidy Markdown export

        static void WriteMarkdown(List<ApiItem> items, string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var bySrc in items.Where(i => i.Obsolete == null).GroupBy(i => i.Src))
            {
                var sdir = Path.Combine(dir, bySrc.Key);
                Directory.CreateDirectory(sdir);
                foreach (var byNs in bySrc.GroupBy(i => i.Ns))
                {
                    var sb = new StringBuilder();
                    sb.Append("# ").Append(byNs.Key).Append('\n');
                    foreach (var byType in byNs.GroupBy(i => i.Type).OrderBy(g => g.Key))
                    {
                        var t = byType.FirstOrDefault(i => i.Kind == "T");
                        if (bySrc.Key != "rs")
                        {
                            sb.Append("\n## ").Append(t?.Sig ?? byType.Key).Append('\n');
                            if (!string.IsNullOrEmpty(t?.Doc)) sb.Append(t.Doc.Split('\n')[0]).Append('\n');
                        }
                        foreach (var it in byType.Where(i => i.Kind != "T").OrderBy(i => KindOrder(i.Kind)).ThenBy(i => i.Name))
                            sb.Append("- ").Append(Line(it, false)).Append('\n');
                    }
                    File.WriteAllText(Path.Combine(sdir, byNs.Key + ".md"), sb.ToString());
                }
            }
        }

        static int KindOrder(string k) => k switch { "C" => 0, "P" => 1, "M" => 2, "S" => 2, "O" => 3, "E" => 4, "F" => 5, _ => 6 };

        // ------------------------------------------------------------------ search

        static readonly string[] SrcNames = { "rc", "gh", "rs" };

        /// One compact line: `Brep.CreateFromLoft(IEnumerable<Curve> curves, …) → Brep[] · static — summary`
        static string Line(ApiItem it, bool withType = true, int overloads = 0)
        {
            var head = it.Kind == "T" ? $"{it.Sig} ({it.Ns})"
                     : it.Src == "rs" ? "rs." + it.Sig
                     : (withType ? it.Type + "." : "") + it.Sig;
            var sb = new StringBuilder(head);
            if (it.Kind != "T" && !string.IsNullOrEmpty(it.Ret) && it.Ret != "void" && it.Kind != "C") sb.Append(" → ").Append(it.Ret);
            if (it.Static && it.Src != "rs" && it.Kind != "O" && it.Kind != "C") sb.Append(" · static");
            if (overloads > 0) sb.Append($" · +{overloads} overload{(overloads > 1 ? "s" : "")}");
            if (it.Obsolete != null) sb.Append(" · OBSOLETE: ").Append(it.Obsolete);
            if (!string.IsNullOrEmpty(it.Sum)) sb.Append(" — ").Append(it.Sum);
            return sb.ToString();
        }

        static IEnumerable<string> Words(string s) => Regex.Matches(s ?? "", @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+").Select(m => m.Value.ToLowerInvariant());

        /// Types people use every day rank above niche ones with similar names.
        static readonly HashSet<string> CoreTypes = new HashSet<string> {
            "Brep", "Curve", "Mesh", "Surface", "NurbsCurve", "NurbsSurface", "Point3d", "Vector3d", "Plane", "Line", "Polyline", "PolylineCurve",
            "Circle", "Arc", "Box", "BoundingBox", "Transform", "Interval", "Extrusion", "Intersect.Intersection", "AreaMassProperties", "VolumeMassProperties",
            "RhinoDoc", "ObjectTable", "LayerTable", "Layer", "ObjectAttributes", "RhinoObject", "RhinoView", "RhinoViewport",
            "GH_Document", "GH_Component", "IGH_Component", "IGH_Param", "IGH_DocumentObject", "GH_Structure", "GH_Path", "DataTree", "GH_ParamAccess" };

        static double NsWeight(ApiItem it)
        {
            var ns = it.Ns ?? "";
            if (it.Src == "rs") return 1.5;
            if (CoreTypes.Contains(it.Type ?? "")) return 3;
            if (ns == "Rhino.Geometry" || ns == "Rhino.DocObjects" || ns == "Rhino" || ns == "Grasshopper.Kernel") return 2;
            if (ns.StartsWith("Rhino.Geometry.") || ns.StartsWith("Rhino.DocObjects.") || ns.StartsWith("Grasshopper.Kernel.") || ns == "Rhino.Display" || ns == "Rhino.Input" || ns == "Rhino.Input.Custom" || ns == "GH_IO.Serialization") return 1;
            if (ns.Contains("Interop") || ns.Contains("Runtime.InteropWrappers") || ns.StartsWith("Rhino.UI.Controls") || ns.StartsWith("Grasshopper.GUI")) return -2;
            return 0;
        }

        static double Score(ApiItem it, List<string> toks, string member, string type)
        {
            double s = 0;
            var name = it.Name.ToLowerInvariant();
            var tname = (it.Type ?? "").ToLowerInvariant();
            if (member != null)
            {
                // "Type.Member" query: both parts must match
                bool tOk = tname == type || tname.EndsWith("." + type) || (it.Src == "rs" && (type == "rs" || type == "rhinoscriptsyntax"));
                if (!tOk) return 0;
                if (name == member) s += 100; else if (name.StartsWith(member)) s += 40; else if (name.Contains(member)) s += 20; else return 0;
                return s + NsWeight(it) - (it.Obsolete != null ? 30 : 0);
            }
            var words = new HashSet<string>(Words(it.Name));
            var twords = new HashSet<string>(Words(it.Type));
            var sum = (it.Sum ?? "").ToLowerInvariant();
            foreach (var t in toks)
            {
                double ts = 0;
                if (name == t) ts = 12;
                else if (words.Contains(t)) ts = 7;
                else if (name.StartsWith(t)) ts = 6;
                else if (name.Contains(t)) ts = 4;
                if (tname == t) ts = Math.Max(ts, it.Kind == "T" ? 14 : 5);
                else if (twords.Contains(t)) ts = Math.Max(ts, 3);
                if (ts == 0 && Regex.IsMatch(sum, @"\b" + Regex.Escape(t))) ts = 1.5;
                if (ts == 0) return 0;                       // every word must match somewhere
                s += ts;
            }
            s += NsWeight(it);
            if (it.Kind == "M" || it.Kind == "S" || it.Kind == "T") s += 1;
            if (it.Kind == "F" || it.Kind == "E") s -= 1;
            if (string.IsNullOrEmpty(it.Sum)) s -= 1;
            if (it.Obsolete != null) s -= 20;
            return s;
        }

        public static void Query(Args a, Reply r)
        {
            var q = (a.Str("filter", null) ?? a.Str("name", null) ?? "").Trim();
            string level = (a.Str("level", null) ?? "list").ToLowerInvariant();
            int limit = Math.Clamp(a.Int("limit", 10), 1, 40), offset = Math.Max(0, a.Int("offset", 0));
            var items = Items(r);
            if (q.Length == 0)
            {
                r.Line($"API reference: {string.Join(", ", SrcNames.Select(s => $"{s} {items.Count(i => i.Src == s)}"))} items (rc RhinoCommon, gh Grasshopper SDK, rs rhinoscriptsyntax).");
                r.Line("search: filter:'loft curves' · 'Brep.CreateFromLoft' · 'Brep' (type overview) · 'rs.AddLoftSrf' · 'src:gh GH_Document' · level:'detail' for full docs of one member.");
                r.Line("tidy Markdown per namespace: " + Path.Combine(Dir, "md"));
                return;
            }

            // source filter: src:rc|gh|rs, or an rs./rhinoscriptsyntax. prefix
            string src = null;
            var m0 = Regex.Match(q, @"\bsrc:(rc|gh|rs)\b", RegexOptions.IgnoreCase);
            if (m0.Success) { src = m0.Groups[1].Value.ToLowerInvariant(); q = q.Remove(m0.Index, m0.Length).Trim(); }
            if (Regex.IsMatch(q, @"^(rs|rhinoscriptsyntax)\.", RegexOptions.IgnoreCase)) src = "rs";
            var pool = src == null ? items : items.Where(i => i.Src == src).ToList();

            // exact "Type.Member" or "Namespace.Type.Member"
            string member = null, type = null;
            var dm = Regex.Match(q, @"^(?:[\w]+\.)*?([\w]+)\.([\w]+)$");
            if (dm.Success && !q.Contains(' ')) { type = dm.Groups[1].Value.ToLowerInvariant(); member = dm.Groups[2].Value.ToLowerInvariant(); }

            // exact type name → type overview
            if (member == null && !q.Contains(' '))
            {
                var types = pool.Where(i => i.Kind == "T" && (i.Name.Equals(q, StringComparison.OrdinalIgnoreCase) || (i.Ns + "." + i.Type).Equals(q, StringComparison.OrdinalIgnoreCase))).ToList();
                if (types.Count > 0) { TypeOverview(pool, types.OrderByDescending(NsWeight).First(), types.Count, r, level == "detail"); return; }
            }
            // a type "Type.Member" pair where Member is really a nested type is handled by the general search below

            var toks = member != null ? new List<string>() : Words(q).Concat(Regex.Matches(q.ToLowerInvariant(), @"[a-z0-9_]+").Select(x => x.Value)).Distinct().ToList();
            toks = toks.Where(t => t.Length > 1 || char.IsDigit(t[0])).Distinct().ToList();
            // prefer the user's whole words over camel-case pieces: drop pieces contained in another token
            toks = toks.Where(t => !toks.Any(o => o != t && o.Contains(t) && q.ToLowerInvariant().Split(' ', '.').Contains(o))).ToList();
            var scored = pool.Select(i => (i, s: Score(i, toks, member, type))).Where(x => x.s > 0).ToList();
            if (scored.Count == 0)
            {
                r.Line($"no API match for '{q}'" + (src != null ? $" in {src}" : "") + ".");
                var near = Near(pool, q);
                if (near.Count > 0) r.Line("did you mean: " + string.Join(", ", near));
                r.Line("try fewer or different words (e.g. 'loft', 'Curve.Offset', 'rs.AddCurve').");
                return;
            }
            // group overloads: one line per Type.Name, best overload shown
            var groups = scored.GroupBy(x => x.i.Src + "|" + x.i.Type + "|" + x.i.Name + "|" + x.i.Kind)
                .Select(g => (best: g.OrderByDescending(x => x.s).ThenBy(x => x.i.Obsolete != null).ThenBy(x => x.i.Sig.Length).First(), n: g.Count(), all: g.Select(x => x.i).ToList()))
                .OrderByDescending(g => g.best.s).ToList();

            if (level == "detail" || (groups.Count == 1) || (member != null && groups[0].best.s >= 100 && (groups.Count == 1 || groups[1].best.s < 100)))
            {
                var g = groups[0];
                Detail(g.all.Where(x => x.Obsolete == null).DefaultIfEmpty(g.all[0]).ToList(), r);
                if (groups.Count > 1) r.Line($"(also: {string.Join(", ", groups.Skip(1).Take(5).Select(x => (x.best.i.Src == "rs" ? "rs." : x.best.i.Type + ".") + x.best.i.Name))})");
                return;
            }
            var page = groups.Skip(offset).Take(limit).ToList();
            r.Line($"{groups.Count} match(es) for '{q}'" + (groups.Count > offset + limit ? $" (showing {offset + 1}-{offset + page.Count}; offset:{offset + limit} for more)" : "") + ". Full docs: filter:'Type.Member', level:'detail'.");
            foreach (var g in page) r.Line(Line(g.best.i, true, g.n - 1));
        }

        static List<string> Near(List<ApiItem> pool, string q)
        {
            var k = q.Split('.').Last().ToLowerInvariant();
            if (k.Length < 3) return new List<string>();
            return pool.Where(i => i.Obsolete == null && (i.Kind == "T" || i.Kind == "M" || i.Kind == "S" || i.Kind == "P"))
                .Select(i => (i, d: Lev(i.Name.ToLowerInvariant(), k)))
                .Where(x => x.d <= Math.Max(2, k.Length / 4))
                .OrderBy(x => x.d).ThenByDescending(x => NsWeight(x.i))
                .Select(x => (x.i.Src == "rs" ? "rs." : x.i.Kind == "T" ? "" : x.i.Type + ".") + x.i.Name).Distinct().Take(5).ToList();
        }

        static int Lev(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) > 4) return 99;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        static void TypeOverview(List<ApiItem> pool, ApiItem t, int sameName, Reply r, bool full)
        {
            r.Line($"{t.Sig}  [{t.Ns}, {SrcLabel(t.Src)}]" + (t.Obsolete != null ? " OBSOLETE: " + t.Obsolete : ""));
            if (!string.IsNullOrEmpty(t.Doc)) r.Line(Fmt.Trunc(t.Doc, full ? 1500 : 400));
            var mem = pool.Where(i => i.Src == t.Src && i.Ns == t.Ns && i.Type == t.Type && i.Kind != "T" && i.Obsolete == null).ToList();
            string Names(IEnumerable<ApiItem> xs) => string.Join(" ", xs.GroupBy(x => x.Name).OrderBy(g => g.Key).Select(g => g.Key + (g.Count() > 1 ? $"({g.Count()})" : "")));
            var ctors = mem.Where(i => i.Kind == "C").ToList();
            if (ctors.Count > 0) r.Line("ctor: " + string.Join(" | ", ctors.Take(full ? 12 : 4).Select(c => c.Sig)) + (ctors.Count > (full ? 12 : 4) ? $" | +{ctors.Count - (full ? 12 : 4)}" : ""));
            void Part(string label, IEnumerable<ApiItem> xs) { var s = Names(xs); if (s.Length > 0) r.Line(label + ": " + (full ? s : Fmt.Trunc(s, 700))); }
            Part("static", mem.Where(i => i.Static && (i.Kind == "M")));
            Part("static props", mem.Where(i => i.Static && (i.Kind == "P" || i.Kind == "F")));
            Part("props", mem.Where(i => !i.Static && i.Kind == "P"));
            Part("methods", mem.Where(i => !i.Static && i.Kind == "M"));
            if (full) { Part("fields", mem.Where(i => !i.Static && i.Kind == "F")); Part("events", mem.Where(i => i.Kind == "E")); Part("operators", mem.Where(i => i.Kind == "O")); }
            r.Line($"(n) = overloads. Signature: filter:'{t.Type}.Name'" + (sameName > 1 ? $" · {sameName - 1} other type(s) share this name: use the namespace, e.g. '{t.Ns}.{t.Type}'" : "") + (full ? "" : " · all members incl. fields/events: level:'detail'"));
        }

        static string SrcLabel(string s) => s == "rc" ? "RhinoCommon" : s == "gh" ? "Grasshopper SDK" : "rhinoscriptsyntax";

        static void Detail(List<ApiItem> overloads, Reply r)
        {
            var first = overloads[0];
            r.Line($"{(first.Src == "rs" ? "rhinoscriptsyntax" : first.Ns + "." + first.Type)}.{first.Name}  [{SrcLabel(first.Src)}]");
            int i = 0;
            var shown = new HashSet<string>();     // doc lines already printed for an earlier overload
            foreach (var it in overloads.Take(6))
            {
                i++;
                r.Line((overloads.Count > 1 ? $"{i}) " : "") + Line(it, true).Split(" — ")[0]);
                // print each doc line once across overloads (same summary / same parameter text)
                var lines = (it.Doc ?? "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
                var fresh = new List<string>();
                bool inExample = false;
                foreach (var l in lines)
                {
                    if (l.StartsWith("example:")) inExample = true;
                    if (inExample || shown.Add(l)) fresh.Add(l);
                }
                if (i > 1) fresh = fresh.Where(l => !l.StartsWith("python: ")).ToList();
                var doc = string.Join("\n", fresh);
                if (doc.Length > 0) r.Line(Indent2(Fmt.Trunc(doc, i == 1 ? 1600 : 500)));
            }
            if (overloads.Count > 6) r.Line($"(+{overloads.Count - 6} more overloads)");
        }

        static string Indent2(string s) => string.Join("\n", s.Split('\n').Select(l => "   " + l));
    }
}
