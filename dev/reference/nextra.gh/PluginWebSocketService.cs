using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Parameters;
using Grasshopper.GUI.Base;
using Rhino.Geometry;

namespace nextra
{
    public static class PluginWebSocketService
    {
        private static ClientWebSocket _webSocket;
        private static CancellationTokenSource _cts;
        private static List<Guid> _lastSpawnedGuids = new List<Guid>();
        private static List<Guid> _sessionSpawnedGuids = new List<Guid>();
        private static DateTime _lastActivity = DateTime.Now;
        private static bool _isRunning = false;

        private static string ServerUrl => NextraConfig.Instance.ServerUrl;

        private static void Log(string message, bool isSensitive = false)
        {
            if (NextraConfig.Instance.DeveloperMode)
            {
                RhinoApp.WriteLine(message.Replace("Shiba Copilot", "nextra.gh"));
            }
            else if (!isSensitive)
            {
                RhinoApp.WriteLine(message.Replace("Shiba Copilot", "nextra.gh"));
            }
            else
            {
                string cleanMsg = message.Replace("Shiba Copilot", "nextra.gh");
                if (cleanMsg.Contains("Connected to backend at"))
                {
                    RhinoApp.WriteLine("[nextra.gh] Connected to cloud backend.");
                }
                else if (cleanMsg.Contains("Connection error:"))
                {
                    RhinoApp.WriteLine("[nextra.gh] Cloud connection lost. Retrying...");
                }
            }
        }

        public static void Start()
        {
            if (_isRunning) return;
            if (_webSocket != null) Stop();

            _isRunning = true;
            _lastActivity = DateTime.Now;
            _cts = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();

            Task.Run(async () => await ConnectionLoop(_cts.Token));
            Task.Run(async () => await IdleCheckLoop(_cts.Token));
        }

        private static async Task IdleCheckLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(60000, token); // Check every minute
                if ((DateTime.Now - _lastActivity).TotalMinutes >= 15)
                {
                    Log("[nextra.gh] Disconnecting from cloud due to 15 minutes of inactivity to save costs.", true);
                    Stop();
                    break;
                }
            }
        }

        public static void Stop()
        {
            _isRunning = false;
            try
            {
                _cts?.Cancel();
                _webSocket?.Dispose();
            }
            catch { }
            _webSocket = null;
        }

        private static GH_Document GetActiveDocument()
        {
            try
            {
                // 1. Try the standard ActiveCanvas
                var canvas = Grasshopper.Instances.ActiveCanvas;
                if (canvas?.Document != null) return canvas.Document;

                // 2. Try our tracked LastCanvas
                if (NextraAssemblyPriority.LastCanvas?.Document != null)
                    return NextraAssemblyPriority.LastCanvas.Document;

                // 3. Exhaustive search in DocumentServer
                var server = Grasshopper.Instances.DocumentServer;
                if (server != null && server.DocumentCount > 0)
                {
                    foreach (var d in server)
                    {
                        var doc = d as GH_Document;
                        if (doc != null) return doc;
                    }
                }

                // 4. Extreme Fallback: Inspect all open forms in the process
                foreach (System.Windows.Forms.Form form in System.Windows.Forms.Application.OpenForms)
                {
                    if (form.GetType().FullName.Contains("GH_Canvas"))
                    {
                        var prop = form.GetType().GetProperty("Document");
                        if (prop != null)
                        {
                            var doc = prop.GetValue(form) as GH_Document;
                            if (doc != null) return doc;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[nextra.gh] GetActiveDocument Error: {ex.Message}");
            }

            Log("[nextra.gh] ERROR: No Grasshopper document found after exhaustive search.");
            return null;
        }

        private static async Task ConnectionLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                    
                    string baseQuery = $"?session_id={NextraConfig.Instance.SessionId}&username={Uri.EscapeDataString(NextraConfig.Instance.Email)}&email={Uri.EscapeDataString(NextraConfig.Instance.Email)}&password={Uri.EscapeDataString(NextraConfig.Instance.Password)}";
                    
                    string[] urlsToTry = new string[] 
                    {
                        "ws://127.0.0.1:58101/ws/plugin" + baseQuery,
                        "ws://127.0.0.1:58201/ws/plugin" + baseQuery,
                        "ws://127.0.0.1:58301/ws/plugin" + baseQuery,
                        ServerUrl
                    };
                    
                    bool connected = false;
                    foreach (var url in urlsToTry)
                    {
                        try 
                        {
                            _webSocket = new ClientWebSocket();
                            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                            await _webSocket.ConnectAsync(new Uri(url), token);
                            Log($"[nextra.gh] Connected to backend at {url}", true);
                            connected = true;
                            break;
                        }
                        catch 
                        {
                            _webSocket?.Dispose();
                            _webSocket = null;
                        }
                    }

                    if (!connected)
                    {
                        Log($"[nextra.gh] Connection failed for all backend URLs. Retrying in 3s...", true);
                        await Task.Delay(3000, token);
                        continue;
                    }

                    // Sync local knowledge files to the cloud backend on connection
                    await SyncKnowledgeFolderAsync();

                    var buffer = new byte[1024 * 1024 * 10];
                    var msgBuffer = new StringBuilder();

                    var pingTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var pingTask = Task.Run(async () => {
                        while (_webSocket != null && _webSocket.State == WebSocketState.Open && !pingTokenSource.Token.IsCancellationRequested)
                        {
                            try { await Task.Delay(20000, pingTokenSource.Token); } catch { break; }
                            if (_webSocket != null && _webSocket.State == WebSocketState.Open && !pingTokenSource.Token.IsCancellationRequested)
                            {
                                try
                                {
                                    string pingMsg = "{\"type\":\"ping\"}";
                                    byte[] bytes = Encoding.UTF8.GetBytes(pingMsg);
                                    await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, pingTokenSource.Token);
                                }
                                catch { break; }
                            }
                        }
                    }, pingTokenSource.Token);

                    while (_webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        try { result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token); }
                        catch { break; }

                        if (result.MessageType == WebSocketMessageType.Close) break;
                        msgBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        _lastActivity = DateTime.Now;

                        if (result.EndOfMessage)
                        {
                            string messageStr = msgBuffer.ToString();
                            msgBuffer.Clear();

                            if (string.IsNullOrEmpty(messageStr)) continue;

                            try
                            {
                                JObject request = JObject.Parse(messageStr);
                                string msgId = request["msg_id"]?.ToString();
                                string type = request["type"]?.ToString();

                                if (!string.IsNullOrEmpty(msgId) && !string.IsNullOrEmpty(type))
                                {
                                    if (type == "debug_info")
                                    {
                                        var debugData = new
                                        {
                                            pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                                            app_domain = AppDomain.CurrentDomain.FriendlyName,
                                            rhino_version = Rhino.RhinoApp.Version.ToString(),
                                            doc_count = Grasshopper.Instances.DocumentServer.DocumentCount,
                                            canvas_ready = (Grasshopper.Instances.ActiveCanvas != null),
                                            last_canvas_ready = (NextraAssemblyPriority.LastCanvas != null)
                                        };

                                        // Direct await instead of Task.Run to ensure reliability and fix warning
                                        try
                                        {
                                            var response = new { msg_id = msgId, status = "success", output = JsonConvert.SerializeObject(debugData) };
                                            byte[] responseBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(response));
                                            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                                            {
                                                await _webSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                                            }
                                        }
                                        catch (Exception sendEx)
                                        {
                                            Log($"[nextra.gh] Debug Send Error: {sendEx.Message}", true);
                                        }
                                    }
                                    else
                                    {
                                        RhinoApp.InvokeOnUiThread((Action)(() =>
                                        {
                                            ProcessAndSendResponse(msgId, type, request);
                                        }));
                                    }
                                }
                            }
                            catch (Exception parseEx)
                            {
                                Log($"[nextra.gh] JSON Parse Error: {parseEx.Message}", true);
                            }
                        }
                    }
                    try { pingTokenSource.Cancel(); } catch { }
                }
                catch (Exception ex)
                {
                    Log($"[nextra.gh] Connection error: {ex.Message}. Retrying...", true);
                    await Task.Delay(3000, token);
                }
            }
        }

        private static string GetKnowledgePath()
        {
            string baseDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            return Path.Combine(baseDir, "knowledge");
        }

        private static async Task SyncKnowledgeFolderAsync()
        {
            try
            {
                string knowledgePath = GetKnowledgePath();
                if (!Directory.Exists(knowledgePath))
                {
                    Directory.CreateDirectory(knowledgePath);
                }
                try
                {
                    var di = new DirectoryInfo(knowledgePath);
                    if (di.Exists)
                    {
                        di.Attributes |= FileAttributes.Hidden;
                    }
                }
                catch { }

                string indexJsonPath = Path.Combine(knowledgePath, "index.json");
                string indexJsonContent = "";
                JObject indexObj = new JObject();
                if (File.Exists(indexJsonPath))
                {
                    indexJsonContent = File.ReadAllText(indexJsonPath);
                    try { indexObj = JObject.Parse(indexJsonContent); } catch { }
                }

                var filesList = new List<object>();
                var mdFiles = Directory.GetFiles(knowledgePath, "*.md", SearchOption.AllDirectories);
                foreach (var file in mdFiles)
                {
                    string relativePath = file.Substring(knowledgePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    relativePath = relativePath.Replace('\\', '/');
                    
                    long fileMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds();
                    bool needsSync = true;
                    
                    if (indexObj.TryGetValue(relativePath, out JToken token) && token is JObject entry)
                    {
                        if (entry.TryGetValue("mtime", out JToken mtimeToken))
                        {
                            long indexMtime = mtimeToken.Value<long>();
                            if (Math.Abs(fileMtime - indexMtime) <= 2) // Allow small precision diffs
                            {
                                needsSync = false;
                            }
                        }
                    }

                    if (needsSync)
                    {
                        string content = File.ReadAllText(file);
                        filesList.Add(new
                        {
                            path = relativePath,
                            content = content
                        });
                    }
                }

                var payload = new
                {
                    type = "sync_knowledge",
                    files = filesList,
                    index_json = indexJsonContent
                };

                string jsonPayload = JsonConvert.SerializeObject(payload);
                byte[] bytes = Encoding.UTF8.GetBytes(jsonPayload);
                if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    Log($"[nextra.gh] Synchronized {filesList.Count} local knowledge files.");
                }
            }
            catch (Exception ex)
            {
                Log($"[nextra.gh] Knowledge sync error: {ex.Message}");
            }
        }

        private static void ProcessAndSendResponse(string msgId, string type, JObject request)
        {
            string output = "";
            string status = "success";

            try
            {
                if (type == "execute_python")
                {
                    string code = request["code"]?.ToString();
                    if (code != null && code.StartsWith("#!")) {
                        int nl = code.IndexOf('\n');
                        code = nl >= 0 ? code.Substring(nl + 1) : "";
                    }
                    var py = Rhino.Runtime.PythonScript.Create();
                    var sb = new System.Text.StringBuilder();
                    py.Output = new Action<string>((val) => { sb.Append(val); });
                    bool success = py.ExecuteScript(code);
                    output = sb.ToString();
                    if (!success) status = "error";
                }
                else if (type == "execute_csharp")
                {
                    string code = request["code"]?.ToString();
                    var originalOut = Console.Out;
                    var sw = new StringWriter();
                    Console.SetOut(sw);
                    
                    try {
                        string csFile = Path.Combine(Path.GetTempPath(), "nextragh_temp.cs");
                        File.WriteAllText(csFile, code ?? "", Encoding.UTF8);
                        RhinoApp.RunScript($@"-_ScriptEditor _Run ""{csFile}""", false);
                        try { File.Delete(csFile); } catch { }
                        output = "C# OK";
                    } catch (Exception ex) { 
                        status = "error";
                        output = ex.InnerException?.Message ?? ex.Message; 
                    } finally {
                        Console.SetOut(originalOut);
                    }
                }
                else if (type == "take_screenshot")
                {
                    string view = request["view"]?.ToString() ?? "Perspective";
                    var rhinoView = Rhino.RhinoDoc.ActiveDoc.Views.Find(view, false);
                    if (rhinoView == null) rhinoView = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView;

                    if (rhinoView != null)
                    {
                        bool oldGrid = rhinoView.ActiveViewport.ConstructionGridVisible;
                        bool oldAxes = rhinoView.ActiveViewport.ConstructionAxesVisible;
                        bool oldWorldAxes = rhinoView.ActiveViewport.WorldAxesVisible;

                        rhinoView.ActiveViewport.ConstructionGridVisible = false;
                        rhinoView.ActiveViewport.ConstructionAxesVisible = false;
                        rhinoView.ActiveViewport.WorldAxesVisible = false;
                        rhinoView.Redraw();

                        var bitmap = rhinoView.CaptureToBitmap(new System.Drawing.Size(800, 600));

                        rhinoView.ActiveViewport.ConstructionGridVisible = oldGrid;
                        rhinoView.ActiveViewport.ConstructionAxesVisible = oldAxes;
                        rhinoView.ActiveViewport.WorldAxesVisible = oldWorldAxes;
                        rhinoView.Redraw();

                        using (MemoryStream ms = new MemoryStream())
                        {
                            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                            output = Convert.ToBase64String(ms.ToArray());
                        }
                    }
                    else
                    {
                        status = "error";
                        output = "No active view found.";
                    }
                }
                else if (type == "investigate_canvas")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        var objs = doc.Objects;
                        var infoList = new System.Collections.Generic.List<object>();
                        foreach (var obj in objs)
                        {
                            var component = obj as Grasshopper.Kernel.IGH_Component;
                            var param = obj as Grasshopper.Kernel.IGH_Param;
                            var activeObj = obj as Grasshopper.Kernel.GH_ActiveObject;

                            string runtimeLevel = "None";
                            var runtimeMessages = new System.Collections.Generic.List<string>();
                            if (activeObj != null)
                            {
                                runtimeLevel = activeObj.RuntimeMessageLevel.ToString();
                                try
                                {
                                    var errs = activeObj.RuntimeMessages(Grasshopper.Kernel.GH_RuntimeMessageLevel.Error);
                                    if (errs != null) foreach (var m in errs) runtimeMessages.Add("Error: " + m);

                                    var warns = activeObj.RuntimeMessages(Grasshopper.Kernel.GH_RuntimeMessageLevel.Warning);
                                    if (warns != null) foreach (var m in warns) runtimeMessages.Add("Warning: " + m);
                                }
                                catch { }
                            }

                            if (component != null)
                            {
                                var inputs = new System.Collections.Generic.List<object>();
                                foreach (var p in component.Params.Input)
                                {
                                    inputs.Add(new { name = p.Name, nick = p.NickName, type = p.TypeName, count = p.VolatileDataCount });
                                }
                                var outputs = new System.Collections.Generic.List<object>();
                                foreach (var p in component.Params.Output)
                                {
                                    outputs.Add(new { name = p.Name, nick = p.NickName, type = p.TypeName, count = p.VolatileDataCount });
                                }
                                infoList.Add(new
                                {
                                    id = component.InstanceGuid,
                                    name = component.Name,
                                    nick = component.NickName,
                                    category = component.Category,
                                    subCategory = component.SubCategory,
                                    isComponent = true,
                                    x = component.Attributes != null ? component.Attributes.Pivot.X : 0f,
                                    y = component.Attributes != null ? component.Attributes.Pivot.Y : 0f,
                                    width = component.Attributes != null ? component.Attributes.Bounds.Width : 0f,
                                    height = component.Attributes != null ? component.Attributes.Bounds.Height : 0f,
                                    inputs = inputs,
                                    outputs = outputs,
                                    runtimeLevel = runtimeLevel,
                                    runtimeMessages = runtimeMessages,
                                    isSelected = obj.Attributes != null && obj.Attributes.Selected
                                });
                            }
                            else if (param != null)
                            {
                                string extraValue = "";
                                if (param is Grasshopper.Kernel.Special.GH_NumberSlider slider)
                                {
                                    extraValue = slider.CurrentValue.ToString();
                                }
                                else if (param is Grasshopper.Kernel.Special.GH_Panel panel)
                                {
                                    extraValue = panel.UserText;
                                }
                                else if (param is Grasshopper.Kernel.Special.GH_BooleanToggle toggle)
                                {
                                    extraValue = toggle.Value.ToString();
                                }

                                infoList.Add(new
                                {
                                    id = param.InstanceGuid,
                                    name = param.Name,
                                    nick = param.NickName,
                                    category = param.Category,
                                    subCategory = param.SubCategory,
                                    isComponent = false,
                                    x = param.Attributes != null ? param.Attributes.Pivot.X : 0f,
                                    y = param.Attributes != null ? param.Attributes.Pivot.Y : 0f,
                                    width = param.Attributes != null ? param.Attributes.Bounds.Width : 0f,
                                    height = param.Attributes != null ? param.Attributes.Bounds.Height : 0f,
                                    volatileDataCount = param.VolatileDataCount,
                                    value = extraValue,
                                    runtimeLevel = runtimeLevel,
                                    runtimeMessages = runtimeMessages,
                                    isSelected = obj.Attributes != null && obj.Attributes.Selected
                                });
                            }
                        }
                        output = JsonConvert.SerializeObject(infoList);
                    }
                    else
                    {
                        status = "error";
                        output = "No active Grasshopper document found.";
                    }
                }
                else if (type == "build_shorthand")
                {
                    string shorthand = request["shorthand"]?.ToString();
                    bool useFallbackLocal = request["use_fallback_local"]?.ToObject<bool>() ?? true;
                    bool cleanPrevious = request["clean_previous"]?.ToObject<bool>() ?? false;

                    PointF? originalAnchor = null;
                    if (request["anchor_x"] != null && request["anchor_y"] != null)
                    {
                        originalAnchor = new PointF(
                            request["anchor_x"].ToObject<float>(),
                            request["anchor_y"].ToObject<float>()
                        );
                    }

                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        if (cleanPrevious)
                        {
                            var guidsToDelete = new List<Guid>();
                            guidsToDelete.AddRange(_lastSpawnedGuids);
                            guidsToDelete.AddRange(_sessionSpawnedGuids);
                            var uniqueGuids = guidsToDelete.Distinct().ToList();

                            foreach (var guid in uniqueGuids)
                            {
                                try
                                {
                                    var obj = doc.FindObject(guid, false);
                                    if (obj != null) doc.RemoveObject(obj, false);
                                }
                                catch { }
                            }
                            _lastSpawnedGuids.Clear();
                            _sessionSpawnedGuids.Clear();
                        }

                        if (!originalAnchor.HasValue)
                        {
                            var validObjs = doc.SelectedObjects().OfType<IGH_DocumentObject>().Where(o => o.Attributes != null).ToList();
                            if (validObjs.Count > 0)
                            {
                                float maxX = validObjs.Max(o => o.Attributes.Bounds.Right);
                                float minY = validObjs.Min(o => o.Attributes.Bounds.Top);
                                originalAnchor = new PointF(maxX + 50f, minY);
                            }
                        }

                        var buildResult = BuildGraphFromShorthand(doc, shorthand, useFallbackLocal, originalAnchor);
                        if (buildResult.BuildErrors.Count > 0)
                        {
                            foreach (var guid in buildResult.SpawnedGuids)
                            {
                                try
                                {
                                    var obj = doc.FindObject(guid, false);
                                    if (obj != null) doc.RemoveObject(obj, false);
                                }
                                catch { }
                            }

                            status = "error";
                            output = JsonConvert.SerializeObject(new
                            {
                                status = "build_failed",
                                details = buildResult.BuildErrors
                            });
                        }
                        else
                        {
                            doc.DeselectAll();
                            foreach (var id in buildResult.SpawnedGuids)
                            {
                                var obj = doc.FindObject(id, false);
                                if (obj != null) obj.Attributes.Selected = true;
                            }

                            _lastSpawnedGuids.Clear();
                            _lastSpawnedGuids.AddRange(buildResult.SpawnedGuids);
                            foreach (var guid in buildResult.SpawnedGuids)
                            {
                                if (!_sessionSpawnedGuids.Contains(guid)) _sessionSpawnedGuids.Add(guid);
                            }

                            try
                            {
                                bool hasGroup = buildResult.SpawnedGuids.Any(id => doc.FindObject(id, false) is GH_Group);
                                if (!hasGroup && buildResult.SpawnedGuids.Count > 0)
                                {
                                    var autoGrp = new GH_Group();
                                    autoGrp.CreateAttributes();
                                    autoGrp.NickName = "Copilot Stage";

                                    autoGrp.Colour = Color.FromArgb(80, 78, 121, 242);

                                    foreach (var id in buildResult.SpawnedGuids)
                                    {
                                        var obj = doc.FindObject(id, false);
                                        if (obj != null && !(obj is GH_Group) && !(obj is GH_Scribble))
                                        {
                                            autoGrp.AddObject(obj.InstanceGuid);
                                        }
                                    }

                                    if (autoGrp.ObjectIDs.Count > 0)
                                    {
                                        doc.AddObject(autoGrp, false);
                                        autoGrp.ExpireCaches();
                                        buildResult.SpawnedGuids.Add(autoGrp.InstanceGuid);
                                        if (!_sessionSpawnedGuids.Contains(autoGrp.InstanceGuid))
                                            _sessionSpawnedGuids.Add(autoGrp.InstanceGuid);
                                    }
                                }
                            }
                            catch { }

                            doc.NewSolution(true);

                            string canvasErrors = GetCanvasErrors(doc, buildResult.GuidToIdMap);

                            output = JsonConvert.SerializeObject(new
                            {
                                status = "success",
                                spawned_guids = buildResult.SpawnedGuids,
                                guid_to_id = buildResult.GuidToIdMap,
                                canvas_errors = canvasErrors
                            });
                        }
                    }
                    else
                    {
                        status = "error";
                        output = "No active Grasshopper document found.";
                    }
                }
                else if (type == "reset_session")
                {
                    _lastSpawnedGuids.Clear();
                    _sessionSpawnedGuids.Clear();
                    status = "success";
                    output = "Session cleared.";
                }
                else if (type == "clear_previous")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        int deletedCount = 0;
                        var guidsToDelete = new List<Guid>();

                        bool specificCleanup = false;

                        if (request["guids"] != null)
                        {
                            specificCleanup = true;
                            foreach (var gTok in request["guids"])
                            {
                                if (Guid.TryParse(gTok.ToString(), out Guid parsedGuid))
                                {
                                    guidsToDelete.Add(parsedGuid);
                                }
                            }
                        }

                        if (request["group_name"] != null)
                        {
                            specificCleanup = true;
                            string groupName = request["group_name"].ToString().ToLower();
                            foreach (var obj in doc.Objects)
                            {
                                if (obj is GH_Group grp && (grp.NickName?.ToLower() == groupName || grp.Name?.ToLower() == groupName))
                                {
                                    guidsToDelete.Add(grp.InstanceGuid);
                                    foreach (var childId in grp.ObjectIDs)
                                    {
                                        guidsToDelete.Add(childId);
                                    }
                                }
                            }
                        }

                        if (!specificCleanup)
                        {
                            guidsToDelete.AddRange(_lastSpawnedGuids);
                        }

                        var uniqueGuids = guidsToDelete.Distinct().ToList();

                        foreach (var guid in uniqueGuids)
                        {
                            try
                            {
                                var obj = doc.FindObject(guid, false);
                                if (obj != null)
                                {
                                    doc.RemoveObject(obj, false);
                                    deletedCount++;

                                    _lastSpawnedGuids.Remove(guid);
                                    _sessionSpawnedGuids.Remove(guid);
                                }
                            }
                            catch { }
                        }

                        if (!specificCleanup)
                        {
                            _lastSpawnedGuids.Clear();
                            _sessionSpawnedGuids.Clear();
                        }

                        doc.NewSolution(true);

                        status = "success";
                        output = $"Successfully cleaned up {deletedCount} components from canvas.";
                    }
                    else
                    {
                        status = "error";
                        output = "No active Grasshopper document found.";
                    }
                }
                else if (type == "get_selected")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        var selected = doc.SelectedObjects().OfType<IGH_DocumentObject>().ToList();
                        var info = selected.Select(s => new
                        {
                            id = s.InstanceGuid,
                            nk = s.NickName,
                            t = s.Name,
                            x = s.Attributes.Pivot.X,
                            y = s.Attributes.Pivot.Y,
                            bounds = s.Attributes.Bounds
                        }).ToList();
                        output = JsonConvert.SerializeObject(info);
                        status = "success";
                    }
                    else
                    {
                        status = "error";
                        output = "No active document.";
                    }
                }
                else if (type == "layout_canvas")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        List<string> targetGuids = null;
                        if (request["target_guids"] != null)
                        {
                            targetGuids = request["target_guids"].ToObject<List<string>>();
                        }
                        else
                        {
                            var sessionAndSelected = new HashSet<Guid>(_sessionSpawnedGuids);
                            var selected = doc.SelectedObjects().OfType<IGH_DocumentObject>();
                            foreach (var s in selected) sessionAndSelected.Add(s.InstanceGuid);

                            if (sessionAndSelected.Count > 0)
                            {
                                targetGuids = sessionAndSelected.Select(g => g.ToString()).ToList();
                            }
                        }
                        LayoutAllCanvasObjects(doc, targetGuids);
                        status = "success";
                        output = "Successfully performed topological auto-layout on the Grasshopper canvas.";
                    }
                    else
                    {
                        status = "error";
                        output = "No active Grasshopper document found.";
                    }
                }
                else if (type == "reverse_engineer")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        var guids = request["guids"]?.ToObject<List<Guid>>();
                        int depth = request["depth"]?.ToObject<int>() ?? 0;

                        if (guids == null || guids.Count == 0)
                        {
                            guids = doc.SelectedObjects().OfType<IGH_DocumentObject>().Select(o => o.InstanceGuid).ToList();
                        }

                        if (guids.Count > 0)
                        {
                            output = GenerateShorthandFromGuids(doc, guids, depth);
                            status = "success";
                        }
                        else
                        {
                            status = "error";
                            output = "No components selected or provided for reverse engineering.";
                        }
                    }
                    else
                    {
                        status = "error";
                        output = "No active document.";
                    }
                }
                else if (type == "examine_canvas")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        int count = doc.ObjectCount;
                        int warningCount = 0;
                        int errorCount = 0;
                        var details = new System.Collections.Generic.List<string>();

                        foreach (var obj in doc.Objects)
                        {
                            var comp = obj as GH_Component;
                            if (comp != null)
                            {
                                if (comp.RuntimeMessageLevel == GH_RuntimeMessageLevel.Warning)
                                {
                                    warningCount++;
                                    var msgs = comp.RuntimeMessages(GH_RuntimeMessageLevel.Warning);
                                    if (msgs != null && msgs.Count > 0)
                                    {
                                        details.Add($"\"Warning on '{comp.Name}' ({comp.NickName}): {string.Join(" | ", msgs).Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ")}\"");
                                    }
                                }
                                else if (comp.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error)
                                {
                                    errorCount++;
                                    var msgs = comp.RuntimeMessages(GH_RuntimeMessageLevel.Error);
                                    if (msgs != null && msgs.Count > 0)
                                    {
                                        details.Add($"\"Error on '{comp.Name}' ({comp.NickName}): {string.Join(" | ", msgs).Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ")}\"");
                                    }
                                }
                            }
                        }

                        RhinoApp.InvokeOnUiThread((Action)(() =>
                        {
                            Grasshopper.Instances.ActiveCanvas?.Refresh();
                        }));

                        status = "success";
                        output = $"{{\"total_elements\": {count}, \"warnings\": {warningCount}, \"errors\": {errorCount}, \"detailed_messages\": [{string.Join(",", details)}]}}";
                    }
                    else
                    {
                        status = "error";
                        output = "No active Grasshopper document found.";
                    }
                }
                else if (type == "group")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        var guids = request["guids"]?.ToObject<List<Guid>>();
                        string name = request["name"]?.ToString() ?? "Group";
                        string colorStr = request["color"]?.ToString() ?? "#ABDBFF";
                        if (guids != null && guids.Count > 0)
                        {
                            var grp = new GH_Group();
                            grp.NickName = name;
                            try { grp.Colour = ColorTranslator.FromHtml(colorStr); } catch { }
                            foreach (var g in guids) grp.AddObject(g);
                            doc.AddObject(grp, false);
                            status = "success";
                            output = grp.InstanceGuid.ToString();
                        }
                        else { status = "error"; output = "No GUIDs provided."; }
                    }
                    else { status = "error"; output = "No active document."; }
                }
                else if (type == "add_to_group")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        var guids = request["guids"]?.ToObject<List<Guid>>();
                        string groupGuidStr = request["group_guid"]?.ToString();
                        if (guids != null && guids.Count > 0 && !string.IsNullOrEmpty(groupGuidStr))
                        {
                            Guid groupGuid = Guid.Parse(groupGuidStr);
                            var grp = doc.FindObject(groupGuid, false) as GH_Group;
                            if (grp != null)
                            {
                                foreach (var g in guids)
                                {
                                    if (!grp.ObjectIDs.Contains(g))
                                    {
                                        grp.AddObject(g);
                                    }
                                }
                                grp.ExpireCaches();
                                doc.NewSolution(true);
                                status = "success";
                                output = grp.InstanceGuid.ToString();
                            }
                            else
                            {
                                status = "error";
                                output = $"Group with Guid '{groupGuidStr}' not found.";
                            }
                        }
                        else
                        {
                            status = "error";
                            output = "Invalid Guids or Group Guid provided.";
                        }
                    }
                    else { status = "error"; output = "No active document."; }
                }
                else if (type == "ungroup")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        var guids = request["guids"]?.ToObject<List<Guid>>();
                        if (guids != null)
                        {
                            foreach (var guid in guids)
                            {
                                var groups = doc.Objects.OfType<GH_Group>().Where(g => g.ObjectIDs.Contains(guid)).ToList();
                                foreach (var g in groups)
                                {
                                    g.RemoveObject(guid);
                                    if (g.ObjectIDs.Count == 0) doc.RemoveObject(g, false);
                                }
                            }
                            status = "success";
                            output = "Components removed from groups.";
                        }
                    }
                }
                else if (type == "disconnect")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        Guid guid = Guid.Parse(request["guid"].ToString());
                        int portIdx = request["port_index"]?.ToObject<int>() ?? 0;
                        bool isInput = request["is_input"]?.ToObject<bool>() ?? true;
                        var obj = doc.FindObject(guid, false);
                        if (obj is IGH_Component comp)
                        {
                            var list = isInput ? comp.Params.Input : comp.Params.Output;
                            if (portIdx < list.Count)
                            {
                                var p = list[portIdx];
                                if (isInput) p.RemoveAllSources();
                                else { var recs = p.Recipients.ToList(); foreach (var r in recs) r.RemoveSource(p); }
                                status = "success";
                            }
                        }
                        else if (obj is IGH_Param param)
                        {
                            param.RemoveAllSources();
                            status = "success";
                        }
                    }
                }
                else if (type == "connect")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        Guid fromGuid = Guid.Parse(request["from_guid"].ToString());
                        int fromPort = request["from_port"]?.ToObject<int>() ?? 0;
                        Guid toGuid = Guid.Parse(request["to_guid"].ToString());
                        int toPort = request["to_port"]?.ToObject<int>() ?? 0;
                        var sObj = doc.FindObject(fromGuid, false);
                        var tObj = doc.FindObject(toGuid, false);
                        IGH_Param sParam = null;
                        if (sObj is IGH_Component sC && fromPort < sC.Params.Output.Count) sParam = sC.Params.Output[fromPort];
                        else if (sObj is IGH_Param sP) sParam = sP;
                        IGH_Param tParam = null;
                        if (tObj is IGH_Component tC && toPort < tC.Params.Input.Count) tParam = tC.Params.Input[toPort];
                        else if (tObj is IGH_Param tP) tParam = tP;
                        if (sParam != null && tParam != null)
                        {
                            tParam.AddSource(sParam);
                            status = "success";
                        }
                        else { status = "error"; output = "Ports not found."; }
                    }
                }
                else if (type == "set_preview")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        var guids = request["guids"]?.ToObject<List<Guid>>();
                        bool visible = request["visible"]?.ToObject<bool>() ?? true;
                        if (guids != null)
                        {
                            foreach (var g in guids)
                            {
                                var obj = doc.FindObject(g, false);
                                if (obj is IGH_PreviewObject po) po.Hidden = !visible;
                            }
                            status = "success";
                        }
                    }
                }
                else if (type == "add_component")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        doc.ScheduleSolution(5, d =>
                        {
                            try
                            {
                                string t = request["type_name"]?.ToString() ?? request["type"]?.ToString();
                                string c = request["category"]?.ToString() ?? "";
                                string sc = request["subcategory"]?.ToString() ?? "";
                                float x = request["x"]?.ToObject<float>() ?? 0;
                                float y = request["y"]?.ToObject<float>() ?? 0;
                                var n = new Node { id = "new", t = t, c = c, sc = sc };
                                var obj = CreateObject(n, doc, new PointF(x, y));
                                if (obj != null)
                                {
                                    
                                    if (!_lastSpawnedGuids.Contains(obj.InstanceGuid)) _lastSpawnedGuids.Add(obj.InstanceGuid);
                                    if (!_sessionSpawnedGuids.Contains(obj.InstanceGuid)) _sessionSpawnedGuids.Add(obj.InstanceGuid);

                                    if (obj is IGH_Component comp)
                                    {
                                        comp.ExpireSolution(true);
                                    }
                                    else if (obj is IGH_Param param)
                                    {
                                        param.ExpireSolution(true);
                                    }
                                    
                                    status = "success";
                                    output = obj.InstanceGuid.ToString();
                                }
                                else { status = "error"; output = $"Component '{t}' not found."; }
                            }
                            catch (Exception ex)
                            {
                                status = "error";
                                output = $"Failed to add component: {ex.Message}";
                            }
                        });
                    }
                    else { status = "error"; output = "No active document."; }
                }
                else if (type == "add_script_component" || type == "modify_script_component")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        doc.ScheduleSolution(5, d =>
                        {
                            try
                            {
                                string lang = request["lang"]?.ToString() ?? "csharp";
                                string code = request["code"]?.ToString() ?? "";
                                
                                IGH_Component scriptComponent = null;

                                if (type == "modify_script_component")
                                {
                                    Guid guid = Guid.Parse(request["guid"].ToString());
                                    scriptComponent = doc.FindObject(guid, false) as IGH_Component;
                                    if (scriptComponent == null)
                                    {
                                        status = "error";
                                        output = "Component not found.";
                                        return;
                                    }
                                }
                                else
                                {
                                    float x = request["x"]?.ToObject<float>() ?? 0;
                                    float y = request["y"]?.ToObject<float>() ?? 0;
                                    
                                    string typeName = lang == "csharp" ? "RhinoCodePluginGH.Components.CSharpComponent, RhinoCodePluginGH" : "RhinoCodePluginGH.Components.Python3Component, RhinoCodePluginGH";
                                    Type sType = Type.GetType(typeName);
                                    if (sType == null)
                                    {
                                        status = "error";
                                        output = $"Error: Rhino 8 {lang} assembly could not be resolved.";
                                        return;
                                    }
                                    
                                    scriptComponent = (IGH_Component)Activator.CreateInstance(sType);
                                    if (scriptComponent != null)
                                    {
                                        scriptComponent.Attributes.Pivot = new PointF(x, y);
                                        scriptComponent.CreateAttributes();
                                        doc.AddObject(scriptComponent, false);
                                        if (!_lastSpawnedGuids.Contains(scriptComponent.InstanceGuid)) _lastSpawnedGuids.Add(scriptComponent.InstanceGuid);
                                        if (!_sessionSpawnedGuids.Contains(scriptComponent.InstanceGuid)) _sessionSpawnedGuids.Add(scriptComponent.InstanceGuid);
                                    }
                                }

                                if (scriptComponent != null)
                                {
                                    dynamic dynamicScript = scriptComponent;
                                    dynamicScript.SetSource(code);

                                    if (lang == "csharp")
                                    {
                                        string pattern = @"private\s+void\s+RunScript\s*\((.*?)\)";
                                        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(code, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
                                        
                                        if (match.Success)
                                        {
                                            string argsString = match.Groups[1].Value;
                                            string[] args = argsString.Split(',');

                                            int inputIndex = 0;
                                            int outputIndex = 1;

                                            foreach (string arg in args)
                                            {
                                                string trimmedArg = arg.Trim();
                                                if (string.IsNullOrEmpty(trimmedArg)) continue;

                                                string[] parts = trimmedArg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                                string paramName = parts.Last();

                                                if (trimmedArg.StartsWith("ref ") || trimmedArg.StartsWith("out "))
                                                {
                                                    if (outputIndex < scriptComponent.Params.Output.Count)
                                                    {
                                                        scriptComponent.Params.Output[outputIndex].NickName = paramName;
                                                        scriptComponent.Params.Output[outputIndex].Name = paramName;
                                                    }
                                                    outputIndex++;
                                                }
                                                else
                                                {
                                                    if (inputIndex < scriptComponent.Params.Input.Count)
                                                    {
                                                        scriptComponent.Params.Input[inputIndex].NickName = paramName;
                                                        scriptComponent.Params.Input[inputIndex].Name = paramName;
                                                    }
                                                    inputIndex++;
                                                }
                                            }

                                            int safeIn = 0;
                                            while (scriptComponent.Params.Input.Count > inputIndex && safeIn++ < 100)
                                                scriptComponent.Params.UnregisterInputParameter(scriptComponent.Params.Input[inputIndex], true);
                                            int safeOut = 0;
                                            while (scriptComponent.Params.Output.Count > outputIndex && safeOut++ < 100)
                                                scriptComponent.Params.UnregisterOutputParameter(scriptComponent.Params.Output[outputIndex], true);
                                        }
                                    }
                                    else if (lang == "python")
                                    {
                                        List<string> inGrips = request["in_grips"]?.ToObject<List<string>>() ?? new List<string>();
                                        List<string> outGrips = request["out_grips"]?.ToObject<List<string>>() ?? new List<string>();

                                        var vpComp = scriptComponent as Grasshopper.Kernel.IGH_VariableParameterComponent;

                                        int inIndex = 0;
                                        foreach (string ip in inGrips)
                                        {
                                            if (string.IsNullOrEmpty(ip)) continue;
                                            if (inIndex < scriptComponent.Params.Input.Count)
                                            {
                                                scriptComponent.Params.Input[inIndex].Name = ip;
                                                scriptComponent.Params.Input[inIndex].NickName = ip;
                                            }
                                            else
                                            {
                                                if (vpComp != null && vpComp.CanInsertParameter(GH_ParameterSide.Input, inIndex))
                                                {
                                                    var newParam = vpComp.CreateParameter(GH_ParameterSide.Input, inIndex);
                                                    if (newParam == null)
                                                    {
                                                        newParam = new Grasshopper.Kernel.Parameters.Param_GenericObject();
                                                        newParam.Access = GH_ParamAccess.item;
                                                    }
                                                    newParam.Name = ip;
                                                    newParam.NickName = ip;
                                                    scriptComponent.Params.RegisterInputParam(newParam);
                                                }
                                            }
                                            inIndex++;
                                        }

                                        int outIndex = 1;
                                        foreach (string op in outGrips)
                                        {
                                            if (string.IsNullOrEmpty(op)) continue;
                                            if (outIndex < scriptComponent.Params.Output.Count)
                                            {
                                                scriptComponent.Params.Output[outIndex].Name = op;
                                                scriptComponent.Params.Output[outIndex].NickName = op;
                                            }
                                            else
                                            {
                                                if (vpComp != null && vpComp.CanInsertParameter(GH_ParameterSide.Output, outIndex))
                                                {
                                                    var newParam = vpComp.CreateParameter(GH_ParameterSide.Output, outIndex);
                                                    if (newParam == null)
                                                    {
                                                        newParam = new Grasshopper.Kernel.Parameters.Param_GenericObject();
                                                        newParam.Access = GH_ParamAccess.item;
                                                    }
                                                    newParam.Name = op;
                                                    newParam.NickName = op;
                                                    scriptComponent.Params.RegisterOutputParam(newParam);
                                                }
                                            }
                                            outIndex++;
                                        }

                                        int safeInPy = 0;
                                        while (scriptComponent.Params.Input.Count > inIndex && safeInPy++ < 100)
                                        {
                                            int last = scriptComponent.Params.Input.Count - 1;
                                            if (vpComp != null && vpComp.CanRemoveParameter(GH_ParameterSide.Input, last))
                                                vpComp.DestroyParameter(GH_ParameterSide.Input, last);
                                            scriptComponent.Params.UnregisterInputParameter(scriptComponent.Params.Input[last], true);
                                        }
                                        int safeOutPy = 0;
                                        while (scriptComponent.Params.Output.Count > outIndex && safeOutPy++ < 100)
                                        {
                                            int last = scriptComponent.Params.Output.Count - 1;
                                            if (vpComp != null && vpComp.CanRemoveParameter(GH_ParameterSide.Output, last))
                                                vpComp.DestroyParameter(GH_ParameterSide.Output, last);
                                            scriptComponent.Params.UnregisterOutputParameter(scriptComponent.Params.Output[last], true);
                                        }
                                    }


                                    
                                    scriptComponent.Params.OnParametersChanged();
                                    
                                    if (scriptComponent.GetType().FullName.Contains("RhinoCodePluginGH"))
                                    {
                                        try
                                        {
                                            dynamic dynComp = scriptComponent;
                                            try { dynComp.SetParametersToScript(); } catch { }
                                            dynComp.VariableParameterMaintenance();
                                        }
                                        catch { }
                                    }

                                    scriptComponent.ExpireSolution(false);
                                    
                                    status = "success";
                                    output = scriptComponent.InstanceGuid.ToString();
                                }
                            }
                            catch (Exception ex)
                            {
                                status = "error";
                                output = $"Failed to manipulate component: {ex.Message}";
                            }
                        });
                    }
                    else { status = "error"; output = "No active document."; }
                }
                else if (type == "modify_component_variables")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        doc.ScheduleSolution(5, d =>
                        {
                            try
                            {
                                Guid guid = Guid.Parse(request["guid"].ToString());
                                string targetMode = request["target_mode"]?.ToString() ?? "Input";
                                string action = request["action"]?.ToString() ?? "rename";
                                List<int> indices = request["indices"]?.ToObject<List<int>>() ?? new List<int>();
                                List<string> names = request["names"]?.ToObject<List<string>>();
                                List<string> accessTypes = request["access_types"]?.ToObject<List<string>>();
                                
                                var comp = doc.FindObject(guid, false) as IGH_Component;
                                if (comp != null)
                                {
                                    bool isOutput = targetMode == "Output";
                                    var paramList = isOutput ? comp.Params.Output : comp.Params.Input;
                                    var vpComp = comp as Grasshopper.Kernel.IGH_VariableParameterComponent;
                                    var side = isOutput ? GH_ParameterSide.Output : GH_ParameterSide.Input;

                                    if (action == "add")
                                    {
                                        if (vpComp != null)
                                        {
                                            // Sort descending to insert without shifting earlier targets
                                            indices.Sort();
                                            indices.Reverse();
                                            foreach (int index in indices)
                                            {
                                                int safeIndex = Math.Max(0, Math.Min(index, paramList.Count));
                                                if (!vpComp.CanInsertParameter(side, safeIndex)) continue;

                                                IGH_Param newParam = vpComp.CreateParameter(side, safeIndex);
                                                if (newParam == null)
                                                {
                                                    newParam = new Grasshopper.Kernel.Parameters.Param_GenericObject();
                                                    newParam.Access = GH_ParamAccess.item;
                                                }

                                                string fallback = (isOutput ? "out_" : "in_") + safeIndex;
                                                newParam.Name = fallback;
                                                newParam.NickName = fallback;

                                                if (isOutput)
                                                {
                                                    comp.Params.RegisterOutputParam(newParam);
                                                    comp.Params.Output.RemoveAt(comp.Params.Output.Count - 1);
                                                    comp.Params.Output.Insert(safeIndex, newParam);
                                                }
                                                else
                                                {
                                                    comp.Params.RegisterInputParam(newParam);
                                                    comp.Params.Input.RemoveAt(comp.Params.Input.Count - 1);
                                                    comp.Params.Input.Insert(safeIndex, newParam);
                                                }
                                            }
                                        }
                                    }
                                    else if (action == "remove")
                                    {
                                        if (vpComp != null)
                                        {
                                            // Sort descending to remove without shifting
                                            indices.Sort();
                                            indices.Reverse();
                                            foreach (int index in indices)
                                            {
                                                if (index >= 0 && index < paramList.Count)
                                                {
                                                    if (!vpComp.CanRemoveParameter(side, index)) continue;
                                                    var paramToRemove = paramList[index];
                                                    vpComp.DestroyParameter(side, index);
                                                    if (isOutput) comp.Params.UnregisterOutputParameter(paramToRemove, true);
                                                    else comp.Params.UnregisterInputParameter(paramToRemove, true);
                                                }
                                            }
                                        }
                                    }
                                    else // rename
                                    {
                                        for (int i = 0; i < indices.Count; i++)
                                        {
                                            int idx = indices[i];
                                            if (idx >= 0 && idx < paramList.Count)
                                            {
                                                if (names != null && i < names.Count && !string.IsNullOrWhiteSpace(names[i]))
                                                {
                                                    paramList[idx].Name = names[i].Trim();
                                                    paramList[idx].NickName = names[i].Trim();
                                                }
                                                if (accessTypes != null && i < accessTypes.Count && !string.IsNullOrWhiteSpace(accessTypes[i]))
                                                {
                                                    string act = accessTypes[i].Trim().ToLower();
                                                    if (act == "list") paramList[idx].Access = GH_ParamAccess.list;
                                                    else if (act == "tree") paramList[idx].Access = GH_ParamAccess.tree;
                                                    else paramList[idx].Access = GH_ParamAccess.item;
                                                }
                                            }
                                        }
                                    }

                                    comp.Params.OnParametersChanged();
                                    
                                    if (comp.GetType().FullName.Contains("RhinoCodePluginGH"))
                                    {
                                        try
                                        {
                                            dynamic dynComp = comp;
                                            dynComp.VariableParameterMaintenance();
                                        }
                                        catch { }
                                    }

                                    comp.ExpireSolution(false);
                                    status = "success";
                                    output = "Variables modified.";
                                }
                                else { status = "error"; output = "Component not found."; }
                            }
                            catch (Exception ex) { status = "error"; output = ex.Message; }
                        });
                    }
                    else { status = "error"; output = "No active document."; }
                }
                else if (type == "modify_component_modifiers")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        doc.ScheduleSolution(5, d =>
                        {
                            try
                            {
                                Guid guid = Guid.Parse(request["guid"].ToString());
                                string targetMode = request["target_mode"]?.ToString() ?? "Input";
                                List<string> modifiers = request["modifiers"]?.ToObject<List<string>>();
                                
                                var comp = doc.FindObject(guid, false) as IGH_Component;
                                if (comp != null)
                                {
                                    var paramList = targetMode == "Output" ? comp.Params.Output : comp.Params.Input;

                                    for (int i = 0; i < paramList.Count; i++)
                                    {
                                        var p = paramList[i];
                                        p.DataMapping = GH_DataMapping.None;
                                        p.Simplify = false;
                                        p.Reverse = false;
                                        
                                        if (modifiers != null && i < modifiers.Count && !string.IsNullOrWhiteSpace(modifiers[i]))
                                        {
                                            string mods = modifiers[i].ToLower();
                                            if (mods.Contains("graft")) p.DataMapping = GH_DataMapping.Graft;
                                            else if (mods.Contains("flatten")) p.DataMapping = GH_DataMapping.Flatten;
                                            
                                            if (mods.Contains("simplify")) p.Simplify = true;
                                            if (mods.Contains("reverse")) p.Reverse = true;
                                        }
                                    }

                                    comp.Params.OnParametersChanged();
                                    comp.ExpireSolution(false);
                                    status = "success";
                                    output = "Modifiers updated.";
                                }
                                else { status = "error"; output = "Component not found."; }
                            }
                            catch (Exception ex) { status = "error"; output = ex.Message; }
                        });
                    }
                    else { status = "error"; output = "No active document."; }
                }
                else if (type == "remove_components")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        var guids = request["guids"]?.ToObject<List<Guid>>();
                        if (guids != null)
                        {
                            foreach (var g in guids)
                            {
                                var obj = doc.FindObject(g, false);
                                if (obj != null) doc.RemoveObject(obj, false);
                            }
                            status = "success";
                        }
                    }
                }
                else if (type == "modify_variable_parameters")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        Guid guid = Guid.Parse(request["guid"].ToString());
                        int targetCount = request["count"]?.ToObject<int>() ?? 0;
                        bool isInput = request["is_input"]?.ToObject<bool>() ?? true;
                        var obj = doc.FindObject(guid, false);

                        if (obj is IGH_VariableParameterComponent vpc)
                        {
                            var comp = obj as IGH_Component;
                            if (comp != null)
                            {
                                var list = isInput ? comp.Params.Input : comp.Params.Output;
                                var side = isInput ? GH_ParameterSide.Input : GH_ParameterSide.Output;

                                int safeList = 0;
                                while (list.Count < targetCount && safeList++ < 100)
                                {
                                    vpc.CreateParameter(side, list.Count);
                                }
                                safeList = 0;
                                while (list.Count > targetCount && safeList++ < 100)
                                {
                                    vpc.DestroyParameter(side, list.Count - 1);
                                }

                                vpc.VariableParameterMaintenance();
                                comp.Attributes.ExpireLayout();
                                comp.Attributes.PerformLayout();
                                doc.NewSolution(true);
                                status = "success";
                                output = $"Adjusted {(isInput ? "inputs" : "outputs")} to {targetCount} for {comp.Name}.";
                            }
                        }
                        else
                        {
                            status = "error";
                            output = "Component does not support variable parameters.";
                        }
                    }
                }
                else if (type == "get_locations")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        var guids = request["guids"]?.ToObject<List<Guid>>();
                        var results = new List<object>();
                        var targets = guids != null ? guids : doc.Objects.Select(o => o.InstanceGuid).ToList();
                        foreach (var g in targets)
                        {
                            var obj = doc.FindObject(g, false);
                            if (obj != null && obj.Attributes != null)
                            {
                                results.Add(new
                                {
                                    id = g,
                                    nk = obj.NickName,
                                    x = obj.Attributes.Pivot.X,
                                    y = obj.Attributes.Pivot.Y,
                                    w = obj.Attributes.Bounds.Width,
                                    h = obj.Attributes.Bounds.Height
                                });
                            }
                        }
                        output = JsonConvert.SerializeObject(results);
                        status = "success";
                    }
                }
                else if (type == "move")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        var guids = request["guids"]?.ToObject<List<Guid>>();
                        float dx = request["dx"]?.ToObject<float>() ?? 0;
                        float dy = request["dy"]?.ToObject<float>() ?? 0;
                        if (guids != null)
                        {
                            foreach (var g in guids)
                            {
                                var obj = doc.FindObject(g, false);
                                if (obj != null && obj.Attributes != null)
                                {
                                    obj.Attributes.Pivot = new PointF(obj.Attributes.Pivot.X + dx, obj.Attributes.Pivot.Y + dy);
                                    obj.Attributes.ExpireLayout();
                                }
                            }
                            status = "success";
                        }
                    }
                }
                else if (type == "get_port_data")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        Guid guid = Guid.Parse(request["guid"].ToString());
                        int portIdx = request["port_index"]?.ToObject<int>() ?? 0;
                        bool isInput = request["is_input"]?.ToObject<bool>() ?? true;
                        var obj = doc.FindObject(guid, false);
                        IGH_Param param = null;
                        if (obj is IGH_Component comp)
                        {
                            var list = isInput ? comp.Params.Input : comp.Params.Output;
                            if (portIdx < list.Count) param = list[portIdx];
                        }
                        else if (obj is IGH_Param p) param = p;
                        if (param != null)
                        {
                            var treeStr = GetTreeStats(param);
                            var snippets = new List<string>();
                            if (param.VolatileDataCount > 0)
                            {
                                int count = 0;
                                foreach (var branch in param.VolatileData.AllData(true))
                                {
                                    if (branch != null)
                                    {
                                        snippets.Add(branch.ToString());
                                        if (++count > 10) break;
                                    }
                                }
                            }
                            output = JsonConvert.SerializeObject(new { tree = treeStr, count = param.VolatileDataCount, snippets = snippets });
                            status = "success";
                        }
                        else { status = "error"; output = "Port not found."; }
                    }
                }
                else if (type == "modify_grips")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        doc.ScheduleSolution(5, d =>
                        {
                            try
                            {
                                Guid guid = Guid.Parse(request["guid"].ToString());
                                var obj = doc.FindObject(guid, false);
                                if (obj is IGH_Component comp)
                                {
                                    var zData = request.ToObject<ZUIData>();
                                    HandleZUI(comp, zData);
                                    comp.Attributes.ExpireLayout();
                                    comp.Attributes.PerformLayout();
                                    comp.ExpireSolution(false);
                                    status = "success";
                                    output = $"Grips modified. Inputs: {comp.Params.Input.Count}, Outputs: {comp.Params.Output.Count}";
                                }
                                else { status = "error"; output = "Component not found or is not a valid component."; }
                            }
                            catch (Exception ex) { status = "error"; output = $"modify_grips failed: {ex.Message}"; }
                        });
                    }
                    else { status = "error"; output = "No active document."; }
                }
                else if (type == "get_ref_geom")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        Guid guid = Guid.Parse(request["guid"].ToString());
                        var obj = doc.FindObject(guid, false);
                        if (obj is IGH_Param param)
                        {
                            var refs = new List<object>();
                            foreach (var item in param.VolatileData.AllData(true))
                            {
                                try
                                {
                                    dynamic dItem = item;
                                    if (dItem.IsReferencedGeometry)
                                    {
                                        refs.Add(new
                                        {
                                            rhino_id = dItem.ReferenceGuid,
                                            type = item.TypeName
                                        });
                                    }
                                }
                                catch { }
                            }
                            output = JsonConvert.SerializeObject(refs);
                            status = "success";
                        }
                    }
                }
                else if (type == "investigate_geometry")
                {
                    var doc = GetActiveDocument();
                    if (doc != null)
                    {
                        Guid guid = Guid.Parse(request["guid"].ToString());
                        int portIdx = request["port_index"]?.ToObject<int>() ?? 0;
                        bool isInput = request["is_input"]?.ToObject<bool>() ?? true;
                        var obj = doc.FindObject(guid, false);
                        IGH_Param param = null;
                        if (obj is IGH_Component comp)
                        {
                            var list = isInput ? comp.Params.Input : comp.Params.Output;
                            if (portIdx < list.Count) param = list[portIdx];
                        }
                        else if (obj is IGH_Param p) param = p;
                        if (param != null)
                        {
                            var geoDetails = new List<object>();
                            foreach (var item in param.VolatileData.AllData(true))
                            {
                                if (item == null) continue;
                                object val = item.ScriptVariable();
                                if (val is Rhino.Geometry.Point3d pt)
                                {
                                    geoDetails.Add(new { type = "Point", x = pt.X, y = pt.Y, z = pt.Z });
                                }
                                else if (val is Rhino.Geometry.Line line)
                                {
                                    geoDetails.Add(new { type = "Line", start = line.From, end = line.To, length = line.Length });
                                }
                                else if (val is Rhino.Geometry.Curve crv)
                                {
                                    geoDetails.Add(new
                                    {
                                        type = "Curve",
                                        start = crv.PointAtStart,
                                        end = crv.PointAtEnd,
                                        mid = crv.PointAtNormalizedLength(0.5),
                                        length = crv.GetLength(),
                                        domain = new { min = crv.Domain.Min, max = crv.Domain.Max }
                                    });
                                }
                                else if (val is Rhino.Geometry.Brep brep)
                                {
                                    geoDetails.Add(new
                                    {
                                        type = "Brep",
                                        faces = brep.Faces.Count,
                                        edges = brep.Edges.Count,
                                        vertices = brep.Vertices.Count,
                                        bbox_center = brep.GetBoundingBox(true).Center
                                    });
                                }
                                else if (val is Rhino.Geometry.Mesh mesh)
                                {
                                    geoDetails.Add(new
                                    {
                                        type = "Mesh",
                                        vertices = mesh.Vertices.Count,
                                        faces = mesh.Faces.Count,
                                        bbox_center = mesh.GetBoundingBox(true).Center
                                    });
                                }
                            }
                            output = JsonConvert.SerializeObject(geoDetails);
                            status = "success";
                        }
                    }
                }
                else if (type == "save_knowledge")
                {
                    try
                    {
                        string title = request["title"]?.ToString();
                        string content = request["content"]?.ToString();
                        string category = request["category"]?.ToString() ?? "General";
                        string embeddingStr = request["embedding"]?.ToString();

                        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(content))
                        {
                            status = "error";
                            output = "Title and content are required.";
                        }
                        else
                        {
                            string knowledgePath = GetKnowledgePath();
                            string categoryPath = Path.Combine(knowledgePath, category);
                            if (!Directory.Exists(categoryPath))
                            {
                                Directory.CreateDirectory(categoryPath);
                            }
                            try 
                            {
                                var di = new DirectoryInfo(knowledgePath);
                                if (di.Exists)
                                {
                                    di.Attributes |= FileAttributes.Hidden;
                                }
                            } 
                            catch {}

                            string safeTitle = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
                            string mdFilename = safeTitle + ".md";
                            string mdFilePath = Path.Combine(categoryPath, mdFilename);
                            File.WriteAllText(mdFilePath, content);

                            string indexJsonPath = Path.Combine(knowledgePath, "index.json");
                            JObject indexObj = new JObject();
                            if (File.Exists(indexJsonPath))
                            {
                                try
                                {
                                    string existingJson = File.ReadAllText(indexJsonPath);
                                    indexObj = JObject.Parse(existingJson);
                                }
                                catch {}
                            }

                            string relativePath = $"{category}/{mdFilename}".Replace('\\', '/');
                            
                            JArray embArray = null;
                            if (!string.IsNullOrEmpty(embeddingStr))
                            {
                                try
                                {
                                    embArray = JArray.Parse(embeddingStr);
                                }
                                catch {}
                            }

                            indexObj[relativePath] = new JObject
                            {
                                ["mtime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                ["emb"] = embArray ?? new JArray()
                            };

                            File.WriteAllText(indexJsonPath, indexObj.ToString(Newtonsoft.Json.Formatting.None));
                            status = "success";
                            output = $"Successfully saved local knowledge file: {relativePath}";
                        }
                    }
                    catch (Exception ex)
                    {
                        status = "error";
                        output = $"Save knowledge error: {ex.Message}";
                    }
                }
                else if (type == "read_knowledge")
                {
                    try
                    {
                        string path = request["path"]?.ToString();
                        if (string.IsNullOrEmpty(path))
                        {
                            status = "error";
                            output = "Path is required.";
                        }
                        else
                        {
                            string knowledgePath = GetKnowledgePath();
                            string fullPath = Path.Combine(knowledgePath, path.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(fullPath))
                            {
                                output = File.ReadAllText(fullPath);
                                status = "success";
                            }
                            else
                            {
                                status = "error";
                                output = "File not found.";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        status = "error";
                        output = $"Read knowledge error: {ex.Message}";
                    }
                }
                else if (type == "manage_knowledge")
                {
                    try
                    {
                        string action = request["action"]?.ToString();
                        string filePath = request["file_path"]?.ToString();
                        string content = request["content"]?.ToString();
                        string embeddingStr = request["embedding"]?.ToString();
                        
                        if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(filePath))
                        {
                            status = "error";
                            output = "Action and file_path are required.";
                        }
                        else
                        {
                            string knowledgePath = GetKnowledgePath();
                            string fullPath = Path.Combine(knowledgePath, filePath.Replace('/', Path.DirectorySeparatorChar));
                            string indexJsonPath = Path.Combine(knowledgePath, "index.json");
                            
                            JObject indexObj = new JObject();
                            if (File.Exists(indexJsonPath))
                            {
                                try { indexObj = JObject.Parse(File.ReadAllText(indexJsonPath)); } catch {}
                            }

                            string relativePath = filePath.Replace('\\', '/');

                            if (action == "update")
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                                File.WriteAllText(fullPath, content ?? "");

                                JArray embArray = null;
                                if (!string.IsNullOrEmpty(embeddingStr))
                                {
                                    try { embArray = JArray.Parse(embeddingStr); } catch {}
                                }

                                indexObj[relativePath] = new JObject
                                {
                                    ["mtime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                    ["emb"] = embArray ?? new JArray()
                                };

                                File.WriteAllText(indexJsonPath, indexObj.ToString(Newtonsoft.Json.Formatting.None));
                                status = "success";
                                output = $"Updated local knowledge file: {relativePath}";
                            }
                            else if (action == "delete")
                            {
                                if (File.Exists(fullPath))
                                {
                                    File.Delete(fullPath);
                                }
                                
                                if (indexObj.ContainsKey(relativePath))
                                {
                                    indexObj.Remove(relativePath);
                                    File.WriteAllText(indexJsonPath, indexObj.ToString(Newtonsoft.Json.Formatting.None));
                                }
                                
                                status = "success";
                                output = $"Deleted local knowledge file: {relativePath}";
                            }
                            else
                            {
                                status = "error";
                                output = $"Unknown action: {action}";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        status = "error";
                        output = $"Manage knowledge error: {ex.Message}";
                    }
                }
                else if (type == "trigger_sync")
                {
                    _ = SyncKnowledgeFolderAsync();
                    status = "success";
                    output = "Local sync triggered.";
                }
                else
                {
                    status = "error";
                    output = $"Unknown request type: {type}";
                }
            }
            catch (Exception ex)
            {
                status = "error";
                output = ex.ToString();
            }

            // Use discard pattern '_' to explicitly mark as fire-and-forget and silence compiler warning
            _ = Task.Run(async () =>
            {
                try
                {
                    var response = new
                    {
                        msg_id = msgId,
                        status = status,
                        output = output
                    };
                    byte[] responseBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(response));
                    if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                    {
                        await _webSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
                catch { }
            });
        }

        public static string GetCanvasErrors(GH_Document ghDoc, Dictionary<Guid, string> guidToIdMap)
        {
            var healthReport = new List<object>();
            foreach (var kvp in guidToIdMap)
            {
                var obj = ghDoc.Objects.FirstOrDefault(o => o.InstanceGuid == kvp.Key) as IGH_ActiveObject;
                if (obj != null)
                {
                    var errors = obj.RuntimeMessages(GH_RuntimeMessageLevel.Error);
                    var warnings = obj.RuntimeMessages(GH_RuntimeMessageLevel.Warning);
                    if (errors.Count > 0 || warnings.Count > 0)
                    {
                        var upstreamInfo = new List<object>();
                        if (obj is IGH_Component comp)
                        {
                            for (int i = 0; i < comp.Params.Input.Count; i++)
                            {
                                var param = comp.Params.Input[i];
                                string hint = "";
                                var sourcesList = new List<object>();
                                foreach (var src in param.Sources)
                                {
                                    sourcesList.Add(new { connected_from_name = src.Attributes?.GetTopLevel?.DocObject?.Name ?? src.Name, connected_from_type = src.TypeName });
                                }
                                if (param.SourceCount > 0)
                                {
                                    if (param.Sources[0].TypeName != param.TypeName && param.TypeName != "Generic Data") hint = "Type Mismatch!";
                                }
                                else if (param.VolatileDataCount == 0 && !param.Optional)
                                {
                                    hint = "Missing mandatory input.";
                                }
                                upstreamInfo.Add(new { port_index = i, port_name = param.Name, expected_type = param.TypeName, actual_tree_received = GetTreeStats(param), potential_fix = hint, sources = sourcesList });
                            }
                        }
                        healthReport.Add(new { node_id = kvp.Value, component_name = obj.Name, errors = errors.ToList(), warnings = warnings.ToList(), upstream_inputs = upstreamInfo });
                    }
                }
            }
            if (healthReport.Count > 0) return JsonConvert.SerializeObject(new { status = "runtime_errors", details = healthReport }, Formatting.Indented);
            return "healthy";
        }

        private static string GetTreeStats(IGH_Param param)
        {
            if (param.VolatileData == null || param.VolatileData.DataCount == 0) return "Empty";
            var data = param.VolatileData;
            int pCount = data.PathCount;
            if (pCount == 0) return "Empty";
            if (pCount == 1)
            {
                GH_Path path = data.get_Path(0);
                System.Collections.IList branch = data.get_Branch(0);
                int bCount = branch != null ? branch.Count : 0;
                return "t=" + path.ToString() + "(" + bCount.ToString() + ")";
            }
            return "t=" + pCount.ToString() + " branches (N=" + data.DataCount.ToString() + ")";
        }

        private static BuildResult BuildGraphFromShorthand(GH_Document ghDoc, string shorthandText, bool useFallbackLocal, PointF? originalAnchor = null)
        {
            var result = new BuildResult();
            try
            {
                var response = ParseNextraAssembly(shorthandText);
                var tracker = new Dictionary<string, IGH_DocumentObject>();
                var overlayNodes = new List<Node>();

                foreach (var n in response.nodes)
                {
                    if (n.t == "Group" || n.t == "Scribble") { overlayNodes.Add(n); continue; }
                    if (tracker.ContainsKey(n.id) || ghDoc.Objects.FirstOrDefault(o => MatchId(o, n.id)) != null) continue;

                    IGH_DocumentObject obj = null;
                    try
                    {
                        var pivot = new PointF(0, 0);
                        obj = CreateObject(n, ghDoc, pivot);
                        if (obj == null) throw new Exception($"Component '{n.t}' not found.");

                        if (obj is IGH_Param && !(obj is IGH_Component)) obj.NickName = n.id;

                        result.SpawnedGuids.Add(obj.InstanceGuid);
                        result.GuidToIdMap[obj.InstanceGuid] = n.id;

                        if (n.v != null) ApplyConfiguration(obj, n.v);
                    }
                    catch (Exception ex)
                    {
                        result.BuildErrors.Add(new { error_type = "NodeCreationError", node_id = n.id, message = ex.Message });
                        continue;
                    }

                    tracker[n.id] = obj;
                    if (n.s != null) HandleState(obj, n.s);

                    if (obj is IGH_Component comp)
                    {
                        if (n.z != null) HandleZUI(comp, n.z);
                        if (n.i != null) ApplyParamConfig(comp.Params.Input, n.i);
                        if (n.o != null) ApplyParamConfig(comp.Params.Output, n.o);
                    }
                    else if (obj is IGH_Param param)
                    {
                        if (n.i != null && n.i.ContainsKey("0")) ApplySingleParamConfig(param, n.i["0"]);
                        if (n.o != null && n.o.ContainsKey("0")) ApplySingleParamConfig(param, n.o["0"]);
                    }
                }

                if (result.BuildErrors.Count == 0)
                {
                    foreach (var w in response.wires)
                    {
                        try
                        {
                            var s = ResolveParam(ghDoc, tracker, w.from, w.fromPort, true);
                            var t = ResolveParam(ghDoc, tracker, w.to, w.toPort, false);
                            if (s != null && t != null)
                            {
                                t.AddSource(s);
                                if (w.state == "h") t.WireDisplay = GH_ParamWireDisplay.hidden;
                                else if (w.state == "f") t.WireDisplay = GH_ParamWireDisplay.faint;
                            }
                            else
                            {
                                throw new Exception("Port not found.");
                            }
                        }
                        catch (Exception ex)
                        {
                            result.BuildErrors.Add(new { error_type = "WireError", from = w.from, to = w.to, message = ex.Message });
                        }
                    }
                }

                if (result.BuildErrors.Count == 0)
                {
                    var existingPivots = new Dictionary<Guid, PointF>();
                    float anchorX = float.MinValue;
                    float anchorY = float.MaxValue;
                    bool hasExisting = false;

                    foreach (var kvp in tracker)
                    {
                        if (!result.SpawnedGuids.Contains(kvp.Value.InstanceGuid))
                        {
                            existingPivots[kvp.Value.InstanceGuid] = kvp.Value.Attributes.Pivot;
                            if (kvp.Value.Attributes.Bounds.Right > anchorX) anchorX = kvp.Value.Attributes.Bounds.Right;
                            if (kvp.Value.Attributes.Bounds.Top < anchorY) anchorY = kvp.Value.Attributes.Bounds.Top;
                            hasExisting = true;
                        }
                    }

                    ApplyRecursiveLayout(tracker, response);

                    foreach (var kvp in existingPivots)
                    {
                        var obj = ghDoc.Objects.FirstOrDefault(o => o.InstanceGuid == kvp.Key);
                        if (obj != null)
                        {
                            obj.Attributes.Pivot = kvp.Value;
                            obj.Attributes.ExpireLayout();
                        }
                    }

                    if (hasExisting)
                    {
                        originalAnchor = new PointF(anchorX + 50f, anchorY);
                    }

                    var scribbles = new List<GH_Scribble>();
                    foreach (var n in overlayNodes.Where(x => x.t == "Scribble"))
                    {
                        var scr = new GH_Scribble();
                        scr.CreateAttributes();
                        scr.Attributes.Pivot = new PointF(0, 0);
                        if (n.v != null) ApplyConfiguration(scr, n.v);
                        ghDoc.AddObject(scr, false);
                        tracker[n.id] = scr;
                        scribbles.Add(scr);
                        result.SpawnedGuids.Add(scr.InstanceGuid);
                    }

                    AlignScribblesToGroupTopLeft(scribbles, response.nodes, tracker, 15f);

                    foreach (var n in overlayNodes.Where(x => x.t == "Group"))
                    {
                        var grp = new GH_Group();
                        grp.CreateAttributes();
                        if (n.v != null)
                        {
                            if (n.v["c"] != null) grp.Colour = ColorTranslator.FromHtml((string)n.v["c"]);
                            if (n.v["n"] != null) grp.NickName = (string)n.v["n"];
                            if (n.v["ids"] is JArray ids)
                            {
                                foreach (var idTok in ids)
                                {
                                    string id = idTok.ToString();
                                    if (tracker.ContainsKey(id)) grp.AddObject(tracker[id].InstanceGuid);
                                }
                            }
                        }
                        ghDoc.AddObject(grp, false);
                        grp.ExpireCaches();
                        result.SpawnedGuids.Add(grp.InstanceGuid);
                    }

                    ApplyFinalPlacement(ghDoc, tracker, response, result.SpawnedGuids, useFallbackLocal, originalAnchor);
                }
                result.Tracker = tracker;
            }
            catch (Exception ex)
            {
                result.BuildErrors.Add(new { error_type = "Fatal", message = ex.Message });
            }
            return result;
        }

        private static NextraResponse ParseNextraAssembly(string text)
        {
            var response = new NextraResponse { ctx = new List<ContextNode>(), nodes = new List<Node>(), wires = new List<Wire>() };
            var nodeDict = new Dictionary<string, Node>();

            Node GetOrCreateNode(string id)
            {
                if (!nodeDict.ContainsKey(id))
                {
                    var n = new Node { id = id, t = "Unknown" };
                    nodeDict[id] = n;
                    response.nodes.Add(n);
                }
                return nodeDict[id];
            }

            var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                string l = line.Trim();
                if (string.IsNullOrEmpty(l) || l.StartsWith("#") || l.StartsWith("```") || l.StartsWith("{") || l.StartsWith("[")) continue;

                try
                {
                    if (l.StartsWith("N "))
                    {
                        var parts = l.Substring(2).Split('|').Select(p => p.Trim()).ToArray();
                        var n = GetOrCreateNode(parts[0]);
                        if (parts.Length > 1) n.t = parts[1];
                        if (parts.Length > 2) n.c = parts[2];
                        if (parts.Length > 3) n.sc = parts[3];
                    }
                    else if (l.StartsWith("C "))
                    {
                        var parts = l.Substring(2).Split('|').Select(p => p.Trim()).ToArray();
                        var n = GetOrCreateNode(parts[0]);
                        if (parts.Length > 1)
                        {
                            var kv = ParseKV(parts[1]);
                            if (n.v == null) n.v = new JObject();
                            foreach (var k in kv)
                            {
                                if (k.Key == "ids")
                                {
                                    var arr = new JArray();
                                    foreach (var i in k.Value.Split(';')) arr.Add(i.Trim());
                                    n.v[k.Key] = arr;
                                }
                                else if (k.Key == "list")
                                {
                                    var arr = new JArray();
                                    foreach (var i in k.Value.Split(';')) arr.Add(i.Trim());
                                    n.v[k.Key] = arr;
                                }
                                else if (k.Key == "p")
                                {
                                    var coords = k.Value.Split(';');
                                    if (coords.Length >= 2) n.p = new float[] { float.Parse(coords[0]), float.Parse(coords[1]) };
                                }
                                else if (k.Key == "guid") n.guid = k.Value;
                                else if (k.Key == "st") n.st = k.Value;
                                else if (k.Value.ToLower() == "true") n.v[k.Key] = true;
                                else if (k.Value.ToLower() == "false") n.v[k.Key] = false;
                                else if (decimal.TryParse(k.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal d)) n.v[k.Key] = d;
                                else n.v[k.Key] = k.Value;
                            }
                        }
                    }
                    else if (l.StartsWith("P "))
                    {
                        var parts = l.Substring(2).Split('|').Select(p => p.Trim()).ToArray();
                        string target = parts[0];
                        if (target.Contains("."))
                        {
                            var tParts = target.Split('.');
                            var n = GetOrCreateNode(tParts[0]);
                            string portStr = tParts[1];
                            bool isInput = portStr.StartsWith("i");
                            string pIdx = portStr.Substring(1);
                            var pd = ParseKVtoParamData(parts.Length > 1 ? parts[1] : "");
                            if (isInput)
                            {
                                if (n.i == null) n.i = new Dictionary<string, ParamData>();
                                n.i[pIdx] = pd;
                            }
                            else
                            {
                                if (n.o == null) n.o = new Dictionary<string, ParamData>();
                                n.o[pIdx] = pd;
                            }
                        }
                    }
                    else if (l.StartsWith("W "))
                    {
                        var parts = l.Substring(2).Split('|').Select(p => p.Trim()).ToArray();
                        string wStr = parts[0].Replace(" ", "");
                        if (wStr.Contains(">"))
                        {
                            var st = wStr.Split('>');
                            var src = st[0].Split('.');
                            var tgt = st[1].Split('.');
                            string srcPortStr = new string(src[1].Where(char.IsDigit).ToArray());
                            string tgtPortStr = new string(tgt[1].Where(char.IsDigit).ToArray());
                            int pFrom = string.IsNullOrEmpty(srcPortStr) ? 0 : int.Parse(srcPortStr);
                            int pTo = string.IsNullOrEmpty(tgtPortStr) ? 0 : int.Parse(tgtPortStr);
                            var w = new Wire { from = src[0], fromPort = pFrom, to = tgt[0], toPort = pTo };
                            if (parts.Length > 1) w.state = parts[1];
                            response.wires.Add(w);
                        }
                    }
                    else if (l.StartsWith("Z "))
                    {
                        var parts = l.Substring(2).Split('|').Select(p => p.Trim()).ToArray();
                        var n = GetOrCreateNode(parts[0]);
                        if (parts.Length > 1)
                        {
                            var kv = ParseKV(parts[1]);
                            if (n.z == null) n.z = new ZUIData();
                            if (kv.ContainsKey("i")) n.z.i = int.Parse(kv["i"]);
                            if (kv.ContainsKey("o")) n.z.o = int.Parse(kv["o"]);
                        }
                    }
                    else if (l.StartsWith("S "))
                    {
                        var parts = l.Substring(2).Split('|').Select(p => p.Trim()).ToArray();
                        var n = GetOrCreateNode(parts[0]);
                        if (parts.Length > 1)
                        {
                            var kv = ParseKV(parts[1]);
                            if (n.s == null) n.s = new StateData();
                            if (kv.ContainsKey("h") && kv["h"].ToLower() == "true") n.s.h = true;
                            if (kv.ContainsKey("l") && kv["l"].ToLower() == "true") n.s.l = true;
                        }
                    }
                }
                catch { }
            }
            return response;
        }

        private static Dictionary<string, string> ParseKV(string kvString)
        {
            var res = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(kvString)) return res;
            var parts = kvString.Split(',');
            string currentKey = null;
            foreach (var p in parts)
            {
                int idx = p.IndexOf('=');
                if (idx > 0)
                {
                    currentKey = p.Substring(0, idx).Trim();
                    res[currentKey] = p.Substring(idx + 1).Trim();
                }
                else if (currentKey != null)
                {
                    res[currentKey] += "," + p;
                }
            }
            return res;
        }

        private static ParamData ParseKVtoParamData(string kvString)
        {
            var pd = new ParamData();
            var dict = ParseKV(kvString);
            var cult = System.Globalization.CultureInfo.InvariantCulture;
            if (dict.ContainsKey("m")) pd.m = dict["m"];
            if (dict.ContainsKey("x")) pd.x = dict["x"];
            if (dict.ContainsKey("wd")) pd.wd = dict["wd"];
            if (dict.ContainsKey("r") && dict["r"].ToLower() == "true") pd.r = true;
            if (dict.ContainsKey("s") && dict["s"].ToLower() == "true") pd.s = true;
            if (dict.ContainsKey("p") && dict["p"].ToLower() == "true") pd.p = true;
            if (dict.ContainsKey("inv") && dict["inv"].ToLower() == "true") pd.inv = true;
            if (dict.ContainsKey("val"))
            {
                string v = dict["val"];
                if (v.ToLower() == "true") pd.val = true;
                else if (v.ToLower() == "false") pd.val = false;
                else if (double.TryParse(v, System.Globalization.NumberStyles.Any, cult, out double d)) pd.val = d;
                else pd.val = v;
            }
            return pd;
        }

        private static void SetPersistentDataGeneric(IGH_Param param, object rawValue)
        {
            if (rawValue == null) return;
            try
            {
                Type baseType = param.GetType();
                while (baseType != null && (!baseType.IsGenericType || baseType.GetGenericTypeDefinition() != typeof(GH_PersistentParam<>))) baseType = baseType.BaseType;
                if (baseType == null) return;

                Type gooType = baseType.GetGenericArguments()[0];
                IGH_Goo tempGoo = null;
                if (gooType.IsInterface || gooType.IsAbstract)
                {
                    if (rawValue is bool) tempGoo = new GH_Boolean((bool)rawValue);
                    else if (rawValue is int) tempGoo = new GH_Integer((int)rawValue);
                    else if (rawValue is double || rawValue is float || rawValue is decimal) tempGoo = new GH_Number(Convert.ToDouble(rawValue));
                    else tempGoo = new GH_String(rawValue.ToString());
                }
                else tempGoo = (IGH_Goo)Activator.CreateInstance(gooType);

                if (tempGoo == null) return;
                bool success = false;
                string strVal = rawValue.ToString().Trim();
                if ((tempGoo is GH_Point || tempGoo is GH_Vector) && strVal.Contains(",") && !strVal.Contains("{")) strVal = "{" + strVal + "}";

                if (tempGoo.CastFrom(rawValue)) success = true;
                else if (tempGoo.CastFrom(strVal)) success = true;
                else if (strVal.Contains(",")) if (tempGoo.CastFrom(strVal.Replace(',', '.'))) success = true;

                if (success)
                {
                    var prop = baseType.GetProperty("PersistentData");
                    if (prop != null)
                    {
                        dynamic persistentData = prop.GetValue(param);
                        if (persistentData != null)
                        {
                            persistentData.Clear();
                            persistentData.Append((dynamic)tempGoo, new GH_Path(0));
                        }
                    }
                }
            }
            catch { }
        }

        private static void ApplyConfiguration(IGH_DocumentObject obj, JToken v)
        {
            if (v == null) return;
            if (obj is GH_NumberSlider s)
            {
                try
                {
                    int d = (int)(v["d"] ?? 2);
                    s.Slider.DecimalPlaces = d;
                    s.Slider.Type = d == 0 ? GH_SliderAccuracy.Integer : GH_SliderAccuracy.Float;
                    s.Slider.Minimum = (decimal)(v["min"] ?? 0.0);
                    s.Slider.Maximum = (decimal)(v["max"] ?? 10.0);
                    s.SetSliderValue(decimal.Parse(v["val"]?.ToString() ?? "0.5"));
                }
                catch { }
            }
            else if (obj is GH_Panel p)
            {
                if (v["wrap"] != null) { try { p.Properties.Wrap = (bool)v["wrap"]; } catch { } }
                if (v["ml"] != null) { try { p.Properties.Multiline = (bool)v["ml"]; } catch { } }
                else { try { p.Properties.Multiline = false; } catch { } }
                if (v["text"] != null)
                {
                    string rawValue = (string)v["text"];
                    string cleanText = rawValue.Replace("\\n", "\n").Replace("%2C", ",").Replace("%3D", "=");
                    p.UserText = cleanText;
                }
                p.OnObjectChanged(GH_ObjectEventType.PersistentData);
                p.ExpireSolution(false);
            }
            else if (obj is GH_Scribble scr && v["text"] != null)
            {
                scr.Text = (string)v["text"];
                scr.Font = new Font("Arial", v["size"] != null ? (float)v["size"] : 30f);
            }
            else if (obj is GH_BooleanToggle t && v["val"] != null) t.Value = (bool)v["val"];
            else if (obj is GH_ValueList vl)
            {
                if (v["list"] is JArray arr)
                {
                    vl.ListItems.Clear();
                    foreach (var itemTok in arr)
                    {
                        string itm = itemTok.ToString();
                        int colonIdx = itm.IndexOf(':');
                        if (colonIdx > 0) vl.ListItems.Add(new GH_ValueListItem(itm.Substring(0, colonIdx), itm.Substring(colonIdx + 1)));
                        else vl.ListItems.Add(new GH_ValueListItem(itm, itm));
                    }
                }
                if (v["val"] != null) { try { vl.SelectItem((int)v["val"]); } catch { } }
            }
            else if (obj is GH_ColourSwatch swatch && v["val"] != null)
            {
                try
                {
                    Color c = ColorTranslator.FromHtml((string)v["val"]);
                    if (v["a"] != null) c = Color.FromArgb((int)v["a"], c.R, c.G, c.B);
                    swatch.SwatchColour = c;
                }
                catch { }
            }
            else if (obj is IGH_Component exprComp && (exprComp.Name == "Expression" || exprComp.NickName == "Eval") && v["val"] != null)
            {
                try
                {
                    var exprProp = exprComp.GetType().GetProperty("Expression");
                    if (exprProp != null)
                    {
                        exprProp.SetValue(exprComp, (string)v["val"]);
                        exprComp.Params.OnParametersChanged();
                    }
                }
                catch { }
            }
            else if (obj is GH_GraphMapper gm && v["gt"] != null)
            {
                try
                {
                    string typeName = (string)v["gt"];
                    Grasshopper.Kernel.Graphs.IGH_Graph myGraph = null;
                    List<GH_IO.Types.GH_Point2D> defaultNodes = new List<GH_IO.Types.GH_Point2D>();
                    switch (typeName.ToLower())
                    {
                        case "linear":
                            myGraph = new Grasshopper.Kernel.Graphs.GH_LinearGraph();
                            defaultNodes.Add(new GH_IO.Types.GH_Point2D(0.0, 0.0));
                            defaultNodes.Add(new GH_IO.Types.GH_Point2D(1.0, 1.0));
                            break;
                        case "bezier":
                            myGraph = new Grasshopper.Kernel.Graphs.GH_BezierGraph();
                            defaultNodes.Add(new GH_IO.Types.GH_Point2D(0.0, 0.0));
                            defaultNodes.Add(new GH_IO.Types.GH_Point2D(0.3, 0.3));
                            defaultNodes.Add(new GH_IO.Types.GH_Point2D(0.6, 0.6));
                            defaultNodes.Add(new GH_IO.Types.GH_Point2D(1.0, 1.0));
                            break;
                        case "conic":
                            myGraph = new Grasshopper.Kernel.Graphs.GH_ConicGraph();
                            defaultNodes.Add(new GH_IO.Types.GH_Point2D(0.0, 0.0));
                            defaultNodes.Add(new GH_IO.Types.GH_Point2D(0.5, 0.5));
                            defaultNodes.Add(new GH_IO.Types.GH_Point2D(1.0, 1.0));
                            break;
                        case "sine":
                        default:
                            myGraph = new Grasshopper.Kernel.Graphs.GH_SineGraph();
                            break;
                    }
                    if (myGraph != null)
                    {
                        if (defaultNodes.Count > 0)
                        {
                            var archive = new GH_IO.Serialization.GH_Archive();
                            archive.CreateNewRoot(true);
                            GH_IO.Serialization.GH_IWriter root = archive.GetRootNode;
                            myGraph.Write(root);
                            for (int i = 0; i < defaultNodes.Count; i++) root.SetPoint2D("Node", i, defaultNodes[i]);
                            myGraph.Read((GH_IO.Serialization.GH_IReader)root);
                        }
                        var container = new Grasshopper.Kernel.Graphs.GH_GraphContainer(myGraph);
                        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        System.Reflection.FieldInfo containerField = typeof(GH_GraphMapper).GetField("m_container", flags);
                        if (containerField != null) containerField.SetValue(gm, container);
                        System.Reflection.MethodInfo addGraph = typeof(Grasshopper.Kernel.Graphs.GH_GraphContainer).GetMethod("AddGraph", flags);
                        if (addGraph != null) addGraph.Invoke(container, new object[] { myGraph });

                        // Force Grasshopper to hook up internal event listeners by doing a Write/Read cycle.
                        // This mimics the copy/paste behavior which wires the node events to output evaluation.
                        try
                        {
                            var archive = new GH_IO.Serialization.GH_Archive();
                            archive.CreateNewRoot(true);
                            GH_IO.Serialization.GH_IWriter root = archive.GetRootNode;
                            gm.Write(root);
                            gm.Read((GH_IO.Serialization.GH_IReader)root);
                        }
                        catch { }

                        using (Bitmap dummyBmp = new Bitmap(10, 10))
                        using (Graphics g = Graphics.FromImage(dummyBmp))
                        {
                            myGraph.Draw_PreRenderGraph(g, container);
                            myGraph.Draw_PostRenderGraph(g, container);
                        }
                        gm.ExpireSolution(false);
                    }
                }
                catch { }
            }
            else if (obj is IGH_Component scriptComp && (scriptComp.Name.Contains("Script") || scriptComp.ComponentGuid.ToString() == "b6ba1144-02d6-4a2d-b53c-ec62e290eeb7" || scriptComp.ComponentGuid.ToString().Equals("77E486D2-4752-44f2-95DF-CAEFDFE2CD8D", StringComparison.OrdinalIgnoreCase) || scriptComp.ComponentGuid.ToString().Equals("6C185A48-E3CE-45eb-89F1-E644A1439F57", StringComparison.OrdinalIgnoreCase)))
            {
                if (v["code"] != null)
                {
                    string code = (string)v["code"];
                    code = code.Replace("\\n", "\n").Replace("\\\\n", "\n");
                    try
                    {
                        var codeProp = scriptComp.GetType().GetProperty("Code", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                        if (codeProp != null && codeProp.CanWrite)
                        {
                            codeProp.SetValue(scriptComp, code);
                        }
                        else
                        {
                            var sourceCodeProp = scriptComp.GetType().GetProperty("SourceCode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (sourceCodeProp != null)
                            {
                                var sourceCodeObj = sourceCodeProp.GetValue(scriptComp);
                                if (sourceCodeObj != null)
                                {
                                    var innerCodeProp = sourceCodeObj.GetType().GetProperty("Code", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                    if (innerCodeProp == null) innerCodeProp = sourceCodeObj.GetType().GetProperty("ScriptCode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                    if (innerCodeProp != null && innerCodeProp.CanWrite)
                                    {
                                        innerCodeProp.SetValue(sourceCodeObj, code);
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            else if (obj is IGH_Param param && param.SourceCount == 0)
            {
                object rawVal = null;
                try
                {
                    if (v is JObject && v["val"] != null) rawVal = (v["val"] is JValue jv) ? jv.Value : v["val"].ToString();
                    else if (v is JValue jvDirect) rawVal = jvDirect.Value;
                }
                catch { }
                if (rawVal != null) SetPersistentDataGeneric(param, rawVal);
            }
        }

        private static void ApplyParamConfig(List<IGH_Param> paramsList, Dictionary<string, ParamData> config)
        {
            foreach (var kvp in config) if (int.TryParse(kvp.Key, out int idx) && idx < paramsList.Count) ApplySingleParamConfig(paramsList[idx], kvp.Value);
        }

        private static void ApplySingleParamConfig(IGH_Param param, ParamData d)
        {
            if (param == null) return;
            if (!string.IsNullOrEmpty(d.nk)) param.NickName = d.nk;
            if (d.m == "f") param.DataMapping = GH_DataMapping.Flatten;
            else if (d.m == "g") param.DataMapping = GH_DataMapping.Graft;
            param.Reverse = (d.r == true);
            param.Simplify = (d.s == true);
            if (d.wd == "h") param.WireDisplay = GH_ParamWireDisplay.hidden;
            else if (d.wd == "f") param.WireDisplay = GH_ParamWireDisplay.faint;
            var type = param.GetType();
            if (!string.IsNullOrEmpty(d.x))
            {
                var prop = type.GetProperty("Expression");
                prop?.SetValue(param, d.x);
            }
            if (d.p == true)
            {
                var prop = type.GetProperty("Reparameterize");
                prop?.SetValue(param, true);
            }
            if (d.inv == true)
            {
                var prop = type.GetProperty("Invert");
                prop?.SetValue(param, true);
            }
            if (d.val != null && param.SourceCount == 0)
            {
                object rawVal = (d.val is Newtonsoft.Json.Linq.JValue jv) ? jv.Value : d.val;
                SetPersistentDataGeneric(param, rawVal);
            }
        }

        private static IGH_Param ResolveParam(GH_Document ghDoc, Dictionary<string, IGH_DocumentObject> t, string id, int port, bool outP)
        {
            if (t.ContainsKey(id))
            {
                var obj = t[id];
                if (obj is IGH_Component c)
                {
                    var list = outP ? c.Params.Output : c.Params.Input;
                    if (port >= list.Count && c is IGH_VariableParameterComponent v)
                    {
                        int safe = 0;
                        GH_ParameterSide sideToTest = outP ? GH_ParameterSide.Output : GH_ParameterSide.Input;
                        if (v.CanInsertParameter(sideToTest, list.Count))
                        {
                            if (!outP)
                            {
                                while (c.Params.Input.Count <= port && safe++ < 100)
                                {
                                    var newP = v.CreateParameter(GH_ParameterSide.Input, c.Params.Input.Count);
                                    if (newP != null) c.Params.RegisterInputParam(newP);
                                    else break;
                                }
                            }
                            else
                            {
                                while (c.Params.Output.Count <= port && safe++ < 100)
                                {
                                    var newP = v.CreateParameter(GH_ParameterSide.Output, c.Params.Output.Count);
                                    if (newP != null) c.Params.RegisterOutputParam(newP);
                                    else break;
                                }
                            }
                            v.VariableParameterMaintenance();
                            c.Params.OnParametersChanged();
                            list = outP ? c.Params.Output : c.Params.Input;
                        }
                    }
                    return port < list.Count ? list[port] : null;
                }
                return obj as IGH_Param;
            }
            var existingObj = ghDoc.Objects.FirstOrDefault(o => MatchId(o, id));
            if (existingObj != null)
            {
                if (existingObj is IGH_Component comp)
                {
                    var list = outP ? comp.Params.Output : comp.Params.Input;
                    return port < list.Count ? list[port] : null;
                }
                return existingObj as IGH_Param;
            }
            return null;
        }

        private static IGH_DocumentObject CreateObject(Node n, GH_Document doc, PointF pivot)
        {
            if (n.t == "Number Slider") { var s = new GH_NumberSlider(); s.CreateAttributes(); s.Attributes.Pivot = pivot; doc.AddObject(s, false); return s; }
            if (n.t == "Panel") { var p = new GH_Panel(); p.CreateAttributes(); p.Attributes.Pivot = pivot; doc.AddObject(p, false); return p; }
            if (n.t == "Boolean Toggle") { var b = new GH_BooleanToggle(); b.CreateAttributes(); b.Attributes.Pivot = pivot; doc.AddObject(b, false); return b; }
            
            var proxies = Instances.ComponentServer.ObjectProxies;
            IGH_ObjectProxy proxy = null;

            IGH_ObjectProxy FindProxy(string nameToFind, string catToFind, string subCatToFind)
            {
                string safeName = nameToFind?.Trim() ?? "";
                
                // First try exact match
                var matches = proxies.Where(p => (p.Desc.Name?.Trim() ?? "").Equals(safeName, StringComparison.OrdinalIgnoreCase)).ToList();
                
                // If no exact match, try partial match (Contains)
                if (!matches.Any())
                {
                    matches = proxies.Where(p => (p.Desc.Name?.Trim() ?? "").IndexOf(safeName, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                }

                if (!matches.Any()) return null;

                // Prioritize active components, but allow obsolete if they are the only ones available
                var activeMatches = matches.Where(p => !p.Obsolete).ToList();
                var searchList = activeMatches.Any() ? activeMatches : matches;

                if (!string.IsNullOrEmpty(catToFind) && !string.IsNullOrEmpty(subCatToFind))
                {
                    var exact = searchList.Where(p => p.Desc.Category.Equals(catToFind, StringComparison.OrdinalIgnoreCase) && p.Desc.SubCategory.Equals(subCatToFind, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (exact.Any()) return exact.FirstOrDefault();
                }

                if (!string.IsNullOrEmpty(catToFind))
                {
                    var catMatch = searchList.Where(p => p.Desc.Category.Equals(catToFind, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (catMatch.Any()) return catMatch.FirstOrDefault();
                }

                return searchList.FirstOrDefault();
            }

            if (!string.IsNullOrEmpty(n.guid) && Guid.TryParse(n.guid, out Guid componentGuid))
            {
                proxy = proxies.FirstOrDefault(p => p.Guid == componentGuid);
            }

            if (proxy == null)
            {
                proxy = FindProxy(n.t, n.c, n.sc);
            }
            if (proxy == null)
            {
                RhinoApp.WriteLine($"[nextra.gh] WARNING: Could not find component '{n.t}' in the Grasshopper Component Server.");
                return null;
            }

            if (proxy != null)
            {
                var canvas = Grasshopper.Instances.ActiveCanvas;
                if (canvas != null)
                {
                    int startCount = doc.Objects.Count;
                    bool success = canvas.InstantiateNewObject(proxy.Guid, pivot, false);
                    if (success && doc.Objects.Count > startCount)
                    {
                        var newComp = doc.Objects[doc.Objects.Count - 1];
                        return newComp;
                    }
                }
                
                // Headless Fallback
                var fallbackComp = proxy.CreateInstance() as IGH_DocumentObject;
                if (fallbackComp != null)
                {
                    fallbackComp.CreateAttributes();
                    fallbackComp.Attributes.Pivot = pivot;
                    doc.AddObject(fallbackComp, false);
                    return fallbackComp;
                }
            }

            RhinoApp.WriteLine($"[nextra.gh] WARNING: Could not find component '{n.t}' in the Grasshopper Component Server.");
            return null;
        }

        private static void HandleState(IGH_DocumentObject obj, StateData s)
        {
            if (s == null) return;
            if (s.h.HasValue && obj is IGH_PreviewObject po) po.Hidden = s.h.Value;
            if (s.l.HasValue && obj is GH_ActiveObject aco) aco.Locked = s.l.Value;
        }

        private static void HandleZUI(IGH_Component comp, ZUIData z)
        {
            if (comp is IGH_VariableParameterComponent v)
            {
                bool changed = false;
                int safe;

                // Handle inputs
                if (z.i > 0)
                {
                    // Grow inputs
                    safe = 0;
                    while (comp.Params.Input.Count < z.i && safe++ < 100)
                    {
                        int idx = comp.Params.Input.Count;
                        if (!v.CanInsertParameter(GH_ParameterSide.Input, idx)) break;
                        var newParam = v.CreateParameter(GH_ParameterSide.Input, idx);
                        if (newParam != null) { comp.Params.RegisterInputParam(newParam); changed = true; }
                        else break;
                    }
                    // Shrink inputs
                    safe = 0;
                    while (comp.Params.Input.Count > z.i && safe++ < 100)
                    {
                        int last = comp.Params.Input.Count - 1;
                        if (!v.CanRemoveParameter(GH_ParameterSide.Input, last)) break;
                        v.DestroyParameter(GH_ParameterSide.Input, last);
                        comp.Params.UnregisterInputParameter(comp.Params.Input[last], true);
                        changed = true;
                    }
                }

                // Handle outputs
                if (z.o > 0)
                {
                    // Grow outputs
                    safe = 0;
                    while (comp.Params.Output.Count < z.o && safe++ < 100)
                    {
                        int idx = comp.Params.Output.Count;
                        if (!v.CanInsertParameter(GH_ParameterSide.Output, idx)) break;
                        var newParam = v.CreateParameter(GH_ParameterSide.Output, idx);
                        if (newParam != null) { comp.Params.RegisterOutputParam(newParam); changed = true; }
                        else break;
                    }
                    // Shrink outputs
                    safe = 0;
                    while (comp.Params.Output.Count > z.o && safe++ < 100)
                    {
                        int last = comp.Params.Output.Count - 1;
                        if (!v.CanRemoveParameter(GH_ParameterSide.Output, last)) break;
                        v.DestroyParameter(GH_ParameterSide.Output, last);
                        comp.Params.UnregisterOutputParameter(comp.Params.Output[last], true);
                        changed = true;
                    }
                }

                if (changed) { v.VariableParameterMaintenance(); comp.Params.OnParametersChanged(); }
            }
        }

        private static void AlignScribblesToGroupTopLeft(List<GH_Scribble> scribbles, List<Node> allJsonNodes, Dictionary<string, IGH_DocumentObject> tracker, float basePadding)
        {
            var nodeToGroup = new Dictionary<string, string>();
            var groupMembers = new Dictionary<string, List<string>>();
            foreach (var n in allJsonNodes.Where(x => x.t == "Group"))
            {
                if (n.v != null && n.v["ids"] is JArray ids)
                {
                    var list = new List<string>();
                    foreach (var idTok in ids)
                    {
                        string id = idTok.ToString();
                        nodeToGroup[id] = n.id;
                        list.Add(id);
                    }
                    groupMembers[n.id] = list;
                }
            }
            foreach (var scr in scribbles)
            {
                string scrId = tracker.FirstOrDefault(x => x.Value == scr).Key;
                if (scrId == null) continue;
                string targetGroupId = null;
                if (nodeToGroup.ContainsKey(scrId)) targetGroupId = nodeToGroup[scrId];
                else
                {
                    double minDist = double.MaxValue;
                    foreach (var kvp in tracker)
                    {
                        if (kvp.Value is IGH_Component comp)
                        {
                            float dx = scr.Attributes.Pivot.X - comp.Attributes.Pivot.X;
                            float dy = scr.Attributes.Pivot.Y - comp.Attributes.Pivot.Y;
                            double dist = dx * dx + dy * dy;
                            if (dist < minDist)
                            {
                                minDist = dist;
                                if (nodeToGroup.ContainsKey(kvp.Key)) targetGroupId = nodeToGroup[kvp.Key];
                                else targetGroupId = null;
                            }
                        }
                    }
                }
                if (targetGroupId != null && groupMembers.ContainsKey(targetGroupId))
                {
                    float minX = float.MaxValue;
                    float topY = float.MaxValue;
                    bool foundComponents = false;
                    foreach (var memId in groupMembers[targetGroupId])
                    {
                        if (memId == scrId) continue;
                        if (tracker.TryGetValue(memId, out var obj))
                        {
                            if (obj.Attributes.Bounds.IsEmpty)
                            {
                                obj.Attributes.ExpireLayout();
                                obj.Attributes.PerformLayout();
                            }
                            if (obj.Attributes.Pivot.X < minX) minX = obj.Attributes.Pivot.X;
                            if (obj.Attributes.Bounds.Top < topY) topY = obj.Attributes.Bounds.Top;
                            foundComponents = true;
                        }
                    }
                    if (foundComponents)
                    {
                        scr.Attributes.ExpireLayout();
                        scr.Attributes.PerformLayout();
                        float scribbleHeight = scr.Attributes.Bounds.Height;
                        if (scribbleHeight < 1) scribbleHeight = scr.Font.Size * 1.5f;
                        float totalVerticalOffset = scribbleHeight + basePadding + (scr.Font.Size * 0.3f);
                        scr.Attributes.Pivot = new PointF(minX, topY - totalVerticalOffset);
                        scr.Attributes.ExpireLayout();
                        scr.Attributes.PerformLayout();
                    }
                }
            }
        }

        private static void ApplyRecursiveLayout(Dictionary<string, IGH_DocumentObject> tracker, NextraResponse response)
        {
            float startX = 0f;
            float startY = 0f;
            var groupNodes = response.nodes.Where(n => n.t == "Group").ToList();
            if (groupNodes.Count == 0)
            {
                var allIds = response.nodes.Where(n => n.t != "Group" && n.t != "Scribble").Select(n => n.id).ToList();
                if (allIds.Count > 0)
                {
                    var ig = new Node { id = "root", t = "Group", v = new JObject() };
                    ig.v["ids"] = JArray.FromObject(allIds);
                    groupNodes.Add(ig);
                }
            }
            var memberToGroup = new Dictionary<string, string>();
            foreach (var gn in groupNodes) if (gn.v != null && gn.v["ids"] is JArray ids) foreach (var id in ids) memberToGroup[id.ToString()] = gn.id;
            var groupBounds = new Dictionary<string, RectangleF>();
            var localOffsets = new Dictionary<string, PointF>();

            foreach (var gn in groupNodes)
            {
                var memberIds = memberToGroup.Where(x => x.Value == gn.id).Select(x => x.Key).ToList();
                if (!memberIds.Any()) continue;
                var items = new List<LayoutItem>();
                foreach (var mid in memberIds) if (tracker.TryGetValue(mid, out var obj)) items.Add(new LayoutItem(mid, obj));
                var internalWires = response.wires.Where(w => memberIds.Contains(w.from) && memberIds.Contains(w.to)).ToList();
                var cleanInternalWires = BreakCycles(memberIds, internalWires);
                RunGripLogic(items, cleanInternalWires, 0, 0, tracker);
                RectangleF bounds = RectangleF.Empty;
                bool first = true;
                foreach (var item in items)
                {
                    var obj = tracker[item.Id];
                    obj.Attributes.Pivot = item.Pivot;
                    obj.Attributes.ExpireLayout();
                    obj.Attributes.PerformLayout();
                    localOffsets[item.Id] = item.Pivot;
                    if (first) { bounds = obj.Attributes.Bounds; first = false; }
                    else bounds = RectangleF.Union(bounds, obj.Attributes.Bounds);
                }
                groupBounds[gn.id] = bounds;
            }

            var macroItems = new List<LayoutItem>();
            foreach (var gn in groupNodes) if (groupBounds.ContainsKey(gn.id)) macroItems.Add(new LayoutItem(gn.id, groupBounds[gn.id].Width, groupBounds[gn.id].Height));
            var macroWires = new List<Wire>();
            foreach (var w in response.wires) if (memberToGroup.TryGetValue(w.from, out string gFrom) && memberToGroup.TryGetValue(w.to, out string gTo)) if (gFrom != gTo) macroWires.Add(new Wire { from = gFrom, to = gTo });
            var macroNodeIds = groupNodes.Select(gn => gn.id).ToList();
            var cleanMacroWires = BreakCycles(macroNodeIds, macroWires);
            RunGripLogic(macroItems, cleanMacroWires, startX, startY, null);

            foreach (var gItem in macroItems)
            {
                var gPos = gItem.Pivot;
                var gB = groupBounds[gItem.Id];
                float globX = gPos.X - gB.X;
                float globY = gPos.Y - gB.Y;
                var members = memberToGroup.Where(x => x.Value == gItem.Id).Select(x => x.Key).ToList();
                foreach (var mid in members)
                {
                    if (tracker.TryGetValue(mid, out var obj) && localOffsets.TryGetValue(mid, out PointF loc))
                    {
                        obj.Attributes.Pivot = new PointF(loc.X + globX, loc.Y + globY);
                        obj.Attributes.ExpireLayout();
                        obj.Attributes.PerformLayout();
                    }
                }
            }
        }

        private static void RunGripLogic(List<LayoutItem> items, List<Wire> wires, float startX, float startY, Dictionary<string, IGH_DocumentObject> tracker)
        {
            if (items.Count == 0) return;
            bool isMacro = (tracker == null); float yGap = isMacro ? 150f : 20f; float xGap = isMacro ? 120f : 150f;
            
            // 🚀 Split Standard Items and Scribbles
            var stdItems = items.Where(i => !i.IsScribble).ToList();
            var scrItems = items.Where(i => i.IsScribble).ToList();
            
            // Failsafe: if the group only contains scribbles
            if (stdItems.Count == 0) { stdItems = scrItems; scrItems = new List<LayoutItem>(); }

            // 🚀 Phase 1: Bind Scribbles to closest component (before any movement)
            var scrToStd = new Dictionary<string, string>();
            foreach(var scr in scrItems) {
                string closestId = null; double minDist = double.MaxValue;
                foreach(var std in stdItems) {
                    double dist = Math.Pow(scr.Pivot.X - std.Pivot.X, 2) + Math.Pow(scr.Pivot.Y - std.Pivot.Y, 2);
                    if(dist < minDist) { minDist = dist; closestId = std.Id; }
                }
                if (closestId != null) scrToStd[scr.Id] = closestId;
            }

            var ranks = new Dictionary<string, int>(); foreach (var item in stdItems) ranks[item.Id] = 0;
            
            for (int i = 0; i < stdItems.Count + 5; i++) { 
                foreach (var w in wires) { 
                    if (ranks.ContainsKey(w.from) && ranks.ContainsKey(w.to)) { 
                        if (ranks[w.from] <= ranks[w.to] && ranks[w.from] < 100) ranks[w.from] = ranks[w.to] + 1; 
                    } 
                } 
            }
            
            int tempMax = ranks.Count > 0 ? ranks.Values.Max() : 0;
            int tempMin = ranks.Count > 0 ? ranks.Values.Min() : 0;

            foreach (var item in stdItems) {
                if (tracker != null && tracker.ContainsKey(item.Id) && tracker[item.Id] is IGH_Param param && !(tracker[item.Id] is IGH_Component)) {
                    if (param.NickName.EndsWith("_In")) ranks[item.Id] = tempMax + 1; 
                    else if (param.NickName.EndsWith("_Out")) ranks[item.Id] = tempMin - 1; 
                }
            }

            int maxRank = ranks.Count > 0 ? ranks.Values.Max() : 0;
            int minRank = ranks.Count > 0 ? ranks.Values.Min() : 0;
            
            var rankX = new Dictionary<int, float>(); float currentX = startX;
            
            for (int r = maxRank; r >= minRank; r--) { 
                var cNodes = stdItems.Where(n => ranks[n.Id] == r).ToList(); 
                if (cNodes.Count == 0) continue;
                
                rankX[r] = currentX; float maxW = 0; 
                foreach(var it in cNodes) { float w = isMacro ? it.Width : tracker[it.Id].Attributes.Bounds.Width; if(w > maxW) maxW = w; } 
                currentX += (maxW + xGap); 
            }
            
            for (int r = minRank; r <= maxRank; r++) {
                var colNodes = stdItems.Where(n => ranks[n.Id] == r).ToList(); if (colNodes.Count == 0) continue;
                float myX = rankX[r]; 
                foreach (var item in colNodes) {
                    if (!isMacro) { var obj = tracker[item.Id]; obj.Attributes.ExpireLayout(); obj.Attributes.PerformLayout(); }
                    float idealY = isMacro ? item.Pivot.Y : tracker[item.Id].Attributes.Pivot.Y;
                    var outWires = wires.Where(w => w.from == item.Id).OrderBy(w => w.fromPort).ToList();
                    if (outWires.Count > 0) {
                        var wire = outWires[0]; float tY = 0;
                        if (isMacro) { var tgtItem = items.FirstOrDefault(x => x.Id == wire.to); if (tgtItem != null) { tY = tgtItem.Pivot.Y; } } 
                        else {
                            var tgtObj = tracker[wire.to]; tY = tgtObj.Attributes.Pivot.Y;
                            if (tgtObj is IGH_Component tc && wire.toPort < tc.Params.Input.Count) { var p = tc.Params.Input[wire.toPort]; if (p.Attributes != null) tY = p.Attributes.InputGrip.Y; }
                            else if (tgtObj is GH_NumberSlider s) { tY = s.Attributes.Bounds.Top + (s.Attributes.Bounds.Height * 0.5f); }
                        }
                        float myOff = 0;
                        if (!isMacro) {
                            if (tracker[item.Id] is IGH_Component sc && wire.fromPort < sc.Params.Output.Count) { var p = sc.Params.Output[wire.fromPort]; if (p.Attributes != null) myOff = p.Attributes.OutputGrip.Y - sc.Attributes.Pivot.Y; }
                            else if (tracker[item.Id] is GH_NumberSlider) { myOff = tracker[item.Id].Attributes.Bounds.Height * 0.5f; }
                        } else { myOff = item.Height * 0.5f; }
                        idealY = tY - myOff;
                    }
                    item.IdealY = idealY;
                    if (isMacro) item.Pivot = new PointF(myX, idealY); else { var obj = tracker[item.Id]; obj.Attributes.Pivot = new PointF(myX, idealY); obj.Attributes.ExpireLayout(); obj.Attributes.PerformLayout(); }
                }
                colNodes.Sort((a, b) => a.IdealY.CompareTo(b.IdealY));
                bool collisionFound = true; int iterations = 0; int maxIter = colNodes.Count * colNodes.Count + 5; 
                while (collisionFound && iterations < maxIter) {
                    collisionFound = false; iterations++;
                    colNodes.Sort((a, b) => (isMacro ? a.Pivot.Y : tracker[a.Id].Attributes.Bounds.Top).CompareTo(isMacro ? b.Pivot.Y : tracker[b.Id].Attributes.Bounds.Top));
                    for (int i = 0; i < colNodes.Count - 1; i++) {
                        var topItem = colNodes[i]; var botItem = colNodes[i+1];
                        RectangleF bTop = isMacro ? new RectangleF(topItem.Pivot.X, topItem.Pivot.Y, topItem.Width, topItem.Height) : tracker[topItem.Id].Attributes.Bounds;
                        RectangleF bBot = isMacro ? new RectangleF(botItem.Pivot.X, botItem.Pivot.Y, botItem.Width, botItem.Height) : tracker[botItem.Id].Attributes.Bounds;
                        if (bBot.Top < bTop.Bottom + yGap - 0.1f) {
                            float shift = (bTop.Bottom + yGap) - bBot.Top;
                            if (isMacro) botItem.Pivot = new PointF(myX, botItem.Pivot.Y + shift);
                            else { var obj = tracker[botItem.Id]; obj.Attributes.Pivot = new PointF(myX, obj.Attributes.Pivot.Y + shift); obj.Attributes.ExpireLayout(); obj.Attributes.PerformLayout(); }
                            collisionFound = true; break; 
                        }
                    }
                }
            }

            // 🚀 Phase 2: Stack Scribbles exactly on top of their bound Column Rank
            var rankToScribbles = new Dictionary<int, List<LayoutItem>>();
            foreach(var scr in scrItems) {
                if (scrToStd.TryGetValue(scr.Id, out string stdId) && ranks.ContainsKey(stdId)) {
                    int r = ranks[stdId];
                    if (!rankToScribbles.ContainsKey(r)) rankToScribbles[r] = new List<LayoutItem>();
                    rankToScribbles[r].Add(scr);
                }
            }

            foreach(var kvp in rankToScribbles) {
                int r = kvp.Key;
                // Sort scribbles by their original vertical position (descending so the lowest one goes first)
                var rankScribbles = kvp.Value.OrderByDescending(s => s.Pivot.Y).ToList();
                
                var colStdItems = stdItems.Where(n => ranks[n.Id] == r).ToList();
                float currentTopY = startY;
                if (colStdItems.Count > 0) {
                    currentTopY = isMacro ? colStdItems.Min(n => n.Pivot.Y) : colStdItems.Min(n => tracker[n.Id].Attributes.Bounds.Top);
                }
                float rX = rankX.ContainsKey(r) ? rankX[r] : startX;

                foreach(var scr in rankScribbles) {
                    float sHeight = scr.Height;
                    if (!isMacro) {
                        var scrObj = tracker[scr.Id];
                        scrObj.Attributes.ExpireLayout(); scrObj.Attributes.PerformLayout();
                        sHeight = scrObj.Attributes.Bounds.Height;
                        if (sHeight < 1) sHeight = 30f;
                    }
                    
                    float padding = 15f; // Visual buffer between text and component
                    currentTopY -= (sHeight + padding); // Push the cursor upwards
                    
                    scr.Pivot = new PointF(rX, currentTopY);
                    if (!isMacro) {
                        var scrObj = tracker[scr.Id];
                        scrObj.Attributes.Pivot = scr.Pivot;
                        scrObj.Attributes.ExpireLayout(); 
                        scrObj.Attributes.PerformLayout();
                    }
                }
            }

            if (!isMacro) { foreach(var item in items) item.Pivot = tracker[item.Id].Attributes.Pivot; }
        }

        private static void ApplyFinalPlacement(GH_Document ghDoc, Dictionary<string, IGH_DocumentObject> tracker, NextraResponse response, List<Guid> spawnedGuids, bool useFallbackLocal, PointF? originalAnchor)
        {
            RectangleF newBounds = RectangleF.Empty;
            bool first = true;
            foreach (var obj in tracker.Values)
            {
                if (obj.Attributes == null || !spawnedGuids.Contains(obj.InstanceGuid)) continue;
                obj.Attributes.ExpireLayout();
                obj.Attributes.PerformLayout();
                if (first) { newBounds = obj.Attributes.Bounds; first = false; }
                else { newBounds = RectangleF.Union(newBounds, obj.Attributes.Bounds); }
            }
            if (newBounds.IsEmpty) return;

            // 1. TOPOLOGICAL ANCHORING: Find existing components to anchor to
            IGH_DocumentObject anchorObj = null;
            bool anchorIsUpstream = true;

            foreach (var w in response.wires)
            {
                // Anchor to an existing upstream component
                if (!tracker.ContainsKey(w.from) && tracker.ContainsKey(w.to))
                {
                    anchorObj = ghDoc.Objects.FirstOrDefault(o => MatchId(o, w.from));
                    if (anchorObj != null) { anchorIsUpstream = true; break; }
                }
                // Anchor to an existing downstream component
                if (tracker.ContainsKey(w.from) && !tracker.ContainsKey(w.to))
                {
                    anchorObj = ghDoc.Objects.FirstOrDefault(o => MatchId(o, w.to));
                    if (anchorObj != null) { anchorIsUpstream = false; break; }
                }
            }

            float targetX = 0f;
            float targetY = 0f;

            if (anchorObj != null && anchorObj.Attributes != null)
            {
                if (anchorIsUpstream)
                {
                    targetX = anchorObj.Attributes.Bounds.Right + 150f;
                    targetY = anchorObj.Attributes.Pivot.Y;
                }
                else
                {
                    targetX = anchorObj.Attributes.Bounds.Left - newBounds.Width - 150f;
                    targetY = anchorObj.Attributes.Pivot.Y;
                }
            }
            else if (originalAnchor.HasValue)
            {
                targetX = originalAnchor.Value.X;
                targetY = originalAnchor.Value.Y;
            }
            else
            {
                // Fallback: Near the "launcher" or center of current view
                IGH_DocumentObject launcher = ghDoc.Objects.FirstOrDefault(o => o.Name == "nextra.gh");
                if (launcher != null && launcher.Attributes != null)
                {
                    targetX = launcher.Attributes.Pivot.X + 250f;
                    targetY = launcher.Attributes.Pivot.Y;
                }
                else if (Instances.ActiveCanvas != null)
                {
                    // Center of the current screen view
                    targetX = Instances.ActiveCanvas.Viewport.Target.X;
                    targetY = Instances.ActiveCanvas.Viewport.Target.Y;
                }
                else
                {
                    targetX = 100f;
                    targetY = 100f;
                }
            }

            // 1.5 Enforce Positive Coordinates FIRST
            if (targetX < 0) targetX = 0f;
            if (targetY < 0) targetY = 0f;

            // 2. OBSTACLE AVOIDANCE: Collect all existing bounds (excluding session/selected components)
            List<RectangleF> obstacles = new List<RectangleF>();
            foreach (var docObj in ghDoc.Objects)
            {
                // If it's not part of the current definition being placed, it's an obstacle
                if (!tracker.ContainsValue(docObj) && !spawnedGuids.Contains(docObj.InstanceGuid))
                {
                    if (docObj.Attributes == null) continue;

                    var b = docObj.Attributes.Bounds;

                    // Explicitly handle GH_Group to ensure we get the full group area even if it's not currently refreshed
                    if (docObj is GH_Group grp && b.IsEmpty)
                    {
                        grp.Attributes.ExpireLayout();
                        grp.Attributes.PerformLayout();
                        b = grp.Attributes.Bounds;
                    }

                    if (!b.IsEmpty)
                    {
                        b.Inflate(40f, 40f); // Add clearance
                        obstacles.Add(b);
                    }
                }
            }

            // 3. COLLISION NUDGE LOOP
            RectangleF testBounds = new RectangleF(targetX, targetY, newBounds.Width, newBounds.Height);
            bool collision = true;
            int safety = 0;

            while (collision && safety < 500)
            {
                collision = false;
                foreach (var obs in obstacles)
                {
                    if (testBounds.IntersectsWith(obs))
                    {
                        collision = true;
                        // Nudge downwards for now (simplest approach)
                        testBounds.Y = obs.Bottom + 50f;
                        break;
                    }
                }
                safety++;
            }

            // 4. APPLY FINAL SHIFT
            float shiftX = testBounds.X - newBounds.Left;
            float shiftY = testBounds.Y - newBounds.Top;
            foreach (var obj in tracker.Values)
            {
                if (spawnedGuids.Contains(obj.InstanceGuid))
                {
                    var old = obj.Attributes.Pivot;
                    obj.Attributes.Pivot = new PointF(old.X + shiftX, old.Y + shiftY);
                    obj.Attributes.ExpireLayout();
                }
            }
        }

        private static bool MatchId(IGH_DocumentObject o, string id)
        {
            if (Guid.TryParse(id, out Guid gid))
            {
                return o.InstanceGuid == gid;
            }
            if (id.Contains("_"))
            {
                var parts = id.Split('_');
                if (parts.Length > 1)
                {
                    if (Guid.TryParse(parts[1], out Guid pGid))
                    {
                        return o.InstanceGuid == pGid;
                    }
                    if (parts[1].Length >= 4)
                    {
                        string shortGuid = parts[1].ToLower();
                        string objGuid = o.InstanceGuid.ToString().Replace("-", "").ToLower();
                        if (objGuid.StartsWith(shortGuid) || objGuid.EndsWith(shortGuid))
                        {
                            return true;
                        }
                    }
                }
            }
            string cleanName = new string(o.NickName.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrEmpty(cleanName)) cleanName = "obj";
            return (cleanName + "_" + o.InstanceGuid.ToString().Substring(0, 4)).Equals(id, StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteNodes(GH_Document ghDoc, List<Guid> idsToDelete)
        {
            var objs = ghDoc.Objects.Where(o => idsToDelete.Contains(o.InstanceGuid)).ToList();
            foreach (var obj in objs) ghDoc.RemoveObject(obj, false);
        }

        public class NextraResponse { public List<ContextNode> ctx; public List<Node> nodes; public List<Wire> wires; }
        public class ContextNode { public string id, guid, nk, t, c; }
        public class Node { public string id, t, c, sc, guid, st; public float[] p; public StateData s; public ZUIData z; public Dictionary<string, ParamData> i; public Dictionary<string, ParamData> o; public JToken v; }
        public class StateData { [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public bool? e, h, l; }
        public class ZUIData { public int i, o; }
        public class ParamData { [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string m, nk, wd, x; [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public bool? r, s, p, inv; [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public object val; }
        public class Wire { public string from, to; public int fromPort, toPort; public string state; }
        public class BuildResult
        {
            public List<Guid> SpawnedGuids = new List<Guid>();
            public Dictionary<Guid, string> GuidToIdMap = new Dictionary<Guid, string>();
            public List<object> BuildErrors = new List<object>();
            public Dictionary<string, IGH_DocumentObject> Tracker = new Dictionary<string, IGH_DocumentObject>();
        }
        public class LayoutItem
        {
            public string Id;
            public int Rank;
            public float Width, Height, IdealY;
            public PointF Pivot;
            public bool IsInput = false;
            public bool IsOutput = false;
            public bool IsScribble = false;
            public LayoutItem(string id, IGH_DocumentObject obj)
            {
                Id = id;
                IsScribble = (obj is GH_Scribble);
                if (obj.Attributes.Bounds.Width < 1)
                {
                    obj.Attributes.ExpireLayout();
                    obj.Attributes.PerformLayout();
                }
                Width = obj.Attributes.Bounds.Width;
                Height = obj.Attributes.Bounds.Height;
                Pivot = obj.Attributes.Pivot;
            }
            public LayoutItem(string id, float w, float h)
            {
                Id = id;
                Width = w;
                Height = h;
            }
        }
        private class MacroNode
        {
            public string Id;
            public bool IsGroup;
            public GH_Group GroupObject;
            public IGH_DocumentObject SingleObject;
            public List<string> MemberIds = new List<string>();
            public float Width;
            public float Height;
            public RectangleF LocalBounds;
        }

        private static List<Wire> BreakCycles(List<string> nodeIds, List<Wire> wires)
        {
            var adj = new Dictionary<string, List<Wire>>();
            foreach (var id in nodeIds) adj[id] = new List<Wire>();
            foreach (var w in wires)
            {
                if (adj.ContainsKey(w.from) && adj.ContainsKey(w.to))
                {
                    adj[w.from].Add(w);
                }
            }

            var visited = new HashSet<string>();
            var visiting = new HashSet<string>();
            var backEdges = new HashSet<Wire>();

            Action<string> dfs = null;
            dfs = (u) =>
            {
                visiting.Add(u);
                foreach (var w in adj[u])
                {
                    string v = w.to;
                    if (visiting.Contains(v))
                    {
                        backEdges.Add(w);
                    }
                    else if (!visited.Contains(v))
                    {
                        dfs(v);
                    }
                }
                visiting.Remove(u);
                visited.Add(u);
            };

            foreach (var id in nodeIds)
            {
                if (!visited.Contains(id))
                {
                    dfs(id);
                }
            }

            var cleanWires = new List<Wire>();
            foreach (var w in wires)
            {
                if (!backEdges.Contains(w))
                {
                    cleanWires.Add(w);
                }
            }
            return cleanWires;
        }

        private static void LayoutAllCanvasObjects(GH_Document doc, List<string> targetGuids = null)
        {
            if (doc == null || doc.ObjectCount == 0) return;

            // 1. Collect all objects and find groups
            var allObjectsQuery = doc.Objects.OfType<IGH_DocumentObject>()
                .Where(obj => obj.Attributes != null && !(obj is GH_Group));

            if (targetGuids != null && targetGuids.Count > 0)
            {
                var targetGuidSet = new HashSet<string>(targetGuids.Select(g => g.ToLower()));
                allObjectsQuery = allObjectsQuery.Where(o => targetGuidSet.Contains(o.InstanceGuid.ToString().ToLower()));
            }

            var allObjects = allObjectsQuery.ToList();
            if (allObjects.Count == 0) return;

            // --- AUTO-REGROUP LOGIC REMOVED ---
            // Individual components will now be naturally treated as their own MacroNode.
            // This prevents independent, un-grouped components from being clustered into a single giant "Copilot Stage" group.

            float originalMinX = allObjects.Min(o => o.Attributes.Pivot.X);
            float originalMinY = allObjects.Min(o => o.Attributes.Pivot.Y);

            var groups = doc.Objects.OfType<GH_Group>().ToList();

            // 2. Identify MacroNodes
            var macroNodes = new List<MacroNode>();
            var componentToMacroNode = new Dictionary<Guid, MacroNode>();

            // Handle real groups
            foreach (var grp in groups)
            {
                var members = allObjects.Where(o => grp.ObjectIDs.Contains(o.InstanceGuid)).ToList();
                if (members.Count == 0) continue;

                var mn = new MacroNode
                {
                    Id = grp.InstanceGuid.ToString(),
                    IsGroup = true,
                    GroupObject = grp
                };
                foreach (var m in members)
                {
                    mn.MemberIds.Add(m.InstanceGuid.ToString());
                    componentToMacroNode[m.InstanceGuid] = mn;
                }
                macroNodes.Add(mn);
            }

            // Handle individual components (not in any group)
            foreach (var obj in allObjects)
            {
                if (!componentToMacroNode.ContainsKey(obj.InstanceGuid))
                {
                    var mn = new MacroNode
                    {
                        Id = obj.InstanceGuid.ToString(),
                        IsGroup = false,
                        SingleObject = obj
                    };
                    mn.MemberIds.Add(obj.InstanceGuid.ToString());
                    componentToMacroNode[obj.InstanceGuid] = mn;
                    macroNodes.Add(mn);
                }
            }

            if (macroNodes.Count == 0) return;

            // 3. Collect all wires/connections
            var wires = new List<Wire>();
            foreach (var obj in allObjects)
            {
                var comp = obj as IGH_Component;
                var param = obj as IGH_Param;
                if (comp != null)
                {
                    for (int i = 0; i < comp.Params.Input.Count; i++)
                    {
                        var ip = comp.Params.Input[i];
                        foreach (var src in ip.Sources)
                        {
                            if (src != null && src.Attributes != null)
                            {
                                var srcObj = src.Attributes.GetTopLevel.DocObject;
                                if (srcObj != null)
                                {
                                    int outIdx = 0;
                                    var srcComp = srcObj as IGH_Component;
                                    if (srcComp != null)
                                    {
                                        outIdx = srcComp.Params.Output.IndexOf(src);
                                        if (outIdx < 0) outIdx = 0;
                                    }
                                    wires.Add(new Wire
                                    {
                                        from = srcObj.InstanceGuid.ToString(),
                                        to = obj.InstanceGuid.ToString(),
                                        fromPort = outIdx,
                                        toPort = i
                                    });
                                }
                            }
                        }
                    }
                }
                else if (param != null)
                {
                    foreach (var src in param.Sources)
                    {
                        if (src != null && src.Attributes != null)
                        {
                            var srcObj = src.Attributes.GetTopLevel.DocObject;
                            if (srcObj != null)
                            {
                                int outIdx = 0;
                                var srcComp = srcObj as IGH_Component;
                                if (srcComp != null)
                                {
                                    outIdx = srcComp.Params.Output.IndexOf(src);
                                    if (outIdx < 0) outIdx = 0;
                                }
                                wires.Add(new Wire
                                {
                                    from = srcObj.InstanceGuid.ToString(),
                                    to = obj.InstanceGuid.ToString(),
                                    fromPort = outIdx,
                                    toPort = 0
                                });
                            }
                        }
                    }
                }
            }

            // 4. Run Micro Layout for each Group MacroNode
            var localOffsets = new Dictionary<string, PointF>();
            var groupBounds = new Dictionary<string, RectangleF>();

            foreach (var mn in macroNodes.Where(n => n.IsGroup))
            {
                var tracker = new Dictionary<string, IGH_DocumentObject>();
                var items = new List<LayoutItem>();
                foreach (var mid in mn.MemberIds)
                {
                    if (Guid.TryParse(mid, out Guid guid))
                    {
                        var obj = doc.FindObject(guid, false);
                        if (obj != null)
                        {
                            tracker[mid] = obj;
                            items.Add(new LayoutItem(mid, obj));
                        }
                    }
                }

                if (items.Count == 0) continue;

                // Extract internal wires for this group
                var internalWires = wires.Where(w => mn.MemberIds.Contains(w.from) && mn.MemberIds.Contains(w.to)).ToList();

                // Break internal cycles!
                var cleanInternalWires = BreakCycles(mn.MemberIds, internalWires);

                // Run layout within the group
                RunGripLogic(items, cleanInternalWires, 0, 0, tracker);

                // Calculate bounding box and cache local offsets
                RectangleF bounds = RectangleF.Empty;
                bool first = true;
                foreach (var item in items)
                {
                    var obj = tracker[item.Id];
                    obj.Attributes.Pivot = item.Pivot;
                    obj.Attributes.ExpireLayout();
                    obj.Attributes.PerformLayout();
                    localOffsets[item.Id] = item.Pivot;

                    if (first) { bounds = obj.Attributes.Bounds; first = false; }
                    else bounds = RectangleF.Union(bounds, obj.Attributes.Bounds);
                }

                mn.Width = bounds.Width;
                mn.Height = bounds.Height;
                mn.LocalBounds = bounds;
                groupBounds[mn.Id] = bounds;
            }

            // For individual (non-group) MacroNodes, set their bounds directly
            foreach (var mn in macroNodes.Where(n => !n.IsGroup))
            {
                var obj = mn.SingleObject;
                if (obj.Attributes.Bounds.Width < 1)
                {
                    obj.Attributes.ExpireLayout();
                    obj.Attributes.PerformLayout();
                }
                mn.Width = obj.Attributes.Bounds.Width;
                mn.Height = obj.Attributes.Bounds.Height;
                mn.LocalBounds = obj.Attributes.Bounds;
            }

            // 5. Run Macro Layout
            var macroItems = new List<LayoutItem>();
            foreach (var mn in macroNodes)
            {
                macroItems.Add(new LayoutItem(mn.Id, mn.Width, mn.Height));
            }

            // Build macro-level wires (wires between different macro nodes)
            var macroWires = new List<Wire>();
            var macroNodeIds = macroNodes.Select(n => n.Id).ToList();

            foreach (var w in wires)
            {
                string gFrom = null;
                string gTo = null;

                foreach (var mn in macroNodes)
                {
                    if (mn.MemberIds.Contains(w.from)) gFrom = mn.Id;
                    if (mn.MemberIds.Contains(w.to)) gTo = mn.Id;
                }

                if (gFrom != null && gTo != null && gFrom != gTo)
                {
                    macroWires.Add(new Wire { from = gFrom, to = gTo });
                }
            }

            // Break macro-level cycles!
            var cleanMacroWires = BreakCycles(macroNodeIds, macroWires);

            // Run macro layout (layout groups/independent nodes relative to each other)
            RunGripLogic(macroItems, cleanMacroWires, originalMinX, originalMinY, null);

            // 6. Pre-calculate final bounds for anchoring
            var movedGuids = new HashSet<Guid>();
            foreach (var mn in macroNodes) foreach (var mid in mn.MemberIds) if (Guid.TryParse(mid, out Guid g)) movedGuids.Add(g);

            RectangleF definitionBounds = RectangleF.Empty;
            bool firstDef = true;
            foreach (var gItem in macroItems)
            {
                var mn = macroNodes.FirstOrDefault(n => n.Id == gItem.Id);
                if (mn == null) continue;
                var gPos = gItem.Pivot;
                var gB = mn.LocalBounds;
                float globX = (gPos.X - gB.X);
                float globY = (gPos.Y - gB.Y);
                RectangleF itemBounds = new RectangleF(gB.X + globX, gB.Y + globY, gB.Width, gB.Height);
                if (firstDef) { definitionBounds = itemBounds; firstDef = false; }
                else definitionBounds = RectangleF.Union(definitionBounds, itemBounds);
            }

            // 7. Final Positioning: Identify Anchor and Apply Global Coordinates
            IGH_DocumentObject rootAnchor = null;
            bool rootIsUpstream = true;

            foreach (var w in wires)
            {
                bool fromIsTarget = movedGuids.Contains(Guid.Parse(w.from));
                bool toIsTarget = movedGuids.Contains(Guid.Parse(w.to));

                if (!fromIsTarget && toIsTarget)
                {
                    rootAnchor = doc.FindObject(Guid.Parse(w.from), false);
                    if (rootAnchor != null) { rootIsUpstream = true; break; }
                }
                if (fromIsTarget && !toIsTarget)
                {
                    rootAnchor = doc.FindObject(Guid.Parse(w.to), false);
                    if (rootAnchor != null) { rootIsUpstream = false; break; }
                }
            }

            // 7. TOPOLOGICAL ANCHORING: Calculate Professional Target Coordinates
            float finalAnchorX = originalMinX;
            float finalAnchorY = originalMinY;

            if (rootAnchor != null && rootAnchor.Attributes != null)
            {
                if (rootIsUpstream)
                {
                    // Match the successful creation engine's 150f padding
                    finalAnchorX = rootAnchor.Attributes.Bounds.Right + 150f;
                    finalAnchorY = rootAnchor.Attributes.Pivot.Y;
                }
                else
                {
                    // Anchor to existing downstream component
                    finalAnchorX = rootAnchor.Attributes.Bounds.Left - definitionBounds.Width - 150f;
                    finalAnchorY = rootAnchor.Attributes.Pivot.Y;
                }
            }

            foreach (var gItem in macroItems)
            {
                var mn = macroNodes.FirstOrDefault(n => n.Id == gItem.Id);
                if (mn == null) continue;

                var gPos = gItem.Pivot;
                var gB = mn.LocalBounds;

                float globX = (gPos.X - gB.X) + (finalAnchorX - originalMinX);
                float globY = (gPos.Y - gB.Y) + (finalAnchorY - originalMinY);

                if (mn.IsGroup)
                {
                    foreach (var mid in mn.MemberIds)
                    {
                        if (Guid.TryParse(mid, out Guid guid))
                        {
                            var obj = doc.FindObject(guid, false);
                            if (obj != null && localOffsets.TryGetValue(mid, out PointF loc))
                            {
                                obj.Attributes.Pivot = new PointF(loc.X + globX, loc.Y + globY);
                                obj.Attributes.ExpireLayout();
                                obj.Attributes.PerformLayout();
                            }
                        }
                    }
                    if (mn.GroupObject != null) mn.GroupObject.ExpireCaches();
                }
                else
                {
                    var obj = mn.SingleObject;
                    if (obj != null)
                    {
                        obj.Attributes.Pivot = new PointF(gB.X + globX, gB.Y + globY);
                        obj.Attributes.ExpireLayout();
                        obj.Attributes.PerformLayout();
                    }
                }
            }

            // 8. Enforce Positive Coordinates FIRST
            if (movedGuids.Count > 0)
            {
                float finalMinX = float.MaxValue;
                float finalMinY = float.MaxValue;
                bool foundAny = false;

                foreach (var g in movedGuids)
                {
                    var obj = doc.FindObject(g, false);
                    if (obj != null && obj.Attributes != null)
                    {
                        finalMinX = Math.Min(finalMinX, obj.Attributes.Pivot.X);
                        finalMinY = Math.Min(finalMinY, obj.Attributes.Pivot.Y);
                        foundAny = true;
                    }
                }

                if (foundAny && (finalMinX < 0 || finalMinY < 0))
                {
                    float finalShiftX = finalMinX < 0 ? -finalMinX : 0;
                    float finalShiftY = finalMinY < 0 ? -finalMinY : 0;

                    foreach (var g in movedGuids)
                    {
                        var obj = doc.FindObject(g, false);
                        if (obj != null && obj.Attributes != null)
                        {
                            var old = obj.Attributes.Pivot;
                            obj.Attributes.Pivot = new PointF(old.X + finalShiftX, old.Y + finalShiftY);
                            obj.Attributes.ExpireLayout();
                        }
                    }

                    // CRITICAL: Update the anchor coordinates so the obstacle nudge knows about this positive shift!
                    finalAnchorX += finalShiftX;
                    finalAnchorY += finalShiftY;
                }
            }

            // 9. Obstacle-Aware Nudge
            if (movedGuids.Count > 0)
            {
                RectangleF actualMovedBounds = RectangleF.Empty;
                bool firstActual = true;
                foreach (var g in movedGuids)
                {
                    var obj = doc.FindObject(g, false);
                    if (obj != null && obj.Attributes != null)
                    {
                        if (firstActual) { actualMovedBounds = obj.Attributes.Bounds; firstActual = false; }
                        else actualMovedBounds = RectangleF.Union(actualMovedBounds, obj.Attributes.Bounds);
                    }
                }

                if (!actualMovedBounds.IsEmpty)
                {
                    var obstacles = doc.Objects.Where(o => !movedGuids.Contains(o.InstanceGuid) && !(o is GH_Group)).Select(o => o.Attributes.Bounds).ToList();
                    for (int i = 0; i < obstacles.Count; i++) { var b = obstacles[i]; b.Inflate(40f, 40f); obstacles[i] = b; }

                    RectangleF testBounds = actualMovedBounds;
                    bool collision = true;
                    int safety = 0;
                    while (collision && safety < 500)
                    {
                        collision = false;
                        foreach (var obs in obstacles)
                        {
                            if (testBounds.IntersectsWith(obs))
                            {
                                collision = true;
                                testBounds.Y = obs.Bottom + 50f;
                                break;
                            }
                        }
                        safety++;
                    }

                    float shiftX = testBounds.X - actualMovedBounds.X;
                    float shiftY = testBounds.Y - actualMovedBounds.Y;

                    if (Math.Abs(shiftX) > 1 || Math.Abs(shiftY) > 1)
                    {
                        foreach (var g in movedGuids)
                        {
                            var obj = doc.FindObject(g, false);
                            if (obj != null)
                            {
                                var old = obj.Attributes.Pivot;
                                obj.Attributes.Pivot = new PointF(old.X + shiftX, old.Y + shiftY);
                                obj.Attributes.ExpireLayout();
                            }
                        }
                    }
                }
            }

            doc.NewSolution(true);
        }

        private static List<IGH_DocumentObject> GetSpiderWebNeighborhood(IGH_DocumentObject startObj, int depth = 2)
        {
            var neighborhood = new HashSet<IGH_DocumentObject>();
            var queue = new Queue<(IGH_DocumentObject obj, int d)>();
            queue.Enqueue((startObj, 0));
            neighborhood.Add(startObj);

            while (queue.Count > 0)
            {
                var (current, d) = queue.Dequeue();
                if (d >= depth) continue;

                var related = new List<IGH_DocumentObject>();
                if (current is IGH_Component comp)
                {
                    foreach (var p in comp.Params.Input) foreach (var s in p.Sources) if (s.Attributes?.GetTopLevel.DocObject != null) related.Add(s.Attributes.GetTopLevel.DocObject);
                    foreach (var p in comp.Params.Output) foreach (var r in p.Recipients) if (r.Attributes?.GetTopLevel.DocObject != null) related.Add(r.Attributes.GetTopLevel.DocObject);
                }
                else if (current is IGH_Param param)
                {
                    foreach (var s in param.Sources) if (s.Attributes?.GetTopLevel.DocObject != null) related.Add(s.Attributes.GetTopLevel.DocObject);
                    foreach (var r in param.Recipients) if (r.Attributes?.GetTopLevel.DocObject != null) related.Add(r.Attributes.GetTopLevel.DocObject);
                }

                foreach (var r in related)
                {
                    if (!neighborhood.Contains(r))
                    {
                        neighborhood.Add(r);
                        queue.Enqueue((r, d + 1));
                    }
                }
            }
            return neighborhood.ToList();
        }

        private static string GenerateShorthandFromGuids(GH_Document doc, List<Guid> guids, int depth = 0)
        {
            var sb = new StringBuilder();
            var tracker = new Dictionary<Guid, string>();
            var objects = new List<IGH_DocumentObject>();

            var initialObjects = new List<IGH_DocumentObject>();
            foreach (var g in guids)
            {
                var obj = doc.FindObject(g, false);
                if (obj != null) initialObjects.Add(obj);
            }

            if (depth > 0)
            {
                var expanded = new HashSet<IGH_DocumentObject>();
                foreach (var obj in initialObjects)
                {
                    foreach (var neighbor in GetSpiderWebNeighborhood(obj, depth))
                    {
                        expanded.Add(neighbor);
                    }
                }
                objects = expanded.ToList();
            }
            else
            {
                objects = initialObjects;
            }

            if (objects.Count == 0) return "No objects found.";

            sb.AppendLine("# PROMPT: Reverse Engineered nextra.gh Shorthand");
            sb.AppendLine("");

            // 1. Generate N and C commands
            foreach (var obj in objects)
            {
                string cleanName = new string(obj.NickName.Where(char.IsLetterOrDigit).ToArray());
                if (string.IsNullOrEmpty(cleanName)) cleanName = "obj";
                string safeId = cleanName + "_" + obj.InstanceGuid.ToString().Substring(0, 4);
                tracker[obj.InstanceGuid] = safeId;

                if (obj is GH_Group grp)
                {
                    sb.AppendLine($"N {safeId} | Group | Special");
                    var memberIds = grp.ObjectIDs.Select(id =>
                    {
                        if (tracker.ContainsKey(id)) return tracker[id];
                        var mem = doc.FindObject(id, false);
                        if (mem == null) return null;
                        string mName = new string(mem.NickName.Where(char.IsLetterOrDigit).ToArray());
                        if (string.IsNullOrEmpty(mName)) mName = "obj";
                        return mName + "_" + mem.InstanceGuid.ToString().Substring(0, 4);
                    }).Where(id => id != null).ToList();

                    string hexColor = $"#{grp.Colour.R:X2}{grp.Colour.G:X2}{grp.Colour.B:X2}";
                    sb.AppendLine($"C {safeId} | n={grp.NickName}, c={hexColor}, ids={string.Join(";", memberIds)}");
                }
                else if (obj is GH_Scribble scr)
                {
                    sb.AppendLine($"N {safeId} | Scribble | Special");
                    sb.AppendLine($"C {safeId} | text={scr.Text.Replace("\n", "\\n")}, size={(int)scr.Font.Size}");
                }
                else
                {
                    sb.AppendLine($"N {safeId} | {obj.Name} | {obj.Category} | {obj.SubCategory}");

                    if (obj is GH_NumberSlider slider)
                    {
                        sb.AppendLine($"C {safeId} | val={slider.CurrentValue}, min={slider.Slider.Minimum}, max={slider.Slider.Maximum}, d={slider.Slider.DecimalPlaces}");
                    }
                    else if (obj is GH_Panel panel)
                    {
                        sb.AppendLine($"C {safeId} | text={panel.UserText.Replace("\n", "\\n")}");
                    }
                    else if (obj is GH_BooleanToggle toggle)
                    {
                        sb.AppendLine($"C {safeId} | val={toggle.Value.ToString().ToLower()}");
                    }
                    else if (obj is IGH_Component comp)
                    {
                        // Handle port settings and persistent data for input params
                        for (int i = 0; i < comp.Params.Input.Count; i++)
                        {
                            var p = comp.Params.Input[i];
                            var modifiers = new List<string>();
                            if (p.DataMapping == GH_DataMapping.Flatten) modifiers.Add("m=f");
                            else if (p.DataMapping == GH_DataMapping.Graft) modifiers.Add("m=g");
                            if (p.Reverse) modifiers.Add("r=true");
                            if (p.Simplify) modifiers.Add("s=true");

                            // Persistent Data (Shorthand format usually supports val=... or x,y,z)
                            if (p.SourceCount == 0 && p.VolatileDataCount > 0)
                            {
                                var first = p.VolatileData.AllData(true).Cast<IGH_Goo>().FirstOrDefault();
                                if (first != null)
                                {
                                    if (first is Grasshopper.Kernel.Types.GH_Number num) modifiers.Add($"val={num.Value}");
                                    else if (first is Grasshopper.Kernel.Types.GH_Integer intVal) modifiers.Add($"val={intVal.Value}");
                                    else if (first is Grasshopper.Kernel.Types.GH_String str) modifiers.Add($"text={str.Value}");
                                    else if (first is Grasshopper.Kernel.Types.GH_Point pt) modifiers.Add($"val={pt.Value.X},{pt.Value.Y},{pt.Value.Z}");
                                    else if (first is Grasshopper.Kernel.Types.GH_Vector vec) modifiers.Add($"val={vec.Value.X},{vec.Value.Y},{vec.Value.Z}");
                                    else if (first is Grasshopper.Kernel.Types.GH_Boolean boo) modifiers.Add($"val={boo.Value.ToString().ToLower()}");
                                }
                            }

                            if (modifiers.Count > 0) sb.AppendLine($"P {safeId}.i{i} | {string.Join(", ", modifiers)}");

                            // Expression support
                            var exprParam = p as Grasshopper.Kernel.Parameters.Param_Number; // Most common for expressions
                            if (exprParam != null && !string.IsNullOrEmpty(exprParam.Expression))
                                sb.AppendLine($"E {safeId}.i{i} | {exprParam.Expression}");
                            else
                            {
                                // Check generic Expression property via reflection for robustness
                                var exprProp = p.GetType().GetProperty("Expression");
                                if (exprProp != null)
                                {
                                    string expr = exprProp.GetValue(p) as string;
                                    if (!string.IsNullOrEmpty(expr))
                                        sb.AppendLine($"E {safeId}.i{i} | {expr}");
                                }
                            }
                        }
                    }
                    else if (obj is IGH_Param pSingle)
                    {
                        var modifiers = new List<string>();
                        if (pSingle.SourceCount == 0 && pSingle.VolatileDataCount > 0)
                        {
                            var first = pSingle.VolatileData.AllData(true).Cast<IGH_Goo>().FirstOrDefault();
                            if (first != null)
                            {
                                if (first is Grasshopper.Kernel.Types.GH_Number num) modifiers.Add($"val={num.Value}");
                                else if (first is Grasshopper.Kernel.Types.GH_Integer intVal) modifiers.Add($"val={intVal.Value}");
                                else if (first is Grasshopper.Kernel.Types.GH_String str) modifiers.Add($"text={str.Value}");
                                else if (first is Grasshopper.Kernel.Types.GH_Point pt) modifiers.Add($"val={pt.Value.X},{pt.Value.Y},{pt.Value.Z}");
                                else if (first is Grasshopper.Kernel.Types.GH_Vector vec) modifiers.Add($"val={vec.Value.X},{vec.Value.Y},{vec.Value.Z}");
                                else if (first is Grasshopper.Kernel.Types.GH_Boolean boo) modifiers.Add($"val={boo.Value.ToString().ToLower()}");
                            }
                        }
                        if (modifiers.Count > 0) sb.AppendLine($"C {safeId} | {string.Join(", ", modifiers)}");
                    }
                }
            }

            // 2. Generate W commands
            foreach (var obj in objects)
            {
                if (obj is IGH_Component comp)
                {
                    for (int i = 0; i < comp.Params.Input.Count; i++)
                    {
                        var p = comp.Params.Input[i];
                        foreach (var src in p.Sources)
                        {
                            if (src.Attributes?.GetTopLevel?.DocObject != null)
                            {
                                var fromObj = src.Attributes.GetTopLevel.DocObject;
                                string fromId = null;
                                int fromPortIdx = 0;

                                if (tracker.ContainsKey(fromObj.InstanceGuid))
                                {
                                    fromId = tracker[fromObj.InstanceGuid];
                                }
                                else
                                {
                                    // It's an external existing component
                                    string fName = new string(fromObj.NickName.Where(char.IsLetterOrDigit).ToArray());
                                    if (string.IsNullOrEmpty(fName)) fName = "obj";
                                    fromId = fName + "_" + fromObj.InstanceGuid.ToString().Substring(0, 4);
                                }

                                if (fromObj is IGH_Component fromComp)
                                {
                                    fromPortIdx = fromComp.Params.Output.IndexOf(src);
                                }

                                if (fromId != null)
                                {
                                    sb.AppendLine($"W {fromId}.{fromPortIdx} > {tracker[obj.InstanceGuid]}.{i}");
                                }
                            }
                        }
                    }
                }
                else if (obj is IGH_Param param)
                {
                    foreach (var src in param.Sources)
                    {
                        if (src.Attributes?.GetTopLevel?.DocObject != null)
                        {
                            var fromObj = src.Attributes.GetTopLevel.DocObject;
                            string fromId = null;
                            int fromPortIdx = 0;

                            if (tracker.ContainsKey(fromObj.InstanceGuid))
                            {
                                fromId = tracker[fromObj.InstanceGuid];
                            }
                            else
                            {
                                string fName = new string(fromObj.NickName.Where(char.IsLetterOrDigit).ToArray());
                                if (string.IsNullOrEmpty(fName)) fName = "obj";
                                fromId = fName + "_" + fromObj.InstanceGuid.ToString().Substring(0, 4);
                            }

                            if (fromObj is IGH_Component fromComp)
                            {
                                fromPortIdx = fromComp.Params.Output.IndexOf(src);
                            }

                            if (fromId != null)
                            {
                                sb.AppendLine($"W {fromId}.{fromPortIdx} > {tracker[obj.InstanceGuid]}.0");
                            }
                        }
                    }
                }
            }

            return sb.ToString();
        }
    }
}

