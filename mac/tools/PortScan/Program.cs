// Portability scan for the Mac build: reads assembly metadata only (nothing is loaded or run).
//   dotnet run --project mac/tools/PortScan -- refs <dll>...        list assembly references and native imports
//   dotnet run --project mac/tools/PortScan -- check <dll>...       fail on Windows-only references / P/Invokes
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;

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

    // members <dll> <Namespace.Type> [filter]: public methods/properties/events of one type, to check an API exists
    static int Members(string path, string typeName, string filter)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        Console.WriteLine($"== {Path.GetFileName(path)} {md.GetAssemblyDefinition().Version}");
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            var full = md.GetString(t.Namespace) + "." + md.GetString(t.Name);
            if (full != typeName) continue;
            bool Keep(string n) => filter == null || n.Contains(filter, StringComparison.OrdinalIgnoreCase);
            foreach (var h in t.GetProperties()) { var n = md.GetString(md.GetPropertyDefinition(h).Name); if (Keep(n)) Console.WriteLine("  prop   " + n); }
            foreach (var h in t.GetEvents()) { var n = md.GetString(md.GetEventDefinition(h).Name); if (Keep(n)) Console.WriteLine("  event  " + n); }
            foreach (var h in t.GetMethods())
            {
                var m = md.GetMethodDefinition(h);
                var n = md.GetString(m.Name);
                if ((m.Attributes & System.Reflection.MethodAttributes.Public) == 0 || n.StartsWith("get_") || n.StartsWith("set_") || n.StartsWith("add_") || n.StartsWith("remove_")) continue;
                if (Keep(n)) Console.WriteLine("  method " + n);
            }
            return 0;
        }
        Console.WriteLine("  type not found: " + typeName);
        return 1;
    }

    // callers <dll> <TypeName>: methods whose IL calls a method of that type (call/callvirt/newobj tokens; metadata only)
    static int Callers(string path, string typeName)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var targets = new Dictionary<int, string>();
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            if (md.GetString(t.Name) != typeName) continue;
            foreach (var mh in t.GetMethods()) targets[MetadataTokens.GetToken(mh)] = typeName + "." + md.GetString(md.GetMethodDefinition(mh).Name);
        }
        // calls through a generic instantiation (WpfWindow<...>.X) use MemberRef tokens: match those by method name
        var names = new HashSet<string>(targets.Values.Select(v => v.Substring(v.LastIndexOf('.') + 1)));
        foreach (var rh in md.MemberReferences)
        {
            var r = md.GetMemberReference(rh);
            var n = md.GetString(r.Name);
            if (r.Parent.Kind == HandleKind.TypeSpecification && names.Contains(n)) targets[MetadataTokens.GetToken(rh)] = typeName + "<>." + n;
        }
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
                var hits = new SortedSet<string>();
                for (int i = 0; i + 4 < il.Length; i++)
                    if (il[i] == 0x28 || il[i] == 0x6F || il[i] == 0x73)
                    {
                        int tok = BitConverter.ToInt32(il, i + 1);
                        if (targets.TryGetValue(tok, out var name)) hits.Add(name);
                    }
                if (hits.Count > 0)
                    Console.WriteLine($"  {md.GetString(t.Namespace)}.{md.GetString(t.Name)}.{md.GetString(m.Name)} -> {string.Join(", ", hits)}");
            }
        }
        return 0;
    }

    // il <dll> <TypeName> <Method>: raw IL bytes of one method, with call targets and float constants decoded (diagnosis)
    static int Il(string path, string typeName, string method)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        string Name(int tok)
        {
            var h = MetadataTokens.EntityHandle(tok);
            if (h.Kind == HandleKind.MethodDefinition) { var m = md.GetMethodDefinition((MethodDefinitionHandle)h); return md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name) + "." + md.GetString(m.Name); }
            if (h.Kind == HandleKind.MemberReference) return md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name);
            return "tok " + tok.ToString("X8");
        }
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            if (md.GetString(t.Name) != typeName) continue;
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (md.GetString(m.Name) != method || m.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
                Console.WriteLine($"{typeName}.{method}: {il.Length} bytes");
                Console.WriteLine("  " + BitConverter.ToString(il));
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] == 0x28 || il[i] == 0x6F || il[i] == 0x73) { var n = Name(BitConverter.ToInt32(il, i + 1)); if (!n.StartsWith("tok")) Console.WriteLine($"  @{i:X2} {(il[i] == 0x73 ? "newobj" : "call")} {n}"); }
                    if (il[i] == 0x22) Console.WriteLine($"  @{i:X2} ldc.r4 {BitConverter.ToSingle(il, i + 1)}");
                }
            }
        }
        return 0;
    }

    // field <dll> <hexToken>: name/type of a field token and every method that loads it (ldsfld/ldfld/ldsflda)
    static int Field(string path, string hexTok)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        int tok = Convert.ToInt32(hexTok, 16);
        var fh = (FieldDefinitionHandle)MetadataTokens.EntityHandle(tok);
        var f = md.GetFieldDefinition(fh);
        Console.WriteLine($"field {md.GetString(md.GetTypeDefinition(f.GetDeclaringType()).Namespace)}.{md.GetString(md.GetTypeDefinition(f.GetDeclaringType()).Name)}.{md.GetString(f.Name)}");
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
                for (int i = 0; i + 4 < il.Length; i++)
                    if ((il[i] == 0x7E || il[i] == 0x7B || il[i] == 0x7F) && BitConverter.ToInt32(il, i + 1) == tok)
                    { Console.WriteLine($"  used in {md.GetString(t.Namespace)}.{md.GetString(t.Name)}.{md.GetString(m.Name)}"); break; }
            }
        }
        return 0;
    }

    static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "field") return Field(args[1], args[2]);
        if (args.Length >= 4 && args[0] == "il") return Il(args[1], args[2], args[3]);
        if (args.Length >= 3 && args[0] == "callers") return Callers(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "members") return Members(args[1], args[2], args.Length > 3 ? args[3] : null);
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
