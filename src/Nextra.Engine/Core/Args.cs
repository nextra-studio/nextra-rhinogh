using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nextra.Engine
{
    /// Tolerant argument reader: accepts strings for numbers/bools, single values for lists,
    /// and reports keys the tool did not use so the LLM learns the real parameter names.
    public class Args
    {
        readonly JsonObject _o;
        readonly HashSet<string> _used = new HashSet<string>();
        Args(JsonObject o) { _o = o ?? new JsonObject(); }

        public static Args Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Args(new JsonObject());
            var n = JsonNode.Parse(json);
            return new Args(n as JsonObject ?? new JsonObject());
        }

        public static Args From(JsonObject o) => new Args(o);

        public bool Has(string k) { _used.Add(k); return _o.ContainsKey(k) && _o[k] != null; }

        public JsonNode Raw(string k) { _used.Add(k); return _o.ContainsKey(k) ? _o[k] : null; }

        public string Str(string k, string d = null)
        {
            var n = Raw(k);
            if (n == null) return d;
            if (n is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s)) return s;
                return v.ToJsonString();
            }
            if (n is JsonArray arr) return string.Join(",", arr.Select(x => x is JsonValue xv && xv.TryGetValue<string>(out var xs) ? xs : x?.ToJsonString()));
            return n.ToJsonString();
        }

        public int Int(string k, int d)
        {
            var n = Raw(k);
            if (n == null) return d;
            try { return n.GetValue<int>(); } catch { }
            try { return (int)n.GetValue<double>(); } catch { }
            return int.TryParse(Str(k), out var i) ? i : d;
        }

        public double Num(string k, double d)
        {
            var n = Raw(k);
            if (n == null) return d;
            try { return n.GetValue<double>(); } catch { }
            return double.TryParse(Str(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : d;
        }

        public bool Bool(string k, bool d)
        {
            var n = Raw(k);
            if (n == null) return d;
            try { return n.GetValue<bool>(); } catch { }
            var s = (Str(k) ?? "").Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
            if (s == "false" || s == "0" || s == "no" || s == "off") return false;
            return d;
        }

        /// List of strings from ["a","b"] or "a,b" or "a b".
        public List<string> List(string k)
        {
            var n = Raw(k);
            var res = new List<string>();
            if (n == null) return res;
            if (n is JsonArray arr)
            {
                foreach (var x in arr)
                {
                    if (x == null) continue;
                    if (x is JsonValue xv && xv.TryGetValue<string>(out var s)) res.Add(s.Trim());
                    else res.Add(x.ToJsonString());
                }
                return res;
            }
            var str = Str(k);
            if (string.IsNullOrWhiteSpace(str)) return res;
            res.AddRange(str.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
            return res;
        }

        public double[] Nums(string k)
        {
            var n = Raw(k);
            if (n == null) return null;
            try
            {
                if (n is JsonArray arr) return arr.Select(x => x.GetValue<double>()).ToArray();
                return Str(k).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            }
            catch { throw new NxError($"'{k}' must be a list of numbers"); }
        }

        public JsonArray Arr(string k) => Raw(k) as JsonArray;
        public JsonObject Obj(string k) => Raw(k) as JsonObject;

        public void ReportUnused(Reply r)
        {
            var extra = _o.Select(kv => kv.Key).Where(x => !_used.Contains(x)).ToList();
            if (extra.Count > 0) r.Note("ignored args: " + string.Join(", ", extra));
        }
    }

    /// Reply envelope. Text lines are the payload the LLM reads.
    public class Reply
    {
        public bool Ok = true;
        readonly StringBuilder _sb = new StringBuilder();
        readonly List<string> _notes = new List<string>();
        public readonly List<string> Images = new List<string>();

        public Reply Line(string s) { _sb.AppendLine(s); return this; }
        public Reply Add(string s) { _sb.Append(s); return this; }
        public void Note(string s) { _notes.Add(s); }

        public void Fail(string msg, string hint = null)
        {
            Ok = false;
            _sb.AppendLine("ERROR: " + msg);
            if (!string.IsNullOrEmpty(hint)) _sb.AppendLine("hint: " + hint);
        }

        public int Length => _sb.Length;

        public string ToJson()
        {
            var text = _sb.ToString().TrimEnd();
            if (_notes.Count > 0) text += "\n(" + string.Join("; ", _notes) + ")";
            var o = new JsonObject { ["ok"] = Ok, ["text"] = text };
            if (Images.Count > 0) o["images"] = new JsonArray(Images.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
            return o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
    }
}
