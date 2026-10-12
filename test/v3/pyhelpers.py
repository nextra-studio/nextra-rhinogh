"""Python snippets the OTHER systems must send through their own Python tools for things their native tools cannot do
(component state, right-click menus, Rhino references, script components, bake, groups, flags, layout).
They are written once here but every call that needs one carries its full text, so the tokens are counted."""

GH_HEAD = """import Grasshopper, System
from Grasshopper.Kernel import GH_DataMapping, GH_ParameterSide, GH_ParamAccess, GH_RuntimeMessageLevel
from Grasshopper.Kernel.Special import GH_Group
from Grasshopper.Kernel.Types import GH_Number, GH_Integer
gd = Grasshopper.Instances.ActiveCanvas.Document
def o(i): return gd.FindObject(System.Guid(i), True)
def inp(i, nick): return [p for p in o(i).Params.Input if p.NickName == nick][0]
"""

# set a saved setting anywhere in a component's archive (dropdowns, on-component buttons, modes)
STATE = """def set_state(i, name, index, value):
    from GH_IO.Serialization import GH_Archive
    obj = o(i); a = GH_Archive(); a.CreateNewRoot(True); root = a.GetRootNode; obj.Write(root)
    def walk(c):
        for it in list(c.Items):
            if it.Name == name and (index < 0 or it.Index == index):
                if it.Index >= 0: c.RemoveItem(it.Name, it.Index)
                else: c.RemoveItem(it.Name)
                if isinstance(value, bool): (c.SetBoolean(name, index, value) if index >= 0 else c.SetBoolean(name, value))
                else: (c.SetString(name, index, value) if index >= 0 else c.SetString(name, value))
                return True
        return any(walk(ch) for ch in c.Chunks)
    if not walk(root): raise Exception('state %s not found' % name)
    obj.Read(root); obj.Params.OnParametersChanged(); obj.ExpireSolution(False)
"""

MENU = """def menu(i, path):
    import System.Windows.Forms as F
    m = F.ToolStripDropDownMenu(); o(i).AppendMenuItems(m); items = m.Items; it = None
    for part in path.split('>'):
        it = [x for x in items if isinstance(x, F.ToolStripMenuItem) and x.Text.replace('&', '') == part][0]; items = it.DropDownItems
    it.PerformClick(); o(i).ExpireSolution(False)
"""

REF = """def ref_layer(i, layer):
    import Rhino
    from Grasshopper.Kernel.Types import GH_Curve, GH_Brep
    rd = Rhino.RhinoDoc.ActiveDoc; p = o(i); p.PersistentData.Clear()
    li = rd.Layers.FindByFullPath(layer, -1)
    for ro in rd.Objects.FindByLayer(rd.Layers[li]):
        g = GH_Curve() if p.TypeName == 'Curve' else GH_Brep(); g.ReferenceID = ro.Id; g.LoadGeometry(rd); p.PersistentData.Append(g)
    p.ExpireSolution(False)
def ref_ids(i, ids):
    import Rhino
    from Grasshopper.Kernel.Types import GH_Brep
    rd = Rhino.RhinoDoc.ActiveDoc; p = o(i); p.PersistentData.Clear()
    for rid in ids:
        g = GH_Brep(); g.ReferenceID = System.Guid(rid); g.LoadGeometry(rd); p.PersistentData.Append(g)
    p.ExpireSolution(False)
"""

FLAGS = """def flag(i, nick, f):
    p = inp(i, nick)
    p.DataMapping = {'f': GH_DataMapping.Flatten, 'g': GH_DataMapping.Graft, 'x': GH_DataMapping.None}[f]
    o(i).ExpireSolution(False)
def literal(i, nick, v):
    p = inp(i, nick); p.PersistentData.Clear()
    p.PersistentData.Append(GH_Integer(int(v)) if p.TypeName == 'Integer' else GH_Number(v)); p.ExpireSolution(False)
def expr(i, nick, e):
    inp(i, nick).Expression = e; o(i).ExpireSolution(False)
"""

GROUP = """def group(name, col, ids):
    g = GH_Group(); g.NickName = name; g.Colour = System.Drawing.ColorTranslator.FromHtml(col)
    for k in ids: g.AddObject(System.Guid(k))
    gd.AddObject(g, False)
"""

BAKE = """def bake(i, layer):
    import Rhino
    rd = Rhino.RhinoDoc.ActiveDoc
    li = rd.Layers.FindByFullPath(layer, -1)
    if li < 0: li = rd.Layers.Add(layer, System.Drawing.Color.Black)
    a = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = li
    ids = System.Collections.Generic.List[System.Guid]()
    o(i).BakeGeometry(rd, a, ids)
    print('baked', ids.Count)
"""

# script component (Rhino 8): place it, set exact inputs/outputs, set code. C# needs ports before code.
SCRIPT = """def script(kind, x, y, ins, outs, code):
    import System.Reflection as R
    guid = {'py': '719467e6-7cf5-4848-99b0-c5dd57e5442c', 'cs': 'a9a8ebd2-fff5-4c44-a8f5-739736d129ba'}[kind]
    c = Grasshopper.Instances.ComponentServer.EmitObjectProxy(System.Guid(guid)).CreateInstance()
    c.CreateAttributes(); c.Attributes.Pivot = System.Drawing.PointF(x, y); gd.AddObject(c, False)
    for p in list(c.Params.Input): c.Params.UnregisterInputParameter(p, True)
    for n, acc in ins:
        p = c.CreateParameter(GH_ParameterSide.Input, c.Params.Input.Count); p.Name = p.NickName = n
        p.Access = {'item': GH_ParamAccess.item, 'list': GH_ParamAccess.list}[acc]; c.Params.RegisterInputParam(p)
    for p in [p for p in c.Params.Output if p.NickName != 'out']: c.Params.UnregisterOutputParameter(p, True)
    for n in outs:
        p = c.CreateParameter(GH_ParameterSide.Output, c.Params.Output.Count); p.Name = p.NickName = n; c.Params.RegisterOutputParam(p)
    c.VariableParameterMaintenance(); c.Params.OnParametersChanged()
    set_code(c, code)
    return c
def set_code(c, code):
    import System.Reflection as R
    m = c.GetType().GetMethod('SetSource', R.BindingFlags.Public | R.BindingFlags.NonPublic | R.BindingFlags.Instance, None, [System.String], None)
    m.Invoke(c, [code]); c.ExpireSolution(False)
def errors(c):
    return [str(m) for m in c.RuntimeMessages(GH_RuntimeMessageLevel.Error)]
"""

LAYOUT = """def tidy():
    objs = [x for x in gd.Objects if x.GetType().Name != 'GH_Group']
    rank = {}
    def up(x):
        ps = x.Params.Input if hasattr(x, 'Params') else ([x] if hasattr(x, 'Sources') else [])
        return [s.Attributes.GetTopLevel.DocObject for p in ps for s in p.Sources]
    for _ in range(len(objs)):
        for x in objs: rank[x.InstanceGuid] = 1 + max([rank.get(u.InstanceGuid, 0) for u in up(x)] or [-1])
    cols = {}
    for x in sorted(objs, key=lambda x: rank[x.InstanceGuid]):
        c = rank[x.InstanceGuid]; r = cols.get(c, 0); cols[c] = r + 1
        x.Attributes.Pivot = System.Drawing.PointF(c * 220, r * 90); x.Attributes.ExpireLayout()
    for g in [x for x in gd.Objects if x.GetType().Name == 'GH_Group']: g.ExpireCaches()
"""

TAIL = "\ngd.NewSolution(True)\nprint('ok')"


def gh(*parts, body=""):
    """Assemble a script: header + the helper functions it uses + the case-specific lines + recompute."""
    return GH_HEAD + "".join(parts) + body + TAIL
