using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Bootstrap;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Model;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Web;
using UnityEngine;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Transport
{
    public sealed class HttpApiServer
    {
        private readonly int port;
        private readonly RemotePostprocessController controller;
        private readonly WsEventHub wsEventHub;
        private readonly WebUiAssetProvider webUiAssetProvider;
        private readonly object socketLock = new object();
        private readonly System.Collections.Generic.List<WebSocket> sockets = new System.Collections.Generic.List<WebSocket>();
        private HttpListener listener;
        private Thread workerThread;
        private volatile bool isRunning;

        public HttpApiServer(int port, RemotePostprocessController controller, WsEventHub wsEventHub)
        {
            this.port = port;
            this.controller = controller;
            this.wsEventHub = wsEventHub;
            webUiAssetProvider = new WebUiAssetProvider();
        }

        public bool IsRunning => isRunning;

        public void Start()
        {
            if (isRunning)
            {
                return;
            }

            listener = CreateListener(port);
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                Debug.LogWarning($"[URP Remote PP] Listener wildcard bind failed: {ex.Message}. Fallback to localhost.");
                listener.Close();
                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
            }
            isRunning = true;

            workerThread = new Thread(ListenLoop)
            {
                Name = "URPRemotePostprocess.HttpApiServer",
                IsBackground = true
            };
            workerThread.Start();
            SubscribeEvents();

            Debug.Log($"[URP Remote PP] HttpApiServer started on port {port}.");
        }

        public void Stop()
        {
            if (!isRunning)
            {
                return;
            }

            isRunning = false;

            try
            {
                listener?.Stop();
                listener?.Close();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[URP Remote PP] Stop listener error: {ex.Message}");
            }

            if (workerThread != null && workerThread.IsAlive)
            {
                workerThread.Join(1000);
            }

            CloseAllSockets();
            UnsubscribeEvents();

            workerThread = null;
            listener = null;

            Debug.Log("[URP Remote PP] HttpApiServer stopped.");
        }

        private static HttpListener CreateListener(int port)
        {
            var created = new HttpListener();
            created.Prefixes.Add($"http://*:{port}/");
            created.Prefixes.Add($"http://localhost:{port}/");
            return created;
        }

        private void ListenLoop()
        {
            while (isRunning && listener != null)
            {
                HttpListenerContext context = null;
                try
                {
                    context = listener.GetContext();
                    HandleContext(context);
                }
                catch (HttpListenerException)
                {
                    if (!isRunning)
                    {
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    if (!isRunning)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[URP Remote PP] Request handling error: {ex}");
                    if (context != null)
                    {
                        SafeWriteJson(context.Response, 500, "{\"ok\":false,\"error\":\"internal_error\"}");
                    }
                }
            }
        }

        private void HandleContext(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            AddCorsHeaders(response);

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            string path = request.Url?.AbsolutePath ?? "/";
            if (request.HttpMethod == "GET" && path == "/health")
            {
                SafeWriteJson(response, 200, "{\"ok\":true}");
                return;
            }

            if (request.HttpMethod == "GET" && path == "/ws")
            {
                if (!request.IsWebSocketRequest)
                {
                    SafeWriteJson(response, 400, "{\"ok\":false,\"error\":\"websocket_required\"}");
                    return;
                }

                AcceptWebSocket(context);
                return;
            }

            if (request.HttpMethod == "GET" && path == "/schema")
            {
                string schemaJson = controller.GetSchemaJson(false);
                SafeWriteJson(response, 200, schemaJson);
                return;
            }

            if (request.HttpMethod == "GET" && path == "/state")
            {
                if (!controller.InvokeOnMainThread(() => controller.BuildCurrentStatePatch(), out StatePatch patch))
                {
                    SafeWriteJson(response, 500, "{\"ok\":false,\"error\":\"state_unavailable\"}");
                    return;
                }

                SafeWriteJson(response, 200, JsonUtility.ToJson(patch));
                return;
            }

            if (request.HttpMethod == "PATCH" && path == "/state")
            {
                string body = ReadRequestBody(request);
                StatePatch patch = TryParseJson<StatePatch>(body);
                if (patch == null)
                {
                    SafeWriteJson(response, 400, "{\"ok\":false,\"error\":\"invalid_patch\"}");
                    return;
                }

                bool ok = controller.InvokeOnMainThread(() => controller.ApplyPatch(patch));
                if (!ok)
                {
                    SafeWriteJson(response, 500, "{\"ok\":false,\"error\":\"apply_failed\"}");
                    return;
                }

                SafeWriteJson(response, 200, "{\"ok\":true}");
                return;
            }

            if (request.HttpMethod == "GET" && path == "/presets")
            {
                if (!controller.InvokeOnMainThread(() =>
                {
                    var data = new PresetListResponse();
                    System.Collections.Generic.IReadOnlyList<string> names = controller.ListPresets();
                    for (int i = 0; i < names.Count; i++)
                    {
                        data.presets.Add(names[i]);
                    }
                    data.selectedPreset = controller.GetSelectedPresetName();
                    return data;
                }, out PresetListResponse payload))
                {
                    SafeWriteJson(response, 500, "{\"ok\":false,\"error\":\"preset_list_failed\"}");
                    return;
                }

                SafeWriteJson(response, 200, JsonUtility.ToJson(payload));
                return;
            }

            if (request.HttpMethod == "POST" && path == "/presets/save")
            {
                string body = ReadRequestBody(request);
                PresetRequest req = TryParseJson<PresetRequest>(body);
                if (req == null || string.IsNullOrWhiteSpace(req.presetName))
                {
                    SafeWriteJson(response, 400, "{\"ok\":false,\"error\":\"invalid_preset_name\"}");
                    return;
                }

                bool ok = false;
                controller.InvokeOnMainThread(() => { ok = controller.SavePreset(req.presetName); });
                SafeWriteJson(response, ok ? 200 : 500, ok ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"save_failed\"}");
                return;
            }

            if (request.HttpMethod == "POST" && path == "/presets/load")
            {
                string body = ReadRequestBody(request);
                PresetRequest req = TryParseJson<PresetRequest>(body);
                if (req == null || string.IsNullOrWhiteSpace(req.presetName))
                {
                    SafeWriteJson(response, 400, "{\"ok\":false,\"error\":\"invalid_preset_name\"}");
                    return;
                }

                bool ok = false;
                controller.InvokeOnMainThread(() => { ok = controller.LoadPreset(req.presetName); });
                SafeWriteJson(response, ok ? 200 : 404, ok ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"preset_not_found\"}");
                return;
            }

            if (request.HttpMethod == "POST" && path == "/presets/delete")
            {
                string body = ReadRequestBody(request);
                PresetRequest req = TryParseJson<PresetRequest>(body);
                if (req == null || string.IsNullOrWhiteSpace(req.presetName))
                {
                    SafeWriteJson(response, 400, "{\"ok\":false,\"error\":\"invalid_preset_name\"}");
                    return;
                }

                bool ok = false;
                controller.InvokeOnMainThread(() => { ok = controller.DeletePreset(req.presetName); });
                SafeWriteJson(response, ok ? 200 : 404, ok ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"preset_not_found\"}");
                return;
            }

            if (request.HttpMethod == "POST" && path == "/presets/rename")
            {
                string body = ReadRequestBody(request);
                RenamePresetRequest req = TryParseJson<RenamePresetRequest>(body);
                if (req == null || string.IsNullOrWhiteSpace(req.fromPresetName) || string.IsNullOrWhiteSpace(req.toPresetName))
                {
                    SafeWriteJson(response, 400, "{\"ok\":false,\"error\":\"invalid_preset_name\"}");
                    return;
                }

                bool ok = false;
                controller.InvokeOnMainThread(() => { ok = controller.RenamePreset(req.fromPresetName, req.toPresetName); });
                SafeWriteJson(response, ok ? 200 : 409, ok ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"rename_failed\"}");
                return;
            }

            if (request.HttpMethod == "POST" && path == "/presets/select")
            {
                string body = ReadRequestBody(request);
                PresetRequest req = TryParseJson<PresetRequest>(body);
                if (req == null || string.IsNullOrWhiteSpace(req.presetName))
                {
                    SafeWriteJson(response, 400, "{\"ok\":false,\"error\":\"invalid_preset_name\"}");
                    return;
                }

                controller.InvokeOnMainThread(() => controller.SetSelectedPresetName(req.presetName));
                SafeWriteJson(response, 200, "{\"ok\":true}");
                return;
            }

            if (request.HttpMethod == "GET")
            {
                if (webUiAssetProvider.TryGetAsset(path, out byte[] payload, out string contentType))
                {
                    SafeWriteBytes(response, 200, payload, contentType);
                    return;
                }
            }

            SafeWriteJson(response, 404, "{\"ok\":false,\"error\":\"not_found\"}");
        }

        private void AcceptWebSocket(HttpListenerContext context)
        {
            try
            {
                HttpListenerWebSocketContext wsContext = context.AcceptWebSocketAsync(null).GetAwaiter().GetResult();
                WebSocket socket = wsContext.WebSocket;
                lock (socketLock)
                {
                    sockets.Add(socket);
                }

                _ = Task.Run(() => ProcessSocketLoop(socket));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[URP Remote PP] WebSocket accept failed: {ex.Message}");
                SafeWriteJson(context.Response, 500, "{\"ok\":false,\"error\":\"websocket_accept_failed\"}");
            }
        }

        private async Task ProcessSocketLoop(WebSocket socket)
        {
            byte[] buffer = new byte[512];
            try
            {
                while (isRunning && socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                RemoveSocket(socket);
                try
                {
                    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                    }
                }
                catch
                {
                }
                socket.Dispose();
            }
        }

        private void SubscribeEvents()
        {
            if (wsEventHub == null)
            {
                return;
            }

            wsEventHub.StateUpdated += OnStateUpdated;
            wsEventHub.PresetLoaded += OnPresetLoaded;
            wsEventHub.Error += OnError;
        }

        private void UnsubscribeEvents()
        {
            if (wsEventHub == null)
            {
                return;
            }

            wsEventHub.StateUpdated -= OnStateUpdated;
            wsEventHub.PresetLoaded -= OnPresetLoaded;
            wsEventHub.Error -= OnError;
        }

        private void OnStateUpdated(StatePatch patch)
        {
            string json = JsonUtility.ToJson(new WsStateUpdatedMessage { data = patch });
            _ = Task.Run(() => BroadcastJsonAsync(json));
        }

        private void OnPresetLoaded(string presetName)
        {
            string json = JsonUtility.ToJson(new WsPresetLoadedMessage { presetName = presetName ?? string.Empty });
            _ = Task.Run(() => BroadcastJsonAsync(json));
        }

        private void OnError(string message)
        {
            string json = JsonUtility.ToJson(new WsErrorMessage { message = message ?? string.Empty });
            _ = Task.Run(() => BroadcastJsonAsync(json));
        }

        private async Task BroadcastJsonAsync(string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            WebSocket[] targets;
            lock (socketLock)
            {
                targets = sockets.ToArray();
            }

            for (int i = 0; i < targets.Length; i++)
            {
                WebSocket socket = targets[i];
                if (socket == null || socket.State != WebSocketState.Open)
                {
                    RemoveSocket(socket);
                    continue;
                }

                try
                {
                    await socket.SendAsync(
                        new ArraySegment<byte>(payload),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
                catch
                {
                    RemoveSocket(socket);
                }
            }
        }

        private void RemoveSocket(WebSocket socket)
        {
            lock (socketLock)
            {
                sockets.Remove(socket);
            }
        }

        private void CloseAllSockets()
        {
            WebSocket[] targets;
            lock (socketLock)
            {
                targets = sockets.ToArray();
                sockets.Clear();
            }

            for (int i = 0; i < targets.Length; i++)
            {
                WebSocket socket = targets[i];
                if (socket == null)
                {
                    continue;
                }

                try
                {
                    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    {
                        socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None).GetAwaiter().GetResult();
                    }
                }
                catch
                {
                }
                finally
                {
                    socket.Dispose();
                }
            }
        }

        private static string ReadRequestBody(HttpListenerRequest request)
        {
            using (var stream = request.InputStream)
            using (var reader = new StreamReader(stream, request.ContentEncoding ?? Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static T TryParseJson<T>(string json) where T : class
        {
            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void AddCorsHeaders(HttpListenerResponse response)
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "GET,POST,PATCH,OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        }

        private static void SafeWriteJson(HttpListenerResponse response, int statusCode, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json ?? "{}");
            SafeWriteBytes(response, statusCode, payload, "application/json; charset=utf-8");
        }

        private static void SafeWriteBytes(HttpListenerResponse response, int statusCode, byte[] payload, string contentType)
        {
            try
            {
                response.StatusCode = statusCode;
                response.ContentType = contentType;
                response.ContentLength64 = payload?.Length ?? 0;
                if (payload != null && payload.Length > 0)
                {
                    response.OutputStream.Write(payload, 0, payload.Length);
                }
                response.OutputStream.Flush();
            }
            finally
            {
                response.Close();
            }
        }

        [Serializable]
        private sealed class PresetRequest
        {
            public string presetName = string.Empty;
        }

        [Serializable]
        private sealed class RenamePresetRequest
        {
            public string fromPresetName = string.Empty;
            public string toPresetName = string.Empty;
        }

        [Serializable]
        private sealed class PresetListResponse
        {
            public System.Collections.Generic.List<string> presets = new System.Collections.Generic.List<string>();
            public string selectedPreset = string.Empty;
        }

        [Serializable]
        private sealed class WsStateUpdatedMessage
        {
            public string eventName = "state.updated";
            public StatePatch data;
        }

        [Serializable]
        private sealed class WsPresetLoadedMessage
        {
            public string eventName = "preset.loaded";
            public string presetName = string.Empty;
        }

        [Serializable]
        private sealed class WsErrorMessage
        {
            public string eventName = "error";
            public string message = string.Empty;
        }
    }
}
