using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Nextra.Engine.Gh.Shorthand;

namespace Nextra.Engine.Gh
{
    /// .ghs = a Grasshopper definition stored as shorthand text: the same grammar gh_edit reads and query prints,
    /// written in full (whole script code, every non-default setting, canvas positions). Readable, diffable,
    /// and an LLM can read or edit the file directly. Open rebuilds it on a new canvas.
    public static class GhsFile
    {
        public const string Ext = ".ghs";

        public static bool Is(string path) => string.Equals(Path.GetExtension(path ?? ""), Ext, StringComparison.OrdinalIgnoreCase);

        public static string Save(GH_Document d, string path)
        {
            var opt = WriteOptions.ForFile();
            var objs = d.Objects.ToList();
            var body = Writer.Write(d, objs, opt);
            int nodes = objs.Count(o => !(o is GH_Group));
            var sb = new StringBuilder();
            sb.Append("# ghs 1 · Grasshopper definition as Nextra shorthand · open: gh_edit {open:'")
              .Append(Path.GetFileName(path)).Append("'} · grammar: query {scope:'docs', filter:'shorthand'}\n");
            sb.Append($"# {nodes} objects, {objs.Count - nodes} groups · saved {DateTime.Now:yyyy-MM-dd HH:mm}");
            if (d.IsFilePathDefined && !Is(d.FilePath)) sb.Append(" from ").Append(Path.GetFileName(d.FilePath));
            sb.Append('\n');
            foreach (var l in opt.Lost.Take(30)) sb.Append("# not stored: ").Append(l).Append('\n');
            sb.Append('\n').Append(body.Replace("\r\n", "\n")).Append('\n');
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            var msg = $"saved {path} (.ghs text: {nodes} objects, {sb.ToString().Split('\n').Length} lines, {(sb.Length + 1023) / 1024} KB)";
            if (opt.Lost.Count > 0)
                msg += $"\nnot stored ({opt.Lost.Count}; internalised geometry and Rhino references need a .gh): " + string.Join(" · ", opt.Lost.Take(6));
            return msg;
        }

        /// Builds the file on a new canvas. Returns the report; doc is the new document.
        public static string Open(string path, bool solve, out GH_Document doc)
        {
            if (!File.Exists(path)) throw new NxError("file not found: " + path);
            var text = File.ReadAllText(path);
            GhCore.EnsureEditor();
            doc = new GH_Document();
            Grasshopper.Instances.DocumentServer.AddDocument(doc);
            Grasshopper.Instances.ActiveCanvas.Document = doc;

            var perr = new List<ParseError>();
            var ops = Parser.Parse(text, perr);
            bool was = GH_Document.EnableSolutions;
            GH_Document.EnableSolutions = false;
            BuildResult br;
            try { br = Builder.Apply(doc, ops, freshGroups: true); }
            finally { GH_Document.EnableSolutions = was; }
            var unplaced = br.Created.Where(o => !(o is GH_Group) && !br.Placed.Contains(o)).ToList();
            if (unplaced.Count > 0) Layout.Place(doc, unplaced);

            var sb = new StringBuilder();
            int nodes = br.Created.Count(o => !(o is GH_Group));
            sb.Append($"opened {Path.GetFileName(path)} (.ghs): {nodes} objects, {br.GroupsNew} groups, {br.Wires} wires");
            var errs = perr.Select(e => $"L{e.Line}: parse error: {e.Message}").Concat(br.Errors).ToList();
            if (solve && was)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                doc.NewSolution(false);
                sb.Append($"; solved in {sw.ElapsedMilliseconds}ms");
            }
            else if (solve) sb.Append("; solutions are disabled in Grasshopper (not solved)");
            if (errs.Count > 0)
            {
                sb.Append($"\n{errs.Count} line(s) failed (fix the file or the canvas):");
                foreach (var e in errs.Take(20)) sb.Append("\n ").Append(e);
            }
            Grasshopper.Instances.ActiveCanvas?.Refresh();
            return sb.ToString();
        }
    }
}
