using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nextra.Engine
{
    /// Local, per-user memory. prefs.md and rules.md are small (hard caps) and meant to be read every session;
    /// mistakes.log is raw and append-only; library/ holds good scripts. Rules only change through
    /// `compact` → user approval → `set_rules`; the shorthand grammar itself is never self-modified.
    public static class Memory
    {
        public const int PrefsCap = 1500, RulesCap = 2000;

        public static string Dir
        {
            get
            {
                var d = EngineConfig.Str("memory_dir", null) ?? Path.Combine(Entry.AppDir, "memory");
                Directory.CreateDirectory(d);
                Directory.CreateDirectory(Path.Combine(d, "library"));
                return d;
            }
        }

        static string P(string f) => Path.Combine(Dir, f);
        public static string Read(string f) => File.Exists(P(f)) ? File.ReadAllText(P(f)) : "";

        public static void AutoLog(string kind, string sig, string note = null, string fix = null)
        {
            try
            {
                var o = new JsonObject { ["t"] = DateTime.Now.ToString("s"), ["kind"] = kind, ["sig"] = sig };
                if (note != null) o["note"] = note;
                if (fix != null) o["fix"] = fix;
                File.AppendAllText(P("mistakes.log"), o.ToJsonString() + "\n");
                Entry.State["mem.lastsig"] = sig;
            }
            catch { }
        }

        static List<JsonObject> Mistakes()
        {
            var res = new List<JsonObject>();
            if (!File.Exists(P("mistakes.log"))) return res;
            foreach (var l in File.ReadLines(P("mistakes.log")))
            {
                try { if (JsonNode.Parse(l) is JsonObject o) res.Add(o); } catch { }
            }
            return res;
        }

        /// One-time hint when the error just logged has happened 3+ times and no rule mentions it yet.
        public static string RecurringHint()
        {
            if (!Entry.State.TryGetValue("mem.lastsig", out var s) || !(s is string sig)) return null;
            Entry.State.Remove("mem.lastsig");
            var key = "mem.hinted." + sig;
            if (Entry.State.ContainsKey(key)) return null;
            int n = Mistakes().Count(m => (string)m["sig"] == sig);
            if (n < 3) return null;
            Entry.State[key] = true;
            return $"recurring mistake ({n}x): {sig}. After fixing, record the lesson: memory {{action:'log_mistake', text, fix}}; later memory {{action:'compact'}}.";
        }

        public static string Session()
        {
            var sb = new StringBuilder();
            var prefs = Read("prefs.md").Trim();
            var rules = Read("rules.md").Trim();
            sb.AppendLine("## prefs\n" + (prefs.Length > 0 ? prefs : "(none)"));
            sb.AppendLine("## rules (learned)\n" + (rules.Length > 0 ? rules : "(none)"));
            sb.AppendLine("## library");
            sb.AppendLine(Library.Index(null, 40));
            var groups = Mistakes().GroupBy(m => (string)m["sig"]).Where(g => g.Count() >= 2 && !rules.Contains(g.Key)).OrderByDescending(g => g.Count()).Take(5).ToList();
            if (groups.Count > 0)
            {
                sb.AppendLine("## recurring mistakes not yet in rules");
                foreach (var g in groups) sb.AppendLine($"- {g.Count()}x {g.Key}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string SavePref(string text)
        {
            text = text.Trim().TrimStart('-', ' ');
            var lines = Read("prefs.md").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            // "key: value" replaces an existing "key:" line
            var m = Regex.Match(text, @"^([\w \-/]{1,30}):");
            int idx = m.Success ? lines.FindIndex(l => l.TrimStart('-', ' ').StartsWith(m.Groups[1].Value + ":", StringComparison.OrdinalIgnoreCase)) : -1;
            if (idx >= 0) lines[idx] = "- " + text; else lines.Add("- " + text);
            var all = string.Join("\n", lines) + "\n";
            if (all.Length > PrefsCap) throw new NxError($"prefs.md would be {all.Length} chars (cap {PrefsCap})", "merge or shorten prefs: memory {action:'set_prefs', text:'…full new prefs…'}");
            File.WriteAllText(P("prefs.md"), all);
            return idx >= 0 ? "pref updated" : "pref added";
        }

        public static string SetFile(string file, string text, int cap)
        {
            text = (text ?? "").Trim() + "\n";
            if (text.Length > cap) throw new NxError($"{file} would be {text.Length} chars (cap {cap})", "merge similar items, drop stale ones, keep each line one rule");
            if (File.Exists(P(file))) File.Copy(P(file), P(file + ".bak"), true);
            File.WriteAllText(P(file), text);
            return $"{file} saved ({text.Length}/{cap} chars)";
        }

        public static string Compact()
        {
            var ms = Mistakes();
            var sb = new StringBuilder();
            sb.AppendLine($"mistakes.log: {ms.Count} entries. Grouped (count · signature · latest fix/note):");
            foreach (var g in ms.GroupBy(m => (string)m["sig"]).OrderByDescending(g => g.Count()).Take(25))
            {
                var last = g.Last();
                var fix = (string)last["fix"] ?? (string)last["note"];
                sb.AppendLine($"- {g.Count()}x [{last["kind"]}] {g.Key}" + (fix != null ? $" → {Fmt.Trunc(fix, 120)}" : ""));
            }
            sb.AppendLine("current rules.md:");
            sb.AppendLine(Read("rules.md").Trim() is var rr && rr.Length > 0 ? rr : "(empty)");
            sb.AppendLine($"Next: draft ≤ {RulesCap} chars of short imperative rules (merge with the current ones), SHOW them to the user, and only after approval call memory {{action:'set_rules', text}}. The raw log is kept.");
            return sb.ToString().TrimEnd();
        }
    }

    public static class MemoryTool
    {
        public static void Run(Args a, Reply r)
        {
            var action = (a.Str("action", "list") ?? "list").ToLowerInvariant();
            switch (action)
            {
                case "list": case "session": r.Line(Memory.Session()); break;
                case "save_pref": r.Line(Memory.SavePref(Req(a, "text"))); break;
                case "set_prefs": r.Line(Memory.SetFile("prefs.md", Req(a, "text"), Memory.PrefsCap)); break;
                case "set_rules": r.Line(Memory.SetFile("rules.md", Req(a, "text"), Memory.RulesCap)); break;
                case "log_mistake":
                    Memory.AutoLog(a.Str("kind", "manual"), Fmt.Trunc(Req(a, "text"), 160), null, a.Str("fix", null));
                    r.Line("logged");
                    break;
                case "compact": r.Line(Memory.Compact()); break;
                case "save_script":
                    r.Line(Library.Save(Req(a, "name"), Req(a, "code"), a.Str("lang", "py"), a.Str("doc", null)));
                    break;
                case "read_script":
                case "read":
                    {
                        var p = Library.Find(Req(a, "name"), out _);
                        var lines = File.ReadAllLines(p);
                        int from = Math.Max(1, a.Int("from", 1)), to = Math.Min(lines.Length, a.Int("to", lines.Length));
                        r.Line($"{p} ({lines.Length} lines)");
                        for (int i = from; i <= to; i++) r.Line($"{i,4}| {lines[i - 1]}");
                        break;
                    }
                case "delete_script":
                    {
                        var p = Library.Find(Req(a, "name"), out _);
                        if (!p.StartsWith(Memory.Dir, StringComparison.OrdinalIgnoreCase)) throw new NxError("only scripts in the memory library can be deleted", p);
                        File.Delete(p);
                        r.Line("deleted " + p);
                        break;
                    }
                default:
                    throw new NxError($"memory action '{action}' unknown",
                        "actions: list | save_pref | set_prefs | log_mistake | compact | set_rules | save_script | read_script | delete_script");
            }
        }

        static string Req(Args a, string k) => a.Str(k, null) ?? throw new NxError($"'{k}' is required for this action");
    }

    /// Script library: the memory library plus any folders the user points at (config library_dirs)
    /// plus `.nextra/library` and `scripts/` next to the open .3dm. Only an index goes to the LLM.
    public static class Library
    {
        public static List<string> Dirs()
        {
            var dirs = new List<string> { Path.Combine(Memory.Dir, "library") };
            dirs.AddRange(EngineConfig.List("library_dirs"));
            var docPath = Rhino.RhinoDoc.ActiveDoc?.Path;
            if (!string.IsNullOrEmpty(docPath))
            {
                var d = Path.GetDirectoryName(docPath);
                dirs.Add(Path.Combine(d, ".nextra", "library"));
                dirs.Add(Path.Combine(d, "scripts"));
            }
            return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static IEnumerable<string> Files() => Dirs().SelectMany(d =>
            Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".py") || f.EndsWith(".cs")).Take(400));

        public static string Summary(string path)
        {
            try
            {
                var lines = File.ReadLines(path).Take(25).ToList();
                foreach (var l in lines)
                {
                    var t = l.Trim().Trim('"', '\'', '#', '/', '*', ' ');
                    if (t.Length > 3 && !t.StartsWith("!") && !t.StartsWith("r:") && !t.StartsWith("import") && !t.StartsWith("using") && !t.StartsWith("from ") && !t.StartsWith("venv") && !t.StartsWith("requirements") && !t.StartsWith("env"))
                        return Fmt.Trunc(t, 90);
                }
            }
            catch { }
            return "";
        }

        public static string Index(string filter, int max)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (var f in Files())
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var sum = Summary(f);
                if (!string.IsNullOrEmpty(filter) && !Fmt.Like(name + " " + sum, filter)) continue;
                if (n++ >= max) { sb.AppendLine("…"); break; }
                sb.AppendLine($"- {name}{Path.GetExtension(f)}: {sum}");
            }
            if (n == 0) sb.Append(string.IsNullOrEmpty(filter) ? "(empty) dirs: " + string.Join("; ", Dirs()) : $"(no script matches '{filter}')");
            return sb.ToString().TrimEnd();
        }

        public static void Query(Args a, Reply r)
        {
            var name = a.Str("name", null);
            if (name != null)
            {
                var p = Find(name, out _);
                r.Line($"{p}\n" + File.ReadAllText(p));
                return;
            }
            r.Line("library dirs: " + string.Join("; ", Dirs()));
            r.Line(Index(a.Str("filter", null), a.Int("limit", 60)));
            r.Line("run one: run {lib:'name', args:{…}} · read: query {scope:'library', name}");
        }

        public static string Find(string name, out string lang)
        {
            if (File.Exists(name)) { lang = name.EndsWith(".cs") ? "cs" : "py"; return name; }
            var n = Path.GetFileNameWithoutExtension(name);
            var hits = Files().Where(f => Path.GetFileNameWithoutExtension(f).Equals(n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
            {
                var near = Files().Select(Path.GetFileNameWithoutExtension).Where(x => x.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0).Take(5);
                throw new NxError($"library script '{name}' not found", "similar: " + string.Join(", ", near) + " · list: query {scope:'library'}");
            }
            lang = hits[0].EndsWith(".cs") ? "cs" : "py";
            return hits[0];
        }

        public static string Save(string name, string code, string lang, string doc)
        {
            name = Regex.Replace(name.Trim(), @"[^\w\-]", "_");
            var ext = lang == "cs" ? ".cs" : ".py";
            var path = Path.Combine(Memory.Dir, "library", name + ext);
            if (!string.IsNullOrEmpty(doc) && !code.TrimStart().StartsWith(lang == "cs" ? "//" : "\"\"\""))
                code = lang == "cs" ? "// " + doc.Replace("\n", "\n// ") + "\n" + code : "\"\"\"" + doc + "\"\"\"\n" + code;
            bool existed = File.Exists(path);
            if (existed) File.Copy(path, path + ".bak", true);
            File.WriteAllText(path, code);
            return $"{(existed ? "updated" : "saved")} library script '{name}{ext}'" + (string.IsNullOrEmpty(doc) && Summary(path).Length == 0 ? " (tip: add a one-line docstring so the index can describe it)" : "");
        }
    }

    /// Serves the skill docs (same files as the Claude skill) to any LLM via query scope:'docs'.
    public static class DocsStore
    {
        static string Dir()
        {
            var d = EngineConfig.Str("skill_dir", null);
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            var p = Path.Combine(Entry.AppDir, "skill");
            if (Directory.Exists(p)) return p;
            // installed package: skill/ sits next to the plug-in
            if (Entry.State != null && Entry.State.TryGetValue("host.dir", out var hd) && hd is string hs && Directory.Exists(Path.Combine(hs, "skill"))) return Path.Combine(hs, "skill");
            return null;
        }

        public static void Query(Args a, Reply r)
        {
            var dir = Dir() ?? throw new NxError("docs folder not found", "set skill_dir in %APPDATA%/Nextra/config.json");
            var files = Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).ToList();
            var topic = a.Str("filter", a.Str("topic", null));
            if (string.IsNullOrEmpty(topic))
            {
                foreach (var f in files)
                {
                    var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                    var head = File.ReadLines(f).FirstOrDefault(l => l.StartsWith("description:")) ?? File.ReadLines(f).FirstOrDefault(l => l.StartsWith("# ")) ?? "";
                    r.Line($"- {rel}: {Fmt.Trunc(head.Replace("description:", "").TrimStart('#', ' '), 100)}");
                }
                r.Line("read: query {scope:'docs', filter:'shorthand'} or 'shorthand#Slider' or a keyword");
                return;
            }
            string section = null;
            if (topic.Contains('#')) { section = topic.Substring(topic.IndexOf('#') + 1); topic = topic.Substring(0, topic.IndexOf('#')); }
            // user plug-in guides (memory/plugins) shadow the bundled ones
            var userPlugins = Path.Combine(Memory.Dir, "plugins");
            if (Directory.Exists(userPlugins)) files.InsertRange(0, Directory.EnumerateFiles(userPlugins, "*.md"));
            string Rel(string f) => (f.StartsWith(userPlugins, StringComparison.OrdinalIgnoreCase) ? "plugins/" + Path.GetFileName(f) : Path.GetRelativePath(dir, f)).Replace('\\', '/');
            var t2 = topic.EndsWith(".md") ? topic : topic + ".md";
            var file = files.FirstOrDefault(f => Rel(f).Equals(t2, StringComparison.OrdinalIgnoreCase))
                       ?? files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(topic, StringComparison.OrdinalIgnoreCase));
            if (file != null && section == null) { r.Line(File.ReadAllText(file)); return; }
            // section or keyword search across files
            string kw = section ?? topic;
            int hits = 0;
            foreach (var f in file != null ? new List<string> { file } : files)
            {
                var lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].StartsWith("#") || lines[i].IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int lvl = lines[i].TakeWhile(c => c == '#').Count();
                    int j = i + 1;
                    while (j < lines.Length && !(lines[j].StartsWith("#") && lines[j].TakeWhile(c => c == '#').Count() <= lvl)) j++;
                    r.Line($"[{Path.GetFileName(f)}]\n" + string.Join("\n", lines.Skip(i).Take(Math.Min(j - i, 60))));
                    if (++hits >= 3) return;
                }
            }
            if (hits == 0) r.Line($"no doc section matches '{kw}'. Files: " + string.Join(", ", files.Select(Path.GetFileNameWithoutExtension)));
        }
    }
}
