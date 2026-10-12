G = {"Rectangle": "d93100b6-d50b-40b2-831a-814659dc38e3", "Rotate": "b7798b74-037e-4f0c-8ac7-dc1043d093e0"}
graph = {
    "sliders": [
        {"Key": "n", "Min": 2, "Value": 12, "Max": 40, "Type": "int", "Name": "Floors", "X": 0, "Y": 0},
        {"Key": "fh", "Min": 2.5, "Value": 3.5, "Max": 5, "Type": "float", "Name": "Floor h", "X": 0, "Y": 40},
        {"Key": "w", "Min": 5, "Value": 20, "Max": 40, "Type": "float", "Name": "Width", "X": 0, "Y": 80},
        {"Key": "tw", "Min": 0, "Value": 45, "Max": 180, "Type": "int", "Name": "Twist", "X": 0, "Y": 120}],
    "components": [
        {"Key": "ser", "Selector": "Series", "X": 250, "Y": 0}, {"Key": "pt", "Selector": "Construct Point", "X": 400, "Y": 0},
        {"Key": "pl", "Selector": "XY Plane", "X": 550, "Y": 0}, {"Key": "rec", "Selector": G["Rectangle"], "X": 700, "Y": 0},
        {"Key": "one", "Selector": "Subtraction", "X": 250, "Y": 150}, {"Key": "ang", "Selector": "Range", "X": 400, "Y": 150},
        {"Key": "mul", "Selector": "Multiplication", "X": 550, "Y": 150}, {"Key": "rad", "Selector": "Radians", "X": 700, "Y": 150},
        {"Key": "rot", "Selector": G["Rotate"], "X": 850, "Y": 80}, {"Key": "lft", "Selector": "Loft", "X": 1000, "Y": 80},
        {"Key": "cap", "Selector": "Cap Holes", "X": 1150, "Y": 80}],
    "wires": [
        {"SrcKey": "fh", "Src": "0", "DstKey": "ser", "Dst": "N"}, {"SrcKey": "n", "Src": "0", "DstKey": "ser", "Dst": "C"},
        {"SrcKey": "ser", "Src": "S", "DstKey": "pt", "Dst": "Z"}, {"SrcKey": "pt", "Src": "Pt", "DstKey": "pl", "Dst": "O"},
        {"SrcKey": "pl", "Src": "P", "DstKey": "rec", "Dst": "P"}, {"SrcKey": "w", "Src": "0", "DstKey": "rec", "Dst": "X"},
        {"SrcKey": "w", "Src": "0", "DstKey": "rec", "Dst": "Y"}, {"SrcKey": "n", "Src": "0", "DstKey": "one", "Dst": "A"},
        {"SrcKey": "one", "Src": "R", "DstKey": "ang", "Dst": "N"}, {"SrcKey": "ang", "Src": "R", "DstKey": "mul", "Dst": "A"},
        {"SrcKey": "tw", "Src": "0", "DstKey": "mul", "Dst": "B"}, {"SrcKey": "mul", "Src": "R", "DstKey": "rad", "Dst": "D"},
        {"SrcKey": "rec", "Src": "R", "DstKey": "rot", "Dst": "G"}, {"SrcKey": "rad", "Src": "R", "DstKey": "rot", "Dst": "A"},
        {"SrcKey": "pl", "Src": "P", "DstKey": "rot", "Dst": "P"}, {"SrcKey": "rot", "Src": "G", "DstKey": "lft", "Dst": "C"},
        {"SrcKey": "lft", "Src": "L", "DstKey": "cap", "Dst": "B"}],
    "solve": True}
fix = """import Grasshopper, System
from Grasshopper.Kernel import GH_DataMapping
from Grasshopper.Kernel.Special import GH_Group
doc = Grasshopper.Instances.ActiveCanvas.Document
byNick = {}
for o in doc.Objects: byNick.setdefault(o.Name, o)
sub = byNick['Subtraction']; sub.Params.Input[1].PersistentData.Clear(); sub.Params.Input[1].PersistentData.Append(Grasshopper.Kernel.Types.GH_Number(1))
byNick['Loft'].Params.Input[0].DataMapping = GH_DataMapping.Flatten
def group(name, names, col):
    g = GH_Group(); g.NickName = name; g.Colour = col
    for o in doc.Objects:
        if o.Name in names or o.NickName in names: g.AddObject(o.InstanceGuid)
    doc.AddObject(g, False)
group('Inputs', ['Floors','Floor h','Width','Twist'], System.Drawing.Color.FromArgb(150,219,171,255))
group('Floors', ['Series','Construct Point','XY Plane','Rectangle'], System.Drawing.Color.FromArgb(150,171,219,255))
group('Twist', ['Subtraction','Range','Multiplication','Radians','Rotate'], System.Drawing.Color.FromArgb(150,255,214,171))
group('Massing', ['Loft','Cap Holes'], System.Drawing.Color.FromArgb(150,178,250,180))
doc.NewSolution(False)
print('ok')"""
mod = """import Grasshopper
doc = Grasshopper.Instances.ActiveCanvas.Document
o = {x.Name: x for x in doc.Objects}
rec, pl, w, rot = o['Rectangle'], o['XY Plane'], [x for x in doc.Objects if x.NickName == 'Width'][0], o['Rotate']
cir = Grasshopper.Instances.ComponentServer.EmitObject(System.Guid('807b86e3-be8d-4970-92b5-f8cdcb45b06b'))
cir.CreateAttributes(); cir.Attributes.Pivot = rec.Attributes.Pivot
doc.AddObject(cir, False)
cir.Params.Input[0].AddSource(pl.Params.Output[0])
cir.Params.Input[1].AddSource(w); cir.Params.Input[1].Expression = 'x/2'
rot.Params.Input[0].RemoveAllSources(); rot.Params.Input[0].AddSource(cir.Params.Output[0])
doc.RemoveObject(rec, False)
doc.NewSolution(False)
print('ok')"""
