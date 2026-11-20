using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Nextra.Host
{
    /// Skills for the assistant's LLM mode: folders with a SKILL.md (front matter name + description, then instructions), the
    /// same format as Claude's skills. Kept in the user folder (user/skills/<name>/), so they survive updates.
    /// Added from a dropped / picked file (.zip, .skill, .md) or a URL (a GitHub repository or marketplace, a .zip / .skill,
    /// a raw SKILL.md). Every SKILL.md found inside is installed as one skill. Nothing in a skill is run on install: it is text
    /// the model reads when a request needs it (read_skill). Switched on / off per skill (config assistant_skills_off).
    static class SkillStore
    {
        const long MaxBytes = 20 * 1024 * 1024;
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        public static string Dir
        {
            get { var d = Path.Combine(Server.AppDir, "user", "skills"); Directory.CreateDirectory(d); return d; }
        }

        static HashSet<string> Off()
        {
            var raw = Config.GetString("assistant_skills_off", "");
            return new HashSet<string>(raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        }

        public static JsonArray List()
        {
            var off = Off();
            var arr = new JsonArray();
            foreach (var d in Directory.GetDirectories(Dir).OrderBy(x => x))
            {
                var md = Path.Combine(d, "SKILL.md");
                if (!File.Exists(md)) continue;
                var (name, desc) = Front(File.ReadAllText(md));
                var id = Path.GetFileName(d);
                var src = File.Exists(Path.Combine(d, ".source")) ? File.ReadAllText(Path.Combine(d, ".source")).Trim() : "";
                arr.Add(new JsonObject { ["id"] = id, ["name"] = name ?? id, ["description"] = desc ?? "", ["enabled"] = !off.Contains(id), ["source"] = src });
            }
            return arr;
        }

        /// front matter name / description of a SKILL.md (or the first heading / line)
        static (string, string) Front(string text)
        {
            string name = null, desc = null;
            var m = Regex.Match(text, @"^---\s*\n(.*?)\n---", RegexOptions.Singleline);
            if (m.Success)
            {
                foreach (var line in m.Groups[1].Value.Split('\n'))
                {
                    var kv = Regex.Match(line, @"^(name|description):\s*(.*)$");
                    if (!kv.Success) continue;
                    var v = kv.Groups[2].Value.Trim().Trim('"', '\'');
                    if (kv.Groups[1].Value == "name") name = v; else desc = v;
                }
            }
            if (name == null) { var h = Regex.Match(text, @"^#\s+(.+)$", RegexOptions.Multiline); if (h.Success) name = h.Groups[1].Value.Trim(); }
            return (name, desc);
        }

        public static void SetEnabled(string id, bool on)
        {
            var off = Off();
            if (on) off.Remove(id); else off.Add(id);
            Config.Set("assistant_skills_off", string.Join("\n", off));
        }

        public static void Remove(string id)
        {
            var d = Safe(id);
            if (Directory.Exists(d)) Directory.Delete(d, true);
        }

        /// SKILL.md or a file the skill refers to (relative path inside the skill folder)
        public static string Read(string id, string file)
        {
            var d = Safe(id);
            var p = Path.GetFullPath(Path.Combine(d, string.IsNullOrWhiteSpace(file) ? "SKILL.md" : file));
            if (!p.StartsWith(d, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("outside the skill");
            if (!File.Exists(p))
            {
                var files = Directory.GetFiles(d, "*", SearchOption.AllDirectories).Select(f => f.Substring(d.Length + 1).Replace('\\', '/')).Where(f => !f.StartsWith(".")).Take(80);
                return $"no file '{file}' in skill {id}. Files: {string.Join(", ", files)}";
            }
            var t = File.ReadAllText(p);
            return t.Length > 60000 ? t.Substring(0, 60000) + "\n… (cut)" : t;
        }

        // ---- Nextra plug-ins: one switch connects a plug-in's AI guides (a skill) and its MCP server (a connector) -------------
        /// Nextra Design: the installed Yak package's agent/ folder (guides) and its runtime's nextra_design.mcp server
        public static string NextraDesignAgentDir()
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McNeel", "Rhinoceros", "packages", "8.0", "nextra-design");
                if (!Directory.Exists(root)) return null;
                return Directory.GetDirectories(root).OrderByDescending(d => File.GetLastWriteTimeUtc(d)).Select(d => Path.Combine(d, "agent")).FirstOrDefault(Directory.Exists);
            }
            catch { return null; }
        }

        public static string NextraDesignPython()
        {
            var bases = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextraDesign", "runtime"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextraDesign", "runtime"),
            };
            foreach (var b in bases)
                foreach (var exe in new[] { Path.Combine(b, "python.exe"), Path.Combine(b, "bin", "python3"), Path.Combine(b, "bin", "python") })
                    if (File.Exists(exe) && Directory.Exists(Path.Combine(b, OperatingSystem.IsWindows() ? "Lib" : "lib"))) return exe;
            return null;
        }


        /// the "Download the AI skill" link of https://nextra.studio/tools/design/install
        public const string NextraDesignSkillUrl = "https://nextra.studio/get/design/skill";

        /// skills installed from a web address: download again and replace the ones whose download changed.
        /// force=false: at most once a day (the panel calls it when it opens)
        public static async System.Threading.Tasks.Task<string> UpdateAll(bool force)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (!force && Config.GetString("skills_checked", "") == today) return "checked today";
            Config.Set("skills_checked", today);
            var updated = new List<string>();
            var failed = new List<string>();
            foreach (var d in Directory.GetDirectories(Dir))
            {
                var srcFile = Path.Combine(d, ".source");
                if (!File.Exists(srcFile)) continue;
                var src = File.ReadAllText(srcFile).Trim();
                if (!src.StartsWith("https://")) continue;
                try
                {
                    var bytes = await Download(src).ConfigureAwait(false);
                    var old = File.Exists(Path.Combine(d, ".sha")) ? File.ReadAllText(Path.Combine(d, ".sha")).Trim() : "";
                    if (old == Sha(bytes)) continue;
                    var id = Path.GetFileName(d);
                    bool off = Off().Contains(id);
                    AddFile(src.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? id + ".md" : "download.zip", bytes, src);
                    if (off) SetEnabled(id, false);
                    updated.Add(id);
                }
                catch { failed.Add(Path.GetFileName(d)); }
            }
            return (updated.Count == 0 ? "all skills are up to date" : "updated: " + string.Join(", ", updated)) + (failed.Count > 0 ? " (could not check: " + string.Join(", ", failed) + ")" : "");
        }

        static async System.Threading.Tasks.Task<byte[]> Download(string url)
        {
            var u = url;
            var gh = Regex.Match(url, @"^https://github\.com/([\w.\-]+)/([\w.\-]+?)(?:\.git)?(?:/tree/([\w.\-/]+))?/?$");
            if (gh.Success) u = $"https://codeload.github.com/{gh.Groups[1].Value}/{gh.Groups[2].Value}/zip/{(gh.Groups[3].Success ? gh.Groups[3].Value : "HEAD")}";
            using var res = await Http.GetAsync(u).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        public static JsonObject Integrations() => new JsonObject
        {
            ["nextraDesign"] = new JsonObject { ["installed"] = NextraDesignAgentDir() != null, ["runtime"] = NextraDesignPython() != null, ["on"] = Directory.Exists(Path.Combine(Dir, "nextra-design")) },
        };

        /// switch Nextra Design on (its guides as the skill "nextra-design", its MCP server as the connector "nextra-design") or off
        public static string SetNextraDesign(bool on)
        {
            var skill = Path.Combine(Dir, "nextra-design");
            if (!on)
            {
                if (Directory.Exists(skill)) Directory.Delete(skill, true);
                Connectors.Remove("nextra-design");
                return "Nextra Design disconnected";
            }
            if (NextraDesignAgentDir() == null) throw new InvalidOperationException("Nextra Design is not installed in Rhino (PackageManager: nextra-design)");
            // the AI skill of the install page (https://nextra.studio/tools/design/install): always the current release, so the
            // update check keeps it current too
            var msg = AddUrl(NextraDesignSkillUrl).GetAwaiter().GetResult();
            var installed = Regex.Match(msg, @"added ([^;]+)").Groups[1].Value.Split(',').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0);
            if (installed != null && installed != "nextra-design")
            {
                // keep one known name for the switch
                if (Directory.Exists(skill)) Directory.Delete(skill, true);
                Directory.Move(Path.Combine(Dir, installed), skill);
            }
            SetEnabled("nextra-design", true);
            var py = NextraDesignPython();
            if (py != null) Connectors.Add("nextra-design", py, new[] { "-m", "nextra_design.mcp" }, null);
            return py != null ? "Nextra Design connected: its guides (skill) and its tools (connector)" : "Nextra Design guides connected; its tools need the Nextra Design runtime (open Nextra Design once to install it)";
        }

        static string Safe(string id)
        {
            var clean = Regex.Replace(id ?? "", @"[^\w.\-]", "-").Trim('.', '-');
            if (clean.Length == 0) throw new ArgumentException("no skill name");
            return Path.Combine(Dir, clean);
        }

        /// a dropped or picked file: .zip / .skill (one or more skill folders inside) or a single .md
        public static string AddFile(string fileName, byte[] bytes, string source)
        {
            if (bytes.Length > MaxBytes) throw new InvalidOperationException("the file is larger than 20 MB");
            var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            if (ext == ".md")
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                var (name, _) = Front(text);
                var d = Safe(name ?? Path.GetFileNameWithoutExtension(fileName));
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "SKILL.md"), text);
                File.WriteAllText(Path.Combine(d, ".source"), source ?? fileName);
                return "added " + Path.GetFileName(d);
            }
            return AddZip(bytes, source ?? fileName);
        }

        static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));

        static string AddZip(byte[] bytes, string source)
        {
            var added = new List<string>();
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            long total = 0;
            // every folder that holds a SKILL.md is one skill (a marketplace or plugin repository has several)
            var roots = zip.Entries.Where(e => Path.GetFileName(e.FullName).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FullName.Substring(0, e.FullName.Length - e.Name.Length)).Distinct().ToList();
            if (roots.Count == 0) throw new InvalidOperationException("no SKILL.md in the archive");
            foreach (var root in roots)
            {
                var skillMd = zip.Entries.First(e => e.FullName == root + "SKILL.md" || e.FullName.Equals(root + "skill.md", StringComparison.OrdinalIgnoreCase));
                string text; using (var r = new StreamReader(skillMd.Open())) text = r.ReadToEnd();
                var (name, _) = Front(text);
                var folder = root.TrimEnd('/').Split('/').LastOrDefault(x => x.Length > 0);
                var d = Safe(name ?? folder ?? "skill");
                if (Directory.Exists(d)) Directory.Delete(d, true);
                Directory.CreateDirectory(d);
                foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(root) && !e.FullName.EndsWith("/")))
                {
                    var rel = e.FullName.Substring(root.Length);
                    var target = Path.GetFullPath(Path.Combine(d, rel));
                    if (!target.StartsWith(d, StringComparison.OrdinalIgnoreCase)) continue;      // no paths out of the folder
                    total += e.Length;
                    if (total > MaxBytes * 3) throw new InvalidOperationException("the archive unpacks to more than 60 MB");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                }
                File.WriteAllText(Path.Combine(d, ".source"), source ?? "");
                File.WriteAllText(Path.Combine(d, ".sha"), Sha(bytes));
                added.Add(Path.GetFileName(d));
            }
            // a plugin brings its MCP servers too (.mcp.json at its root): they become connectors
            var conns = new List<string>();
            foreach (var e in zip.Entries.Where(e => e.Name.Equals(".mcp.json", StringComparison.OrdinalIgnoreCase)))
            {
                try { using var r = new StreamReader(e.Open()); conns.AddRange(Connectors.AddFromMcpJson(r.ReadToEnd())); } catch { }
            }
            return "added " + string.Join(", ", added) + (conns.Count > 0 ? "; connectors: " + string.Join(", ", conns) : "");
        }

        /// a URL: GitHub repository (whole repo: every SKILL.md), .zip / .skill, or a raw SKILL.md
        public static async Task<string> AddUrl(string url)
        {
            if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) || u.Scheme != "https") throw new InvalidOperationException("an https address is needed");
            var gh = Regex.Match(u.ToString(), @"^https://github\.com/([\w.\-]+)/([\w.\-]+?)(?:\.git)?(?:/tree/([\w.\-/]+))?/?$");
            string download = u.ToString();
            if (gh.Success) download = $"https://codeload.github.com/{gh.Groups[1].Value}/{gh.Groups[2].Value}/zip/{(gh.Groups[3].Success ? gh.Groups[3].Value : "HEAD")}";
            using var res = await Http.GetAsync(download, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"{u.Host} answered {(int)res.StatusCode}{(gh.Success ? " (a private repository needs a download as .zip instead)" : "")}");
            if (res.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("the download is larger than 20 MB");
            var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var name = u.AbsolutePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(u.AbsolutePath) : "download.zip";
            return AddFile(name, bytes, u.ToString());
        }
    }
}
