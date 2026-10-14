# Plugin and marketplace folder rules

Checked on 2026-10-12 against the official documentation, this repository, and generated release archives.

## 1. The three roots

| Root | Meaning in Nextra |
|---|---|
| Repository / marketplace root | `RhinoGh/`: the folder containing the marketplace catalogs. |
| Plugin root | `RhinoGh/plugin/nextra-rhinogh/`: the folder installed as one plugin. |
| Archive root | The top level inside an uploaded ZIP. A standalone plugin ZIP starts directly with plugin files. |

A marketplace entry points to a plugin root. The plugin contains the skill and MCP server. A ZIP can carry that plugin independently of a marketplace.

Folder names can repeat at different roots because their files have different roles. A root `.claude-plugin/marketplace.json` is a catalog; a plugin's `.claude-plugin/plugin.json` is a manifest. Nextra keeps the Claude manifest in the plugin folder, so the catalog entry is only a pointer.

## 2. Claude rules

### Standalone Claude plugin

```text
my-plugin/                       # plugin root
├── .claude-plugin/
│   └── plugin.json              # identity and component configuration
├── skills/
│   └── my-skill/SKILL.md
└── server/index.js              # our local MCP implementation
```

Keep component files outside the metadata directory. For local MCP, Nextra's manifest declares `command: "node"` and the argument `${CLAUDE_PLUGIN_ROOT}/server/index.js`. This resolves against the installed plugin. [Claude manifest reference](https://code.claude.com/docs/en/plugins-reference).

The supplied Claude upload screenshot accepts a `.zip` or `.plugin` archive with `.claude-plugin/plugin.json`, or a qualifying top-level `SKILL.md`. Nextra bundles tools and a skill, so its standalone archives use the manifest route:

```text
nextra-rhinogh-plugin.zip
├── .claude-plugin/plugin.json   # must be inside the ZIP
├── .mcp.json                    # Claude MCP launch
├── .codex-plugin/plugin.json    # also supports OpenAI
├── codex.mcp.json
├── skills/nextra-rhinogh/SKILL.md
├── server/index.js
└── assets/icon.svg
```

The repository's marketplace file is not included in this standalone archive. Its purpose is already covered by the archive's manifest.

### Claude marketplace

Claude discovers `.claude-plugin/marketplace.json` at the marketplace root. Its `plugins` entries identify each plugin and its `source`; relative sources resolve from that root. Nextra uses `./plugin/nextra-rhinogh`.

Claude Code also accepts a manifest-free plugin directory (the marketplace entry becomes the manifest), but Claude Desktop's server-side marketplace sync fails on one ("Marketplace sync failed"). Nextra therefore follows `anthropics/knowledge-work-plugins`: a slim catalog entry plus `plugin.json` and `.mcp.json` in the plugin folder. [Claude marketplace reference](https://code.claude.com/docs/en/plugins/marketplace-reference#how-an-entry-combines-with-pluginjson).

```text
RhinoGh/                         # marketplace root
├── .claude-plugin/
│   └── marketplace.json         # catalog: name, displayName, description, source
└── plugin/nextra-rhinogh/        # entry's source
    ├── .claude-plugin/plugin.json
    ├── .mcp.json
    ├── skills/
    ├── server/
    └── ...                      # OpenAI metadata and assets
```

## 3. OpenAI / Codex rules

New portable plugins use root `plugin.json`, `skills/`, and optional `mcp.json`; OpenAI-specific metadata goes in `extensions.com.openai`. OpenAI also accepts compatibility manifests at `.codex-plugin/plugin.json`. Nextra currently uses that accepted compatibility format, with `skills: "./skills/"` and `mcpServers: "./codex.mcp.json"`.

Catalog discovery uses `.agents/plugins/marketplace.json`; `.claude-plugin/marketplace.json` is also a legacy-compatible catalog location. Catalog `source.path` resolves from the marketplace root. Manifest resource paths resolve from the plugin root. [OpenAI packaging and marketplace rules](https://developers.openai.com/plugins/build/plugins).

```text
RhinoGh/                                  # marketplace root
├── .agents/plugins/marketplace.json       # OpenAI catalog
└── plugin/nextra-rhinogh/                  # plugin root
    ├── .codex-plugin/plugin.json          # compatibility manifest
    ├── codex.mcp.json                    # launch configuration
    ├── skills/nextra-rhinogh/SKILL.md
    ├── server/index.js
    └── assets/icon.svg
```

The generated `nextra-rhinogh-gpt-plugin.zip` currently has the same bytes as the Claude plugin ZIP. Both contain both hosts' manifests, the same server, and the same skill. The two filenames make the README downloads easier to identify; they are not separate implementations.

The current package is a local stdio integration. Being downloadable from GitHub does not mean it is published or approved in OpenAI's universal plugin directory.

## 4. Workspace folders and general agent kit

| Folder / file | Role | Repository policy |
|---|---|---|
| Root `.claude/` | Local Claude workspace resources, including skills. | Ignored by Git. |
| Root `.codex/config.toml` | Local Codex project settings and direct MCP configuration. | Ignored by Git. |
| `.agents/plugins/marketplace.json` | OpenAI marketplace catalog. | Tracked and generated. |
| `.agents/skills/` | Shared local skills for compatible agents. | Current local copies are untracked. |
| `skill/nextra/` | Authoritative Nextra skill source. | Tracked. |
| `bridge/server/` | Authoritative MCP bridge source. | Tracked. |

`.codex` is runtime/project configuration; `.codex-plugin` is installable-plugin metadata. One does not replace the other. No `.gpt-plugin` discovery convention is used.

The general agent kit is an explicitly packaged workspace. It includes `.claude/skills/`, `.agents/skills/`, `.codex/config.toml`, `.mcp.json`, `AGENTS.md`, the server, and a plugin marketplace. Ignoring root workspace folders in this Git repository does not prevent the packaging script from putting workspace configuration into that kit.

## 5. Implementation audit

| Check | Result |
|---|---|
| Claude catalog points to an existing plugin directory | Pass: `./plugin/nextra-rhinogh`. |
| Claude inline entry contains identity and local MCP startup settings | Pass; supported by Claude Code's documented manifest rules. |
| OpenAI catalog points to the same plugin | Pass. |
| OpenAI compatibility manifest references existing skills, MCP config, and icons | Pass. |
| Standalone ZIP contains the Claude manifest required by the screenshot | Pass, for both named plugin ZIPs. |
| General kit carries its own catalog and workspace configuration | Pass. |
| Generated server and skill match their source files | Pass. |
| Package and bridge-manifest versions match | Pass: `0.1.2`. |
| Archives have valid checksums and safe internal paths | Pass. |
| MCP starts from the generated directory and each extracted archive | Pass: `initialize` and `tools/list` return all six tools. |
| OpenAI listing subtitle is within the packaging skill's 30-character limit | Fixed: `Rhino + Grasshopper tools`. |
| Generator retains the user's current starter prompts | Fixed: the two existing skyscraper prompts are now in the generator. |

The prompt fix preserves the existing text, including spacing and order. Future prompt edits should be made in `tools/make_plugin.py` and regenerated; editing only the generated manifest will be overwritten on the next build.

The checks establish package structure and MCP startup. They do not exercise Rhino geometry, prove a desktop UI import succeeds, or prove Claude Desktop's GitHub sync handles the inline manifest identically to Claude Code. The screenshot establishes the Claude archive requirement; it does not show OpenAI's importer. Claude CLI is unavailable in this environment, so native `claude plugin validate` and a fresh desktop marketplace installation remain untested.

## 6. Editing and verification

Edit `skill/nextra/`, `bridge/server/`, or the metadata in `tools/make_plugin.py`. Then:

```powershell
python tools/make_plugin.py
python tools/verify_plugin.py
```

Commit the generated `plugin/` files and both root catalogs together. `tools/package.ps1` now runs the verification after plugin generation and stops if either step fails. Publish all three ZIPs from `dist/` when their contents change.

The verifier does not install plugins or modify Rhino. It checks paths, metadata synchronization, source copies, archive integrity, and starts the packaged bridge to perform the MCP handshake and discover its six tools.
