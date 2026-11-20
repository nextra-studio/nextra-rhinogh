using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;

namespace Nextra.Host
{
    /// Settings of the optional decision-API assistant (off by default). Non-secret settings live in config.json; the keys
    /// only in KeyStore (the system's secret store).
    static class AssistantSettings
    {
        /// how the assistant thinks: api (the person's keys), cli (their AI command-line tool), desktop (they use Claude /
        /// ChatGPT desktop with the plug-in; the panel only shows how to connect)
        public static string Mode => Config.GetString("assistant_mode", "api");
        /// the CLI in cli mode: claude | codex | gemini; its model ("" = the CLI's default)
        public static string Cli => Config.GetString("assistant_cli", "");
        public static string CliModel => Config.GetString("assistant_cli_model", "");
        public static bool Enabled => Config.GetBool("assistant_enabled", false);
        /// the typed-decision provider: openai (Decisions API, gpt-6-luna) | jev | gemini (Gemini Decision 1)
        public static string Decider => Config.GetString("assistant_decider", "openai");
        /// the chat provider (LLM mode, fallbacks): openai | gemini; Jev has no chat
        public static string Chat => Config.GetString("assistant_chat", "openai");
        /// ready to open: switched on and the decider's key saved
        public static bool Ready => Enabled && KeyStore.Has(Decider);
        public static bool ChatReady => KeyStore.Has(Chat);
    }

    /// NextraDecisionApi: the setup popup. Paste your own OpenAI / Gemini / Jev key, check it, pick the decider.
    public class NextraDecisionApiCommand : Rhino.Commands.Command
    {
        public override string EnglishName => "NextraDecisionApi";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            DecisionApiDialog.ShowSetup();
            return Result.Success;
        }
    }

    class DecisionApiDialog : Dialog<bool>
    {
        static readonly (string id, string label)[] Deciders =
        {
            ("openai", "OpenAI Decisions API (gpt-6-luna) - most accurate"),
            ("jev", "Jev - fastest"),
            ("gemini", "Gemini Decision 1"),
        };
        static readonly (string id, string label)[] Chats = { ("openai", "OpenAI"), ("gemini", "Gemini") };

        readonly CheckBox _enabled = new CheckBox { Text = "Use the assistant (click Nexiy, then Open assistant)" };
        readonly Dictionary<string, PasswordBox> _boxes = new Dictionary<string, PasswordBox>();
        readonly Dictionary<string, Label> _state = new Dictionary<string, Label>();
        readonly DropDown _decider = new DropDown();
        readonly DropDown _chat = new DropDown();
        readonly Label _note = new Label { Wrap = WrapMode.Word, TextColor = Colors.Gray };
        readonly Button _check = new Button { Text = "Save and check keys" };

        public static void ShowSetup()
        {
            var d = new DecisionApiDialog();
            Window owner = null;
            try { owner = Rhino.UI.RhinoEtoApp.MainWindow; } catch { }
            if (d.ShowModal(owner)) RhinoApp.WriteLine(AssistantSettings.Ready
                ? "NextraRhinoGh: decision API on. Click Nexiy > Open assistant."
                : AssistantSettings.Enabled ? $"NextraRhinoGh: decision API on, but no {KeyStore.Label(AssistantSettings.Decider)} key yet."
                : "NextraRhinoGh: decision API off.");
        }

        DecisionApiDialog()
        {
            Title = "Nextra decision API - your own keys";
            Resizable = false;
            Padding = 14;
            _enabled.Checked = AssistantSettings.Enabled;
            foreach (var (id, label) in Deciders) _decider.Items.Add(new ListItem { Key = id, Text = label });
            foreach (var (id, label) in Chats) _chat.Items.Add(new ListItem { Key = id, Text = label });
            _decider.SelectedKey = AssistantSettings.Decider;
            _chat.SelectedKey = AssistantSettings.Chat;

            var keys = new TableLayout { Spacing = new Size(8, 6) };
            foreach (var p in KeyStore.Providers)
            {
                var box = new PasswordBox { Width = 300 };
                var st = new Label { Text = KeyStore.Hint(p) is string h ? "saved " + h : "no key", TextColor = Colors.Gray, VerticalAlignment = VerticalAlignment.Center };
                var remove = new LinkButton { Text = "remove" };
                string pid = p;
                remove.Click += (s, e) => { KeyStore.Clear(pid); st.Text = "no key"; };
                _boxes[p] = box; _state[p] = st;
                keys.Rows.Add(new TableRow(new Label { Text = KeyStore.Label(p), VerticalAlignment = VerticalAlignment.Center }, box, st, remove));
            }

            _check.Click += async (s, e) => await SaveAndCheck();
            _decider.SelectedKeyChanged += (s, e) => UpdateNote();
            _chat.SelectedKeyChanged += (s, e) => UpdateNote();
            UpdateNote();

            var ok = new Button { Text = "Save" };
            ok.Click += (s, e) => { if (SaveAll()) Close(true); };
            var cancel = new Button { Text = "Cancel" };
            cancel.Click += (s, e) => Close(false);
            DefaultButton = ok; AbortButton = cancel;

            Content = new StackLayout
            {
                Spacing = 10,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new Label { Text = "Off by default. Your key stays on this computer (encrypted by the system) and goes only to the provider you pick.", Wrap = WrapMode.Word, Width = 520 },
                    _enabled,
                    keys,
                    new TableLayout { Spacing = new Size(8, 6), Rows = { new TableRow(new Label { Text = "Decides", VerticalAlignment = VerticalAlignment.Center }, _decider), new TableRow(new Label { Text = "Chat model", VerticalAlignment = VerticalAlignment.Center }, _chat) } },
                    _note,
                    new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, Items = { _check, null, ok, cancel } },
                },
            };
        }

        void UpdateNote()
        {
            var dec = _decider.SelectedKey ?? "openai";
            var chat = _chat.SelectedKey ?? "openai";
            bool hasChat = KeyStore.Has(chat) || !string.IsNullOrEmpty(_boxes[chat].Text);
            _note.Text = (dec == "jev" ? "Jev only decides: questions it cannot settle go to the chat model. " : "")
                + (hasChat ? "" : $"No {KeyStore.Label(chat)} key: LLM mode is off, and Quick asks you instead of using a chat model.");
        }

        bool SaveAll()
        {
            foreach (var p in KeyStore.Providers)
            {
                var t = _boxes[p].Text?.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                try { KeyStore.Set(p, t); _boxes[p].Text = ""; _state[p].Text = "saved " + KeyStore.Hint(p); }
                catch (Exception ex) { MessageBox.Show(this, $"{KeyStore.Label(p)}: {ex.Message}", MessageBoxType.Warning); return false; }
            }
            Config.Set("assistant_enabled", _enabled.Checked == true);
            Config.Set("assistant_decider", _decider.SelectedKey ?? "openai");
            Config.Set("assistant_chat", _chat.SelectedKey ?? "openai");
            return true;
        }

        async Task SaveAndCheck()
        {
            if (!SaveAll()) return;
            _check.Enabled = false;
            foreach (var p in KeyStore.Providers.Where(KeyStore.Has)) _state[p].Text = "checking…";
            foreach (var p in KeyStore.Providers.Where(KeyStore.Has))
            {
                string res;
                try { res = await Task.Run(() => AiProxy.Check(p)); } catch (Exception ex) { res = "check failed: " + ex.Message; }
                _state[p].Text = $"{KeyStore.Hint(p)} {res}";
                _state[p].TextColor = res == "works" ? Color.FromArgb(40, 150, 70) : Color.FromArgb(200, 60, 60);
            }
            _check.Enabled = true;
            UpdateNote();
        }
    }
}
