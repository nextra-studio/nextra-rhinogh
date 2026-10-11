using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;

namespace Nextra.Engine.Gh
{
    /// Right-click menu and on-component clicks, driven exactly like a user would:
    /// the component builds its own context menu (AppendMenuItems) and we click an item by name;
    /// or a mouse down/up is sent to the component's attributes at a point on its body.
    public static class MenuIO
    {
        // standard GH items every object has: not interesting to list
        static readonly HashSet<string> Std = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Preview", "Enabled", "Bake", "Bake...", "Help", "Help...", "Wire Display", "Reverse", "Flatten", "Graft", "Simplify",
            "Expression...", "Manage Params", "Extract parameter", "Degrees", "Principal", "Data Viewer", "Set one", "Set Multiple",
            "Clear values", "Internalise data", "Extract parameter", "Copy Data Only", "Save Default Values", "Show Name", "Set dashes",
            "Default", "Faint", "Hidden", "Dispatch", "Invert", "Reparameterize", "Runtime warnings", "Runtime errors", "Sort", "Cluster",
        };

        static string Clean(string t) => (t ?? "").Replace("&", "").Trim();

        static List<(ToolStripMenuItem item, string path)> Items(IGH_DocumentObject o)
        {
            var menu = new ToolStripDropDownMenu();
            try { o.AppendMenuItems(menu); } catch (Exception e) { throw new NxError($"{o.Name} menu could not be built: {e.Message}"); }
            var res = new List<(ToolStripMenuItem, string)>();
            void Walk(ToolStripItemCollection items, string prefix)
            {
                foreach (ToolStripItem it in items)
                {
                    if (!(it is ToolStripMenuItem mi) || string.IsNullOrWhiteSpace(mi.Text)) continue;
                    var path = prefix + Clean(mi.Text);
                    res.Add((mi, path));
                    if (mi.DropDownItems.Count > 0) Walk(mi.DropDownItems, path + ">");
                }
            }
            Walk(menu.Items, "");
            return res;
        }

        /// e.g. "[x] Union Box | [ ] Avoid duplicates | Mode>Linear"
        public static string Describe(IGH_DocumentObject o)
        {
            try
            {
                // component-specific items only: skip GH standard items, per-input/output submenus and disabled ones
                var items = Items(o).Where(x => !Std.Contains(Clean(x.item.Text)) && !x.path.Contains("Wire Display>") && x.item.Enabled
                                               && !x.path.StartsWith("Input:") && !x.path.StartsWith("Output:") && !x.path.Contains(">Help")).ToList();
                if (items.Count == 0) return null;
                return string.Join(" | ", items.Take(30).Select(x => (x.item.CheckOnClick || x.item.Checked ? (x.item.Checked ? "[x] " : "[ ] ") : "") + x.path + (x.item.Enabled ? "" : " (disabled)")));
            }
            catch { return null; }
        }

        /// "Union Box" toggles; "Union Box:on" / ":off" only clicks when needed; "Mode>Linear" walks a sub-menu.
        public static string Click(IGH_DocumentObject o, string spec)
        {
            string want = null;
            var m = System.Text.RegularExpressions.Regex.Match(spec, @"^(.*?)\s*:\s*(on|off|true|false)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) { spec = m.Groups[1].Value; want = m.Groups[2].Value.ToLowerInvariant(); }
            var items = Items(o);
            var hit = items.Where(x => x.path.Equals(spec, StringComparison.OrdinalIgnoreCase) || Clean(x.item.Text).Equals(spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 0) hit = items.Where(x => x.path.IndexOf(spec, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hit.Count == 0) throw new NxError($"no menu item '{spec}' on {o.Name}", "menu: " + (Describe(o) ?? "(only standard items)"));
            if (hit.Count > 1) throw new NxError($"menu item '{spec}' is ambiguous", string.Join(" | ", hit.Select(x => x.path)));
            var mi = hit[0].item;
            if (mi.Text.EndsWith("...") || mi.Text.EndsWith("…")) throw new NxError($"'{hit[0].path}' opens a dialog (would block Rhino)", "use $state keys or inputs instead, or ask the user to do this one by hand");
            if (!mi.Enabled) throw new NxError($"'{hit[0].path}' is disabled right now");
            bool on = want == "on" || want == "true";
            if (want != null && mi.Checked == on) return $"menu '{hit[0].path}' already {(on ? "on" : "off")}";
            mi.PerformClick();
            if (o is IGH_ActiveObject ao) ao.ExpireSolution(false);
            o.Attributes?.ExpireLayout();
            return $"menu '{hit[0].path}' clicked";
        }

        /// Mouse click at fractional position (fx, fy) of the component body, e.g. "0.3,0.85".
        public static string ClickAt(IGH_DocumentObject o, string spec)
        {
            var parts = spec.Split(',');
            if (parts.Length != 2 || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fx)
                || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fy))
                throw new NxError("click needs 'fx,fy' fractions of the component box, e.g. click=\"0.3,0.85\"", "view {target:'gh', focus:'gN'} first to see where the widget is");
            var cv = Grasshopper.Instances.ActiveCanvas ?? throw new NxError("no GH canvas");
            var b = o.Attributes.Bounds;
            var pt = new PointF(b.X + fx * b.Width, b.Y + fy * b.Height);
            var scr = cv.Viewport.ProjectPoint(pt);
            var args = new MouseEventArgs(MouseButtons.Left, 1, (int)Math.Round(scr.X), (int)Math.Round(scr.Y), 0);
            var ev = new GH_CanvasMouseEvent(cv.Viewport, args);
            var r1 = o.Attributes.RespondToMouseDown(cv, ev);
            var r2 = o.Attributes.RespondToMouseUp(cv, ev);
            if (o is IGH_ActiveObject ao) ao.ExpireSolution(false);
            return $"clicked {o.Name.Trim()} at ({fx:0.##},{fy:0.##}) → {r1}/{r2}" + (r1 == GH_ObjectResponse.Ignore && r2 == GH_ObjectResponse.Ignore ? " (nothing there reacted: check the spot with a zoomed view)" : "");
        }
    }
}
