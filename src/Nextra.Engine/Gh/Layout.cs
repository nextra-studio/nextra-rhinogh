using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Nextra.Engine.Gh
{
    /// Layered (Sugiyama-style) layout, two levels: micro (members inside each group) and macro
    /// (groups and loose objects as blocks). Ported from nextra.gh with these changes:
    ///  - y is the barycenter of ALL downstream grips (not just the first wire), sinks re-aligned to sources
    ///  - Place(): only the given objects move; the block is anchored beside its existing neighbours
    ///    and nudged clear of everything else (modification case)
    ///  - Tidy(): whole canvas or subset, keeping groups as blocks
    public static class Layout
    {
        class Item
        {
            public string Id;
            public IGH_DocumentObject Obj;      // null for macro blocks
            public float W, H, X, Y;            // X,Y = top-left
            public int Rank;
            public bool Scribble;
            public RectangleF Rect => new RectangleF(X, Y, W, H);
        }

        class Edge { public string From, To; public int FromPort, ToPort; }

        public const float MicroX = 90f, MicroY = 22f, MacroX = 120f, MacroY = 70f, GroupPad = 12f, GroupTitle = 22f;

        // ---------- public entry points ----------

        /// Modification placement. New nodes that joined an existing group: that group is re-laid out in
        /// place (top-left kept) and anything it now overlaps is pushed down. Other new nodes: placed as a
        /// block next to their existing neighbours.
        public static string Place(GH_Document d, List<IGH_DocumentObject> created)
        {
            created = created.Where(o => !(o is GH_Group) && o.Attributes != null).ToList();
            if (created.Count == 0) return null;
            var newSet = new HashSet<Guid>(created.Select(o => o.InstanceGuid));
            var joined = new HashSet<Guid>();
            foreach (var g in d.Objects.OfType<GH_Group>())
            {
                var members = g.ObjectIDs.Select(id => d.FindObject(id, false)).Where(o => o != null && !(o is GH_Group) && o.Attributes != null).ToList();
                var old = members.Where(o => !newSet.Contains(o.InstanceGuid)).ToList();
                var add = members.Where(o => newSet.Contains(o.InstanceGuid)).ToList();
                if (old.Count == 0 || add.Count == 0) continue;
                var anchor = Bounds(old);
                // seed new members right of the group so the layered layout has a sane start
                float x0 = anchor.Right + MicroX;
                foreach (var o in add) SetPivot(o, new PointF(x0, anchor.Y));
                Arrange(d, members, out var blk);
                Shift(members, anchor.X - blk.X, anchor.Y - blk.Y);
                foreach (var o in add) joined.Add(o.InstanceGuid);
                g.ExpireCaches(); g.Attributes?.ExpireLayout();
                PushAway(d, g, members);
            }
            var moving = created.Where(o => !joined.Contains(o.InstanceGuid)).ToList();
            if (moving.Count > 0) PlaceBlock(d, moving);
            RepairBounds(d);
            return null;
        }

        /// Shift every other group / loose object that overlaps group g downwards (cascading).
        static void PushAway(GH_Document d, GH_Group g, List<IGH_DocumentObject> members)
        {
            var fixedSet = new HashSet<Guid>(members.Select(o => o.InstanceGuid));
            var queue = new Queue<RectangleF>();
            queue.Enqueue(Inflate(GroupRect(d, g), 15));
            int safety = 0;
            while (queue.Count > 0 && safety++ < 200)
            {
                var r = queue.Dequeue();
                // blocks = other groups (as units) + loose objects
                var blocks = new List<List<IGH_DocumentObject>>();
                var seen = new HashSet<Guid>(fixedSet);
                foreach (var og in d.Objects.OfType<GH_Group>())
                {
                    if (og == g) continue;
                    var ms = og.ObjectIDs.Select(id => d.FindObject(id, false)).Where(o => o != null && !(o is GH_Group) && o.Attributes != null && !seen.Contains(o.InstanceGuid)).ToList();
                    if (ms.Count == 0) continue;
                    foreach (var m in ms) seen.Add(m.InstanceGuid);
                    blocks.Add(ms);
                }
                foreach (var o in d.Objects.Where(o => !(o is GH_Group) && o.Attributes != null && !seen.Contains(o.InstanceGuid))) blocks.Add(new List<IGH_DocumentObject> { o });
                foreach (var b in blocks)
                {
                    var br = Inflate(Bounds(b), GroupPad + 4); br.Y -= GroupTitle;
                    if (!br.IntersectsWith(r)) continue;
                    float dy = r.Bottom + 10 - br.Y;
                    Shift(b, 0, dy);
                    foreach (var m in b) fixedSet.Add(m.InstanceGuid);
                    br.Y += dy;
                    queue.Enqueue(br);
                }
            }
            ExpireGroups(d, new HashSet<Guid>(d.Objects.Select(o => o.InstanceGuid)));
        }

        static string PlaceBlock(GH_Document d, List<IGH_DocumentObject> moving)
        {
            var before = Bounds(moving);
            Arrange(d, moving, out var block);
            // anchor
            var set = new HashSet<Guid>(moving.Select(o => o.InstanceGuid));
            var up = new List<IGH_DocumentObject>(); var down = new List<IGH_DocumentObject>();
            foreach (var o in moving)
            {
                foreach (var p in InputsOf(o)) foreach (var s in p.Sources) { var t = Top(s); if (t != null && !set.Contains(t.InstanceGuid)) up.Add(t); }
                foreach (var p in OutputsOf(o)) foreach (var r in p.Recipients) { var t = Top(r); if (t != null && !set.Contains(t.InstanceGuid)) down.Add(t); }
            }
            var others = d.Objects.Where(o => !set.Contains(o.InstanceGuid) && !(o is GH_Group) && o.Attributes != null).ToList();
            PointF target;
            if (up.Count > 0)
                target = new PointF(up.Max(o => o.Attributes.Bounds.Right) + MacroX, up.Min(o => o.Attributes.Bounds.Top));
            else if (down.Count > 0)
                target = new PointF(down.Min(o => o.Attributes.Bounds.Left) - block.Width - MacroX, down.Min(o => o.Attributes.Bounds.Top));
            else if (others.Count > 0)
            {
                var ob = Bounds(others);
                target = new PointF(ob.Left, ob.Bottom + MacroY * 1.5f);
            }
            else target = new PointF(60, 60);
            Shift(moving, target.X - block.X, target.Y - block.Y);

            // obstacle avoidance: other objects and other groups' boxes
            var obstacles = others.Select(o => Inflate(o.Attributes.Bounds, 25)).ToList();
            obstacles.AddRange(d.Objects.OfType<GH_Group>().Where(g => !g.ObjectIDs.Any(set.Contains) && g.ObjectIDs.Count > 0).Select(g => Inflate(GroupRect(d, g), 15)));
            var cur = Bounds(moving);
            cur = Inflate(cur, GroupPad + 4); cur.Y -= GroupTitle;
            for (int safety = 0; safety < 400; safety++)
            {
                var hit = obstacles.Where(o => o.IntersectsWith(cur)).ToList();
                if (hit.Count == 0) break;
                float dy = hit.Max(o => o.Bottom) + 10 - cur.Y;
                cur.Y += dy;
                Shift(moving, 0, dy);
            }
            ExpireGroups(d, set);
            return null;
        }

        public static string Tidy(GH_Document d, List<IGH_DocumentObject> scope)
        {
            var objs = scope.Where(o => !(o is GH_Group) && o.Attributes != null).ToList();
            if (objs.Count == 0) return "nothing to tidy";
            int repaired = RepairBounds(d);
            var before = Bounds(objs);
            Arrange(d, objs, out var block);
            Shift(objs, before.X - block.X, before.Y - block.Y);
            if (objs.Count < d.Objects.Count(o => !(o is GH_Group)))
            {
                // subset tidy: make sure it does not land on the rest
                var set = new HashSet<Guid>(objs.Select(o => o.InstanceGuid));
                var obstacles = d.Objects.Where(o => !set.Contains(o.InstanceGuid) && !(o is GH_Group) && o.Attributes != null).Select(o => Inflate(o.Attributes.Bounds, 25)).ToList();
                var cur = Inflate(Bounds(objs), GroupPad);
                for (int s = 0; s < 400; s++)
                {
                    var hit = obstacles.Where(o => o.IntersectsWith(cur)).ToList();
                    if (hit.Count == 0) break;
                    float dy = hit.Max(o => o.Bottom) + 10 - cur.Y;
                    cur.Y += dy; Shift(objs, 0, dy);
                }
            }
            ExpireGroups(d, new HashSet<Guid>(objs.Select(o => o.InstanceGuid)));
            var after = Bounds(objs);
            return (repaired > 0 ? $"repaired {repaired} stretched component(s); " : "") + $"tidied {objs.Count} objects ({Fmt.N(before.Width)}x{Fmt.N(before.Height)} → {Fmt.N(after.Width)}x{Fmt.N(after.Height)} canvas units)";
        }

        // ---------- core ----------

        static void Arrange(GH_Document d, List<IGH_DocumentObject> objs, out RectangleF block)
        {
            var set = new HashSet<Guid>(objs.Select(o => o.InstanceGuid));
            foreach (var o in objs) { o.Attributes.ExpireLayout(); o.Attributes.PerformLayout(); }
            SnapshotConsumers(objs);
            var edges = Edges(objs, set);

            // macro membership: smallest group containing the object
            var groups = d.Objects.OfType<GH_Group>().Where(g => g.ObjectIDs.Any(set.Contains)).OrderBy(g => g.ObjectIDs.Count).ToList();
            var owner = new Dictionary<Guid, string>();
            foreach (var g in groups)
                foreach (var id in g.ObjectIDs)
                    if (set.Contains(id) && !owner.ContainsKey(id)) owner[id] = "G" + g.InstanceGuid;

            var macroItems = new List<Item>();
            var local = new Dictionary<Guid, PointF>();   // member offset inside its block
            var blockOf = new Dictionary<Guid, string>();
            foreach (var grp in objs.GroupBy(o => owner.TryGetValue(o.InstanceGuid, out var gid) ? gid : "S" + o.InstanceGuid))
            {
                var members = grp.ToList();
                var items = members.Select(o => new Item { Id = o.InstanceGuid.ToString(), Obj = o, W = o.Attributes.Bounds.Width, H = o.Attributes.Bounds.Height, X = o.Attributes.Bounds.X, Y = o.Attributes.Bounds.Y, Scribble = o is GH_Scribble }).ToList();
                if (members.Count > 1)
                {
                    var ids = new HashSet<string>(items.Select(i => i.Id));
                    Layer(items, edges.Where(e => ids.Contains(e.From) && ids.Contains(e.To)).ToList(), MicroX, MicroY, micro: true);
                }
                var r = Bounds(members);
                bool isGroup = grp.Key.StartsWith("G");
                float padL = isGroup ? GroupPad : 0, padT = isGroup ? GroupPad + GroupTitle : 0;
                foreach (var o in members) { local[o.InstanceGuid] = new PointF(o.Attributes.Pivot.X - r.X + padL, o.Attributes.Pivot.Y - r.Y + padT); blockOf[o.InstanceGuid] = grp.Key; }
                macroItems.Add(new Item { Id = grp.Key, W = r.Width + 2 * padL, H = r.Height + padT + (isGroup ? GroupPad : 0), X = r.X, Y = r.Y });
            }

            var macroEdges = new List<Edge>();
            foreach (var e in edges)
            {
                var a = blockOf[Guid.Parse(e.From)]; var b = blockOf[Guid.Parse(e.To)];
                if (a != b) macroEdges.Add(new Edge { From = a, To = b, FromPort = e.FromPort, ToPort = e.ToPort });
            }
            Layer(macroItems, macroEdges, MacroX, MacroY, micro: false);
            var byId = macroItems.ToDictionary(i => i.Id);
            foreach (var o in objs)
            {
                var b = byId[blockOf[o.InstanceGuid]];
                var l = local[o.InstanceGuid];
                SetPivot(o, new PointF(b.X + l.X, b.Y + l.Y));
            }
            block = Bounds(objs);
        }

        /// One level of layered layout over items (micro uses live grips, macro uses block centers).
        static void Layer(List<Item> items, List<Edge> edgesIn, float xGap, float yGap, bool micro)
        {
            var std = items.Where(i => !i.Scribble).ToList();
            var scr = items.Where(i => i.Scribble).ToList();
            if (std.Count == 0) { std = scr; scr = new List<Item>(); }
            var ids = new HashSet<string>(std.Select(i => i.Id));
            var edges = BreakCycles(std.Select(i => i.Id).ToList(), edgesIn.Where(e => ids.Contains(e.From) && ids.Contains(e.To)).ToList());
            var byId = std.ToDictionary(i => i.Id);
            // scribble → nearest item, before moving anything
            var scrTo = scr.ToDictionary(s => s, s => std.OrderBy(i => Sq(i.X - s.X) + Sq(i.Y - s.Y)).FirstOrDefault());

            // rank = longest path to a sink (sinks = 0, drawn rightmost)
            foreach (var i in std) i.Rank = 0;
            for (int it = 0; it < std.Count + 2; it++)
            {
                bool changed = false;
                foreach (var e in edges)
                    if (byId[e.From].Rank < byId[e.To].Rank + 1) { byId[e.From].Rank = byId[e.To].Rank + 1; changed = true; }
                if (!changed) break;
            }
            int maxR = std.Max(i => i.Rank);
            var colX = new Dictionary<int, float>();
            float x = std.Min(i => i.X);
            for (int r = maxR; r >= 0; r--)
            {
                var col = std.Where(i => i.Rank == r).ToList();
                if (col.Count == 0) continue;
                colX[r] = x;
                x += col.Max(i => i.W) + xGap;
            }
            float y0 = std.Min(i => i.Y);

            // pass A: right → left, align to downstream barycenter
            for (int r = 0; r <= maxR; r++)
            {
                var col = std.Where(i => i.Rank == r).ToList();
                if (col.Count == 0) continue;
                foreach (var i in col)
                {
                    var outs = edges.Where(e => e.From == i.Id).ToList();
                    float ideal = i.Y;
                    if (outs.Count > 0)
                        ideal = outs.Average(e => InY(byId[e.To], e.ToPort, micro)) - OutOffset(i, e0(outs).FromPort, micro);
                    else if (r == 0 && micro && i.Obj != null) ideal = i.Y;
                    Move(i, colX[r], ideal, micro);
                }
                Resolve(col, yGap, micro);
            }
            // pass B: sinks follow their sources
            var sinks = std.Where(i => i.Rank == 0).ToList();
            foreach (var i in sinks)
            {
                var ins = edges.Where(e => e.To == i.Id).ToList();
                if (ins.Count == 0) continue;
                float ideal = ins.Average(e => OutY(byId[e.From], e.FromPort, micro)) - InOffset(i, ins[0].ToPort, micro);
                Move(i, i.X, ideal, micro);
            }
            Resolve(sinks, yGap, micro);

            // members with no wires inside this block (e.g. a group of sliders): pack them tightly in their current
            // order instead of keeping scattered gaps
            // order by where their consumers sit (left→right, top→bottom) so wires leave the block uncrossed
            var loose = std.Where(i => !edges.Any(e => e.From == i.Id || e.To == i.Id)).OrderBy(i => ConsumerKey(i).x).ThenBy(i => ConsumerKey(i).y).ThenBy(i => i.Y).ToList();
            if (loose.Count > 1)
            {
                float cy = loose[0].Y;
                foreach (var i in loose) { Move(i, i.X, cy, micro); cy = i.Y + i.H + yGap; }
                foreach (var grp in std.Where(i => !loose.Contains(i)).GroupBy(i => i.Rank)) Resolve(grp.ToList(), yGap, micro);
                foreach (var grp in std.GroupBy(i => i.Rank)) Resolve(grp.ToList(), yGap, micro);
            }

            // normalise top to original top
            float top = std.Min(i => i.Y);
            foreach (var i in std) Move(i, i.X, i.Y - top + y0, micro);

            // scribbles stacked above their item's column
            foreach (var grp in scr.GroupBy(s => scrTo[s]?.Rank ?? 0))
            {
                var colItems = std.Where(i => i.Rank == grp.Key).ToList();
                float cur = colItems.Count > 0 ? colItems.Min(i => i.Y) : y0;
                float cx = colX.TryGetValue(grp.Key, out var v) ? v : std.Min(i => i.X);
                foreach (var s in grp.OrderByDescending(s => s.Y))
                {
                    cur -= s.H + 12;
                    Move(s, cx, cur, micro);
                }
            }
        }

        static Edge e0(List<Edge> l) => l[0];

        static Dictionary<Guid, (float x, float y)> _consumerKeys = new Dictionary<Guid, (float, float)>();

        static (float x, float y) ConsumerKey(Item i) =>
            i.Obj != null && _consumerKeys.TryGetValue(i.Obj.InstanceGuid, out var k) ? k : (float.MaxValue, i.Y);

        /// consumer positions BEFORE anything moves (layout moves blocks one by one)
        static void SnapshotConsumers(List<IGH_DocumentObject> objs)
        {
            _consumerKeys = new Dictionary<Guid, (float, float)>();
            foreach (var o in objs)
            {
                var rec = OutputsOf(o).SelectMany(p => p.Recipients).Select(r => r.Attributes).Where(a => a != null).ToList();
                // the recipient input grip's own canvas rect = where the wire lands
                if (rec.Count > 0) _consumerKeys[o.InstanceGuid] = (rec.Min(a => (float)Math.Round(a.Bounds.X / 50f)), rec.Min(a => a.Bounds.Y));
            }
        }

        static void Resolve(List<Item> col, float gap, bool micro)
        {
            col.Sort((a, b) => a.Y.CompareTo(b.Y));
            for (int k = 1; k < col.Count; k++)
            {
                var prev = col[k - 1]; var cur = col[k];
                float minY = prev.Y + prev.H + gap;
                if (cur.Y < minY) Move(cur, cur.X, minY, micro);
            }
        }

        static void Move(Item i, float x, float y, bool micro)
        {
            i.X = x; i.Y = y;
            if (micro && i.Obj != null)
            {
                var b = i.Obj.Attributes.Bounds; var p = i.Obj.Attributes.Pivot;
                // pivot-to-bounds offset differs per object type; keep bounds top-left at (x,y)
                SetPivot(i.Obj, new PointF(p.X + (x - b.X), p.Y + (y - b.Y)));
                var nb = i.Obj.Attributes.Bounds; i.W = nb.Width; i.H = nb.Height; i.X = nb.X; i.Y = nb.Y;
            }
        }

        // grips (micro: real port grips; macro: block center)
        static float InY(Item t, int port, bool micro)
        {
            if (!micro || t.Obj == null) return t.Y + t.H / 2;
            if (t.Obj is IGH_Component c && port >= 0 && port < c.Params.Input.Count && c.Params.Input[port].Attributes != null) return c.Params.Input[port].Attributes.InputGrip.Y;
            return t.Y + t.H / 2;
        }

        static float OutY(Item s, int port, bool micro)
        {
            if (!micro || s.Obj == null) return s.Y + s.H / 2;
            if (s.Obj is IGH_Component c && port >= 0 && port < c.Params.Output.Count && c.Params.Output[port].Attributes != null) return c.Params.Output[port].Attributes.OutputGrip.Y;
            return s.Y + s.H / 2;
        }

        static float OutOffset(Item s, int port, bool micro) => OutY(s, port, micro) - s.Y;
        static float InOffset(Item t, int port, bool micro) => InY(t, port, micro) - t.Y;

        /// Move an object. Child parameter attributes move by the same delta first: some plug-ins
        /// (Karamba's collapsible inputs) do not re-place hidden params in PerformLayout, and stale
        /// param rects would otherwise stretch the component's bounds across the canvas.
        public static void SetPivot(IGH_DocumentObject o, PointF target)
        {
            var a = o.Attributes;
            float dx = target.X - a.Pivot.X, dy = target.Y - a.Pivot.Y;
            if (o is IGH_Component c && (Math.Abs(dx) > 0.01f || Math.Abs(dy) > 0.01f))
                foreach (var p in c.Params.Input.Concat(c.Params.Output))
                {
                    var pa = p.Attributes;
                    if (pa == null) continue;
                    pa.Pivot = new PointF(pa.Pivot.X + dx, pa.Pivot.Y + dy);
                    var pb = pa.Bounds; pb.Offset(dx, dy); pa.Bounds = pb;
                }
            a.Pivot = target;
            a.ExpireLayout();
            a.PerformLayout();
        }

        static readonly Dictionary<Guid, List<PointF>> FreshOffsets = new Dictionary<Guid, List<PointF>>();

        /// Repair components whose child params drifted from where the component itself puts them
        /// (stretched bounds, stray grips floating above): snap every param back to the offset it has
        /// on a fresh instance, then let the component lay out its visible params again.
        public static int RepairBounds(GH_Document d)
        {
            int fixedN = 0;
            foreach (var c in d.Objects.OfType<IGH_Component>())
            {
                var a = c.Attributes;
                if (a == null) continue;
                if (!FreshOffsets.TryGetValue(c.ComponentGuid, out var offs))
                {
                    offs = null;
                    try
                    {
                        var fresh = Grasshopper.Instances.ComponentServer.EmitObjectProxy(c.ComponentGuid)?.CreateInstance() as IGH_Component;
                        if (fresh != null)
                        {
                            fresh.CreateAttributes(); fresh.Attributes.Pivot = PointF.Empty; fresh.Attributes.PerformLayout();
                            offs = fresh.Params.Input.Concat(fresh.Params.Output).Select(p => p.Attributes?.Pivot ?? PointF.Empty).ToList();
                        }
                    }
                    catch { }
                    FreshOffsets[c.ComponentGuid] = offs;
                }
                var ps = c.Params.Input.Concat(c.Params.Output).ToList();
                if (offs == null || offs.Count != ps.Count) continue;     // variable-param components: nothing to compare
                bool drift = false;
                for (int i = 0; i < ps.Count; i++)
                {
                    var pa = ps[i].Attributes; if (pa == null) continue;
                    float ox = pa.Pivot.X - a.Pivot.X, oy = pa.Pivot.Y - a.Pivot.Y;
                    if (Math.Abs(ox - offs[i].X) > 3 || Math.Abs(oy - offs[i].Y) > 3) { drift = true; break; }
                }
                if (!drift) continue;
                for (int i = 0; i < ps.Count; i++)
                {
                    var pa = ps[i].Attributes; if (pa == null) continue;
                    var target = new PointF(a.Pivot.X + offs[i].X, a.Pivot.Y + offs[i].Y);
                    float dx = target.X - pa.Pivot.X, dy = target.Y - pa.Pivot.Y;
                    pa.Pivot = target;
                    var pb = pa.Bounds; pb.Offset(dx, dy); pa.Bounds = pb;
                }
                a.ExpireLayout(); a.PerformLayout();
                fixedN++;
            }
            return fixedN;
        }

        // ---------- graph helpers ----------

        static IEnumerable<IGH_Param> InputsOf(IGH_DocumentObject o) => o is IGH_Component c ? c.Params.Input : o is IGH_Param p ? new[] { p } : Enumerable.Empty<IGH_Param>();
        static IEnumerable<IGH_Param> OutputsOf(IGH_DocumentObject o) => o is IGH_Component c ? c.Params.Output : o is IGH_Param p ? new[] { p } : Enumerable.Empty<IGH_Param>();
        static IGH_DocumentObject Top(IGH_Param p) => p?.Attributes?.GetTopLevel?.DocObject;

        static List<Edge> Edges(List<IGH_DocumentObject> objs, HashSet<Guid> set)
        {
            var res = new List<Edge>();
            foreach (var o in objs)
            {
                var ins = InputsOf(o).ToList();
                for (int i = 0; i < ins.Count; i++)
                    foreach (var s in ins[i].Sources)
                    {
                        var t = Top(s);
                        if (t == null || !set.Contains(t.InstanceGuid) || t == o) continue;
                        int fp = t is IGH_Component sc ? Math.Max(0, sc.Params.Output.IndexOf(s)) : 0;
                        res.Add(new Edge { From = t.InstanceGuid.ToString(), To = o.InstanceGuid.ToString(), FromPort = fp, ToPort = o is IGH_Component ? i : 0 });
                    }
            }
            return res;
        }

        static List<Edge> BreakCycles(List<string> nodes, List<Edge> edges)
        {
            var adj = nodes.ToDictionary(n => n, n => new List<Edge>());
            foreach (var e in edges) adj[e.From].Add(e);
            var visited = new HashSet<string>(); var onStack = new HashSet<string>(); var back = new HashSet<Edge>();
            void Dfs(string u)
            {
                onStack.Add(u);
                foreach (var e in adj[u])
                {
                    if (onStack.Contains(e.To)) back.Add(e);
                    else if (!visited.Contains(e.To)) Dfs(e.To);
                }
                onStack.Remove(u); visited.Add(u);
            }
            foreach (var n in nodes) if (!visited.Contains(n)) Dfs(n);
            return edges.Where(e => !back.Contains(e)).ToList();
        }

        // ---------- geometry helpers ----------

        public static RectangleF Bounds(IEnumerable<IGH_DocumentObject> objs)
        {
            RectangleF r = RectangleF.Empty; bool first = true;
            foreach (var o in objs)
            {
                if (o.Attributes == null) continue;
                var b = o.Attributes.Bounds;
                r = first ? b : RectangleF.Union(r, b); first = false;
            }
            return r;
        }

        public static RectangleF GroupRect(GH_Document d, GH_Group g)
        {
            g.Attributes?.ExpireLayout(); g.Attributes?.PerformLayout();
            var b = g.Attributes?.Bounds ?? RectangleF.Empty;
            if (b.Width > 1) return b;
            return Inflate(Bounds(g.ObjectIDs.Select(id => d.FindObject(id, false)).Where(o => o != null)), GroupPad);
        }

        static RectangleF Inflate(RectangleF r, float v) { r.Inflate(v, v); return r; }
        static float Sq(float v) => v * v;

        static void Shift(List<IGH_DocumentObject> objs, float dx, float dy)
        {
            if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return;
            foreach (var o in objs)
            {
                var p = o.Attributes.Pivot;
                SetPivot(o, new PointF(p.X + dx, p.Y + dy));
            }
        }

        static void ExpireGroups(GH_Document d, HashSet<Guid> moved)
        {
            foreach (var g in d.Objects.OfType<GH_Group>())
                if (g.ObjectIDs.Any(moved.Contains)) { g.ExpireCaches(); g.Attributes?.ExpireLayout(); }
        }
    }
}
