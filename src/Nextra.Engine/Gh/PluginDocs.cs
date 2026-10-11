using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grasshopper.Kernel;

namespace Nextra.Engine.Gh
{
    /// Plug-in specific guides (skill/plugins/<Category>.md, user overrides in memory/plugins/).
    /// When a component of a plug-in category shows up for the first time in a session, the guide's
    /// "## Rules" section is attached to the reply (mode auto), or just pointed to (hint), or nothing (off).
    public static class PluginDocs
    {
        class Guide { public string Path, Name, Rules; public List<string> Categories = new List<string>(); }

        static List<Guide> Load()
        {
            var dirs = new List<string>();
            var skill = EngineConfig.Str("skill_dir", null) ?? System.IO.Path.Combine(Entry.AppDir, "skill");
            dirs.Add(System.IO.Path.Combine(Memory.Dir, "plugins"));   // user guides first: they win
            dirs.Add(System.IO.Path.Combine(skill, "plugins"));
            var res = new List<Guide>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs.Where(Directory.Exists))
                foreach (var f in Directory.EnumerateFiles(d, "*.md"))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(f);
                    if (name.StartsWith("_")) continue;
                    var text = File.ReadAllText(f);
                    var g = new Guide { Path = f, Name = name };
                    var pl = text.Split('\n').FirstOrDefault(l => l.StartsWith("plugin:"));
                    if (pl != null) g.Categories.AddRange(pl.Substring(7).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
                    if (g.Categories.Count == 0) g.Categories.Add(name);
                    if (g.Categories.All(c => seen.Contains(c))) continue;
                    foreach (var c in g.Categories) seen.Add(c);
                    g.Rules = Section(text, "Rules");
                    res.Add(g);
                }
            return res;
        }

        static string Section(string text, string head)
        {
            var lines = text.Replace("\r", "").Split('\n');
            int i = Array.FindIndex(lines, l => l.TrimStart('#', ' ').StartsWith(head, StringComparison.OrdinalIgnoreCase) && l.StartsWith("#"));
            if (i < 0) return null;
            int lvl = lines[i].TakeWhile(c => c == '#').Count();
            var outL = new List<string>();
            for (int j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].StartsWith("#") && lines[j].TakeWhile(c => c == '#').Count() <= lvl) break;
                outL.Add(lines[j]);
            }
            return string.Join("\n", outL).Trim();
        }

        static HashSet<string> Shown
        {
            get
            {
                if (!Entry.State.TryGetValue("plugindocs.shown", out var o) || !(o is HashSet<string> s)) { s = new HashSet<string>(StringComparer.OrdinalIgnoreCase); Entry.State["plugindocs.shown"] = s; }
                return s;
            }
        }

        public static void Check(IEnumerable<string> categories, Reply r, string modeOverride = null)
        {
            var mode = (modeOverride ?? EngineConfig.Str("plugin_docs", "auto") ?? "auto").ToLowerInvariant();
            if (mode == "off") return;
            var cats = new HashSet<string>(categories.Where(c => !string.IsNullOrEmpty(c)), StringComparer.OrdinalIgnoreCase);
            if (cats.Count == 0) return;
            List<Guide> guides;
            try { guides = Load(); } catch { return; }
            foreach (var g in guides)
            {
                if (!g.Categories.Any(cats.Contains) || Shown.Contains(g.Name)) continue;
                Shown.Add(g.Name);
                if (mode == "hint" || string.IsNullOrEmpty(g.Rules))
                    r.Line($"plugin guide: {g.Name} → query {{scope:'docs', filter:'plugins/{g.Name}'}}");
                else
                    r.Line($"plugin rules ({g.Name}, shown once; full guide: query {{scope:'docs', filter:'plugins/{g.Name}'}}):\n{g.Rules}");
            }
        }

        public static void Check(IEnumerable<IGH_DocumentObject> objs, Reply r, string modeOverride = null) =>
            Check(objs.Where(o => o != null).Select(o => o.Category), r, modeOverride);
    }
}
