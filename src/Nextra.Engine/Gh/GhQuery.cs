using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Nextra.Engine.Gh.Shorthand;

namespace Nextra.Engine.Gh
{
    /// `query scope:'gh'`: summary → list (shorthand) → detail (full state, code, data) → data (values).
    /// Filter tokens: g12 · group:Name · type:Loft · name:nick* · errors · warnings · sel · cells:C3:E5 · near:g12[:2] · all
    public static class GhQuery
    {
        public static List<IGH_DocumentObject> Select(GH_Document d, string filter, out List<string> notes)
        {
            notes = new List<string>();
            var all = d.Objects.ToList();
            if (string.IsNullOrWhiteSpace(filter) || filter == "all" || filter == "*") return all.Where(o => !(o is GH_Group)).ToList();
            IEnumerable<IGH_DocumentObject> cur = all;
            var handles = new List<IGH_DocumentObject>();
            bool anyHandle = false;
            foreach (var tok in filter.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = tok.Trim();
                int c = t.IndexOf(':');
                string k = c > 0 ? t.Substring(0, c).ToLowerInvariant() : t.ToLowerInvariant();
                string v = c > 0 ? t.Substring(c + 1) : null;
                if (Handles.IsGhHandle(t))
                {
                    anyHandle = true;
                    var o = GhCore.Find(d, t);
                    if (o == null) notes.Add($"{t} not found");
                    else if (o is GH_Group g) handles.AddRange(g.ObjectIDs.Select(id => d.FindObject(id, false)).Where(x => x != null));
                    else handles.Add(o);
                    continue;
                }
                switch (k)
                {
                    case "group":
                        {
                            var gs = all.OfType<GH_Group>().Where(g => Fmt.Like(g.NickName, v)).ToList();
                            var ids = new HashSet<Guid>(gs.SelectMany(g => g.ObjectIDs));
                            cur = cur.Where(o => ids.Contains(o.InstanceGuid));
                            break;
                        }
                    case "type": cur = cur.Where(o => Fmt.Like(o.Name, v)); break;
                    case "name": case "nick": cur = cur.Where(o => Fmt.Like(o.NickName, v)); break;
                    case "errors": cur = cur.Where(o => o is IGH_ActiveObject ao && ao.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error); break;
                    case "warnings": cur = cur.Where(o => o is IGH_ActiveObject ao && ao.RuntimeMessageLevel >= GH_RuntimeMessageLevel.Warning); break;
                    case "sel": case "selected": cur = cur.Where(o => o.Attributes?.Selected == true); break;
                    case "scripts": cur = cur.Where(Scripts.IsScript); break;
                    case "cells": case "cell":
                        {
                            var rect = GhView.CellRect(v);
                            cur = cur.Where(o => o.Attributes != null && rect.Contains(Center(o.Attributes.Bounds)));
                            break;
                        }
                    case "near":
                        {
                            var parts = v.Split(':');
                            var o = GhCore.Find(d, parts[0]) ?? throw new NxError($"{parts[0]} not found");
                            int depth = parts.Length > 1 ? int.Parse(parts[1]) : 1;
                            var set = Neighbourhood(o, depth);
                            cur = cur.Where(x => set.Contains(x.InstanceGuid));
                            break;
                        }
                    default: throw new NxError($"unknown gh filter '{t}'", "g12 · group:Name · type:Loft · name:nick · errors · warnings · sel · scripts · cells:C3:E5 · near:g12[:2]");
                }
            }
            var res = (anyHandle ? handles.Where(h => cur.Contains(h)) : cur).Where(o => !(o is GH_Group)).Distinct().ToList();
            return res;
        }

        static PointF Center(RectangleF r) => new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);

        public static HashSet<Guid> Neighbourhood(IGH_DocumentObject start, int depth)
        {
            var seen = new HashSet<Guid> { start.InstanceGuid };
            var frontier = new List<IGH_DocumentObject> { start };
            for (int i = 0; i < depth; i++)
            {
                var next = new List<IGH_DocumentObject>();
                foreach (var o in frontier)
                {
                    var ps = o is IGH_Component c ? c.Params.Input.Concat(c.Params.Output) : o is IGH_Param pp ? new[] { pp } : Enumerable.Empty<IGH_Param>();
                    foreach (var p in ps)
                        foreach (var q in p.Sources.Concat(p.Recipients))
                        {
                            var t = q.Attributes?.GetTopLevel?.DocObject;
                            if (t != null && seen.Add(t.InstanceGuid)) next.Add(t);
                        }
                }
                frontier = next;
            }
            return seen;
        }

        public static void Run(Args a, Reply r)
        {
            var d = GhCore.Doc(false);
            if (d == null) { r.Line("no Grasshopper document open. gh_edit {new:true} or {open:'path.gh'} creates one; gh_edit {ops} also creates one."); return; }
            var filter = a.Str("filter", null);
            var objs = Select(d, filter, out var notes);
            foreach (var n in notes) r.Line(n);
            var level = (a.Str("level", string.IsNullOrEmpty(filter) ? "summary" : "list") ?? "list").ToLowerInvariant();
            int limit = a.Int("limit", level == "detail" ? 6 : 120), offset = a.Int("offset", 0);
            switch (level)
            {
                case "summary": Summary(d, r); break;
                case "list":
                    {
                        var page = objs.Skip(offset).Take(limit).ToList();
                        r.Line($"{objs.Count} object(s){(objs.Count > page.Count ? $", {offset + 1}-{offset + page.Count}" : "")}:");
                        r.Line(Writer.Write(d, page, new WriteOptions { Messages = true, Data = a.Bool("data", false), MaxObjects = limit }));
                        if (objs.Count > offset + page.Count) r.Line($"more: offset:{offset + page.Count}");
                        break;
                    }
                case "detail":
                    foreach (var o in objs.Skip(offset).Take(limit))
                    {
                        r.Line(Writer.Write(d, new[] { o }, new WriteOptions { Messages = true, Data = true, Compact = false, FullState = true }).Replace("## ", "# in group: "));
                        if (o is IGH_Component c)
                        {
                            r.Line("  " + ComponentSearch.Describe(c, false));
                            r.Line("  " + ComponentSearch.Describe(c, true));
                            if (Scripts.IsScript(o))
                            {
                                var code = Scripts.GetCode(c);
                                r.Line($"  code ({Scripts.Lang(c)}):");
                                r.Line(Scripts.Numbered(code, a.Int("from", 1), a.Int("to", 400)));
                            }
                        }
                    }
                    break;
                case "data":
                    {
                        var port = a.Str("port", null);
                        foreach (var o in objs.Take(limit))
                        {
                            var ps = o is IGH_Component c ? (port != null ? new List<IGH_Param> { ComponentSearch.FindPort(c, port, true, out var e) ?? ComponentSearch.FindPort(c, port, false, out e) ?? throw new NxError(e) } : c.Params.Output) : new List<IGH_Param> { (IGH_Param)o };
                            foreach (var p in ps) Data(d, o, p, a.Int("items", 20), r);
                        }
                        break;
                    }
                default: throw new NxError($"level '{level}' unknown", "summary | list | detail | data");
            }
        }

        static void Data(GH_Document d, IGH_DocumentObject o, IGH_Param p, int max, Reply r)
        {
            var data = p.VolatileData;
            r.Line($"{GhCore.Handle(d, o)}.{p.NickName}: {Writer.DataSummary(p) ?? "empty"}");
            int shown = 0;
            for (int i = 0; i < data.PathCount && shown < max; i++)
            {
                var br = data.get_Branch(i);
                var items = new List<string>();
                foreach (var x in br) { if (shown++ >= max) break; items.Add(x is Grasshopper.Kernel.Types.IGH_Goo g ? Lit(g) : x?.ToString()); }
                r.Line($"  {data.get_Path(i)}: {string.Join(" ", items)}");
            }
        }

        static string Lit(Grasshopper.Kernel.Types.IGH_Goo g)
        {
            var s = Writer.Lit(g);
            if (s.StartsWith("<"))
            {
                // geometry: short description instead of raw data
                var geo = Grasshopper.Kernel.GH_Convert.ToGeometryBase(g);
                if (geo != null)
                {
                    var obb = Rh.Geo.ComputeObb(geo);
                    return $"{Rh.Geo.TypeWord(geo)}[{(obb != null ? obb.Text() : "")}]";
                }
            }
            return s;
        }

        static void Summary(GH_Document d, Reply r)
        {
            var objs = d.Objects.ToList();
            var comps = objs.OfType<IGH_Component>().Count();
            var groups = objs.OfType<GH_Group>().ToList();
            var parms = objs.Count(o => o is IGH_Param);
            r.Line($"gh doc '{(d.IsFilePathDefined ? System.IO.Path.GetFileName(d.FilePath) : "unsaved")}': {comps} components, {parms} params, {groups.Count} groups, {objs.Count(o => o is GH_Scribble)} scribbles");
            foreach (var g in groups.OrderBy(g => g.Attributes?.Bounds.X ?? 0).Take(30))
                r.Line($"## {g.NickName} ({GhCore.Handle(d, g)}): {g.ObjectIDs.Count} members " + ViewTool.Ranges(g.ObjectIDs.Select(id => d.FindObject(id, false)).Where(o => o != null).Select(o => GhCore.Handle(d, o))));
            var ungrouped = objs.Where(o => !(o is GH_Group) && !groups.Any(g => g.ObjectIDs.Contains(o.InstanceGuid))).ToList();
            if (ungrouped.Count > 0) r.Line($"ungrouped: {ungrouped.Count} " + ViewTool.Ranges(ungrouped.Select(o => GhCore.Handle(d, o)).Take(60)));
            var msgs = GhCore.Messages(d);
            var errs = msgs.Where(m => m.level == "error").ToList();
            var warns = msgs.Where(m => m.level == "warn").ToList();
            r.Line($"errors {errs.Select(m => m.o).Distinct().Count()}, warnings {warns.Select(m => m.o).Distinct().Count()}");
            foreach (var m in errs.Take(8)) r.Line($" {GhCore.Handle(d, m.o)} {m.o.Name} ! {Fmt.Trunc(m.msg.Replace("\n", " "), 140)}");
            foreach (var m in warns.Take(4)) r.Line($" {GhCore.Handle(d, m.o)} {m.o.Name} ? {Fmt.Trunc(m.msg.Replace("\n", " "), 120)}");
            var plugins = objs.Where(o => !new[] { "Params", "Maths", "Sets", "Vector", "Curve", "Surface", "Mesh", "Intersect", "Transform", "Display", "Rhino", "Script" }.Contains(o.Category) && !string.IsNullOrEmpty(o.Category)).Select(o => o.Category).Distinct().ToList();
            if (plugins.Count > 0) r.Line("plug-ins used: " + string.Join(", ", plugins));
            var sel = d.SelectedObjects();
            if (sel.Count > 0) r.Line("selected: " + ViewTool.Ranges(sel.Select(o => GhCore.Handle(d, o))));
            r.Line("next: query {scope:'gh', filter:'group:Name' | 'g12' | 'errors'} (shorthand) → level:'detail'; view {target:'gh'}");
        }
    }
}
