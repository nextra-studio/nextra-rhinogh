## Per case (output tokens / input tokens · calls · of which Python fallback)

| case | Nextra | McNeel | RhinoMCP |
|---|---|---|---|
| 00 session start | 4 / 202 · 1 | — | — |
| 01 Rhino frame from scratch | 526 / 818 · 2 | 583 / 795 · 2 (1 py) | 586 / 682 · 2 (1 py) |
| 02 Rhino modify (add storey, move, undo) | 48 / 208 · 3 | 644 / 42 · 3 (2 py) | 648 / 59 · 3 (2 py) |
| 03 GH twisting tower | 162 / 1,674 · 2 | 1,272 / 4,481 · 8 (1 py) | 926 / 19,650 · 6 (1 py) |
| 04 GH modify (rectangle → circle) | 18 / 66 · 1 | 414 / 135 · 3 (1 py) | 363 / 1,657 · 2 (1 py) |
| 05 Rhino slab → GH fins → bake | 229 / 152 · 3 | 1,682 / 3,787 · 8 (3 py) | 1,395 / 20,060 · 6 (3 py) |
| 06 GH Python component (+ error, fix) | 168 / 216 · 3 | 2,050 / 155 · 4 (3 py) | 2,051 / 1,415 · 4 (3 py) |
| 07 GH C# component | 185 / 64 · 1 | 1,558 / 150 · 3 (2 py) | 1,559 / 1,419 · 3 (2 py) |
| 08 Python component with radio buttons (+ click) | 55 / 117 · 2 | 1,982 / 42 · 2 (2 py) | 1,982 / 62 · 2 (2 py) |
| 09 save a script | 62 / 68 · 2 | 134 / 8 · 1 (1 py) | 138 / 12 · 1 (1 py) |
| 10 run a script from a folder | 32 / 116 · 1 | 86 / 39 · 1 (1 py) | 89 / 40 · 1 (1 py) |
| 12 Karamba beam (state, dropdown) | 143 / 561 · 1 | 1,503 / 6,541 · 13 (1 py) | 1,236 / 25,782 · 11 (1 py) |
| 13 Marina Bay Sands script | 640 / 859 · 2 | 704 / 822 · 2 (1 py) | 707 / 706 · 2 (1 py) |
| 14 house → FE lines/shells → Karamba | 1,642 / 608 · 3 | 4,101 / 11,048 · 18 (3 py) | 3,791 / 42,631 · 17 (3 py) |
| 15 data tree via Python + wire | 214 / 99 · 2 | 290 / 366 · 4 (1 py) | 262 / 930 · 2 (1 py) |
| 16 messy canvas → regroup + tidy | 83 / 324 · 2 | 732 / 4,547 · 2 (1 py) | 537 / 13,286 · 3 (1 py) |
| 17 freeform canopy (+ inspect, raise crown) | 513 / 606 · 4 | 1,159 / 118 · 3 (3 py) | 1,168 / 240 · 3 (3 py) |
| 18 GH data trees (+ swap graft) | 166 / 221 · 2 | 1,725 / 5,587 · 10 (2 py) | 1,288 / 24,396 · 8 (2 py) |
| 19 1,224-object model: overview, find, count, image | 62 / 923 · 4 | 254 / 817 · 5 (3 py) | 190 / 1,212 · 4 (2 py) |
| 20 Karamba shell plate (+ thicker) | 280 / 264 · 2 | 2,305 / 9,515 · 16 (2 py) | 1,807 / 39,747 · 16 (1 py) |

## Whole session (all cases in one conversation)

| | calls | of which Python fallback | output | input from results | fixed context | **total** | billed input, no cache |
|---|---|---|---|---|---|---|---|
| Nextra | 43 | 0 | 5,232 | 8,166 | 2,756 | **16,154** | 426,146 |
| McNeel | 108 | 34 | 23,178 | 48,995 | 4,779 | **76,952** (4.8×) | 4,121,513 |
| RhinoMCP | 96 | 32 | 20,723 | 193,986 | 23,029 | **237,738** (14.7×) | 11,456,003 |

## Correctness (neutral checks after each case)

| case | Nextra | McNeel | RhinoMCP |
|---|---|---|---|
| 01 | {'model': "{'Slabs': 3, 'Beams': 51, 'Columns': 36}"} | {'model': "{'Slabs': 3, 'Beams': 51, 'Columns': 36}"} | {'model': "{'Slabs': 3, 'Beams': 51, 'Columns': 36}"} |
| 02 | {'after_move': '[\'1 object(s); units millimeters\', \'r12739 polysurface solid [Slabs] "S_L4" 18600x12600x250 | {'after_move': '[\'1 object(s); units millimeters\', \'r12830 polysurface solid [Slabs] "S_L4" 18600x12600x250 | {'after_move': '[\'1 object(s); units millimeters\', \'r12950 polysurface solid [Slabs] "S_L4" 18600x12600x250 |
| 03 | {'gh': "groups 4 errors 0 dangling 0 {'Loft': 1, 'Cap Holes': 1}", 'case04': {'gh': "groups 4 errors 0 danglin | {'gh': "groups 4 errors 0 dangling 0 {'Loft': 1, 'Cap Holes': 1}", 'case04': {'gh': "groups 4 errors 0 danglin | {'gh': "groups 4 errors 0 dangling 0 {'Loft': 1, 'Cap Holes': 1}", 'case04': {'gh': "groups 4 errors 0 danglin |
| 04 | {'gh': "groups 4 errors 0 dangling 0 {'Cap Holes': 1, 'Circle': 12}"} | {'gh': "groups 4 errors 0 dangling 0 {'Cap Holes': 1, 'Circle': 12}"} | {'gh': "groups 4 errors 0 dangling 0 {'Cap Holes': 1, 'Circle': 12}"} |
| 05 | {'gh': "groups 2 errors 0 dangling 0 {'Extrude': 24}", 'model': "{'Fins': 24, 'Slab': 1}"} | {'gh': "groups 2 errors 0 dangling 0 {'Extrude': 24}", 'model': "{'Fins': 24, 'Slab': 1}"} | {'gh': "groups 2 errors 0 dangling 0 {'Extrude': 24}", 'model': "{'Fins': 24, 'Slab': 1}"} |
| 06 | {'gh': "groups 1 errors 0 dangling 0 {'Python 3 Script': 10}"} | {'gh': "groups 0 errors 0 dangling 0 {'Python 3 Script': 10}"} | {'gh': "groups 0 errors 0 dangling 0 {'Python 3 Script': 10}"} |
| 07 | {'gh': "groups 1 errors 0 dangling 0 {'C# Script': 8}"} | {'gh': "groups 0 errors 0 dangling 0 {'C# Script': 8}"} | {'gh': "groups 0 errors 0 dangling 0 {'C# Script': 8}"} |
| 08 | {'choice': "VAL ['Timber']"} | {'choice': "VAL ['Timber']"} | {'choice': "VAL ['Timber']"} |
| 09 | {'saved': True} | {'saved': True} | {'saved': True} |
| 10 | {'model': "{'Slabs': 2, 'Beams': 34, 'Columns': 24}"} | {'model': "{'Slabs': 2, 'Beams': 34, 'Columns': 24}"} | {'model': "{'Slabs': 2, 'Beams': 34, 'Columns': 24}"} |
| 12 | {'max_disp_cm': "VAL ['47.037724']"} | {'max_disp_cm': "VAL ['47.037724']"} | {'max_disp_cm': "VAL ['47.037724']"} |
| 13 | {'model': "{'MBS::Podium': 1, 'MBS::Skypark': 1, 'MBS::Towers': 6}"} | {'model': "{'MBS::Podium': 1, 'MBS::Skypark': 1, 'MBS::Towers': 6}"} | {'model': "{'MBS::Podium': 1, 'MBS::Skypark': 1, 'MBS::Towers': 6}"} |
| 14 | {'mass_kg': "VAL ['99582.500668']", 'max_disp_cm': "VAL ['0.096729']", 'model': "{'Shells': 6, 'Lines': 14, 'A | {'mass_kg': "VAL ['99582.500668']", 'max_disp_cm': "VAL ['0.096729']", 'model': "{'Shells': 6, 'Lines': 14, 'A | {'mass_kg': "VAL ['99582.500668']", 'max_disp_cm': "VAL ['0.096729']", 'model': "{'Shells': 6, 'Lines': 14, 'A |
| 15 | {'gh': "groups 1 errors 0 dangling 0 {'Mass Addition': 7}"} | {'gh': "groups 0 errors 0 dangling 0 {'Mass Addition': 7}"} | {'gh': "groups 0 errors 0 dangling 0 {'Mass Addition': 7}"} |
| 16 | {'gh': "groups 6 errors 0 dangling 0 {'Cap Holes': 72}", 'overlaps': 'OVERLAPS 0'} | {'gh': "groups 6 errors 0 dangling 0 {'Cap Holes': 72}", 'overlaps': 'OVERLAPS 0'} | {'gh': "groups 6 errors 0 dangling 0 {'Cap Holes': 72}", 'overlaps': 'OVERLAPS 0'} |
| 17 | {'model': "{'Canopy Columns': 8, 'Canopy': 1}", 'crown': '8650 n 1\r\nrhino: no changes'} | {'model': "{'Canopy Columns': 8, 'Canopy': 1}", 'crown': '8650 n 1\r\nrhino: no changes'} | {'model': "{'Canopy Columns': 8, 'Canopy': 1}", 'crown': '8650 n 1\r\nrhino: no changes'} |
| 18 | {'before': "groups 3 errors 0 dangling 0 {'Interpolate': 48, 'Loft': 1}", 'after': 'MAP Graft None branches [6 | {'before': "groups 3 errors 0 dangling 0 {'Interpolate': 48, 'Loft': 1}", 'after': 'MAP Graft None branches [6 | {'before': "groups 3 errors 0 dangling 0 {'Interpolate': 48, 'Loft': 1}", 'after': 'MAP Graft None branches [6 |
| 19 | {'longest_beam_ok': True, 'region_count_ok': True} | {'longest_beam_ok': True, 'region_count_ok': True} | {'longest_beam_ok': True, 'region_count_ok': True} |
| 20 | {'disp_20cm': "VAL ['2.902301']", 'disp_30cm': "VAL ['1.289912']"} | {'disp_20cm': "VAL ['2.902301']", 'disp_30cm': "VAL ['1.289912']"} | {'disp_20cm': "VAL ['2.902301']", 'disp_30cm': "VAL ['1.289912']"} |
