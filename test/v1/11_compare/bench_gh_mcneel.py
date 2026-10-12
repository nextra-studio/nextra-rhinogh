"""McNeel GH sequence with the recovery an LLM must do after ambiguity errors, on a fresh canvas."""
import json, os, sys
sys.path.insert(0, os.path.dirname(__file__)); sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "tools"))
import nx
import bench_gh_parts as P
from bench_rhino_rows import rows, mcneel, OUT

nx.call("gh_edit", {"new": True}, log=False)
mcneel("G1_build", "g1_search_components", {"query": "Rectangle"})
mcneel("G1_build", "g1_search_components", {"query": "Rotate"})
first = json.loads(mcneel("G1_build", "g1_apply_graph", P.graph))
ids = {p["Key"]: p["Id"] for p in first["Placed"]}
# recovery: the 3 ambiguous components by GUID (guids come from the error text), then their wires by object id
rec = {"components": [
    {"Key": "pt", "Selector": "3581f42a-9592-4549-bd6b-1c0fc39d067b", "X": 400, "Y": 0},
    {"Key": "one", "Selector": "9c007a04-d0d9-48e4-9da3-9ba142bc4d46", "X": 250, "Y": 150},
    {"Key": "mul", "Selector": "ce46b74e-00c9-43c4-805a-193b69ea4a11", "X": 550, "Y": 150}], "sliders": [], "wires": [], "solve": False}
second = json.loads(mcneel("G1_build", "g1_apply_graph", rec))
ids.update({p["Key"]: p["Id"] for p in second["Placed"]})
W = lambda s, sp, d, dp: {"SrcId": ids[s], "Src": sp, "DstId": ids[d], "Dst": dp}
mcneel("G1_build", "g1_connect_many", {"wires": [W("ser", "S", "pt", "Z"), W("pt", "Pt", "pl", "O"), W("n", "0", "one", "A"),
                                                  W("one", "R", "ang", "N"), W("ang", "R", "mul", "A"), W("tw", "0", "mul", "B"),
                                                  W("mul", "R", "rad", "D")], "solve": True})
mcneel("G1_build", "run_python", {"script": P.fix})
mcneel("G1_build", "g1_get_canvas_graph", {"include_data": False})
mcneel("G2_modify", "run_python", {"script": "import System\n" + P.mod})
mcneel("G2_modify", "g1_get_canvas_graph", {"include_data": False})
mcneel("G3_readback", "g1_get_canvas_graph", {"include_data": True, "sample_size": 3})
json.dump(rows, open(os.path.join(OUT, "rows_gh_mcneel.json"), "w"), indent=1)
for r in rows: print(r)
