using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nextra.Host
{
    /// The assistant page's only way to the AI providers (the same rules as nextra.structuralweb's ai_proxy.py):
    /// the page names a provider and an endpoint; this adds the person's key (KeyStore) and forwards it. In-process only:
    /// nothing is added to the 127.0.0.1 server, so no other program can spend the key.
    /// - Endpoints: an exact list per provider. The upstream URL is a constant base plus the matched entry, never the path as sent.
    /// - Keys are never returned; anything key-like in an error body is masked.
    /// - Always called off the UI thread (HttpClient, up to 5 min for long reasoning calls).
    static class AiProxy
    {
        class Prov { public string Upstream; public HashSet<string> Endpoints; }

        static readonly Dictionary<string, Prov> Providers = new Dictionary<string, Prov>
        {
            ["openai"] = new Prov { Upstream = "https://api.openai.com/v1/", Endpoints = new HashSet<string> { "GET models", "POST responses", "POST chat/completions", "POST decisions" } },
            // Gemini chat and models through Google's OpenAI-compatible endpoint; decisions through Gemini's own Decisions API
            ["gemini"] = new Prov { Upstream = "https://generativelanguage.googleapis.com/v1beta/openai/", Endpoints = new HashSet<string> { "GET models", "POST chat/completions", "POST decisions" } },
            ["jev"] = new Prov { Upstream = "https://api.typesafe.ai/v1/", Endpoints = new HashSet<string> { "POST systemone" } },
            ["anthropic"] = new Prov { Upstream = "https://api.anthropic.com/v1/", Endpoints = new HashSet<string> { "GET models", "POST messages" } },
        };
        const string GeminiNative = "https://generativelanguage.googleapis.com/v1beta/";
        static readonly Regex KeyLike = new Regex(@"(sk-|AIza|Bearer\s+)[\w\-]{6,}", RegexOptions.Compiled);
        const int MaxBody = 8 * 1024 * 1024;     // pictures as data URLs fit; anything larger is refused

        static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(5) };

        public class Reply { public int Status; public string Body; public string ContentType = "application/json"; public string RetryAfter; }

        /// Anthropic beta flags the page may ask for (sent as the anthropic-beta header; anything else is dropped)
        static readonly HashSet<string> AnthropicBetas = new HashSet<string> { "server-side-fallback-2026-07-01" };

        public static async Task<Reply> Forward(string provider, string method, string path, string body, CancellationToken ct = default, string beta = null)
        {
            method = (method ?? "GET").ToUpperInvariant();
            path = (path ?? "").Trim('/');
            if (!Providers.TryGetValue(provider ?? "", out var prov) || !prov.Endpoints.Contains(method + " " + path))
                return Error(404, "not_allowed", $"{provider} {method} {path} is not an assistant endpoint");
            var key = KeyStore.Get(provider);
            if (string.IsNullOrEmpty(key))
                return Error(401, "no_key", $"no {KeyStore.Label(provider)} key saved: run NextraDecisionApi to add one");
            if (body != null && body.Length > MaxBody) return Error(413, "too_large", "the request is too large");

            bool native = provider == "gemini" && path == "decisions";
            var url = (native ? GeminiNative : prov.Upstream) + path;
            using var req = new HttpRequestMessage(new HttpMethod(method), url);
            if (native) req.Headers.Add("x-goog-api-key", key);
            else if (provider == "anthropic")
            {
                req.Headers.TryAddWithoutValidation("x-api-key", key);
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                var flags = (beta ?? "").Split(',').Select(x => x.Trim()).Where(AnthropicBetas.Contains).ToArray();
                if (flags.Length > 0) req.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", flags));
            }
            else req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            if (method == "POST") req.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
            try
            {
                using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
                var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var r = new Reply { Status = (int)res.StatusCode, Body = res.IsSuccessStatusCode ? text : Mask(text) };
                if (res.Content.Headers.ContentType != null) r.ContentType = res.Content.Headers.ContentType.ToString();
                if (res.Headers.RetryAfter?.Delta != null) r.RetryAfter = ((int)res.Headers.RetryAfter.Delta.Value.TotalSeconds).ToString();
                return r;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Error(499, "cancelled", "cancelled"); }
            catch (Exception e) { return Error(502, "network", $"could not reach {KeyStore.Label(provider)}: {Mask(e.Message)}"); }
        }

        static string Mask(string s) => KeyLike.Replace(s ?? "", m => m.Groups[1].Value + "…");

        static Reply Error(int status, string code, string message) => new Reply
        {
            Status = status,
            Body = System.Text.Json.JsonSerializer.Serialize(new { error = new { code, message } }),
        };

        /// "Check keys": the cheapest call that proves the key works (a model list, or one tiny decision for Jev).
        public static async Task<string> Check(string provider)
        {
            if (!KeyStore.Has(provider)) return "no key";
            Reply r;
            if (provider == "jev")
                r = await Forward("jev", "POST", "systemone", "{\"model\":\"jev-latest\",\"state\":{\"request\":\"move the curve 2 m in x\"},\"questions\":{\"fn\":{\"type\":\"choice\",\"instructions\":\"Which function?\",\"criteria\":{\"move\":null,\"copy\":null}}}}").ConfigureAwait(false);
            else
                r = await Forward(provider, "GET", "models", null).ConfigureAwait(false);
            if (r.Status == 200) return "works";
            try
            {
                var j = System.Text.Json.Nodes.JsonNode.Parse(r.Body);
                var msg = (string)(j?["error"]?["message"] ?? j?[0]?["error"]?["message"]);
                return $"refused ({r.Status}){(msg != null ? ": " + Short(msg) : "")}";
            }
            catch { return $"refused ({r.Status})"; }
        }

        static string Short(string s) => s.Length > 120 ? s.Substring(0, 120) + "…" : s;
    }
}
