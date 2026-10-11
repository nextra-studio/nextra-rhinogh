using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Rhino;

namespace Nextra.Host
{
    /// Minimal HTTP/1.1 server bound to 127.0.0.1 only, token-authenticated.
    /// Runs on its own thread so it can always answer (timeouts, dialog detection) even when the UI thread is blocked.
    static class Server
    {
        static TcpListener _listener;
        static Thread _thread;
        static volatile bool _running;
        static int _restarts;
        public static int Port { get; private set; }
        public static string Token { get; private set; }
        public static bool IsListening => _running && _listener != null;
        public static DateTime LastClientCall = DateTime.MinValue;

        public static string AppDir
        {
            get
            {
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nextra");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static void Start()
        {
            if (_running) return;
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            int basePort = Config.GetInt("port", 8765) + _restarts % 3;  // a restart after failure tries other ports too
            _restarts++;
            for (int p = basePort; p < basePort + 20; p++)
            {
                try { _listener = new TcpListener(IPAddress.Loopback, p); _listener.Start(); Port = p; break; }
                catch { _listener = null; }
            }
            if (_listener == null) throw new Exception("no free port in " + basePort + ".." + (basePort + 19));

            var info = new JsonObject
            {
                ["port"] = Port,
                ["token"] = Token,
                ["pid"] = Environment.ProcessId,
                ["started"] = DateTime.Now.ToString("s"),
            };
            // latest instance wins server.json; per-pid file lets a bridge pick a specific Rhino
            File.WriteAllText(Path.Combine(AppDir, "server.json"), info.ToJsonString());
            File.WriteAllText(Path.Combine(AppDir, $"server-{Environment.ProcessId}.json"), info.ToJsonString());

            _running = true;
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "NextraServer" };
            _thread.Start();
            RhinoApp.WriteLine($"Nextra: listening on 127.0.0.1:{Port}");
        }

        public static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { File.Delete(Path.Combine(AppDir, $"server-{Environment.ProcessId}.json")); } catch { }
        }

        static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (!_running) break; continue; }
                ThreadPool.QueueUserWorkItem(_ => Handle(c));
            }
        }

        static void Handle(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                string body;
                int code = 200;
                try
                {
                    var req = HttpLite.Read(stream);
                    if (req == null) return;
                    if (!req.Headers.TryGetValue("x-nextra-token", out var tok) || tok != Token)
                    {
                        code = 401;
                        body = "{\"ok\":false,\"text\":\"bad token: read %APPDATA%/Nextra/server.json\"}";
                    }
                    else { LastClientCall = DateTime.Now; body = Route(req); }
                }
                catch (Exception e)
                {
                    code = 500;
                    body = JsonSerializer.Serialize(new { ok = false, text = "host error: " + e.Message });
                }
                try { HttpLite.Write(stream, code, body); } catch { }
            }
        }

        static string Route(HttpLite.Request req)
        {
            switch (req.Path)
            {
                case "/health":
                    return JsonSerializer.Serialize(new { ok = true, port = Port, engine = EngineLoader.Describe(), busy = Dispatcher.BusyText() });
                case "/reload":
                    return Dispatcher.Reload();
                case "/call":
                    var j = JsonNode.Parse(req.Body ?? "{}");
                    string tool = j?["tool"]?.GetValue<string>() ?? "";
                    string args = j?["args"]?.ToJsonString() ?? "{}";
                    int timeout = 60;
                    try { if (j?["timeout"] != null) timeout = j["timeout"].GetValue<int>(); } catch { }
                    return Dispatcher.Call(tool, args, Math.Clamp(timeout, 1, 1800) * 1000);
                default:
                    return "{\"ok\":false,\"text\":\"unknown path; use POST /call {tool,args}\"}";
            }
        }
    }

    static class HttpLite
    {
        public class Request
        {
            public string Method, Path, Body;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public static Request Read(Stream s)
        {
            var head = new MemoryStream();
            int b, state = 0;
            while ((b = s.ReadByte()) != -1)
            {
                head.WriteByte((byte)b);
                state = (b == '\r' && (state == 0 || state == 2)) ? state + 1 : (b == '\n' && (state == 1 || state == 3)) ? state + 1 : 0;
                if (state == 4) break;
                if (head.Length > 65536) throw new Exception("header too large");
            }
            if (head.Length == 0) return null;
            var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
            var first = lines[0].Split(' ');
            var r = new Request { Method = first[0], Path = first.Length > 1 ? first[1] : "/" };
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) r.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }
            if (r.Headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out int n) && n > 0)
            {
                var buf = new byte[n];
                int read = 0;
                while (read < n)
                {
                    int k = s.Read(buf, read, n - read);
                    if (k <= 0) break;
                    read += k;
                }
                r.Body = Encoding.UTF8.GetString(buf, 0, read);
            }
            return r;
        }

        public static void Write(Stream s, int code, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : "ERR")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }
    }

    static class Config
    {
        static JsonObject Load()
        {
            try
            {
                var p = Path.Combine(Server.AppDir, "config.json");
                if (File.Exists(p)) return JsonNode.Parse(File.ReadAllText(p)) as JsonObject;
            }
            catch { }
            return new JsonObject();
        }

        public static string GetString(string key, string dflt)
        {
            try { var v = Load()[key]; return v == null ? dflt : v.GetValue<string>(); } catch { return dflt; }
        }

        public static bool GetBool(string key, bool dflt)
        {
            try { var v = Load()[key]; return v == null ? dflt : v.GetValue<bool>(); } catch { return dflt; }
        }

        public static void Set(string key, JsonNode value)
        {
            try
            {
                var o = Load();
                o[key] = value;
                File.WriteAllText(Path.Combine(Server.AppDir, "config.json"), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static int GetInt(string key, int dflt)
        {
            try { var v = Load()[key]; return v == null ? dflt : v.GetValue<int>(); } catch { return dflt; }
        }
    }
}
