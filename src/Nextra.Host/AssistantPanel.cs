using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace Nextra.Host
{
    /// The decision-API assistant: a borderless Nexiy window that is the assistant page and nothing else (header, close and
    /// resize grip are drawn by the page, so the whole window is one glass surface). The page is assistant/assistant.html,
    /// built from the closed-source nextra-assistant-rhino project, in an Eto WebView (WebView2 on Windows, WKWebView on Mac).
    /// Opened from Nexiy's menu once NextraDecisionApi is set up.
    /// See-through: Windows 11 acrylic behind a transparent WebView2 (WindowsQuirks.Backdrop), Mac window alpha.
    /// Moving / resizing: the page asks; Windows hands the drag to the system's own move / size loop (PlatformQuirks.SystemDrag),
    /// Mac moves the window by the pointer deltas the page sends.
    /// The page has no network access of its own. It queues messages; this window drains the queue every 50 ms
    /// (window.__nxDrain, URL-encoded) and answers each one (window.__nxReply):
    ///   ai → AiProxy (adds the person's key; the key never reaches the page) · tool → Dispatcher.Call (the MCP clients' queue)
    ///   settings / savePrefs / state / close / drag / resize / moveBy
    /// Threading: the WebView calls back on the UI thread, and Dispatcher.Call waits for the UI thread, so every ai / tool
    /// message runs on a background task and only the reply is marshalled back (otherwise Rhino would deadlock).
    class AssistantPanel : Form
    {
        static AssistantPanel _instance;
        readonly WebView _web = new WebView();
        readonly UITimer _poll = new UITimer { Interval = 0.05 };
        readonly bool _dark;
        string _backdrop = "none";   // acrylic (Windows 11) | alpha (Mac) | none: how see-through the window is
        bool _draining, _loaded;
        DateTime _drainStart;

        public static bool IsOpen => _instance != null;

        public static void Toggle()
        {
            if (_instance != null) { _instance.AnimatedClose(); return; }
            var html = FindPage();
            if (html == null)
            {
                RhinoApp.WriteLine("NextraRhinoGh: the assistant page is missing (assistant/assistant.html next to the plug-in). Reinstall NextraRhinoGh.");
                return;
            }
            _instance = new AssistantPanel(html);
            var p = _instance;
            var target = p.Target();
            var from = StatusDot.ScreenBounds;
            if (from is Rectangle nx)
            {
                // grow out of Nexiy: start at Nexiy's spot and size, open to the panel's place
                p.MinimumSize = new Size(1, 1);
                p.Bounds = nx;
            }
            else p.Bounds = target;
            p.Show();
            PlatformQuirks.RoundCorners(p);
            p._backdrop = PlatformQuirks.Backdrop(p, p._web, p._dark);
            PlatformQuirks.KeepEditKeys(p._web);     // Ctrl+C / V / X / A / Z in the page are the page's, not Rhino's
            if (from is Rectangle nx2) p.Animate(nx2, target, 0.2, () => p.MinimumSize = new Size(320, 380));
        }

        /// the page shipped next to the .rhp (Yak package), or config assistant_html (development)
        static string FindPage()
        {
            var dir = Path.GetDirectoryName(typeof(AssistantPanel).Assembly.Location) ?? "";
            var p = Path.Combine(dir, "assistant", "assistant.html");
            if (File.Exists(p)) return p;
            var dev = Config.GetString("assistant_html", null);
            return dev != null && File.Exists(dev) ? dev : null;
        }

        static bool RhinoDark()
        {
            try { return (bool)(typeof(Rhino.Runtime.HostUtils).GetProperty("RunningInDarkMode")?.GetValue(null) ?? false); } catch { return false; }
        }

        AssistantPanel(string page)
        {
            _dark = RhinoDark();
            Title = "Nexiy";
            WindowStyle = WindowStyle.None;
            ClientSize = new Size(420, 640);
            MinimumSize = new Size(320, 380);
            Resizable = true;
            Minimizable = false;
            Maximizable = false;
            ShowInTaskbar = false;
            BackgroundColor = _dark ? Color.FromArgb(0x1b, 0x21, 0x1e) : Color.FromArgb(0xf1, 0xf4, 0xee);   // only seen before the page draws
            try { Owner = Rhino.UI.RhinoEtoApp.MainWindow; } catch { }
            Content = _web;

            _web.DocumentLoading += (s, e) =>
            {
                // after the page has loaded nothing else is navigated to: a link opens in the browser instead. (Eto may serve
                // LoadHtml from a local address, so loads before that are never touched.)
                if (!_loaded) return;
                var u = e.Uri;
                if (u == null || u.IsLoopback || u.Scheme == "about" || u.Scheme == "data") { _loaded = false; return; }
                e.Cancel = true;
                if (u.Scheme == "https" || u.Scheme == "http") { try { Application.Instance.Open(u.ToString()); } catch { } }
            };
            _web.DocumentLoaded += (s, e) => { _loaded = true; _draining = false; };
            _web.LoadHtml(File.ReadAllText(page).Replace("<html lang=\"en\">", $"<html lang=\"en\" data-theme=\"{(_dark ? "dark" : "light")}\">"));
            _poll.Elapsed += async (s, e) => await Drain();
            _poll.Start();
        }

        // ---- open out of Nexiy, close back into it ------------------------------------------------------------------------
        UITimer _anim;
        bool _closing;

        /// where the panel opens: above Nexiy's corner of the Rhino window
        Rectangle Target()
        {
            var size = new Size(420, 640);
            try
            {
                var b = Owner?.Bounds ?? Rectangle.Empty;
                if (b.Width > 0) return new Rectangle(new Point(b.X + b.Width - size.Width - 60, Math.Max(b.Y + 80, b.Y + b.Height - size.Height - 90)), size);
            }
            catch { }
            return new Rectangle(new Point(200, 200), size);
        }

        /// the window's bounds from `a` to `b` over `sec` seconds (eased), then `done`
        void Animate(Rectangle a, Rectangle b, double sec, Action done)
        {
            _anim?.Stop();
            var t0 = DateTime.Now;
            _anim = new UITimer { Interval = 0.015 };
            _anim.Elapsed += (s, e) =>
            {
                double t = Math.Min(1, (DateTime.Now - t0).TotalSeconds / sec);
                double k = 1 - Math.Pow(1 - t, 3);      // ease out
                int L(int x, int y) => (int)Math.Round(x + (y - x) * k);
                try { Bounds = new Rectangle(L(a.X, b.X), L(a.Y, b.Y), Math.Max(1, L(a.Width, b.Width)), Math.Max(1, L(a.Height, b.Height))); } catch { }
                if (t >= 1) { _anim.Stop(); _anim = null; done?.Invoke(); }
            };
            _anim.Start();
        }

        /// shrink back into Nexiy, then close (straight away when Nexiy is hidden)
        public void AnimatedClose()
        {
            if (_closing) return;
            _closing = true;
            if (StatusDot.ScreenBounds is Rectangle nx)
            {
                MinimumSize = new Size(1, 1);
                Animate(Bounds, nx, 0.16, () => { try { Close(); } catch { } });
            }
            else Close();
        }

        /// the setup guide on the website (the panel's "How to set up" button)
        public const string GuideUrl = "https://nextra.studio/journal/nexiy-assistant-in-rhino";

        static JsonObject KeyHints()
        {
            var o = new JsonObject();
            foreach (var p in KeyStore.Providers) o[p] = KeyStore.Hint(p);
            return o;
        }

        void Place()
        {
            try
            {
                var b = Owner?.Bounds ?? Rectangle.Empty;
                if (b.Width > 0) Location = new Point(b.X + b.Width - Width - 60, Math.Max(b.Y + 80, b.Y + b.Height - Height - 90));
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            _poll.Stop();
            StatusDot.Thinking = false;
            _instance = null;
            base.OnClosed(e);
        }

        async Task Drain()
        {
            // a script call made while the page navigates may never return: give up on it after 3 s instead of waiting forever
            if (_draining && (DateTime.Now - _drainStart).TotalSeconds > 3) _draining = false;
            if (_draining || !_loaded) return;
            _draining = true;
            _drainStart = DateTime.Now;
            try
            {
                // the page returns its queue URL-encoded (no quotes or backslashes): WebView2 hands script results back still
                // JSON-escaped, WKWebView does not; an encoded string reads the same on both
                var task = _web.ExecuteScriptAsync("return window.__nxDrain ? window.__nxDrain() : '';");
                if (await Task.WhenAny(task, Task.Delay(3000)) != task) return;
                var raw = (task.Result ?? "").Trim('"');
                if (raw.Length == 0 || raw == "null") return;
                if (!(JsonNode.Parse(Uri.UnescapeDataString(raw)) is JsonArray list)) return;
                foreach (var item in list)
                {
                    JsonObject msg;
                    try { msg = JsonNode.Parse((string)item) as JsonObject; } catch { continue; }
                    if (msg == null) continue;
                    // a message the plug-in cannot handle is answered with the error, never dropped (the page would wait forever)
                    try { Handle(msg); }
                    catch (Exception e)
                    {
                        if (msg["id"] is JsonValue v && v.TryGetValue<int>(out var mid)) Reply(mid, Dispatcher.Reply(false, "the plug-in could not handle " + (string)msg["kind"] + ": " + e.Message));
                    }
                }
            }
            catch { /* the page is (re)loading */ }
            finally { _draining = false; }
        }

        void Handle(JsonObject m)
        {
            int id = (int?)m["id"] ?? 0;
            switch ((string)m["kind"])
            {
                case "settings":
                    Reply(id, new JsonObject
                    {
                        ["decider"] = AssistantSettings.Decider,
                        ["chat"] = AssistantSettings.Chat,
                        ["keys"] = new JsonArray(Array.ConvertAll(KeyStore.Saved(), x => (JsonNode)x)),
                        ["prefs"] = Config.GetString("assistant_prefs", null),
                        ["dark"] = _dark,
                        ["backdrop"] = _backdrop,
                        ["nexiyLevel"] = StatusDot.Level,
                        ["nexiySpeed"] = StatusDot.SpeedIndex,
                        ["mode"] = AssistantSettings.Mode,
                        ["cli"] = AssistantSettings.Cli,
                        ["cliModel"] = AssistantSettings.CliModel,
                        ["clis"] = new JsonArray(CliRunner.Detect().Keys.Select(k => (JsonNode)k).ToArray()),
                        ["hints"] = KeyHints(),
                        ["guide"] = GuideUrl,
                        ["mac"] = !OperatingSystem.IsWindows(),
                    }.ToJsonString());
                    break;
                case "assistantSet":
                    foreach (var k in new[] { "mode", "decider", "chat", "cli", "cli_model" })
                        if (m[k] != null) Config.Set("assistant_" + k, (string)m[k]);
                    Config.Set("assistant_enabled", true);
                    Reply(id, "{}");
                    break;
                case "keySet":
                {
                    string provider = (string)m["provider"], key = (string)m["key"];
                    Task.Run(async () =>
                    {
                        JsonObject r;
                        try { KeyStore.Set(provider, key); var check = await AiProxy.Check(provider).ConfigureAwait(false); r = new JsonObject { ["ok"] = check == "works", ["text"] = check, ["hint"] = KeyStore.Hint(provider) }; }
                        catch (Exception e) { r = new JsonObject { ["ok"] = false, ["text"] = e.Message }; }
                        Reply(id, r.ToJsonString());
                    });
                    break;
                }
                case "keyRemove":
                    KeyStore.Clear((string)m["provider"]);
                    Reply(id, new JsonObject { ["hints"] = KeyHints() }.ToJsonString());
                    break;
                case "cliStart":
                    try
                    {
                        var pics = (m["images"] as JsonArray)?.Select(x => (string)x).Where(x => !string.IsNullOrEmpty(x)).ToList() ?? new List<string>();
                        Reply(id, new JsonObject { ["id"] = CliRunner.Start((string)m["cli"], (string)m["prompt"], (string)m["model"], pics) }.ToJsonString());
                    }
                    catch (Exception e) { Reply(id, new JsonObject { ["error"] = e.Message }.ToJsonString()); }
                    break;
                case "snip":
                {
                    // the panel steps aside, the person snips a part of the screen, the panel comes back with the picture and
                    // (Windows) the Rhino objects in it
                    var form = this;
                    form.Visible = false;
                    var t = new UITimer { Interval = 0.22 };
                    t.Elapsed += (s, e) =>
                    {
                        t.Stop();
                        Snip.Start(res =>
                        {
                            form.Visible = true;
                            try { form.BringToFront(); } catch { }
                            if (res?.Png == null) { Reply(id, new JsonObject { ["ok"] = false, ["cancelled"] = true }.ToJsonString()); return; }
                            var data = "data:image/png;base64," + Convert.ToBase64String(res.Png);
                            if (res.Screen == null) { Reply(id, new JsonObject { ["ok"] = true, ["data"] = data }.ToJsonString()); return; }
                            var r = res.Screen.Value;
                            Task.Run(() =>
                            {
                                string map = "";
                                try
                                {
                                    var args = new JsonObject { ["screenmap"] = new JsonArray(r.X, r.Y, r.Width, r.Height), ["quiet"] = true };
                                    var j = JsonNode.Parse(Dispatcher.Call("view", args.ToJsonString(), 15000)) as JsonObject;
                                    if ((bool?)j?["ok"] == true) map = (string)j["text"] ?? "";
                                }
                                catch { }
                                Reply(id, new JsonObject { ["ok"] = true, ["data"] = data, ["map"] = map }.ToJsonString());
                            });
                        });
                    };
                    t.Start();
                    break;
                }
                case "ghOpen":
                {
                    // is the Grasshopper window open (the panel shows its capture button only then); never loads Grasshopper
                    bool open = false;
                    try { open = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Grasshopper") && GhWindowOpen(); } catch { }
                    Reply(id, new JsonObject { ["open"] = open }.ToJsonString());
                    break;
                }
                case "capture":
                {
                    // a clean picture of the Rhino view or the Grasshopper canvas (no handle tags, no grid) for the chat: the
                    // view tool renders it, the page gets it as a data URL to draw on and send
                    var target = (string)m["target"] == "gh" ? "gh" : "rhino";
                    Task.Run(() =>
                    {
                        JsonObject r;
                        try
                        {
                            var args = new JsonObject { ["target"] = target, ["tags"] = "none", ["grid"] = false, ["size"] = new JsonArray(1600, 1000), ["quiet"] = true };
                            var res = JsonNode.Parse(Dispatcher.Call("view", args.ToJsonString(), 60000)) as JsonObject;
                            var path = (res?["images"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
                            if (path == null || !File.Exists(path)) r = new JsonObject { ["ok"] = false, ["text"] = (string)res?["text"] ?? "no picture" };
                            else
                            {
                                var mime = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png";
                                r = new JsonObject { ["ok"] = true, ["data"] = $"data:{mime};base64," + Convert.ToBase64String(File.ReadAllBytes(path)) };
                            }
                        }
                        catch (Exception e) { r = new JsonObject { ["ok"] = false, ["text"] = e.Message }; }
                        Reply(id, r.ToJsonString());
                    });
                    break;
                }
                case "cliPoll":
                    Reply(id, CliRunner.Poll((string)m["run"]).ToJsonString());
                    break;
                case "cliCancel":
                    CliRunner.Cancel((string)m["run"]);
                    Reply(id, "{}");
                    break;
                case "cliDetect":
                    Reply(id, new JsonObject { ["clis"] = new JsonArray(CliRunner.Detect(true).Keys.Select(k => (JsonNode)k).ToArray()) }.ToJsonString());
                    break;
                case "connectors":
                    Reply(id, new JsonObject { ["connectors"] = Connectors.ListJson() }.ToJsonString());
                    break;
                case "connectorAdd":
                    try
                    {
                        var cargs = (m["args"] as JsonArray)?.Select(x => (string)x).ToList() ?? new System.Collections.Generic.List<string>();
                        Connectors.Add((string)m["name"], (string)m["command"], cargs, null);
                        Reply(id, "{\"ok\":true}");
                    }
                    catch (Exception e) { Reply(id, new JsonObject { ["ok"] = false, ["text"] = e.Message }.ToJsonString()); }
                    break;
                case "connectorRemove":
                    Connectors.Remove((string)m["name"]);
                    Reply(id, "{}");
                    break;
                case "connectorToggle":
                    Connectors.Toggle((string)m["name"], (bool?)m["enabled"] ?? true);
                    Reply(id, "{}");
                    break;
                case "openUrl":
                {
                    var u = (string)m["url"];
                    if (u != null && (u.StartsWith("https://nextra.studio/") || u.StartsWith("https://github.com/") || u.StartsWith("https://console.anthropic.com") || u.StartsWith("https://platform.openai.com") || u.StartsWith("https://aistudio.google.com") || u.StartsWith("https://typesafe.ai")))
                        try { Application.Instance.Open(u); } catch { }
                    Reply(id, "{}");
                    break;
                }
                case "nexiySpeed":
                    StatusDot.SetSpeed((int?)m["speed"] ?? 1);
                    Reply(id, "{}");
                    break;
                case "nexiyLevel":
                    StatusDot.SetLevel((int?)m["level"] ?? 1);
                    Reply(id, "{}");
                    break;
                case "savePrefs":
                    Config.Set("assistant_prefs", (string)m["prefs"]);
                    Reply(id, "{}");
                    break;
                case "state":
                    if (m["thinking"] != null) StatusDot.Thinking = (bool)m["thinking"];
                    if (m["done"] != null) StatusDot.Celebrate();
                    if (m["failed"] != null) StatusDot.Fail();
                    Reply(id, "{}");
                    break;
                case "activate":
                    // the pointer is on a button of the inactive panel: activate it so the first click reaches the page
                    try { if (!HasFocus) { BringToFront(); Focus(); _web.Focus(); } } catch { }
                    Reply(id, "{}");
                    break;
                case "close":
                    Reply(id, "{}");
                    Application.Instance.AsyncInvoke(AnimatedClose);
                    break;
                case "drag":
                case "resize":
                {
                    // Windows: the system's own move / size loop (smooth, snaps); elsewhere the page moves the window by deltas
                    bool native = PlatformQuirks.SystemDrag(this, (string)m["kind"] == "resize");
                    Reply(id, native ? "{\"native\":true}" : "{\"native\":false}");
                    break;
                }
                case "moveBy":
                {
                    int dx = (int?)m["dx"] ?? 0, dy = (int?)m["dy"] ?? 0;
                    if ((bool?)m["resize"] == true) Size = new Size(Math.Max(MinimumSize.Width, Width + dx), Math.Max(MinimumSize.Height, Height + dy));
                    else Location = new Point(Location.X + dx, Location.Y + dy);
                    Reply(id, "{}");
                    break;
                }
                case "integrations":
                    Reply(id, SkillStore.Integrations().ToJsonString());
                    break;
                case "integrationSet":
                {
                    bool on = (bool?)m["on"] ?? false;
                    Task.Run(() =>      // downloads: never on the UI thread
                    {
                        try { Reply(id, new JsonObject { ["ok"] = true, ["text"] = SkillStore.SetNextraDesign(on), ["state"] = SkillStore.Integrations() }.ToJsonString()); }
                        catch (Exception e) { Reply(id, new JsonObject { ["ok"] = false, ["text"] = (e.InnerException ?? e).Message, ["state"] = SkillStore.Integrations() }.ToJsonString()); }
                    });
                    break;
                }
                case "skillsUpdate":
                {
                    bool force = (bool?)m["force"] ?? false;
                    Task.Run(async () =>
                    {
                        string t;
                        try { t = await SkillStore.UpdateAll(force).ConfigureAwait(false); } catch (Exception e) { t = "could not check: " + e.Message; }
                        Reply(id, new JsonObject { ["text"] = t }.ToJsonString());
                    });
                    break;
                }
                case "skills":
                    Reply(id, new JsonObject { ["skills"] = SkillStore.List(), ["folder"] = SkillStore.Dir }.ToJsonString());
                    break;
                case "skillToggle":
                    SkillStore.SetEnabled((string)m["skill"], (bool?)m["enabled"] ?? true);
                    Reply(id, "{}");
                    break;
                case "skillRemove":
                    try { SkillStore.Remove((string)m["skill"]); Reply(id, "{\"ok\":true}"); }
                    catch (Exception e) { Reply(id, new JsonObject { ["ok"] = false, ["error"] = e.Message }.ToJsonString()); }
                    break;
                case "skillRead":
                    try { Reply(id, new JsonObject { ["ok"] = true, ["text"] = SkillStore.Read((string)m["skill"], (string)m["file"]) }.ToJsonString()); }
                    catch (Exception e) { Reply(id, new JsonObject { ["ok"] = false, ["text"] = e.Message }.ToJsonString()); }
                    break;
                case "skillAdd":
                {
                    string url = (string)m["url"], name = (string)m["name"], b64 = (string)m["data"];
                    Task.Run(async () =>
                    {
                        JsonObject r;
                        try
                        {
                            var msg = url != null ? await SkillStore.AddUrl(url).ConfigureAwait(false) : SkillStore.AddFile(name, Convert.FromBase64String(b64 ?? ""), name);
                            r = new JsonObject { ["ok"] = true, ["text"] = msg };
                        }
                        catch (Exception e) { r = new JsonObject { ["ok"] = false, ["text"] = e.Message }; }
                        Reply(id, r.ToJsonString());
                    });
                    break;
                }
                case "ai":
                {
                    string provider = (string)m["provider"], method = (string)m["method"], path = (string)m["path"], body = (string)m["body"], beta = (string)m["beta"];
                    Task.Run(async () =>
                    {
                        var r = await AiProxy.Forward(provider, method, path, body, default, beta).ConfigureAwait(false);
                        Reply(id, new JsonObject { ["status"] = r.Status, ["body"] = r.Body, ["contentType"] = r.ContentType, ["retryAfter"] = r.RetryAfter }.ToJsonString());
                    });
                    break;
                }
                case "tool":
                {
                    var tool = (string)m["tool"];
                    var args = m["args"]?.ToJsonString() ?? "{}";
                    if (!(tool == "ops" || tool == "query" || tool == "run" || tool == "gh_edit" || tool == "undo" || tool == "view"))
                    {
                        Reply(id, Dispatcher.Reply(false, "not an assistant tool: " + tool));
                        break;
                    }
                    Task.Run(() =>
                    {
                        string res;
                        try { res = Dispatcher.Call(tool, args, 120000); }
                        catch (Exception e) { res = Dispatcher.Reply(false, "tool failed: " + e.Message); }
                        Reply(id, res);
                    });
                    break;
                }
                default:
                    Reply(id, Dispatcher.Reply(false, "unknown message"));
                    break;
            }
        }

        /// the answer to message `id` (any thread): runs window.__nxReply on the UI thread
        /// in its own method so the Grasshopper types are only touched once Grasshopper is loaded
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool GhWindowOpen()
        {
            var ed = Grasshopper.Instances.DocumentEditor;
            return ed != null && ed.Visible && ed.WindowState != System.Windows.Forms.FormWindowState.Minimized;
        }

        void Reply(int id, string json)
        {
            Application.Instance.AsyncInvoke(() =>
            {
                if (_instance != this) return;
                try { _ = _web.ExecuteScriptAsync($"window.__nxReply({id}, {json}); return '';"); } catch { }
            });
        }
    }
}
