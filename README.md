# Nextra for Rhino 8 + Grasshopper

Six token-lean tools let an LLM build and change Rhino models and Grasshopper definitions. It works from ChatGPT desktop / Codex, Claude Desktop, Claude Code, or any MCP client. Replies say exactly what changed, images carry tags the model can point at, and every edit can be undone.

**[Installation guide](#install)** · **[Claude plugin ZIP](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest/download/nextra-rhinogh-plugin.zip)** · **[ChatGPT / Codex ZIP](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest/download/nextra-rhinogh-agent-kit.zip)**

| tool | what it does |
|---|---|
| `view` | Rhino or GH canvas image with an 8×8 grid and short handle tags (`r12`, `g4`). Region-first: a crowded view returns per-cell counts, then `focus:'C3:E5'` zooms in. Also shows GH preview geometry in Rhino. |
| `query` | Text, cheapest first. **Rhino:** summary → one line per object with an oriented box → detail → vertices. **GH:** the definition as shorthand. Also searches installed components (with ports), the **API reference** (RhinoCommon, Grasshopper SDK, rhinoscriptsyntax), the docs, your workflows, the script library and status. |
| `run` | Python 3 / C# in Rhino with helpers (`h('r3')`, `q('layer:X')`, `obb()`, `ghdoc()`). Supports patch edits. Replies with the Rhino + GH delta, errors at your own line numbers, and a checkpoint. |
| `gh_edit` | Shorthand diffs: create, modify, wire, group, and component state (dropdowns, on-component toggles, right-click menu, clicks). Also script components, layout/tidy (with group interface relays), and open/save/bake. Definitions can be saved as **`.ghs`**: the same shorthand as readable, diffable text that opens back into an identical canvas. |
| `undo` | Exact revert to any checkpoint (Rhino objects + GH document), independent of Rhino's own undo stack. |
| `memory` | Your preferences and workflows, learned rules (user-approved), mistake log, script library. |

**Tokens.** All three MCPs were measured live in the same Rhino. Input and output are counted separately, including schemas and docs. Each result was checked neutrally, and every case gave the same correct model on all three.

**v3** ([test/v3/REPORT.md](test/v3/REPORT.md)) covers 19 cases in one session. They include:
- Rhino frames
- GH towers and fins
- Python and C# components
- Karamba beam, house FE and shell
- data trees, a messy canvas, a freeform canopy, and a 1,224-object model

| one session | Nextra | McNeel Rhino-MCP-Platform | RhinoMCP |
|---|---|---|---|
| tool calls (Python fallbacks) | **43 (0)** | 108 (34) | 96 (32) |
| total tokens | **≈ 16.2k** | ≈ 77.0k (4.8×) | ≈ 237.7k (14.7×) |
| billed input, no cache | **≈ 0.43M** | ≈ 4.12M (9.7×) | ≈ 11.46M (27×) |

The earlier v2 benchmark (office, facade and gridshell) is in [test/v2/REPORT.md](test/v2/REPORT.md): 3.6× and 7.1× fewer tokens than the other two.

**Testing.** A regression suite ([test/regress](test/regress/regress.py)) runs every case in one Rhino session and checks the results: 238 steps, all passing.

## Install

**Windows · Rhino 8 · Node.js 18+ · your preferred AI desktop app.**

Install the Rhino plug-in once, then choose **one** installation method for your AI app below. Each AI plugin includes both the tools and the skill.

### 1. Install in Rhino

1. In Rhino, run **`PackageManager`**.
2. Search **`Nextra-RhinoGh`**, click **Install**, and restart Rhino.
3. Look for the status dot in the bottom-right corner.

![Rhino Package Manager: search Nextra-RhinoGh, click Install](docs/img/install/rhino-package-manager.svg)

![Status dot in the bottom-right corner of Rhino](docs/img/status-dot-real.png)

![Green = connected, amber = ready, red = click to restart](docs/img/dot-states.svg)

### 2. Install in your AI app

| Your app | One-click download | What to do with the download |
|---|---|---|
| **Claude Desktop / Cowork** | **[Download plugin ZIP](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest/download/nextra-rhinogh-plugin.zip)** | Upload the ZIP in **Customize → Plugins → Add → Upload plugin**. |
| **ChatGPT desktop / Codex** | **[Download agent kit ZIP](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest/download/nextra-rhinogh-agent-kit.zip)** | Extract it, then paste the installation prompt below into Codex. |

**The links download the ready-made packages.** A GitHub download cannot complete installation inside another app: follow the short steps for your app. You can also add the marketplace without downloading a ZIP.

#### Claude Desktop

Use **Cowork** for the plugin's local Rhino tools. For the Chat tab, use the desktop extension described below.

**Way A — Add the marketplace**

1. Open **Customize → Plugins → Add → Add marketplace → Add from a repository**.
2. Paste this repository name:

   ```text
   Tatsuki0123/nextra-rhinogh
   ```

3. Sync/add the marketplace, open **nextra-rhinogh**, and click **Install**.
4. Start a new Cowork session with Rhino open.

![Claude Desktop: add the Nextra marketplace](docs/img/install/claude-desktop-marketplace.svg)

**Way B — Download and upload**

1. **[Download the Claude plugin ZIP](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest/download/nextra-rhinogh-plugin.zip)**.
2. Open **Customize → Plugins → Add → Upload plugin**.
3. Select **`nextra-rhinogh-plugin.zip`** without extracting it, then complete the installation.
4. Start a new Cowork session with Rhino open.

![Claude Desktop: upload the plugin ZIP](docs/img/install/claude-desktop-plugin.svg)

<details>
<summary>Claude Desktop Chat tab: use the desktop extension</summary>

Download the **`.mcpb`** file from the [latest release](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest), double-click it, and complete Claude's installation prompts. Restart Claude. This installs the local MCP tools for chat; the plugin's bundled skill is a separate install in this route. The tools also serve the Nextra instructions through `query`.

</details>

#### ChatGPT desktop / Codex

Open **Codex** in the desktop app to run the installation prompt. Node.js and the `codex` CLI must be available on your computer.

**Way A — Add the marketplace**

Copy this entire prompt into Codex:

```text
Install Nextra for Rhino from its public GitHub marketplace. Run:
codex plugin marketplace add Tatsuki0123/nextra-rhinogh
codex plugin add nextra-rhinogh@nextra
```

Restart the desktop app and start a new chat. If the plugin is listed but disabled, enable **Nextra for Rhino** in the **Plugins Directory**.

![ChatGPT / Codex: install from the Nextra marketplace](docs/img/install/chatgpt-marketplace.svg)

**Way B — Download and install from a folder**

1. **[Download the agent kit ZIP](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest/download/nextra-rhinogh-agent-kit.zip)**.
2. Right-click the ZIP → **Extract All**. Keep the extracted folder.
3. Copy this prompt into Codex, replacing the example path with the folder containing **`AGENTS.md`**:

   ```text
   Install Nextra for Rhino from my downloaded folder. Run:
   codex plugin marketplace add "C:/Users/you/Downloads/nextra-rhinogh-kit"
   codex plugin add nextra-rhinogh@nextra
   ```

4. Restart the desktop app and start a new chat.

![ChatGPT / Codex: install from the extracted agent kit](docs/img/install/chatgpt-prompt.svg)

If `codex` is unavailable, open the extracted **`nextra-rhinogh-kit`** folder as a Codex project, trust the project, restart the app, and select **Nextra → Nextra for Rhino → Install** in the **Plugins Directory**. The folder includes the local marketplace and MCP configuration.

<details>
<summary>Claude Code and other MCP clients</summary>

**Claude Code:** run these two commands, then start a new session:

```text
/plugin marketplace add Tatsuki0123/nextra-rhinogh
/plugin install nextra-rhinogh@nextra
```

![Claude Code: add the marketplace and install Nextra](docs/img/install/claude-code.svg)

**Other agents (Cursor, Cline, Gemini CLI, …):** [download the agent kit](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest/download/nextra-rhinogh-agent-kit.zip), extract it, and open **`nextra-rhinogh-kit`** as your workspace. Ask the agent to read **`AGENTS.md`** for setup. If your client needs manual MCP configuration, use command **`node`**, argument **`<full path to the kit>/server/index.js`**, and read **`skills/nextra-rhinogh/SKILL.md`**.

</details>

### 3. Try it

Keep Rhino open and ask: **“Create a simple timber house in Rhino.”** The dot turns green when the AI connects.

### Troubleshooting

| You see | What to do |
|---|---|
| `node` or `codex` is not recognized | Install [Node.js](https://nodejs.org/en/download). For Codex, use the folder/Plugins Directory method above or [install the Codex CLI](https://developers.openai.com/codex/cli). |
| No Rhino tools in the AI app | Finish step 2, restart the app, and start a new session. In Claude, use Cowork for the plugin or the `.mcpb` for chat. |
| `Unknown command: NextraOn` | Restart Rhino. If still missing: **`PlugInManager` → Nextra.Host → Load**. |
| `Blocking plug-in Nextra.Host` | Close all Rhino windows, then start Rhino again. |
| No dot / red dot | Run **`NextraOn`** / click the dot. |
| Several Rhino windows | Tell the AI which file to use, for example **“Use house.3dm.”** |

The app pictures are simplified illustrations; labels and plugin availability can vary by app version and workspace settings. Installation references: [OpenAI plugin setup](https://developers.openai.com/plugins/build/plugins) · [Claude plugin setup](https://support.claude.com/en/articles/13837440-use-plugins-in-claude).

<details>
<summary>Maintainers: publish from this public repository</summary>

Source, marketplaces, and release downloads live together in **`Tatsuki0123/nextra-rhinogh`**.

Bump the version in `tools/package.ps1`, `tools/deploy_dev.ps1`, `bridge/manifest.json`, `bridge/server/index.js`, and both `.csproj` files. Then build:

```powershell
./tools/package.ps1
```

Commit the generated `plugin/`, `.claude-plugin/marketplace.json`, and `.agents/plugins/marketplace.json` along with the source changes. Both marketplaces must list `nextra-rhinogh`. Publish the packages to this repository (replace `<ver>`):

```powershell
yak push dist/nextra-rhinogh-<ver>-rh8_35-win.yak
gh release create v<ver> dist/nextra-rhinogh-plugin.zip dist/nextra-rhinogh-agent-kit.zip dist/nextra-rhinogh-<ver>.mcpb dist/nextra-rhinogh-<ver>-rh8_35-win.yak --repo Tatsuki0123/nextra-rhinogh
```

Keep the two ZIP filenames stable so the download links above keep working. Regenerate illustrations with `python tools/make_install_images.py`.

</details>

## Your own instructions

The plug-in creates a folder at first start, `%APPDATA%\Nextra\user`; the Rhino command `NextraFolder` opens it. It holds:

| file / folder | what the AI does with it |
|---|---|
| `preferences.md` | Read at the start of every session. |
| `workflows/<name>.md` | Each starts with a `when:` line; the AI opens the one matching your request. |
| `plugins/<Category>.md` | Rules attached automatically when that plug-in's components are used. |
| `scripts/` | Your scripts, available to `run`. |

The folder works however the skill is installed, including as a zip uploaded to claude.ai, because the plug-in serves it through the tools. Plug-in updates never touch it. The README in the folder explains how to write each file.

**Config** is in `%APPDATA%/Nextra/config.json`:

| key | meaning |
|---|---|
| `port` | server port |
| `enabled` | start the server with Rhino |
| `plugin_docs` | `auto`, `hint` or `off` |
| `library_dirs` | extra script folders |
| `user_dir` | location of your folder |
| `memory_dir` | location of the AI's memory |
| `auto_dismiss` | dialog titles to close automatically |
| `capture_copy_dir` | extra copy location for captured images |

## Repository

```
src/Nextra.Host     .rhp: 127.0.0.1 server (token), UI-thread dispatcher with timeouts + late results, dialog probe/dismiss, status dots, engine hot-reload
src/Nextra.Engine   all Rhino/GH logic (hot-reloaded on rebuild): tools, shorthand parser/builder/writer, layout, vision, checkpoints, memory, API reference, user folder
bridge/             MCP stdio bridge (zero-dependency Node) + .mcpb manifest
skill/nextra/       SKILL.md, shorthand.md, workflows.md, api/*, plugins/<Category>.md (plug-in guides, auto-attached)
plugin/             generated by tools/make_plugin.py: one plug-in (bridge + skill) for Claude Code and Codex / ChatGPT desktop
.claude-plugin/     Claude Code marketplace (this repo);  .agents/plugins/ = Codex / ChatGPT marketplace
docs/img/           README images (install pictures: tools/make_install_images.py)
tools/              build.py, deploy_dev.ps1 (dev install with hot reload), package.ps1 (yak + mcpb + skill zip + plugin/), nx.py (test client), mcp_http.py
test/               v1/ = first round (cases 01-16), v2/ = detailed benchmark, v3/ = all cases on three systems, regress/ = full regression suite
dev/                developer guide, original plan, build checklist, reference code (information only, not part of the skill)
```

## Develop

1. Run `powershell tools/deploy_dev.ps1` once, with Rhino closed.
2. Start Rhino.
3. Edit the engine code, then run `python tools/build.py`. The next call uses the new engine; no Rhino restart is needed. Host changes do need a restart.
4. Try calls with `python tools/nx.py query '{}'`, and check everything with `python test/regress/regress.py`.

See [dev/](dev/README.md) for the full developer guide.

## License

[Apache License 2.0](LICENSE). Free to use, including commercially. Keep the copyright and license notice ([NOTICE](NOTICE)) and mark any changes you make. The license also grants a patent right to use the code.
