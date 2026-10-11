---
plugin: CategoryName
description: one line — what this plugin is for and what this guide covers
---
# <Plugin> guide (template)

How this folder works
- One file per GH plug-in, named after the plug-in's **component category** as shown in `query {scope:'components'}` (e.g. `Karamba3D.md`, `LunchBox.md`, `Kangaroo2.md`). The `plugin:` line may list several categories: `plugin: Kangaroo2, K2Goals`.
- The engine attaches the `## Rules` section **automatically, once per session**, the first time a component of that category is created, edited or listed. The rest of the file is read on demand: `query {scope:'docs', filter:'plugins/<Name>'}`.
- Your own guides go in `%APPDATA%/Nextra/memory/plugins/` (same format; they win over the bundled ones).
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
