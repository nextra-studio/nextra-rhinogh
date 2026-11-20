using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nextra.Host
{
    /// Windows secret store for KeyStore: each value encrypted with DPAPI for the current Windows user (CryptProtectData, the
    /// same protection browsers use for saved passwords), kept in secrets.json in the app folder. Only this Windows account can
    /// decrypt it. P/Invoke instead of the ProtectedData package: no extra DLL to ship.
    /// Not compiled on Mac (mac/Nextra.Host.Mac/MacPlatform.cs has the Keychain version).
    static class PlatformSecrets
    {
        static string File => Path.Combine(Server.AppDir, "secrets.json");
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NextraRhinoGh.ai-keys.v1");

        public static string Read(string name)
        {
            var o = Load();
            var b64 = (string)o[name];
            if (string.IsNullOrEmpty(b64)) return null;
            return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(b64)));
        }

        public static void Write(string name, string value)
        {
            var o = Load();
            o[name] = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(value)));
            Save(o);
        }

        public static void Delete(string name)
        {
            var o = Load();
            if (o.Remove(name)) Save(o);
        }

        static JsonObject Load()
        {
            try { if (System.IO.File.Exists(File)) return JsonNode.Parse(System.IO.File.ReadAllText(File)) as JsonObject ?? new JsonObject(); } catch { }
            return new JsonObject();
        }

        static void Save(JsonObject o)
        {
            Directory.CreateDirectory(Server.AppDir);
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(tmp, File, true);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Blob { public int Size; public IntPtr Data; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref Blob dataIn, string description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob dataOut);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr h);

        const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        static byte[] Protect(byte[] data) => Run(data, true);
        static byte[] Unprotect(byte[] data) => Run(data, false);

        static byte[] Run(byte[] data, bool protect)
        {
            var hIn = GCHandle.Alloc(data, GCHandleType.Pinned);
            var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            var bin = new Blob { Size = data.Length, Data = hIn.AddrOfPinnedObject() };
            var ent = new Blob { Size = Entropy.Length, Data = hEnt.AddrOfPinnedObject() };
            var bout = new Blob();
            try
            {
                bool ok = protect
                    ? CryptProtectData(ref bin, "NextraRhinoGh AI key", ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref bout)
                    : CryptUnprotectData(ref bin, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref bout);
                if (!ok) throw new InvalidOperationException("Windows could not " + (protect ? "encrypt" : "decrypt") + " the key (error " + Marshal.GetLastWin32Error() + ")");
                var res = new byte[bout.Size];
                Marshal.Copy(bout.Data, res, 0, bout.Size);
                return res;
            }
            finally
            {
                hIn.Free(); hEnt.Free();
                if (bout.Data != IntPtr.Zero) LocalFree(bout.Data);
            }
        }
    }
}
