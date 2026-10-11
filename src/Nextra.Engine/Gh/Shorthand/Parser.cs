using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Nextra.Engine.Gh.Shorthand
{
    // ---------------- AST ----------------

    public abstract class Op { public int Line; public string Raw; public string Group; }

    /// `id = Type @Cat (inputs) {config} -> (outputs)` (Create) or `g12 (inputs) {config}` (modify).
    public class NodeOp : Op
    {
        public string Id;
        public bool Create;
        public string Type, Category;
        public List<PortOp> Inputs = new List<PortOp>();
        public List<PortOp> OutputMods = new List<PortOp>();
        public List<KeyValuePair<string, Val>> Config = new List<KeyValuePair<string, Val>>();
        public List<OutDecl> Outputs;           // script outputs (exact set when given)
        public List<string> RemoveInputs = new List<string>();
        public bool BareMember;                 // a lone handle inside a group section
    }

    public class OutDecl { public string Name; public string Flags; }

    public class PortOp
    {
        public string Port;                     // name, nickname, or index
        public string Access, TypeHint;         // script input declaration
        public string Mode = "=";               // = | += | -=
        public Val Value;                       // null when only flags are set
        public string Flags = "";               // f g s r p i h d x
        public string Expr;
    }

    public class WireOp : Op { public string From, FromPort, To, ToPort; public bool Remove; public string Flags = ""; }
    public class DeleteOp : Op { public List<string> Ids = new List<string>(); }
    public class GroupOp : Op { public string Handle, Name, Color; public bool End; public List<string> Remove = new List<string>(); }

    public enum VK { Num, Str, Bool, Point, List, Interval, Ref, Color, Ident, Clear }

    public class Val
    {
        public VK Kind;
        public double Num;
        public string Str;
        public double[] Xyz;
        public List<Val> Items;
        public List<(string id, string port)> Refs;   // a.0 + b.R
        public override string ToString() => Kind switch
        {
            VK.Num => Fmt.N(Num),
            VK.Str => "\"" + Str + "\"",
            VK.Bool => Str,
            VK.Point => "{" + string.Join(",", Xyz.Select(Fmt.N)) + "}",
            VK.List => "[" + string.Join(",", Items) + "]",
            VK.Interval => Fmt.N(Xyz[0]) + ".." + Fmt.N(Xyz[1]),
            VK.Ref => string.Join("+", Refs.Select(r => r.port == null ? r.id : r.id + "." + r.port)),
            VK.Color => Str,
            VK.Clear => "_",
            _ => Str,
        };
    }

    public class ParseError
    {
        public int Line; public string Message;
        public ParseError(int l, string m) { Line = l; Message = m; }
    }

    // ---------------- Parser ----------------

    public static class Parser
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly Regex IdRx = new Regex(@"^[A-Za-z_][\w]*$");

        public static List<Op> Parse(string text, List<ParseError> errors)
        {
            var ops = new List<Op>();
            string group = null;
            foreach (var (no, line) in LogicalLines(text))
            {
                var l = line.Trim();
                if (l.Length == 0 || l.StartsWith("```")) continue;
                try
                {
                    if (l.StartsWith("##"))
                    {
                        var g = ParseGroup(l.Substring(2).Trim());
                        g.Line = no; g.Raw = l;
                        group = g.End ? null : (g.Handle ?? g.Name);
                        ops.Add(g);
                        continue;
                    }
                    if (l.StartsWith("#")) continue;                       // comment
                    l = StripComment(l);
                    if (l.Length == 0) continue;
                    Op op;
                    if (l.StartsWith("-") && !l.StartsWith("->"))
                    {
                        var d = new DeleteOp();
                        d.Ids.AddRange(l.Substring(1).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries));
                        op = d;
                    }
                    else if (IsWire(l)) op = ParseWire(l);
                    else op = ParseNode(l);
                    op.Line = no; op.Raw = l; op.Group = group;
                    ops.Add(op);
                }
                catch (FormatException e) { errors.Add(new ParseError(no, e.Message)); }
                catch (Exception e) { errors.Add(new ParseError(no, "cannot parse: " + e.Message)); }
            }
            return ops;
        }

        /// Joins physical lines while brackets/quotes are open (multi-line code strings, long input lists).
        static IEnumerable<(int no, string line)> LogicalLines(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            int start = 0, depth = 0;
            bool inStr = false, inTriple = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (sb.Length == 0) start = i + 1;
                else sb.Append('\n');
                var l = lines[i];
                sb.Append(l);
                for (int k = 0; k < l.Length; k++)
                {
                    char ch = l[k];
                    if (inTriple) { if (l.Substring(k).StartsWith("\"\"\"")) { inTriple = false; k += 2; } continue; }
                    if (inStr) { if (ch == '\\') k++; else if (ch == '"') inStr = false; continue; }
                    if (l.Substring(k).StartsWith("\"\"\"")) { inTriple = true; k += 2; continue; }
                    if (ch == '"') { inStr = true; continue; }
                    if (ch == '#' && depth == 0 && (k == 0 || l[k - 1] == ' ') && !(k + 1 < l.Length && Uri.IsHexDigit(l[k + 1]) && Regex.IsMatch(l.Substring(k), @"^#[0-9A-Fa-f]{6}\b"))) break;
                    if (ch == '(' || ch == '{' || ch == '[') depth++;
                    else if (ch == ')' || ch == '}' || ch == ']') depth = Math.Max(0, depth - 1);
                }
                inStr = false; // strings never span lines (use """ for that)
                if (depth == 0 && !inTriple) { yield return (start, sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) yield return (start, sb.ToString());
        }

        /// Removes a trailing ` # comment` that is outside quotes/brackets (but keeps #RRGGBB colours).
        static string StripComment(string l)
        {
            int depth = 0; bool inStr = false, inTriple = false;
            for (int k = 0; k < l.Length; k++)
            {
                char ch = l[k];
                if (inTriple) { if (l.Substring(k).StartsWith("\"\"\"")) { inTriple = false; k += 2; } continue; }
                if (inStr) { if (ch == '\\') k++; else if (ch == '"') inStr = false; continue; }
                if (l.Substring(k).StartsWith("\"\"\"")) { inTriple = true; k += 2; continue; }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '(' || ch == '{' || ch == '[') depth++;
                else if (ch == ')' || ch == '}' || ch == ']') depth--;
                else if (ch == '#' && depth == 0 && k > 0 && l[k - 1] == ' ' && !Regex.IsMatch(l.Substring(k), @"^#[0-9A-Fa-f]{6}\b")) return l.Substring(0, k).TrimEnd();
            }
            return l;
        }

        static GroupOp ParseGroup(string s)
        {
            var g = new GroupOp();
            if (s.Length == 0 || s == "end") { g.End = true; return g; }
            var m = Regex.Match(s, @"#([0-9A-Fa-f]{6})\b");
            if (m.Success) { g.Color = "#" + m.Groups[1].Value; s = s.Remove(m.Index, m.Length).Trim(); }
            // trailing "(g40)" written by the reverse engine
            var hm = Regex.Match(s, @"\((g\d+)\)\s*$");
            if (hm.Success) { g.Handle = hm.Groups[1].Value; s = s.Substring(0, hm.Index).Trim(); }
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count > 0 && Handles.IsGhHandle(parts[0])) { g.Handle = parts[0]; parts.RemoveAt(0); }
            foreach (var p in parts.Where(p => p.StartsWith("-") && Handles.IsGhHandle(p.Substring(1))).ToList()) { g.Remove.Add(p.Substring(1)); parts.Remove(p); }
            var name = string.Join(" ", parts).Trim().Trim('"');
            g.Name = name.Length > 0 ? name : null;
            return g;
        }

        static bool IsWire(string l)
        {
            // a.P > b.Q  (no '=' before the '>' at top level)
            int depth = 0; bool inStr = false;
            for (int k = 0; k < l.Length; k++)
            {
                char ch = l[k];
                if (inStr) { if (ch == '"') inStr = false; continue; }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '(' || ch == '{' || ch == '[') depth++;
                else if (ch == ')' || ch == '}' || ch == ']') depth--;
                else if (depth == 0 && ch == '=') return false;
                else if (depth == 0 && ch == '>' && (k == 0 || l[k - 1] != '-')) return true;
            }
            return false;
        }

        static WireOp ParseWire(string l)
        {
            var w = new WireOp();
            var flags = "";
            var fm = Regex.Match(l, @"\^([a-z]+)\s*$");
            if (fm.Success) { flags = fm.Groups[1].Value; l = l.Substring(0, fm.Index).Trim(); }
            w.Flags = flags;
            string[] parts;
            if (l.Contains("!>")) { w.Remove = true; parts = l.Split("!>"); }
            else parts = l.Split('>');
            if (parts.Length != 2) throw new FormatException("wire must be `a.Port > b.Port` (or `!>` to remove)");
            (w.From, w.FromPort) = SplitRef(parts[0].Trim());
            (w.To, w.ToPort) = SplitRef(parts[1].Trim());
            return w;
        }

        static (string, string) SplitRef(string s)
        {
            int dot = s.IndexOf('.');
            return dot < 0 ? (s, null) : (s.Substring(0, dot), s.Substring(dot + 1));
        }

        static NodeOp ParseNode(string l)
        {
            var n = new NodeOp();
            int i = 0;
            // head: id [= Type [@Cat]]
            int eq = TopLevelIndex(l, '=');
            int firstBracket = FirstTopBracket(l);
            if (eq > 0 && (firstBracket < 0 || eq < firstBracket))
            {
                n.Id = l.Substring(0, eq).Trim();
                n.Create = true;
                i = eq + 1;
                while (i < l.Length && char.IsWhiteSpace(l[i])) i++;
                if (i < l.Length && l[i] == '"')
                {   // quoted type name, may contain parentheses: id = "Support (Karamba3D)" (…)
                    n.Type = ReadString(l, ref i);
                    int at2 = l.IndexOf('@', i);
                    int nb = FirstTopBracket(l, i);
                    if (at2 >= 0 && (nb < 0 || at2 < nb)) { int e2 = nb < 0 ? l.Length : nb; n.Category = l.Substring(at2 + 1, e2 - at2 - 1).Trim(); i = e2; }
                    if (!IdRx.IsMatch(n.Id)) throw new FormatException($"bad id '{n.Id}' (letters, digits, _)");
                    goto body;
                }
                int end = FirstTopBracket(l, i);
                int arrow = l.IndexOf("->", i, StringComparison.Ordinal);
                if (arrow >= 0 && (end < 0 || arrow < end)) end = arrow;
                var typePart = (end < 0 ? l.Substring(i) : l.Substring(i, end - i)).Trim();
                i = end < 0 ? l.Length : end;
                int at = typePart.LastIndexOf('@');
                if (at > 0) { n.Category = typePart.Substring(at + 1).Trim(); typePart = typePart.Substring(0, at).Trim(); }
                n.Type = typePart.Trim('"');
                if (n.Type.Length == 0) throw new FormatException("missing component type after '='");
                if (!IdRx.IsMatch(n.Id)) throw new FormatException($"bad id '{n.Id}' (letters, digits, _)");
                // `g12 = Type …` is allowed (it is what query prints, so definitions can be copied to another canvas);
                // the builder refuses it only when g12 already exists on this canvas
                if (Handles.IsRhHandle(n.Id)) throw new FormatException($"id '{n.Id}' looks like a Rhino handle; pick another local id (e.g. 'pt1')");
            }
            else
            {
                int end = firstBracket < 0 ? l.Length : firstBracket;
                int arrow = l.IndexOf("->", StringComparison.Ordinal);
                if (arrow >= 0 && arrow < end) end = arrow;
                n.Id = l.Substring(0, end).Trim();
                i = end;
                if (n.Id.Length == 0 || n.Id.Contains(' ')) throw new FormatException($"expected `id = Type …` or `handle (…)`, got '{Fmt.Trunc(l, 60)}'");
                if (i >= l.Length) n.BareMember = true;
            }

            body:
            while (i < l.Length)
            {
                char ch = l[i];
                if (char.IsWhiteSpace(ch)) { i++; continue; }
                if (ch == '(') { var body = Block(l, ref i, '(', ')'); ParseInputs(body, n); continue; }
                if (ch == '{') { var body = Block(l, ref i, '{', '}'); ParseConfig(body, n); continue; }
                if (l.Substring(i).StartsWith("->"))
                {
                    i += 2;
                    while (i < l.Length && char.IsWhiteSpace(l[i])) i++;
                    if (i >= l.Length || l[i] != '(') throw new FormatException("-> must be followed by (out1, out2)");
                    var body = Block(l, ref i, '(', ')');
                    n.Outputs = SplitTop(body, ',').Select(s => s.Trim()).Where(s => s.Length > 0).Select(s =>
                    {
                        var f = "";
                        int c = s.IndexOf('^'); if (c > 0) { f = s.Substring(c + 1); s = s.Substring(0, c); }
                        return new OutDecl { Name = s.Trim(), Flags = f };
                    }).ToList();
                    continue;
                }
                if (ch == '"')
                {   // trailing "nick" shortcut
                    var s = ReadString(l, ref i);
                    n.Config.Add(new KeyValuePair<string, Val>("nick", new Val { Kind = VK.Str, Str = s }));
                    continue;
                }
                throw new FormatException($"unexpected '{l.Substring(i, Math.Min(12, l.Length - i))}'");
            }
            return n;
        }

        static void ParseInputs(string body, NodeOp n)
        {
            foreach (var raw in SplitTop(body, ','))
            {
                var s = raw.Trim();
                if (s.Length == 0) continue;
                if (s.StartsWith("-") && !s.Contains('=')) { n.RemoveInputs.Add(s.Substring(1).Trim()); continue; }
                bool output = s.StartsWith(">");
                if (output) s = s.Substring(1).Trim();
                var p = new PortOp();
                int opIdx = TopLevelIndex(s, '=');
                if (opIdx < 0) throw new FormatException($"input '{s}' needs Port=value");
                var lhs = s.Substring(0, opIdx);
                var rhs = s.Substring(opIdx + 1).Trim();
                if (lhs.EndsWith("+")) { p.Mode = "+="; lhs = lhs.Substring(0, lhs.Length - 1); }
                else if (lhs.EndsWith("-")) { p.Mode = "-="; lhs = lhs.Substring(0, lhs.Length - 1); }
                var lp = lhs.Trim().Split(':');
                p.Port = lp[0].Trim();
                if (lp.Length > 1) p.Access = lp[1].Trim().ToLowerInvariant();
                if (lp.Length > 2) p.TypeHint = lp[2].Trim();
                // flags ^fg… and ^e(expr) at the end of the value
                var em = Regex.Match(rhs, @"\^e\((.*)\)\s*$");
                if (em.Success) { p.Expr = em.Groups[1].Value; rhs = rhs.Substring(0, em.Index).Trim(); }
                var fm = Regex.Match(rhs, @"\^([a-z]+)\s*$");
                if (fm.Success && !rhs.TrimEnd().EndsWith("\"")) { p.Flags = fm.Groups[1].Value; rhs = rhs.Substring(0, fm.Index).Trim(); }
                else if (fm.Success) { p.Flags = fm.Groups[1].Value; rhs = rhs.Substring(0, fm.Index).Trim(); }
                p.Value = rhs.Length == 0 ? null : ParseValue(rhs);
                (output ? n.OutputMods : n.Inputs).Add(p);
            }
        }

        static void ParseConfig(string body, NodeOp n)
        {
            foreach (var raw in SplitTop(body, ','))
            {
                var s = raw.Trim();
                if (s.Length == 0) continue;
                int eq = TopLevelIndex(s, '=');
                if (eq < 0) { n.Config.Add(new KeyValuePair<string, Val>(s, new Val { Kind = VK.Bool, Str = "true" })); continue; }
                var key = s.Substring(0, eq).Trim();
                var v = s.Substring(eq + 1).Trim();
                Val val;
                if (v.StartsWith("@") && v.Length > 1) val = new Val { Kind = VK.Str, Str = v };       // @file reference
                else val = ParseValue(v, configMode: true);
                n.Config.Add(new KeyValuePair<string, Val>(key, val));
            }
        }

        static readonly Regex NumRx = new Regex(@"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$");

        public static Val ParseValue(string s, bool configMode = false)
        {
            s = s.Trim();
            if (s == "_") return new Val { Kind = VK.Clear };
            if (s.StartsWith("\"\"\"") && s.EndsWith("\"\"\"") && s.Length >= 6) return new Val { Kind = VK.Str, Str = s.Substring(3, s.Length - 6).Trim('\n') };
            if (s.StartsWith("\"")) { int k = 0; var str = ReadString(s, ref k); return new Val { Kind = VK.Str, Str = str }; }
            if (s.StartsWith("{") && s.EndsWith("}"))
            {
                var parts = s.Substring(1, s.Length - 2).Split(',');
                return new Val { Kind = VK.Point, Xyz = parts.Select(p => double.Parse(p.Trim(), C)).Concat(new double[] { 0, 0, 0 }).Take(3).ToArray() };
            }
            if (s.StartsWith("[") && s.EndsWith("]"))
                return new Val { Kind = VK.List, Items = SplitTop(s.Substring(1, s.Length - 2), ',').Where(x => x.Trim().Length > 0).Select(x => ParseValue(x, configMode)).ToList() };
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase))
                return new Val { Kind = VK.Bool, Str = s.ToLowerInvariant() };
            if (Regex.IsMatch(s, @"^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$")) return new Val { Kind = VK.Color, Str = s };
            var iv = Regex.Match(s, @"^([-+]?[\d.eE+-]+)\s*\.\.\s*([-+]?[\d.eE+-]+)$");
            if (iv.Success) return new Val { Kind = VK.Interval, Xyz = new[] { double.Parse(iv.Groups[1].Value, C), double.Parse(iv.Groups[2].Value, C) } };
            if (NumRx.IsMatch(s)) return new Val { Kind = VK.Num, Num = double.Parse(s, C) };
            // references: a, a.0, a.R, a+b.1, r3+r4
            if (Regex.IsMatch(s, @"^[A-Za-z_][\w]*(\.[\w ]+)?(\s*\+\s*[A-Za-z_][\w]*(\.[\w ]+)?)*$") && !configMode)
            {
                var refs = s.Split('+').Select(x => SplitRef(x.Trim())).ToList();
                return new Val { Kind = VK.Ref, Refs = refs };
            }
            return new Val { Kind = VK.Ident, Str = s };
        }

        static string ReadString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("expected string");
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char ch = s[i++];
                if (ch == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', '"' => '"', '\\' => '\\', _ => e });
                }
                else if (ch == '"') return sb.ToString();
                else sb.Append(ch);
            }
            throw new FormatException("unterminated string");
        }

        static string Block(string s, ref int i, char open, char close)
        {
            int depth = 0, start = i + 1;
            bool inStr = false, inTriple = false;
            for (; i < s.Length; i++)
            {
                char ch = s[i];
                if (inTriple) { if (string.CompareOrdinal(s, i, "\"\"\"", 0, 3) == 0) { inTriple = false; i += 2; } continue; }
                if (inStr) { if (ch == '\\') i++; else if (ch == '"') inStr = false; continue; }
                if (string.CompareOrdinal(s, i, "\"\"\"", 0, 3) == 0) { inTriple = true; i += 2; continue; }
                if (ch == '"') { inStr = true; continue; }
                if (ch == open) depth++;
                else if (ch == close && --depth == 0) { var body = s.Substring(start, i - start); i++; return body; }
            }
            throw new FormatException($"missing '{close}'");
        }

        public static List<string> SplitTop(string s, char sep)
        {
            var res = new List<string>();
            int depth = 0, last = 0;
            bool inStr = false, inTriple = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (inTriple) { if (string.CompareOrdinal(s, i, "\"\"\"", 0, 3) == 0) { inTriple = false; i += 2; } continue; }
                if (inStr) { if (ch == '\\') i++; else if (ch == '"') inStr = false; continue; }
                if (string.CompareOrdinal(s, i, "\"\"\"", 0, 3) == 0) { inTriple = true; i += 2; continue; }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '(' || ch == '{' || ch == '[') depth++;
                else if (ch == ')' || ch == '}' || ch == ']') depth--;
                else if (ch == sep && depth == 0) { res.Add(s.Substring(last, i - last)); last = i + 1; }
            }
            res.Add(s.Substring(last));
            return res;
        }

        static int TopLevelIndex(string s, char target)
        {
            int depth = 0; bool inStr = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (inStr) { if (ch == '\\') i++; else if (ch == '"') inStr = false; continue; }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '(' || ch == '{' || ch == '[') depth++;
                else if (ch == ')' || ch == '}' || ch == ']') depth--;
                else if (ch == target && depth == 0) return i;
            }
            return -1;
        }

        static int FirstTopBracket(string s, int from = 0)
        {
            bool inStr = false;
            for (int i = from; i < s.Length; i++)
            {
                char ch = s[i];
                if (inStr) { if (ch == '"') inStr = false; continue; }
                if (ch == '"') return i;
                if (ch == '(' || ch == '{') return i;
            }
            return -1;
        }
    }
}
