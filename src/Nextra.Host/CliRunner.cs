using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nextra.Host
{
    /// CLI mode of the assistant: the person's own AI command-line tool (Claude Code `claude`, OpenAI `codex`, Google `gemini`)
    /// with their own subscription does the thinking; it works Rhino through NextraRhinoGh's MCP bridge (and the connectors
    /// the person added), exactly as the desktop apps do. Only those MCP tools are pre-approved. The request goes in on stdin
    /// (never on a command line); the CLI's JSON event stream comes back as steps the panel shows like LLM mode.
    static class CliRunner
    {
        public static readonly string[] Known = { "claude", "codex", "gemini" };
        public static string Label(string id) => id == "claude" ? "Claude Code" : id == "codex" ? "Codex (OpenAI)" : id == "gemini" ? "Gemini CLI" : id;

        class Run
        {
            public Process P;
            public readonly ConcurrentQueue<JsonObject> Events = new ConcurrentQueue<JsonObject>();
            public volatile bool Done;
            public string Final = "", Error;
            public string TempDir;
            public DateTime Started = DateTime.Now;
        }
        static readonly ConcurrentDictionary<string, Run> Runs = new ConcurrentDictionary<string, Run>();
        static int _next;

        // ---- finding the CLIs ------------------------------------------------------------------------------------------------
        static Dictionary<string, string> _found;

        /// installed CLIs: id -> path (cached; Refresh re-reads)
        public static Dictionary<string, string> Detect(bool refresh = false)
        {
            if (_found != null && !refresh) return _found;
            var d = new Dictionary<string, string>();
            foreach (var id in Known)
            {
                var p = Which(id);
                if (p != null) d[id] = p;
            }
            return _found = d;
        }

        static bool Windows => OperatingSystem.IsWindows();

        static string Which(string name)
        {
            try
            {
                var psi = Windows ? new ProcessStartInfo("where.exe", name) : new ProcessStartInfo("/bin/zsh", $"-lc \"command -v {name}\"");
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.UseShellExecute = false; psi.CreateNoWindow = true;
                using var p = Process.Start(psi);
                var o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                var lines = o.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (Windows) return lines.FirstOrDefault(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault(x => x.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
                return lines.FirstOrDefault();
            }
            catch { return null; }
        }

        /// (file, args) to start a CLI: on Windows an npm .cmd shim is resolved to `node <script>`, so every argument is passed
        /// as a separate argument (no shell quoting of config values or paths)
        static (string, List<string>) Launcher(string path)
        {
            if (Windows && path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                var text = File.ReadAllText(path);
                var m = Regex.Match(text, @"%(?:~)?dp0%?\\?([^""%]+?\.(?:js|mjs|cjs))", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var script = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), m.Groups[1].Value.TrimStart('\\')));
                    var node = Path.Combine(Path.GetDirectoryName(path), "node.exe");
                    return (File.Exists(node) ? node : "node", new List<string> { script });
                }
            }
            return (path, new List<string>());
        }

        static string NodeAndPathEnv()
        {
            if (Windows) return Environment.GetEnvironmentVariable("PATH");
            // Rhino on Mac is started by the Finder, without the login shell's PATH (node, npm globals, ~/.local/bin)
            try
            {
                var psi = new ProcessStartInfo("/bin/zsh", "-lc \"echo $PATH\"") { RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                var o = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                return o.Length > 0 ? o : Environment.GetEnvironmentVariable("PATH");
            }
            catch { return Environment.GetEnvironmentVariable("PATH"); }
        }

        // ---- running ---------------------------------------------------------------------------------------------------------
        /// MCP servers for the CLI: the NextraRhinoGh bridge, then the person's connectors
        static List<(string name, string command, List<string> args, Dictionary<string, string> env)> Servers()
        {
            var list = new List<(string, string, List<string>, Dictionary<string, string>)>();
            var bridge = NextraHostPlugIn.BridgePath;
            if (File.Exists(bridge)) list.Add(("nextra-rhinogh", "node", new List<string> { bridge.Replace('\\', '/') }, new Dictionary<string, string>()));
            foreach (var c in Connectors.Enabled()) list.Add((c.Name, c.Command, c.Args, c.Env));
            return list;
        }

        public static string Start(string cli, string prompt, string model)
        {
            var found = Detect();
            if (!found.TryGetValue(cli, out var path)) throw new InvalidOperationException($"{Label(cli)} is not installed (or not on the PATH)");
            var servers = Servers();
            if (servers.Count == 0 || servers[0].name != "nextra-rhinogh") throw new InvalidOperationException("the NextraRhinoGh bridge is missing: reinstall NextraRhinoGh");
            var (file, args) = Launcher(path);
            var tmp = Path.Combine(Path.GetTempPath(), "nexiy-cli-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = tmp,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PATH"] = NodeAndPathEnv();
            psi.Environment["NO_COLOR"] = "1";
            var allowed = servers.Select(s => s.name).ToList();

            if (cli == "claude")
            {
                var cfg = new JsonObject();
                var ms = new JsonObject();
                foreach (var s in servers) ms[s.name] = ServerJson(s.command, s.args, s.env);
                cfg["mcpServers"] = ms;
                var cfgPath = Path.Combine(tmp, "mcp.json");
                File.WriteAllText(cfgPath, cfg.ToJsonString());
                foreach (var a in new[] { "-p", "--output-format", "stream-json", "--verbose", "--mcp-config", cfgPath, "--strict-mcp-config" }) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("--allowedTools");
                foreach (var n in allowed) psi.ArgumentList.Add("mcp__" + n);
                if (!string.IsNullOrWhiteSpace(model)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model); }
            }
            else if (cli == "codex")
            {
                foreach (var a in new[] { "exec", "--json", "--skip-git-repo-check", "-s", "read-only", "-c", "approval_policy=\"never\"" }) psi.ArgumentList.Add(a);
                foreach (var s in servers)
                {
                    psi.ArgumentList.Add("-c"); psi.ArgumentList.Add($"mcp_servers.{s.name}.command={Toml(s.command)}");
                    psi.ArgumentList.Add("-c"); psi.ArgumentList.Add($"mcp_servers.{s.name}.args=[{string.Join(",", s.args.Select(Toml))}]");
                    psi.ArgumentList.Add("-c"); psi.ArgumentList.Add($"mcp_servers.{s.name}.default_tools_approval_mode=\"approve\"");
                    foreach (var kv in s.env) { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add($"mcp_servers.{s.name}.env.{kv.Key}={Toml(kv.Value)}"); }
                }
                if (!string.IsNullOrWhiteSpace(model)) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(model); }
                psi.ArgumentList.Add("-");
            }
            else if (cli == "gemini")
            {
                Directory.CreateDirectory(Path.Combine(tmp, ".gemini"));
                var ms = new JsonObject();
                foreach (var s in servers) { var j = ServerJson(s.command, s.args, s.env); j["trust"] = true; ms[s.name] = j; }
                var settings = new JsonObject { ["mcpServers"] = ms };
                var key = KeyStore.Get("gemini");
                if (!string.IsNullOrEmpty(key))
                {
                    psi.Environment["GEMINI_API_KEY"] = key;    // the person's Gemini key (their own CLI sign-in otherwise)
                    settings["security"] = new JsonObject { ["auth"] = new JsonObject { ["selectedType"] = "gemini-api-key" } };
                }
                File.WriteAllText(Path.Combine(tmp, ".gemini", "settings.json"), settings.ToJsonString());
                foreach (var a in new[] { "-o", "stream-json", "-m", string.IsNullOrWhiteSpace(model) ? "gemini-3.8-flash" : model, "-p", "Do what the message above asks." }) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("--allowed-mcp-server-names");
                foreach (var n in allowed) psi.ArgumentList.Add(n);
            }
            else throw new InvalidOperationException("unknown CLI " + cli);

            var run = new Run { TempDir = tmp };
            var id = "cli" + System.Threading.Interlocked.Increment(ref _next);
            Runs[id] = run;
            var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + Label(cli));
            run.P = p;
            p.StandardInput.Write(prompt);
            p.StandardInput.Close();
            var err = new StringBuilder();
            p.ErrorDataReceived += (s, e) => { if (e.Data != null && err.Length < 4000) err.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null) Parse(cli, line, run);
                    p.WaitForExit();
                    if (p.ExitCode != 0 && string.IsNullOrEmpty(run.Final))
                        run.Error = Short(Regex.Replace(err.ToString(), @"(sk-|AIza)[\w\-]{6,}", "$1…"), 400) ?? $"{Label(cli)} stopped (exit {p.ExitCode})";
                }
                catch (Exception e) { run.Error = e.Message; }
                finally { run.Done = true; try { Directory.Delete(tmp, true); } catch { } }
            });
            return id;
        }

        static JsonObject ServerJson(string command, List<string> args, Dictionary<string, string> env)
        {
            var j = new JsonObject { ["command"] = command, ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray()) };
            if (env.Count > 0) { var e = new JsonObject(); foreach (var kv in env) e[kv.Key] = kv.Value; j["env"] = e; }
            return j;
        }

        static string Toml(string s) => "\"" + (s ?? "").Replace("\\", "/").Replace("\"", "\\\"") + "\"";
        static string Short(string s, int n) { s = (s ?? "").Trim(); return s.Length == 0 ? null : s.Length > n ? s.Substring(s.Length - n) : s; }

        static void Emit(Run r, string kind, string text, string tool = null)
        {
            var o = new JsonObject { ["kind"] = kind, ["text"] = text ?? "" };
            if (tool != null) o["tool"] = tool;
            r.Events.Enqueue(o);
        }

        /// one line of the CLI's JSON stream -> steps (tool, text) and the final answer
        static void Parse(string cli, string line, Run r)
        {
            JsonNode j;
            try { j = JsonNode.Parse(line); } catch { return; }
            if (j == null) return;
            var type = (string)j["type"];
            if (cli == "claude")
            {
                if (type == "assistant")
                    foreach (var c in j["message"]?["content"]?.AsArray() ?? new JsonArray())
                    {
                        var ct = (string)c?["type"];
                        if (ct == "tool_use") Emit(r, "tool", Short(c["input"]?.ToJsonString(), 160), ToolName((string)c["name"]));
                        else if (ct == "text") Emit(r, "text", (string)c["text"]);
                    }
                else if (type == "result") { r.Final = (string)j["result"] ?? r.Final; if ((bool?)j["is_error"] == true) r.Error = r.Final; }
            }
            else if (cli == "codex")
            {
                var it = j["item"];
                if (type == "item.completed" && it != null)
                {
                    var t = (string)it["type"];
                    if (t == "mcp_tool_call") Emit(r, "tool", Short(it["arguments"]?.ToJsonString(), 160), (string)it["tool"]);
                    else if (t == "agent_message") { Emit(r, "text", (string)it["text"]); r.Final = (string)it["text"]; }
                    else if (t == "command_execution") Emit(r, "tool", Short((string)it["command"], 120), "shell (read-only)");
                }
                else if (type == "turn.failed" || type == "error") r.Error = Short(j.ToJsonString(), 300);
            }
            else if (cli == "gemini")
            {
                if (type == "tool_use") Emit(r, "tool", Short(j["parameters"]?.ToJsonString(), 160), ToolName((string)j["tool_name"]));
                else if (type == "message" && (string)j["role"] == "assistant") r.Final += (string)j["content"];
                else if (type == "result" && (string)j["status"] != "success") r.Error = Short(j.ToJsonString(), 300);
            }
        }

        static string ToolName(string n) => Regex.Replace(n ?? "", @"^mcp_{1,2}[\w\-]+?_{1,2}", "");

        /// new steps since the last poll, and the answer when finished
        public static JsonObject Poll(string id)
        {
            if (!Runs.TryGetValue(id, out var r)) return new JsonObject { ["done"] = true, ["error"] = "unknown run" };
            var arr = new JsonArray();
            while (r.Events.TryDequeue(out var e)) arr.Add(e);
            var o = new JsonObject { ["events"] = arr, ["done"] = r.Done, ["seconds"] = (int)(DateTime.Now - r.Started).TotalSeconds };
            if (r.Done)
            {
                o["final"] = r.Final;
                if (r.Error != null) o["error"] = r.Error;
                Runs.TryRemove(id, out _);
            }
            return o;
        }

        public static void Cancel(string id)
        {
            if (Runs.TryGetValue(id, out var r)) try { r.P?.Kill(true); } catch { }
        }
    }

    /// The person's MCP connectors (other apps' tools for the assistant), kept in config "assistant_connectors" as JSON:
    /// [{name, command, args[], env{}, enabled}]. Used by CLI mode (added to the CLI's MCP servers).
    static class Connectors
    {
        public class Item { public string Name, Command; public List<string> Args = new List<string>(); public Dictionary<string, string> Env = new Dictionary<string, string>(); public bool Enabled = true; }

        public static List<Item> All()
        {
            var list = new List<Item>();
            try
            {
                if (JsonNode.Parse(Config.GetString("assistant_connectors", "[]")) is JsonArray a)
                    foreach (var n in a)
                    {
                        var it = new Item { Name = (string)n["name"], Command = (string)n["command"], Enabled = (bool?)n["enabled"] ?? true };
                        foreach (var x in n["args"]?.AsArray() ?? new JsonArray()) it.Args.Add((string)x);
                        if (n["env"] is JsonObject e) foreach (var kv in e) it.Env[kv.Key] = (string)kv.Value;
                        if (!string.IsNullOrWhiteSpace(it.Name) && !string.IsNullOrWhiteSpace(it.Command)) list.Add(it);
                    }
            }
            catch { }
            return list;
        }

        public static IEnumerable<Item> Enabled() => All().Where(c => c.Enabled);

        public static void Save(List<Item> items)
        {
            var a = new JsonArray();
            foreach (var c in items)
            {
                var env = new JsonObject(); foreach (var kv in c.Env) env[kv.Key] = kv.Value;
                a.Add(new JsonObject { ["name"] = c.Name, ["command"] = c.Command, ["args"] = new JsonArray(c.Args.Select(x => (JsonNode)x).ToArray()), ["env"] = env, ["enabled"] = c.Enabled });
            }
            Config.Set("assistant_connectors", a.ToJsonString());
        }

        public static JsonArray ListJson()
        {
            var a = new JsonArray();
            foreach (var c in All()) a.Add(new JsonObject { ["name"] = c.Name, ["command"] = c.Command + " " + string.Join(" ", c.Args), ["enabled"] = c.Enabled });
            return a;
        }

        /// add or replace one connector; name: letters, digits, - and _
        public static void Add(string name, string command, IEnumerable<string> args, Dictionary<string, string> env)
        {
            name = Regex.Replace(name ?? "", @"[^\w\-]", "-").Trim('-');
            if (name.Length == 0 || name == "nextra-rhinogh") throw new ArgumentException("give the connector a name");
            if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("give the command that starts it (e.g. node, python, npx)");
            var all = All().Where(c => c.Name != name).ToList();
            all.Add(new Item { Name = name, Command = command.Trim(), Args = args?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>(), Env = env ?? new Dictionary<string, string>() });
            Save(all);
        }

        public static void Remove(string name) => Save(All().Where(c => c.Name != name).ToList());
        public static void Toggle(string name, bool on) { var all = All(); foreach (var c in all) if (c.Name == name) c.Enabled = on; Save(all); }

        /// connectors from a plugin's .mcp.json (or a plugin.json with mcpServers) inside a downloaded skill/plugin archive
        public static List<string> AddFromMcpJson(string json)
        {
            var added = new List<string>();
            var root = JsonNode.Parse(json);
            var servers = root?["mcpServers"] as JsonObject ?? root as JsonObject;
            if (servers == null) return added;
            foreach (var kv in servers)
            {
                var s = kv.Value;
                var cmd = (string)s?["command"];
                if (string.IsNullOrWhiteSpace(cmd)) continue;    // remote (url) servers: not yet
                var args = (s["args"]?.AsArray() ?? new JsonArray()).Select(x => (string)x).ToList();
                var env = new Dictionary<string, string>();
                if (s["env"] is JsonObject e) foreach (var ev in e) env[ev.Key] = (string)ev.Value;
                Add(kv.Key, cmd, args, env);
                added.Add(kv.Key);
            }
            return added;
        }
    }
}
