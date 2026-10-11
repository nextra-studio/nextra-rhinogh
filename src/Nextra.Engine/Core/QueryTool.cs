using System;
using System.Collections.Generic;
using System.Linq;
using Nextra.Engine.Gh;
using Nextra.Engine.Rh;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Nextra.Engine
{
    /// `query`: text answers, deepest-first never. Levels go summary → list → detail → verts,
    /// so the LLM pays for vertices only when it explicitly asks for them.
    public static class QueryTool
    {
        public static void Run(Args a, Reply r)
        {
            var scope = (a.Str("scope", "rhino") ?? "rhino").ToLowerInvariant();
            switch (scope)
            {
                case "rhino": Rhino(a, r); break;
                case "gh": case "grasshopper": GhQuery.Run(a, r); break;
                case "components": case "plugins": case "gh_types": ComponentSearch.Run(a, r); break;
                case "docs": DocsStore.Query(a, r); break;
                case "library": Library.Query(a, r); break;
                case "api": case "sdk": case "rhinocommon": ApiDocs.Query(a, r); break;
                case "status": r.Line("engine ok; " + (RhinoDoc.ActiveDoc?.Name ?? "untitled") + "; gh " + (GhCore.Doc(false) != null ? "open" : "not open")); break;
                default: throw new NxError($"scope '{scope}' unknown", "scope: rhino | gh | components | api | docs | library | status");
            }
        }

        static void Rhino(Args a, Reply r)
        {
            var doc = RhinoDoc.ActiveDoc ?? throw new NxError("no active Rhino document");
            var filterStr = a.Str("filter", null);
            var f = Filter.Parse(filterStr);
            if (f.BadHandles.Count > 0)
                r.Line($"unknown handles: {string.Join(" ", f.BadHandles)} (deleted, or from another session; run query to re-list)");
            var level = (a.Str("level", f.IsEmpty ? "summary" : "list") ?? "list").ToLowerInvariant();
            int limit = a.Int("limit", level == "detail" ? 10 : level == "verts" ? 3 : 60);
            int offset = a.Int("offset", 0);

            var objs = f.Apply(doc);
            if (level == "summary") { Summary(doc, objs, f.IsEmpty, r); return; }
            if (objs.Count == 0)
            {
                r.Line($"0 objects match '{filterStr}'.");
                r.Line("hint: query level:'summary' lists layers and types.");
                return;
            }
            var page = objs.Skip(offset).Take(limit).ToList();
            var units = doc.ModelUnitSystem.ToString().ToLowerInvariant();
            r.Line($"{objs.Count} object(s){(objs.Count > page.Count ? $", showing {offset + 1}-{offset + page.Count}" : "")}; units {units}");
            foreach (var o in page)
            {
                switch (level)
                {
                    case "detail": r.Line(Geo.Detail(o)); break;
                    case "verts": r.Line(Geo.Verts(o, a.Int("max_points", 100))); break;
                    default: r.Line(Geo.Describe(o)); break;
                }
            }
            if (objs.Count > offset + page.Count) r.Line($"more: offset:{offset + page.Count}");
        }

        static void Summary(RhinoDoc doc, List<RhinoObject> objs, bool whole, Reply r)
        {
            var units = doc.ModelUnitSystem.ToString().ToLowerInvariant();
            r.Line($"doc '{doc.Name ?? "untitled"}' units {units} tol {Fmt.N(doc.ModelAbsoluteTolerance)}; {objs.Count} objects" +
                   (doc.Objects.GetSelectedObjects(false, false).Any() ? $", {doc.Objects.GetSelectedObjects(false, false).Count()} selected" : ""));
            if (objs.Count > 0)
            {
                var bb = BoundingBox.Empty;
                foreach (var o in objs) bb.Union(o.Geometry.GetBoundingBox(true));
                r.Line($"extents {Fmt.P(bb.Min)}..{Fmt.P(bb.Max)} size {Fmt.Dims(bb.Diagonal.X, bb.Diagonal.Y, bb.Diagonal.Z)}");
            }
            // layer × type matrix, compact
            var byLayer = objs.GroupBy(o => o.Attributes.LayerIndex).OrderBy(g => doc.Layers[g.Key]?.FullPath);
            foreach (var g in byLayer)
            {
                var l = doc.Layers[g.Key];
                var types = g.GroupBy(o => Geo.TypeWord(o.Geometry)).OrderByDescending(t => t.Count()).Select(t => $"{t.Count()} {t.Key}");
                var hs = g.Count() <= 6 ? " (" + ViewTool.Ranges(g.Select(o => Handles.Rh(o.Id))) + ")" : "";
                r.Line($"[{l?.FullPath}]{(l != null && !l.IsVisible ? " (off)" : "")}: {string.Join(", ", types)}{hs}");
            }
            if (whole)
            {
                var empty = doc.Layers.Where(l => !l.IsDeleted && !objs.Any(o => o.Attributes.LayerIndex == l.Index)).Select(l => l.FullPath).ToList();
                if (empty.Count > 0) r.Line("empty layers: " + string.Join(", ", empty.Take(20)) + (empty.Count > 20 ? " …" : ""));
                var blocks = doc.InstanceDefinitions.Where(d => d != null && !d.IsDeleted).Select(d => d.Name).ToList();
                if (blocks.Count > 0) r.Line("block defs: " + string.Join(", ", blocks.Take(15)));
            }
            r.Line("next: query filter:'layer:X' (list) → level:'detail' → level:'verts'; or view.");
        }
    }
}
