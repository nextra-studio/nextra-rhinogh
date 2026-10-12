using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Nextra.Engine
{
    /// The user's own instructions, in a folder the plug-in creates on first start (default %APPDATA%\Nextra\user,
    /// config "user_dir"; Rhino command NextraFolder opens it). They reach the LLM through the tools, so they work however the skill was installed
    /// (uploaded .zip on claude.ai, Claude Code folder, any other MCP client):
    ///   preferences.md      always shown at session start (memory list)
    ///   workflows/*.md      index (file name + `when:` line) at session start; full text on demand
    ///   plugins/*.md        plug-in guides: `## Rules` attached automatically when that plug-in is used
    ///   scripts/            script library folder (run {lib:'name'})
    /// Files starting with "_" are templates and ignored. Nextra never overwrites user files.
    public static class UserDocs
    {
        public const int PrefsShowCap = 2500;

        public static string Dir
        {
            get
            {
                // local, not synced, never touched by plug-in updates (the Yak package folder is replaced on update)
                var d = EngineConfig.Str("user_dir", null);
                if (string.IsNullOrEmpty(d)) d = Path.Combine(Entry.AppDir, "user");
                try { Ensure(d); } catch { }
                return d;
            }
        }

        /// Plug-in guides that ship with the Rhino plug-in (Yak package: <package>/guides/plugins; dev: config
        /// "guides_dir" or the repo's guides/plugins next to skill_dir). Read-only defaults; the user's own guides win.
        public static string BundledGuides
        {
            get
            {
                var cfg = EngineConfig.Str("guides_dir", null);
                if (!string.IsNullOrEmpty(cfg) && Directory.Exists(cfg)) return cfg;
                if (Entry.State != null && Entry.State.TryGetValue("host.dir", out var hd) && hd is string hs)
                {
                    var p = Path.Combine(hs, "guides", "plugins");
                    if (Directory.Exists(p)) return p;
                }
                var skill = EngineConfig.Str("skill_dir", null);
                if (!string.IsNullOrEmpty(skill))
                {
                    var repo = Path.GetFullPath(Path.Combine(skill, "..", "..", "guides", "plugins"));
                    if (Directory.Exists(repo)) return repo;
                }
                return null;
            }
        }

        public static string Workflows => Path.Combine(Dir, "workflows");
        public static string Plugins => Path.Combine(Dir, "plugins");
        public static string Scripts => Path.Combine(Dir, "scripts");
        public static string Preferences => Path.Combine(Dir, "preferences.md");

        static void Ensure(string d)
        {
            Directory.CreateDirectory(d);
            foreach (var sub in new[] { "workflows", "plugins", "scripts" }) Directory.CreateDirectory(Path.Combine(d, sub));
            void Seed(string rel, string text) { var p = Path.Combine(d, rel); if (!File.Exists(p)) File.WriteAllText(p, text.Replace("\r\n", "\n")); }
            Seed("README.md", Readme);
            Seed("preferences.md", PrefsStarter);
            Seed(Path.Combine("workflows", "_TEMPLATE.md"), WorkflowTemplate);
            // the template that ships with the plug-in, so it matches the installed version (built-in text as a fallback)
            string shipped = null;
            try { var b = BundledGuides; if (b != null && File.Exists(Path.Combine(b, "_TEMPLATE.md"))) shipped = File.ReadAllText(Path.Combine(b, "_TEMPLATE.md")); } catch { }
            Seed(Path.Combine("plugins", "_TEMPLATE.md"), shipped ?? PluginTemplate);
        }

        // ------------------------------------------------------------------ reading

        /// Front-matter value (`key: value` between --- lines, or anywhere in the first 8 lines).
        public static string Meta(string text, string key)
        {
            foreach (var l in text.Replace("\r", "").Split('\n').Take(12))
            {
                var m = Regex.Match(l, @"^\s*" + Regex.Escape(key) + @"\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
                if (m.Success) return Regex.Replace(m.Groups[1].Value, @"\s+#.*$", "").Trim();
            }
            return null;
        }

        /// preferences.md without comment lines (<!-- … --> and lines starting with //).
        public static string PreferencesText()
        {
            if (!File.Exists(Preferences)) return "";
            var t = File.ReadAllText(Preferences).Replace("\r", "");
            t = Regex.Replace(t, @"<!--.*?-->", "", RegexOptions.Singleline);
            var lines = t.Split('\n').Where(l => !l.TrimStart().StartsWith("//") && !l.StartsWith("# ")).ToList();
            t = Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
            return t;
        }

        public static IEnumerable<string> WorkflowFiles() =>
            Directory.Exists(Workflows) ? Directory.EnumerateFiles(Workflows, "*.md").Where(f => !Path.GetFileName(f).StartsWith("_")).OrderBy(f => f) : Enumerable.Empty<string>();

        public static IEnumerable<string> PluginFiles() =>
            Directory.Exists(Plugins) ? Directory.EnumerateFiles(Plugins, "*.md").Where(f => !Path.GetFileName(f).StartsWith("_")).OrderBy(f => f) : Enumerable.Empty<string>();

        /// Lines for `memory list`: the user's own instructions, compact.
        public static string Session()
        {
            var sb = new StringBuilder();
            var prefs = PreferencesText();
            sb.AppendLine($"## your preferences ({Preferences})");
            sb.AppendLine(prefs.Length == 0 ? "(none yet)" : prefs.Length > PrefsShowCap ? prefs.Substring(0, PrefsShowCap) + $"\n…(cut; full file {prefs.Length} chars: query {{scope:'docs', filter:'preferences'}})" : prefs);
            var wf = WorkflowFiles().ToList();
            if (wf.Count > 0)
            {
                sb.AppendLine("## your workflows (follow the matching one; read it: query {scope:'docs', filter:'workflows/<name>'})");
                foreach (var f in wf.Take(40))
                {
                    var text = SafeRead(f);
                    sb.AppendLine($"- {Path.GetFileNameWithoutExtension(f)}: {Fmt.Trunc(Meta(text, "when") ?? Meta(text, "description") ?? FirstLine(text), 110)}");
                }
            }
            var pg = PluginFiles().ToList();
            if (pg.Count > 0)
                sb.AppendLine("## your plug-in guides (rules attach automatically when used): " + string.Join(", ", pg.Select(f => Meta(SafeRead(f), "plugin") ?? Path.GetFileNameWithoutExtension(f))));
            return sb.ToString().TrimEnd();
        }

        static string SafeRead(string f) { try { return File.ReadAllText(f); } catch { return ""; } }

        static string FirstLine(string text) =>
            text.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('#', ' ')).FirstOrDefault(l => l.Length > 0 && l != "---" && !l.Contains(':')) ?? "";

        // ------------------------------------------------------------------ writing (only when the user asks)

        public static string SaveWorkflow(string name, string text, string when)
        {
            name = Slug(name);
            var p = Path.Combine(Workflows, name + ".md");
            text = (text ?? "").Replace("\r\n", "\n").Trim();
            if (Meta(text, "when") == null)
            {
                if (string.IsNullOrWhiteSpace(when)) throw new NxError("a workflow needs a `when` line (one sentence: when should it be used?)", "memory {action:'save_workflow', name, when:'…', text}");
                text = $"---\nwhen: {when.Trim()}\n---\n" + text;
            }
            return Write(p, text + "\n");
        }

        public static string SaveGuide(string plugin, string text)
        {
            if (string.IsNullOrWhiteSpace(plugin)) throw new NxError("plugin category required (as shown by query {scope:'components', filter:'@Name'})");
            var p = Path.Combine(Plugins, Slug(plugin) + ".md");
            text = (text ?? "").Replace("\r\n", "\n").Trim();
            if (Meta(text, "plugin") == null) text = $"---\nplugin: {plugin.Trim()}\nwhen: {plugin.Trim()} components are used\n---\n" + text;
            if (!Regex.IsMatch(text, @"^##\s+Rules", RegexOptions.Multiline)) text += "\n\n## Rules\n- (add short rules here; they are attached automatically)";
            return Write(p, text + "\n");
        }

        static string Write(string p, string text)
        {
            bool existed = File.Exists(p);
            if (existed) File.Copy(p, p + ".bak", true);
            File.WriteAllText(p, text);
            return $"{(existed ? "updated" : "saved")} {p}" + (existed ? " (previous version: .bak)" : "");
        }

        static string Slug(string s) => Regex.Replace((s ?? "").Trim(), @"[^\w\-]+", "_").Trim('_') is var x && x.Length > 0 ? x : throw new NxError("name required");

        // ------------------------------------------------------------------ seeded files

        const string Readme = @"# Nextra — your folder

Everything here is **yours**: Nextra (the Rhino plug-in) reads it and hands it to the AI through its tools.
It works the same whether the Nextra skill was uploaded as a .zip on claude.ai, installed in Claude Code,
or used from another AI app. Nextra never overwrites your files. Files whose name starts with `_` are templates
and are ignored.

| file / folder | what the AI does with it |
|---|---|
| `preferences.md` | read at the start of every session; treated as your standing instructions (units, layers, naming, style) |
| `workflows/*.md` | the list (file name + its `when:` line) is shown at session start; the AI opens a workflow when your request matches its `when:` |
| `plugins/*.md` | guide for one Grasshopper plug-in; its `## Rules` section is attached automatically the first time that plug-in's components are used in a session |
| `scripts/` | your Python / C# scripts; the AI can list and run them (`run {lib:'name'}`) — put a one-line docstring at the top |

## How to write a workflow (`workflows/facade_panels.md`)
- **File name** = short name, lowercase, underscores: it is how the AI refers to it.
- **First lines** = a `when:` line: one sentence saying when to use it. This is the only part the AI always sees,
  so make it specific (""when the user asks for a facade panel definition in Grasshopper"", not ""facade"").
- **Body** = numbered steps, rules, preferred components, checks. Keep it short; it costs tokens when used.
See `workflows/_TEMPLATE.md`.

## How to write a plug-in guide (`plugins/Karamba3D.md`)
- **`plugin:` line** = the plug-in's component category exactly as Grasshopper shows it (ask the AI:
  ""query components @Karamba3D"" lists categories). Several categories: `plugin: Kangaroo2, K2Goals`.
- **`## Rules`** = up to ~12 short lines that must always be followed with this plug-in (units, which components,
  typical mistakes). Only this section is attached automatically.
- Anything else (examples, port lists) is read on demand.
- A guide here **replaces** the built-in guide for the same plug-in.
See `plugins/_TEMPLATE.md`.

## preferences.md
Plain lines, e.g. `- units: mm for architecture, m for structural work`. Lines starting with `//` and
`<!-- comments -->` are ignored.

You can also ask the AI: ""save this as my workflow"" / ""save these as my Karamba rules"" — it writes here
(the previous version is kept as `.bak`).

This folder is created by the Nextra plug-in the first time Rhino starts after installing it. Open it any time
with the Rhino command `NextraFolder`. It lives outside the plug-in's install folder, so updates never touch it.
To use another folder (e.g. a synced or shared one for your team), set `user_dir` in %APPDATA%\Nextra\config.json.
";

        const string PrefsStarter = @"# My preferences
<!-- One line per preference. The AI reads this at the start of every session. Lines starting with // are ignored. -->
// - units: mm for architecture, m for structural models
// - layers: put new geometry on layers named by element type (Columns, Beams, Slabs)
// - grasshopper: always group by purpose and add a short Scribble per group
";

        const string WorkflowTemplate = @"---
when: one sentence — when should the AI use this workflow? (be specific: the request, the context, the plug-in)
---
# <Workflow name>

1. First step (what to check / ask / read first)
2. Main steps (components or scripts to use, in order)
3. Checks before saying it is done (counts, units, view)

Rules
- Things that must always / never be done in this workflow.
";

        const string PluginTemplate = @"---
plugin: CategoryName            # component category as Grasshopper shows it; several: A, B
when: CategoryName components are used
---
# <Plug-in> guide

## Rules
- Units / tolerances the plug-in assumes.
- Which components to use (and which outdated ones to avoid).
- Settings that are easy to get wrong ($state keys, dropdowns, menu items).
- How to check the result (which output, what range is plausible).

## Example (shorthand)
```
x = Some Component @CategoryName (In=…) {$Key=""Option""}
```
";
    }
}
