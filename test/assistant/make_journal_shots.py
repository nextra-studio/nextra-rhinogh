"""Screenshots for the website's Nexiy guide and journal posts, from a live Rhino with the real panel.
usage: python test/assistant/make_journal_shots.py <step> <out dir>     steps: setup, tidy, gh, massing, restore"""
import os, sys, time
sys.path.insert(0, os.path.dirname(__file__))
import shots as S  # noqa: E402

step, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
P = lambda n: os.path.join(out, n)
press = lambda sel: S.js(f"var e=document.querySelector('{sel}'); if(!e) return 'missing'; e.dispatchEvent(new PointerEvent('pointerdown', {{button:0, bubbles:true}})); e.click && e.click(); return 'ok';")
VIEW = dict(size=[1400, 860], tags="none", grid=False)

if step == "setup":
    S.config(assistant_mode="api", assistant_decider="openai", assistant_chat="openai", assistant_prefs='{\\"mode\\":\\"llm\\",\\"apply\\":\\"auto\\"}')
    S.panel()
    S.shot_panel(P("panel-hello.png"))
    S.js("var g=document.getElementById('gear'); g.dispatchEvent(new PointerEvent('pointerdown',{button:0,bubbles:true})); return 'ok';")
    time.sleep(1.2); S.mask_keys(); time.sleep(0.3)
    S.shot_panel(P("settings-api-keys.png"))
    S.js("document.querySelector('[data-conn=cli]').click(); return 'ok';"); time.sleep(1.5)
    S.shot_panel(P("settings-cli.png"))
    S.js("document.querySelector('[data-conn=desktop]').click(); return 'ok';"); time.sleep(1.5)
    S.shot_panel(P("settings-desktop.png"))
    S.js("document.querySelector('[data-conn=api]').click(); return 'ok';"); time.sleep(1.5); S.mask_keys()
    S.js("var s=document.getElementById('settings'); var t=[...s.querySelectorAll('label')].find(function(l){return l.textContent.indexOf('Nexiy liveliness')===0;}); if(t) s.scrollTop=t.offsetTop-40; return 'ok';"); time.sleep(0.8)
    S.shot_panel(P("settings-nexiy-plugins.png"))
    S.js("var s=document.getElementById('settings'); s.scrollTop=s.scrollHeight; return 'ok';"); time.sleep(0.8)
    S.shot_panel(P("settings-skills-connectors.png"))
    S.config(assistant_mode="desktop"); S.panel()
    S.shot_panel(P("panel-desktop-mode.png"))
    S.config(assistant_mode="api"); S.panel(False)

elif step == "tidy":
    # a messy model: two buildings' worth of parts on Default, duplicates, empty layers, a stray bit
    print(S.py("""import Rhino, scriptcontext
doc = Rhino.RhinoDoc.ActiveDoc
doc.Objects.Clear()
doc.Layers.SetCurrentLayerIndex(0, True)
for l in list(doc.Layers):
    if l.Index != 0 and not l.IsDeleted: doc.Layers.Purge(l.Index, True)
for n in ['Old','Temp','Copy of Walls','tmp 2']:
    lay = Rhino.DocObjects.Layer()
    lay.Name = n
    doc.Layers.Add(lay)
G = Rhino.Geometry
for i in range(6):
    b = G.Box(G.Plane.WorldXY, G.Interval(i*5000, i*5000+300), G.Interval(0, 300), G.Interval(0, 3500))
    doc.Objects.AddBox(b);
    if i % 2 == 0: doc.Objects.AddBox(b)
for i in range(4):
    doc.Objects.AddCurve(G.Rectangle3d(G.Plane.WorldXY, G.Interval(i*7000, i*7000+5000), G.Interval(3000, 7000)).ToNurbsCurve())
doc.Objects.AddLine(G.Point3d(0,9000,0), G.Point3d(25000,9000,0))
doc.Objects.AddPoint(G.Point3d(12000, 4000, 0))
doc.Views.Redraw()
print('messy model', doc.Objects.Count)""")[-300:])
    S.view(P("tidy-before.png"), **VIEW, view="Perspective")
    S.config(assistant_mode="api", assistant_decider="openai", assistant_chat="openai", assistant_prefs='{\\"mode\\":\\"quick\\",\\"apply\\":\\"auto\\"}')
    S.panel()
    for q in ["How many objects are there, and on which layers?", "Delete the empty layers", "Delete duplicate objects", "Tidy up: sort everything onto layers by type"]:
        print(q, "->", S.ask(q, 60))
    S.js("var l=document.getElementById('log'); l.scrollTop=l.scrollHeight; return 'ok';"); time.sleep(0.6)
    S.shot_panel(P("tidy-panel.png"))
    S.view(P("tidy-after.png"), **VIEW, view="Perspective")
    S.panel(False)

elif step == "gh":
    S.py("import Rhino\ndoc = Rhino.RhinoDoc.ActiveDoc\ndoc.Objects.Clear()\ndoc.Views.Redraw()\nprint('clear')")
    import nx  # noqa
    nx.call("gh_edit", {"new": True, "quiet": True}, log=False)
    S.config(assistant_mode="cli", assistant_cli="claude", assistant_cli_model="", assistant_prefs='{\\"mode\\":\\"llm\\",\\"apply\\":\\"auto\\"}')
    S.panel()
    print(S.ask("In Grasshopper, build a definition: a circle with a radius slider (10000, range 1000 to 30000), divided into 24 points, with a vertical line 15000 long from each point. Group the inputs, tidy the canvas, then bake the lines to a layer named Lines.", 240))
    S.js("var l=document.getElementById('log'); l.scrollTop=l.scrollHeight; return 'ok';"); time.sleep(0.6)
    S.shot_panel(P("gh-panel.png"))
    S.view(P("gh-canvas.png"), target="gh", size=[1400, 860])
    S.view(P("gh-baked.png"), **VIEW, view="Perspective")
    S.config(assistant_mode="api"); S.panel(False)

elif step == "massing":
    S.py("import Rhino\ndoc = Rhino.RhinoDoc.ActiveDoc\ndoc.Objects.Clear()\ndoc.Views.Redraw()\nprint('clear')")
    S.config(assistant_mode="api", assistant_decider="openai", assistant_chat="openai", assistant_prefs='{\\"mode\\":\\"llm\\",\\"apply\\":\\"auto\\"}')
    S.panel()
    print(S.ask("Build a 4-storey building massing: 5 by 3 columns at 6 m spacing, storeys of 3.5 m, with floor slabs. Colour the columns blue and the slabs grey.", 180))
    print(S.ask("What is the total volume of the slabs, in cubic metres?", 120))
    print("chat at its end by itself:", S.js("var l=document.getElementById('log'); return String(l.scrollHeight - l.scrollTop - l.clientHeight < 4);"))
    S.shot_panel(P("massing-panel.png"))
    S.view(P("massing-model.png"), **VIEW, view="Perspective", mode="shaded")
    S.panel(False)
print("done", step)
