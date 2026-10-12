# Shared agent resources

This directory is for cross-agent resources. `.agents/skills/` is the shared
skill convention used by compatible agents.

OpenAI also discovers marketplace catalogs at `.agents/plugins/marketplace.json`.
The catalog points to the self-contained plugin at `plugin/nextra-rhinogh/`.

The source skill lives in `skill/nextra/`. The generated installable package
lives in `plugin/nextra-rhinogh/` and carries the OpenAI manifest. Claude's manifest
fields live in the root marketplace entry. Standalone ZIPs receive a Claude
manifest during packaging, without adding a second `.claude-plugin/` to the repository.

`.codex/config.toml`, when present in a workspace, configures the Codex runtime
and MCP server. It is not the catalog or the plugin manifest.
