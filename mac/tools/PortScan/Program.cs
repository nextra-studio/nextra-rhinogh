// Portability scan for the Mac build: reads assembly metadata only (nothing is loaded or run).
//   dotnet run --project mac/tools/PortScan -- refs <dll>...        list assembly references and native imports
//   dotnet run --project mac/tools/PortScan -- check <dll>...       fail on Windows-only references / P/Invokes
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class PortScan
{
    // Assemblies that exist only in the Windows Desktop runtime (WPF, UI Automation, Win32 registry)
    static readonly string[] WindowsOnlyAssemblies =
    {
        "WindowsBase", "PresentationCore", "PresentationFramework", "System.Xaml",
        "UIAutomationClient", "UIAutomationTypes", "UIAutomationProvider", "Microsoft.Win32.Registry",
        "System.Windows.Forms.Design", "Microsoft.VisualBasic.Forms",
    };
    static readonly string[] WindowsOnlyNativeLibs =
        { "user32", "kernel32", "gdi32", "ole32", "oleaut32", "shell32", "advapi32", "comctl32", "dwmapi", "uxtheme" };

    static int Main(string[] args)
    {
        if (args.Length < 2 || (args[0] != "refs" && args[0] != "check"))
        {
            Console.Error.WriteLine("usage: PortScan refs|check <dll>...");
            return 2;
        }
        int bad = 0;
        foreach (var path in args.Skip(1))
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            var md = pe.GetMetadataReader();
            var asm = md.GetAssemblyDefinition();
            var tfm = TargetFramework(md);
            Console.WriteLine($"== {Path.GetFileName(path)} ({md.GetString(asm.Name)} {asm.Version}, {tfm ?? "no TFM attribute"})");

            foreach (var h in md.AssemblyReferences)
            {
                var r = md.GetAssemblyReference(h);
                var name = md.GetString(r.Name);
                bool winOnly = WindowsOnlyAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (args[0] == "refs" || winOnly)
                    Console.WriteLine($"  ref    {name} {r.Version}{(winOnly ? "   <-- Windows-only" : "")}");
                if (winOnly) bad++;
            }

            // DllImport targets: ModuleRef rows referenced by ImplMap (P/Invoke) entries
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                foreach (var mh in t.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    var imp = m.GetImport();
                    if (imp.Module.IsNil) continue;
                    var lib = md.GetString(md.GetModuleReference(imp.Module).Name);
                    var baseName = Path.GetFileNameWithoutExtension(lib).ToLowerInvariant();
                    bool winOnly = WindowsOnlyNativeLibs.Contains(baseName);
                    Console.WriteLine($"  pinv   {lib}!{md.GetString(m.Name)} in {md.GetString(t.Name)}{(winOnly ? "   <-- Windows-only" : "")}");
                    if (winOnly) bad++;
                }
            }
            if (tfm != null && tfm.Contains("-windows", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("  target framework is Windows-only   <--"); bad++; }
        }
        if (args[0] == "check") Console.WriteLine(bad == 0 ? "PASS: no Windows-only references or P/Invokes" : $"FAIL: {bad} Windows-only item(s)");
        return args[0] == "check" && bad > 0 ? 1 : 0;
    }

    // [assembly: TargetFramework(".NETCoreApp,Version=v8.0")] and [assembly: TargetPlatform("Windows7.0")] decoded by hand
    static string TargetFramework(MetadataReader md)
    {
        string fw = null, platform = null;
        foreach (var ch in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var ca = md.GetCustomAttribute(ch);
            if (ca.Constructor.Kind != HandleKind.MemberReference) continue;
            var parent = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference) continue;
            var tn = md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name);
            if (tn != "TargetFrameworkAttribute" && tn != "TargetPlatformAttribute") continue;
            var blob = md.GetBlobReader(ca.Value);
            blob.ReadUInt16();   // prolog
            var s = blob.ReadSerializedString();
            if (tn == "TargetFrameworkAttribute") fw = s; else platform = s;
        }
        if (fw == null) return null;
        return platform == null ? fw : fw + "-" + platform.ToLowerInvariant();
    }
}
