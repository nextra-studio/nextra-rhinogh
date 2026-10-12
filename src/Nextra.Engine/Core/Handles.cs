using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino.Geometry;

namespace Nextra.Engine
{
    /// GUID <-> short handle tables (r12 for Rhino objects, g4 for GH objects).
    /// Stored in the host state bag using BCL types only, so they survive engine hot-reloads.
    /// One table per document: switching between GH documents (or Rhino files) and back keeps every handle
    /// meaning the same object, instead of renumbering and letting an old `g12` hit a different node.
    public static class Handles
    {
        static Dictionary<string, object> Table(string prefix, object docKey)
        {
            var key = "handles." + prefix + "." + docKey;
            if (!Entry.State.TryGetValue(key, out var o) || !(o is Dictionary<string, object> t))
            {
                t = new Dictionary<string, object>
                {
                    ["doc"] = docKey,
                    ["h2g"] = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase),
                    ["g2h"] = new Dictionary<Guid, string>(),
                    ["next"] = new int[] { 1 },
                };
                Entry.State[key] = t;
            }
            return t;
        }

        static string Get(string prefix, object docKey, Guid g)
        {
            var t = Table(prefix, docKey);
            var g2h = (Dictionary<Guid, string>)t["g2h"];
            if (g2h.TryGetValue(g, out var h)) return h;
            var next = (int[])t["next"];
            h = prefix + next[0]++;
            g2h[g] = h;
            ((Dictionary<string, Guid>)t["h2g"])[h] = g;
            return h;
        }

        static bool TryGuid(string prefix, object docKey, string h, out Guid g)
        {
            g = Guid.Empty;
            if (string.IsNullOrWhiteSpace(h)) return false;
            h = h.Trim();
            if (Guid.TryParse(h, out g)) return true;
            var t = Table(prefix, docKey);
            return ((Dictionary<string, Guid>)t["h2g"]).TryGetValue(h, out g);
        }

        static object RhDocKey => Rhino.RhinoDoc.ActiveDoc?.RuntimeSerialNumber ?? 0u;

        public static string Rh(Guid g) => Get("r", RhDocKey, g);
        public static bool TryRh(string h, out Guid g) => TryGuid("r", RhDocKey, h, out g);
        public static bool IsRhHandle(string s) => s != null && s.Length > 1 && (s[0] == 'r' || s[0] == 'R') && s.Skip(1).All(char.IsDigit);
        public static bool IsGhHandle(string s) => s != null && s.Length > 1 && (s[0] == 'g' || s[0] == 'G') && s.Skip(1).All(char.IsDigit);

        public static string Gh(object ghDocKey, Guid g) => Get("g", ghDocKey, g);
        public static bool TryGh(object ghDocKey, string h, out Guid g) => TryGuid("g", ghDocKey, h, out g);
        public static string GhPeek(object ghDocKey, Guid g)
        {
            var t = Table("g", ghDocKey);
            return ((Dictionary<Guid, string>)t["g2h"]).TryGetValue(g, out var h) ? h : null;
        }
    }

    /// Compact number / vector formatting shared by every reply.
    public static class Fmt
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public static string N(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return "nan";
            double a = Math.Abs(x);
            if (a < 1e-9) return "0";
            string s = a >= 1000 ? x.ToString("0.#", C) : a >= 1 ? x.ToString("0.###", C) : x.ToString("0.####", C);
            return s == "-0" ? "0" : s;
        }

        public static string P(Point3d p) => $"({N(p.X)},{N(p.Y)},{N(p.Z)})";
        public static string P2(double x, double y) => $"({N(x)},{N(y)})";

        public static string V(Vector3d v)
        {
            v.Unitize();
            if (v.IsParallelTo(Vector3d.ZAxis, 0.02) != 0) return v.Z > 0 ? "+z" : "-z";
            if (v.IsParallelTo(Vector3d.XAxis, 0.02) != 0) return v.X > 0 ? "+x" : "-x";
            if (v.IsParallelTo(Vector3d.YAxis, 0.02) != 0) return v.Y > 0 ? "+y" : "-y";
            return $"({v.X.ToString("0.##", C)},{v.Y.ToString("0.##", C)},{v.Z.ToString("0.##", C)})";
        }

        public static string Dims(double a, double b, double c) => $"{N(a)}x{N(b)}x{N(c)}";

        public static string Trunc(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", "");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// Filter tokens: whitespace separated; "quoted values" kept; words after `key:value` that are not
        /// keys/handles/keywords continue that value (so `type:Bounding Box` and `layer:My Layer` work).
        public static List<string> FilterTokens(string s, params string[] keywords)
        {
            var res = new List<string>();
            var rx = new System.Text.RegularExpressions.Regex(@"(\w+:""[^""]*"")|(""[^""]*"")|(\S+)");
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(s ?? ""))
            {
                var t = m.Value.Replace("\"", "");
                bool keyed = System.Text.RegularExpressions.Regex.IsMatch(t, @"^\w+:");
                bool handle = Handles.IsGhHandle(t) || Handles.IsRhHandle(t) || t.Contains(',') || Guid.TryParse(t, out _);
                bool kw = keywords.Contains(t.ToLowerInvariant());
                if (!keyed && !handle && !kw && res.Count > 0 && System.Text.RegularExpressions.Regex.IsMatch(res[res.Count - 1], @"^\w+:"))
                    res[res.Count - 1] += " " + t;
                else res.Add(t);
            }
            return res.SelectMany(t => t.Contains(':') ? new[] { t } : t.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
        }

        /// Selector match: exact (case-insensitive) unless the pattern has * or ?. Used where a selection is acted on
        /// (layer:Beams must not also pick "Beams_old"; group:Inputs must not also tidy "Inputs 2").
        public static bool Match(string text, string pattern) =>
            pattern == null || (pattern.Contains('*') || pattern.Contains('?') ? Like(text, pattern) : string.Equals((text ?? "").Trim(), pattern.Trim(), StringComparison.OrdinalIgnoreCase));

        /// Wildcard match (* and ?), case-insensitive; without wildcards a substring match (for searches).
        public static bool Like(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            text ??= "";
            if (!pattern.Contains('*') && !pattern.Contains('?')) return text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(text, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
    }
}
