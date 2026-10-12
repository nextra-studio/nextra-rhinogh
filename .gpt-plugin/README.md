# OpenAI plugin metadata

This directory groups the ChatGPT / Codex metadata for maintainers.
`tools/make_plugin.py` generates it from the same source as the native files.

- `plugin.json` is a metadata copy of `plugin/nextra-rhinogh/.codex-plugin/plugin.json`.
  Its relative paths resolve in the packaged plugin directory, not in this authoring directory.
- `marketplace.json` mirrors `.agents/plugins/marketplace.json`.

OpenAI discovers the native `.agents/plugins/marketplace.json` catalog and
`.codex-plugin/plugin.json` manifest. `.gpt-plugin` is this repository's organization
convention, not a replacement discovery path. Keep the native files when packaging.
