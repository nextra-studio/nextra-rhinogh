# Shiba Tidy (reference, from the user, 2026-10-11)

C# script component that tidies *selected* GH objects:
1. Inserts relay params at group boundaries (`<input>_In` for wires entering a group, `<output>_Out` for wires leaving it),
   so each group is a block with all inputs on the left and all outputs on the right.
2. Two-layer layout (micro per group, macro across groups) — same RunGripLogic as nextra.gh.

Nextra adaptation: `gh_edit {layout:'tidy', relays:true}` (engine `Layout.AddRelays` + relay-aware ranking).
Source: ShibaTidy.cs (relay passes verbatim; its layout section is abbreviated because it is the same RunGripLogic as reference/nextra.gh/PluginWebSocketService.cs).
