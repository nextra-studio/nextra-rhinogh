using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Nextra.Host
{
    /// Copy this Rhino plug-in's data out of the old shared Nextra folder once.
    /// The source stays intact in case another product still uses it.
    static class AppDataMigration
    {
        public static void Ensure()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var legacy = Path.Combine(appData, "Nextra");
            var current = Path.Combine(appData, "NextraRhinoGh");
            var marker = Path.Combine(current, ".legacy-nextra-imported");
            var config = Path.Combine(legacy, "config.json");
            var readme = Path.Combine(legacy, "user", "README.md");
            var knownConfig = File.Exists(config) && IsRhinoGhConfig(config);
            if (!knownConfig && !IsRhinoGhReadme(readme)) return;

            // An older Rhino session can still write to the legacy folder after the first copy.
            // Import those changes once more if the destination has not changed since the copy.
            if (File.Exists(marker))
            {
                RefreshLegacyChanges(legacy, current, File.GetLastWriteTimeUtc(marker), knownConfig);
                return;
            }

            Directory.CreateDirectory(current);
            var newConfig = Path.Combine(current, "config.json");
            if (knownConfig)
            {
                CopyMissing(config, newConfig);
                if (File.Exists(newConfig)) RewriteDefaultPaths(newConfig, legacy, current);
            }
            CopyMissing(Path.Combine(legacy, "dot.txt"), Path.Combine(current, "dot.txt"));
            CopyMissingTree(Path.Combine(legacy, "user"), Path.Combine(current, "user"));
            CopyMissingTree(Path.Combine(legacy, "memory"), Path.Combine(current, "memory"));
            UpdateCopiedReadme(Path.Combine(current, "user", "README.md"));
            File.WriteAllText(marker, legacy + Environment.NewLine);
        }

        static bool IsRhinoGhReadme(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                var text = File.ReadAllText(path);
                return text.StartsWith("# Nextra — your folder", StringComparison.Ordinal)
                    && text.Contains("Rhino plug-in", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        static void RefreshLegacyChanges(string legacy, string current, DateTime copiedAt, bool knownConfig)
        {
            if (knownConfig)
            {
                var config = Path.Combine(current, "config.json");
                if (CopyNewerUnchanged(Path.Combine(legacy, "config.json"), config, copiedAt))
                    RewriteDefaultPaths(config, legacy, current);
            }
            CopyNewerUnchanged(Path.Combine(legacy, "dot.txt"), Path.Combine(current, "dot.txt"), copiedAt);
            foreach (var sub in new[] { "user", "memory" })
            {
                var source = Path.Combine(legacy, sub);
                if (!Directory.Exists(source)) continue;
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(current, sub, Path.GetRelativePath(source, file));
                    if (CopyNewerUnchanged(file, destination, copiedAt) &&
                        string.Equals(destination, Path.Combine(current, "user", "README.md"), StringComparison.OrdinalIgnoreCase))
                        UpdateCopiedReadme(destination);
                }
            }
        }

        static bool CopyNewerUnchanged(string source, string target, DateTime copiedAt)
        {
            if (!File.Exists(source) || File.GetLastWriteTimeUtc(source) <= copiedAt) return false;
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) > copiedAt) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (File.Exists(target))
            {
                var backup = target + ".before-legacy-refresh.bak";
                if (!File.Exists(backup)) File.Copy(target, backup);
            }
            File.Copy(source, target, true);
            return true;
        }

        static void RewriteDefaultPaths(string path, string legacy, string current)
        {
            var config = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (config == null) return;
            bool changed = false;
            foreach (var key in new[] { "user_dir", "memory_dir" })
            {
                var value = (string)config[key];
                if (!string.Equals(value?.TrimEnd('\\', '/'), Path.Combine(legacy, key == "user_dir" ? "user" : "memory"), StringComparison.OrdinalIgnoreCase)) continue;
                config[key] = Path.Combine(current, key == "user_dir" ? "user" : "memory");
                changed = true;
            }
            if (changed) File.WriteAllText(path, config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        static void UpdateCopiedReadme(string path)
        {
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            if (!text.StartsWith("# Nextra — your folder", StringComparison.Ordinal)) return;
            var updated = text.Replace("# Nextra — your folder", "# NextraRhinoGh — your folder")
                .Replace("Nextra (the Rhino plug-in)", "NextraRhinoGh (the Rhino plug-in)")
                .Replace("Nextra skill", "NextraRhinoGh skill")
                .Replace("Nextra plug-in", "NextraRhinoGh plug-in")
                .Replace("NextraFolder", "NextraRhinoGhFolder")
                .Replace("%APPDATA%\\Nextra\\config.json", "%APPDATA%\\NextraRhinoGh\\config.json");
            if (updated == text) return;
            if (!File.Exists(path + ".before-name-change.bak")) File.Copy(path, path + ".before-name-change.bak");
            File.WriteAllText(path, updated);
        }

        static bool IsRhinoGhConfig(string path)
        {
            try
            {
                var config = JsonNode.Parse(File.ReadAllText(path));
                var engine = (string)config?["engine_path"];
                var skill = (string)config?["skill_dir"];
                return string.Equals(Path.GetFileName(engine), "Nextra.Engine.dll", StringComparison.OrdinalIgnoreCase)
                    || (skill?.Replace('\\', '/').EndsWith("/skill/nextra", StringComparison.OrdinalIgnoreCase) ?? false);
            }
            catch { return false; }
        }

        static void CopyMissing(string source, string target)
        {
            if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
        }

        static void CopyMissingTree(string source, string target)
        {
            if (!Directory.Exists(source)) return;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                CopyMissing(file, destination);
            }
        }
    }
}
