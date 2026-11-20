using System;
using System.Linq;

namespace Nextra.Host
{
    /// The person's own AI keys (bring your own key) for the optional decision-API assistant.
    /// Stored by the operating system's secret store (PlatformSecrets: Windows DPAPI for the current user, WindowsSecrets.cs;
    /// Mac Keychain, mac/Nextra.Host.Mac/MacPlatform.cs). A key never goes to config.json, the user folder, memory, logs,
    /// the MCP server or the assistant page: only AiProxy reads it, to add it to the one request it forwards.
    static class KeyStore
    {
        public static readonly string[] Providers = { "openai", "gemini", "jev", "anthropic" };
        public static string Label(string p) => p == "openai" ? "OpenAI" : p == "gemini" ? "Gemini" : p == "jev" ? "Jev" : p == "anthropic" ? "Claude (Anthropic)" : p;

        static string Name(string provider) => "ai-key-" + provider;

        public static bool Has(string provider) => !string.IsNullOrEmpty(Get(provider));

        /// The last 4 characters, for "saved: …abcd" (never more).
        public static string Hint(string provider)
        {
            var k = Get(provider);
            return string.IsNullOrEmpty(k) ? null : "…" + (k.Length > 4 ? k.Substring(k.Length - 4) : "");
        }

        internal static string Get(string provider)
        {
            if (!Providers.Contains(provider)) return null;
            try { return PlatformSecrets.Read(Name(provider)); } catch { return null; }
        }

        public static void Set(string provider, string key)
        {
            if (!Providers.Contains(provider)) throw new ArgumentException("unknown provider " + provider);
            key = (key ?? "").Trim();
            if (key.Length == 0) { PlatformSecrets.Delete(Name(provider)); return; }
            if (key.Length < 20 || key.Length > 400 || key.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')))
                throw new ArgumentException("this does not look like an API key (20-400 letters, digits, _ - .)");
            PlatformSecrets.Write(Name(provider), key);
        }

        public static void Clear(string provider) { try { PlatformSecrets.Delete(Name(provider)); } catch { } }

        /// The providers with a key, in the order of Providers.
        public static string[] Saved() => Providers.Where(Has).ToArray();
    }
}
