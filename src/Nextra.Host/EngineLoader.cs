using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Nextra.Host
{
    /// Loads Nextra.Engine.dll from bytes into a fresh AssemblyLoadContext whenever the file changes,
    /// so the engine can be rebuilt while Rhino keeps running. State that must survive a reload
    /// (handle tables, checkpoints) lives in <see cref="State"/>, which only holds BCL types.
    static class EngineLoader
    {
        public static readonly Dictionary<string, object> State = new Dictionary<string, object>();

        static Func<string, string, IDictionary<string, object>, string> _call;
        static Action _shutdown;
        static DateTime _stamp;
        static string _loadedPath;
        static int _generation;

        class EngineContext : AssemblyLoadContext
        {
            public EngineContext(string name) : base(name, isCollectible: false) { }

            // Reuse whatever Rhino already loaded (RhinoCommon, Grasshopper, ...) so types match exactly.
            protected override Assembly Load(AssemblyName name)
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var n = a.GetName();
                    if (string.Equals(n.Name, name.Name, StringComparison.OrdinalIgnoreCase) && !a.IsDynamic) return a;
                }
                return null;
            }
        }

        public static string EnginePath
        {
            get
            {
                var cfg = Config.GetString("engine_path", null);
                if (!string.IsNullOrEmpty(cfg) && File.Exists(cfg)) return cfg;
                var here = Path.GetDirectoryName(typeof(EngineLoader).Assembly.Location);
                return Path.Combine(here, "Nextra.Engine.dll");
            }
        }

        public static string Describe() =>
            _call == null ? "not loaded" : $"gen {_generation} {Path.GetFileName(_loadedPath)} @ {_stamp.ToLocalTime():HH:mm:ss}";

        public static void EnsureFresh()
        {
            var p = EnginePath;
            if (!File.Exists(p)) throw new FileNotFoundException("engine dll not found: " + p);
            var t = File.GetLastWriteTimeUtc(p);
            if (_call == null || t != _stamp || p != _loadedPath) Load(p, t);
        }

        public static void ForceReload()
        {
            var p = EnginePath;
            Load(p, File.GetLastWriteTimeUtc(p));
        }

        public static string Call(string tool, string args) => _call(tool, args, State);

        static byte[] ReadShared(string path)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    return ms.ToArray();
                }
                catch (IOException) when (i < 10) { System.Threading.Thread.Sleep(200); }
            }
        }

        /// Loads the new engine completely before retiring the old one: a broken or half-written dll leaves the
        /// previous engine working (and the error is reported) instead of leaving no engine at all.
        static void Load(string path, DateTime stamp)
        {
            Func<string, string, IDictionary<string, object>, string> newCall;
            Action newShutdown;
            MethodInfo init;
            try
            {
                var bytes = ReadShared(path);
                var pdbPath = Path.ChangeExtension(path, ".pdb");
                var pdb = File.Exists(pdbPath) ? ReadShared(pdbPath) : null;
                var ctx = new EngineContext("NextraEngine#" + (_generation + 1));
                var asm = pdb != null ? ctx.LoadFromStream(new MemoryStream(bytes), new MemoryStream(pdb)) : ctx.LoadFromStream(new MemoryStream(bytes));
                var entry = asm.GetType("Nextra.Engine.Entry", throwOnError: true);
                var call = entry.GetMethod("Call", BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException("Entry.Call");
                newCall = (Func<string, string, IDictionary<string, object>, string>)Delegate.CreateDelegate(typeof(Func<string, string, IDictionary<string, object>, string>), call);
                var sd = entry.GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static);
                newShutdown = sd == null ? null : (Action)Delegate.CreateDelegate(typeof(Action), sd);
                init = entry.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception e)
            {
                if (_call == null) throw;
                Rhino.RhinoApp.WriteLine($"NextraRhinoGh: new engine at {path} failed to load ({e.Message}); still using gen {_generation}");
                _stamp = stamp;   // do not retry on every call; the next rebuild changes the stamp
                return;
            }
            Unload();
            _generation++;
            _call = newCall;
            _shutdown = newShutdown;
            State["host.port"] = Server.Port;
            State["host.appdir"] = Server.AppDir;
            State["host.dir"] = Path.GetDirectoryName(typeof(EngineLoader).Assembly.Location);
            State["host.generation"] = _generation;
            init?.Invoke(null, new object[] { State });
            _stamp = stamp;
            _loadedPath = path;
        }

        public static void Unload()
        {
            try { _shutdown?.Invoke(); } catch { }
            _call = null;
            _shutdown = null;
        }
    }
}
