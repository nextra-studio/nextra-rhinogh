---
plugin: CategoryName
description: one line — what this plugin is for and what this guide covers
---
# <Plugin> guide (template)

How this folder works
- One file per GH plug-in, named after the plug-in's **component category** as shown in `query {scope:'components'}` (e.g. `Karamba3D.md`, `LunchBox.md`, `Kangaroo2.md`). The `plugin:` line may list several categories: `plugin: Kangaroo2, K2Goals`.
- This folder lives in the skill. A new guide = copy this file next to it as `<Category>.md` and fill it in (with the user: their rules, units, checks). If the skill folder is read-only (uploaded .zip), save it with `memory {action:'save_guide', name:'<Category>', text}` instead — same format, kept in the user's Nextra folder (`plugins/`), and it wins over a guide here.
- The AI reads the guide before using that plug-in. When the skill is installed as a folder (Claude Code `~/.claude/skills/nextra-rhinogh`), the plug-in also attaches the `## Rules` section **automatically, once per session**, the first time a component of that category is created, edited or listed.
- Mode (config.json `"plugin_docs"` or per call `gh_edit {plugin_docs:…}`): `auto` (attach Rules) · `hint` (one line pointing at the file) · `off`.
- Keep `## Rules` short (≤ 12 lines): it is paid for in tokens whenever the plug-in is used.

## Rules
- Units / tolerances the plug-in assumes.
- Which components are current vs. outdated duplicates.
- Non-obvious inputs (index lists, enums, dropdown `$state` keys).
- How to verify a result (which output to read, plausible ranges).

## Example (shorthand)
```
## <Plugin> #ABDBFF
x = Some Component @CategoryName (In=…) {$DropdownKey="Option"}
```

## Ports
Key components and their ports (copy from `query {scope:'components', filter:'@CategoryName'}`).
