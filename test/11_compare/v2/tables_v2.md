## Fixed context (in every request)

| | tool schemas | server instructions | skill (loaded once) | total |
|---|---|---|---|---|
| Nextra | 1,406 | 91 | 1,260 | **2,757** |
| McNeel Rhino-MCP-Platform | 4,779 | 0 | 0 | **4,779** |
| RhinoMCP | 22,935 | 94 | 0 | **23,029** |

## A · Rhino: 4-storey office (548 objects) + add storey, move core, delete facade side, undo

| | calls | output tokens (model writes) | input tokens from results (model reads) | + fixed context | **total** | billed input, no cache | billed input, prompt cache |
|---|---|---|---|---|---|---|---|
| Nextra | 8 | 969 | 1,728 | 2,757 | **5,454** | 41,172 | 9,025 |
| McNeel Rhino-MCP-Platform | 7 | 2,027 | 1,269 | 4,779 | **8,075** | 56,083 | 12,875 |
| RhinoMCP | 7 | 2,034 | 2,494 | 23,029 | **27,557** | 208,076 | 45,608 |

per task — out / in (calls):

| task | Nextra | McNeel Rhino-MCP-Platform | RhinoMCP |
|---|---|---|---|
| 0_session | 4 / 193 (1) | — | — |
| 1_build | 891 / 171 (1) | 934 / 49 (1) | 937 / 65 (1) |
| 2_look | 3 / 735 (1) | 19 / 774 (1) | 14 / 642 (1) |
| 3_inspect_core | 6 / 341 (1) | 4 / 374 (1) | 6 / 1,682 (1) |
| 4_add_storey | 13 / 191 (1) | 940 / 49 (1) | 943 / 66 (1) |
| 5_move_core | 28 / 61 (1) | 61 / 9 (1) | 64 / 16 (1) |
| 6_delete_south | 19 / 24 (1) | 64 / 9 (1) | 67 / 18 (1) |
| 7_undo | 5 / 12 (1) | 5 / 5 (1) | 3 / 5 (1) |

correctness (neutral check after the tasks):

- Nextra: {'after_edits': "{'Core': 20, 'Mullions': 235, 'Stairs': 10, 'Slabs': 5, 'Beams': 190, 'Columns': 120} core x [17750, 18000] MS 0", 'after_undo': "{'Mullions': 340, 'Core': 20, 'Stairs': 10, 'Slabs': 5, 'Beams': 190, 'Columns': 120} core x [17750, 18000] MS 105"}
- McNeel Rhino-MCP-Platform: {'after_edits': "{'Core': 20, 'Mullions': 235, 'Stairs': 10, 'Slabs': 5, 'Beams': 190, 'Columns': 120} core x [17750, 18000] MS 0", 'after_undo': "{'Mullions': 340, 'Core': 20, 'Stairs': 10, 'Slabs': 5, 'Beams': 190, 'Columns': 120} core x [17750, 18000] MS 105"}
- RhinoMCP: {'after_edits': "{'Core': 20, 'Mullions': 235, 'Stairs': 10, 'Slabs': 5, 'Beams': 190, 'Columns': 120} core x [17750, 18000] MS 0", 'after_undo': "{'Mullions': 340, 'Core': 20, 'Stairs': 10, 'Slabs': 5, 'Beams': 190, 'Columns': 120} core x [17750, 18000] MS 105"}

## B · GH: attractor facade (27 components, 5 groups) + attractor→curve, opening scale slider, tidy

| | calls | output tokens (model writes) | input tokens from results (model reads) | + fixed context | **total** | billed input, no cache | billed input, prompt cache |
|---|---|---|---|---|---|---|---|
| Nextra | 6 | 371 | 2,272 | 2,757 | **5,400** | 31,976 | 8,057 |
| McNeel Rhino-MCP-Platform | 14 | 2,955 | 8,670 | 4,779 | **16,404** | 177,868 | 32,550 |
| RhinoMCP | 6 | 1,564 | 19,942 | 23,029 | **44,535** | 253,711 | 65,452 |

per task — out / in (calls):

| task | Nextra | McNeel Rhino-MCP-Platform | RhinoMCP |
|---|---|---|---|
| 0_docs | 10 / 1,514 (1) | — | — |
| 1_build | 270 / 191 (1) | 956 / 1,101 (1) | 1,080 / 11,477 (1) |
| 3_attractor_curve | 49 / 76 (1) | 404 / 400 (3) | 320 / 4,236 (1) |
| 4_open_scale | 34 / 49 (1) | 252 / 158 (3) | 156 / 1,751 (1) |
| 5_tidy | 4 / 25 (1) | 218 / 7 (1) | 0 / 1,482 (1) |
| 9_look | 4 / 417 (1) | 19 / 771 (1) | 0 / 642 (1) |
| 1_search | — | 8 / 326 (1) | 8 / 354 (1) |
| 1_recover | — | 478 / 626 (2) | — |
| 1_fix | — | 610 / 7 (1) | — |
| 1_verify | — | 10 / 5,274 (1) | — |

correctness (neutral check after the tasks):

- Nextra: {'built': "objects 32 groups 5 sink {'Extrude': 90} errors 0", 'after_edits': "objects 36 groups 5 sink {'Extrude': 90} errors 0", 'ambiguous': ['Rectangle']}
- McNeel Rhino-MCP-Platform: {'built': "objects 32 groups 5 sink {'Extrude': 90} errors 0", 'after_edits': "objects 36 groups 5 sink {'Extrude': 90} errors 0", 'ambiguous': ['Rectangle']}
- RhinoMCP: {'built': "objects 32 groups 5 sink {'Extrude': 90} errors 0", 'after_edits': "objects 36 groups 5 sink {'Extrude': 90} errors 0", 'ambiguous': ['Rectangle']}

## C · GH: organic gridshell (27 components) + parameter change, edge beams

| | calls | output tokens (model writes) | input tokens from results (model reads) | + fixed context | **total** | billed input, no cache | billed input, prompt cache |
|---|---|---|---|---|---|---|---|
| Nextra | 4 | 279 | 739 | 2,757 | **3,775** | 16,389 | 5,036 |
| McNeel Rhino-MCP-Platform | 13 | 3,008 | 10,213 | 4,779 | **18,000** | 160,692 | 32,269 |
| RhinoMCP | 10 | 1,434 | 14,449 | 23,029 | **38,912** | 356,160 | 70,636 |

per task — out / in (calls):

| task | Nextra | McNeel Rhino-MCP-Platform | RhinoMCP |
|---|---|---|---|
| 1_build | 250 / 225 (1) | 974 / 1,558 (1) | 1,134 / 11,667 (1) |
| 3_params | 7 / 31 (1) | 86 / 7 (1) | 15 / 81 (2) |
| 4_edge_beams | 18 / 67 (1) | 203 / 116 (3) | 114 / 1,284 (2) |
| 9_look | 4 / 416 (1) | 19 / 771 (1) | 0 / 642 (1) |
| 1_search | — | 24 / 604 (3) | 24 / 764 (3) |
| 1_recover | — | 950 / 1,218 (2) | — |
| 1_fix | — | 742 / 7 (1) | 147 / 11 (1) |
| 1_verify | — | 10 / 5,932 (1) | — |

correctness (neutral check after the tasks):

- Nextra: {'built': "objects 31 groups 4 sink {'Pipe': 32} errors 0", 'after_edits': "objects 33 groups 4 sink {'Pipe': 42} errors 0", 'ambiguous': ['Addition', 'Loft Options', 'Merge']}
- McNeel Rhino-MCP-Platform: {'built': "objects 31 groups 4 sink {'Pipe': 32} errors 0", 'after_edits': "objects 33 groups 4 sink {'Pipe': 42} errors 0", 'ambiguous': ['Addition', 'Loft Options', 'Merge']}
- RhinoMCP: {'built': "objects 32 groups 4 sink {'Pipe': 32} errors 0", 'after_edits': "objects 34 groups 4 sink {'Pipe': 42} errors 0", 'ambiguous': ['Addition', 'Loft Options', 'Merge']}

## Whole session (A + B + C in one conversation)

| | calls | output | input from results | fixed | total | billed input, no cache |
|---|---|---|---|---|---|---|
| Nextra | 18 | 1,619 | 4,739 | 2,757 | **9,115** | 121,565 |
| McNeel Rhino-MCP-Platform | 34 | 7,990 | 20,152 | 4,779 | **32,921** | 625,202 |
| RhinoMCP | 23 | 5,032 | 36,885 | 23,029 | **64,946** | 1,059,397 |
