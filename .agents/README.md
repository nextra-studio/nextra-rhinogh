# Shared agent resources

This directory is for cross-agent resources. `.agents/skills/` is the shared
skill convention used by compatible agents.

OpenAI also discovers marketplace catalogs at `.agents/plugins/marketplace.json`.
The catalog is generated together with the OpenAI-facing copy at
`.gpt-plugin/marketplace.json`. Both point to the same self-contained plugin.

The source skill lives in `skill/nextra/`. The generated installable package
lives in `plugin/nextra-rhinogh/` and carries both Claude and OpenAI manifests.

`.codex/config.toml`, when present in a workspace, configures the Codex runtime
and MCP server. It is not the catalog or the plugin manifest.
