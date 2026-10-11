using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GH_IO.Serialization;
using GH_IO.Types;
using Grasshopper;
using Grasshopper.Kernel;

namespace Nextra.Engine.Gh
{
    /// Generic component state through GH's own serialization: every setting a component saves in a .gh
    /// (dropdown selections, menu toggles, modes — e.g. Karamba dropdowns) becomes `$key=value`.
    /// Only values that differ from a freshly created instance are shown, so output stays small.
    public static class StateIO
    {
        static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Name", "NickName", "Description", "InstanceGuid", "IconDisplay", "IconOverride", "Hidden", "Locked", "Enabled",
            "Message", "ComponentGuid", "Optional", "SourceCount", "Source", "Mapping", "Reverse", "Simplify", "Access",
            "Bounds", "Pivot", "Selected", "ParameterData", "Expanded", "InputCount", "OutputCount", "InputId", "OutputId",
        };
        static readonly HashSet<string> SkipChunks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "param_input", "param_output", "PersistentData", "ParameterData", "InputParam", "OutputParam", "Script", "CodeContent",
        };

        const string Unstable = "\u0001unstable";
        static bool Noise(string k) => k.IndexOf("Seed", StringComparison.OrdinalIgnoreCase) >= 0 || k.EndsWith("/count") || k.IndexOf("Parallel", StringComparison.OrdinalIgnoreCase) >= 0;

        static readonly Dictionary<Guid, Dictionary<string, string>> Defaults = new Dictionary<Guid, Dictionary<string, string>>();

        static GH_Chunk Serialize(IGH_DocumentObject o)
        {
            var a = new GH_Archive();
            a.CreateNewRoot(true);
            var root = a.GetRootNode;
            o.Write(root);
            return root;
        }

        /// Flattened key → printable value. Keys of nested chunks become "Chunk/Item" (index suffix "[i]" if any).
        public static Dictionary<string, string> Read(IGH_DocumentObject o)
        {
            var res = new Dictionary<string, string>();
            try { Walk(Serialize(o), "", res, 0); } catch { }
            return res;
        }

        static void Walk(GH_IChunk c, string prefix, Dictionary<string, string> res, int depth)
        {
            foreach (var it in c.Items)
            {
                if (Skip.Contains(it.Name)) continue;
                var v = Show(it);
                if (v == null) continue;
                res[prefix + it.Name + (it.Index > 0 || c.Items.Count(x => x.Name == it.Name) > 1 ? $"[{it.Index}]" : "")] = v;
            }
            if (depth >= 7) return;
            foreach (var ch in c.Chunks)
            {
                if (SkipChunks.Contains(ch.Name)) continue;
                Walk(ch, prefix + ch.Name + (ch.Index > 0 ? $"[{ch.Index}]" : "") + "/", res, depth + 1);
            }
        }

        static string Show(GH_Item it)
        {
            switch (it.Type)
            {
                case GH_Types.gh_bool: return it._bool ? "true" : "false";
                case GH_Types.gh_int32: return it._int32.ToString(CultureInfo.InvariantCulture);
                case GH_Types.gh_int64: return it._int64.ToString(CultureInfo.InvariantCulture);
                case GH_Types.gh_double: return Fmt.N(it._double);
                case GH_Types.gh_single: return Fmt.N(it._single);
                case GH_Types.gh_decimal: return it._decimal.ToString(CultureInfo.InvariantCulture);
                case GH_Types.gh_string: return it._string == null ? null : "\"" + Fmt.Trunc(it._string.Replace("\n", "\\n"), 80) + "\"";
                case GH_Types.gh_guid: return null;
                case GH_Types.gh_drawing_color: var col = it._drawing_color; return $"#{col.R:X2}{col.G:X2}{col.B:X2}";
                case GH_Types.gh_bytearray: case GH_Types.gh_drawing_bitmap: return null;
                default: return null;
            }
        }

        /// Non-default custom state, e.g. {"Family":"\"HEA\"","SelectedIndex":"3"}.
        public static Dictionary<string, string> Diff(IGH_DocumentObject o)
        {
            var cur = Read(o);
            if (cur.Count == 0) return cur;
            if (!Defaults.TryGetValue(o.ComponentGuid, out var def))
            {
                def = new Dictionary<string, string>();
                try
                {
                    var proxy = Instances.ComponentServer.EmitObjectProxy(o.ComponentGuid);
                    var f1 = proxy?.CreateInstance(); var f2 = proxy?.CreateInstance();
                    if (f1 != null && f2 != null)
                    {
                        f1.CreateAttributes(); f2.CreateAttributes();
                        var d1 = Read(f1); var d2 = Read(f2);
                        foreach (var kv in d1)
                            def[kv.Key] = d2.TryGetValue(kv.Key, out var v2) && v2 == kv.Value ? kv.Value : Unstable;
                    }
                }
                catch { }
                Defaults[o.ComponentGuid] = def;
            }
            return cur.Where(kv => !Noise(kv.Key) && (!def.TryGetValue(kv.Key, out var dv) || (dv != kv.Value && dv != Unstable)) && !(def.Count > 0 && !def.ContainsKey(kv.Key) && kv.Key.Contains("/count")))
                      .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        /// Set "$key" values and read the archive back into the object.
        /// Shortest suffix of a key that is still unique among the object's keys ("Active/button[2]" → "button[2]").
        public static string ShortKey(string key, List<string> all)
        {
            var parts = key.Split('/');
            for (int n = 1; n <= parts.Length; n++)
            {
                var suf = string.Join("/", parts.Skip(parts.Length - n));
                if (all.Count(k => k == suf || k.EndsWith("/" + suf, StringComparison.OrdinalIgnoreCase)) == 1) return suf;
            }
            return key;
        }

        /// "$button[2]" or "$Active/button[2]" → the unique full key that ends with it.
        static string Expand(IGH_DocumentObject o, string key)
        {
            var keys = Read(o).Keys.ToList();
            if (keys.Contains(key)) return key;
            var hits = keys.Where(k => k.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) throw new NxError($"state key '{key}' is ambiguous", "use a longer suffix: " + string.Join(", ", hits.Take(6)));
            return key;
        }

        public static string Write(IGH_DocumentObject o, List<KeyValuePair<string, string>> sets)
        {
            var root = Serialize(o);
            var notes = new List<string>();
            foreach (var kv0 in sets)
            {
                var kv = new KeyValuePair<string, string>(Expand(o, kv0.Key), kv0.Value);
                var path = kv.Key.Split('/');
                GH_IChunk c = root;
                for (int i = 0; i < path.Length - 1; i++)
                {
                    var (cn, ci) = SplitIndex(path[i]);
                    var next = c.Chunks.FirstOrDefault(x => x.Name.Equals(cn, StringComparison.OrdinalIgnoreCase) && (x.Index == ci || ci == -1 && x.Index <= 0));
                    if (next == null) throw new NxError($"state chunk '{path[i]}' not found on {o.Name}", "known keys: " + string.Join(", ", Read(o).Keys.Take(25)));
                    c = next;
                }
                var (name, idx) = SplitIndex(path[path.Length - 1]);
                var item = c.Items.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (x.Index == idx || idx == -1 && x.Index <= 0));
                if (item == null) throw new NxError($"state key '{kv.Key}' not found on {o.Name}", "known keys: " + string.Join(", ", Read(o).Keys.Take(25)));
                var w = (GH_IWriter)c;
                var v = kv.Value.Trim();
                if (v.Length >= 2 && v.StartsWith("\"") && v.EndsWith("\"")) v = v.Substring(1, v.Length - 2);
                try
                {
                    // GH_IO setters append; remove the old item first so the value is replaced, not duplicated
                    var chunk = (GH_Chunk)c;
                    if (item.Index >= 0) chunk.RemoveItem(item.Name, item.Index); else chunk.RemoveItem(item.Name);
                    int ix = item.Index;
                    switch (item.Type)
                    {
                        case GH_Types.gh_bool: { bool x = v == "true" || v == "1"; if (ix >= 0) w.SetBoolean(item.Name, ix, x); else w.SetBoolean(item.Name, x); break; }
                        case GH_Types.gh_int32: { int x = int.Parse(v, CultureInfo.InvariantCulture); if (ix >= 0) w.SetInt32(item.Name, ix, x); else w.SetInt32(item.Name, x); break; }
                        case GH_Types.gh_int64: { long x = long.Parse(v, CultureInfo.InvariantCulture); if (ix >= 0) w.SetInt64(item.Name, ix, x); else w.SetInt64(item.Name, x); break; }
                        case GH_Types.gh_double: { double x = double.Parse(v, CultureInfo.InvariantCulture); if (ix >= 0) w.SetDouble(item.Name, ix, x); else w.SetDouble(item.Name, x); break; }
                        case GH_Types.gh_single: { float x = float.Parse(v, CultureInfo.InvariantCulture); if (ix >= 0) w.SetSingle(item.Name, ix, x); else w.SetSingle(item.Name, x); break; }
                        case GH_Types.gh_decimal: { decimal x = decimal.Parse(v, CultureInfo.InvariantCulture); if (ix >= 0) w.SetDecimal(item.Name, ix, x); else w.SetDecimal(item.Name, x); break; }
                        case GH_Types.gh_string: { var x = v.Replace("\\n", "\n"); if (ix >= 0) w.SetString(item.Name, ix, x); else w.SetString(item.Name, x); break; }
                        default: throw new NxError($"state key '{kv.Key}' has type {item.Type}, not editable as text");
                    }
                }
                catch (FormatException) { throw new NxError($"value '{v}' does not fit {item.Type} for '{kv.Key}'"); }
                notes.Add(kv.Key);
            }
            var piv = o.Attributes?.Pivot;
            if (!o.Read(root)) throw new NxError($"{o.Name} rejected the new state", "the component may validate values; check allowed options with query level:'detail'");
            if (piv.HasValue && o.Attributes != null) o.Attributes.Pivot = piv.Value;
            if (o is IGH_Component comp) { comp.Params.OnParametersChanged(); }
            o.Attributes?.ExpireLayout();
            if (o is IGH_ActiveObject ao) ao.ExpireSolution(false);
            return "state " + string.Join(", ", notes);
        }

        static (string, int) SplitIndex(string s)
        {
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^(.*)\[(\d+)\]$");
            return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value)) : (s, -1);   // GH_IO: unindexed = -1
        }
    }
}
