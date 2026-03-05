using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Bootstrap;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Model;
using UnityEngine;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Transport
{
    public sealed class SyncTargetService
    {
        private const string SyncTargetFileName = "syncTargets.json";
        private const int HealthTimeoutMs = 1200;
        private const int PatchTimeoutMs = 1800;
        private const int ProbeIntervalMs = 3000;

        private readonly object syncRoot = new object();
        private readonly string syncTargetFilePath;
        private readonly int defaultPort;
        private readonly RemotePostprocessController controller;
        private readonly List<SyncTargetConfigData> targets = new List<SyncTargetConfigData>();
        private readonly Dictionary<string, SyncTargetRuntimeState> runtimeStatesById = new Dictionary<string, SyncTargetRuntimeState>();
        private volatile bool isRunning;
        private Thread probeThread;

        public SyncTargetService(string remotePostprocessRootPath, int defaultPort, RemotePostprocessController controller)
        {
            if (string.IsNullOrWhiteSpace(remotePostprocessRootPath))
            {
                throw new ArgumentException("remotePostprocessRootPath is required.", nameof(remotePostprocessRootPath));
            }

            this.defaultPort = defaultPort > 0 ? defaultPort : 8080;
            this.controller = controller;
            syncTargetFilePath = Path.Combine(remotePostprocessRootPath, SyncTargetFileName);
            LoadConfig();
        }

        public void Start()
        {
            if (isRunning)
            {
                return;
            }

            isRunning = true;
            probeThread = new Thread(ProbeLoop)
            {
                Name = "URPRemotePostprocess.SyncProbe",
                IsBackground = true
            };
            probeThread.Start();
        }

        public void Stop()
        {
            if (!isRunning)
            {
                return;
            }

            isRunning = false;
            if (probeThread != null && probeThread.IsAlive)
            {
                probeThread.Join(1000);
            }

            probeThread = null;
        }

        public SyncTargetSnapshot[] GetSnapshots()
        {
            lock (syncRoot)
            {
                var list = new List<SyncTargetSnapshot>(targets.Count);
                for (int i = 0; i < targets.Count; i++)
                {
                    SyncTargetConfigData target = targets[i];
                    SyncTargetRuntimeState runtime = GetOrCreateRuntimeStateNoLock(target.id);
                    string error = string.IsNullOrWhiteSpace(runtime.connectionError)
                        ? (runtime.syncError ?? string.Empty)
                        : runtime.connectionError;
                    list.Add(new SyncTargetSnapshot
                    {
                        id = target.id,
                        address = target.address,
                        enabled = target.enabled,
                        connected = runtime.connected,
                        lastError = error,
                        lastCheckedUtc = runtime.lastCheckedUtc == DateTime.MinValue ? string.Empty : runtime.lastCheckedUtc.ToString("O"),
                        lastSyncedUtc = runtime.lastSyncedUtc == DateTime.MinValue ? string.Empty : runtime.lastSyncedUtc.ToString("O")
                    });
                }

                return list.ToArray();
            }
        }

        public bool TryAddOrUpdateTarget(string address, string password, out string error)
        {
            error = string.Empty;
            if (!TryNormalizeAddress(address, out string normalizedAddress, out error))
            {
                return false;
            }

            string trimmedPassword = string.IsNullOrWhiteSpace(password) ? string.Empty : password.Trim();
            string token = string.Empty;
            string tokenExpiresAtUtc = string.Empty;
            if (!string.IsNullOrEmpty(trimmedPassword))
            {
                if (!TryLogin(normalizedAddress, trimmedPassword, out token, out tokenExpiresAtUtc, out error))
                {
                    return false;
                }
            }

            lock (syncRoot)
            {
                SyncTargetConfigData target = FindByAddressNoLock(normalizedAddress);
                if (target == null)
                {
                    target = new SyncTargetConfigData
                    {
                        id = Guid.NewGuid().ToString("N"),
                        address = normalizedAddress,
                        enabled = true
                    };
                    targets.Add(target);
                }

                target.address = normalizedAddress;
                target.enabled = true;
                if (!string.IsNullOrEmpty(trimmedPassword))
                {
                    target.password = trimmedPassword;
                    target.accessToken = token;
                    target.tokenExpiresAtUtc = tokenExpiresAtUtc;
                }

                SaveConfigNoLock();
            }

            UpdateConnectionState(normalizedAddress);
            return true;
        }

        public bool TryRemoveTarget(string targetId, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(targetId))
            {
                error = "invalid_target";
                return false;
            }

            lock (syncRoot)
            {
                int index = targets.FindIndex((t) => string.Equals(t.id, targetId.Trim(), StringComparison.Ordinal));
                if (index < 0)
                {
                    error = "target_not_found";
                    return false;
                }

                string id = targets[index].id;
                targets.RemoveAt(index);
                runtimeStatesById.Remove(id);
                SaveConfigNoLock();
                return true;
            }
        }

        public bool TrySetEnabled(string targetId, bool enabled, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(targetId))
            {
                error = "invalid_target";
                return false;
            }

            lock (syncRoot)
            {
                SyncTargetConfigData target = FindByIdNoLock(targetId.Trim());
                if (target == null)
                {
                    error = "target_not_found";
                    return false;
                }

                target.enabled = enabled;
                SaveConfigNoLock();
            }

            return true;
        }

        public void ForwardPatchAsync(StatePatch patch)
        {
            if (patch == null || patch.entries == null || patch.entries.Count == 0)
            {
                return;
            }

            SyncTargetConfigData[] snapshot;
            lock (syncRoot)
            {
                var enabledTargets = new List<SyncTargetConfigData>();
                for (int i = 0; i < targets.Count; i++)
                {
                    if (!targets[i].enabled)
                    {
                        continue;
                    }

                    enabledTargets.Add(CloneConfig(targets[i]));
                }

                snapshot = enabledTargets.ToArray();
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            string patchJson = JsonUtility.ToJson(patch);
            ThreadPool.QueueUserWorkItem(_ => ForwardPatchInternal(snapshot, patchJson));
        }

        private void ForwardPatchInternal(SyncTargetConfigData[] snapshot, string patchJson)
        {
            for (int i = 0; i < snapshot.Length; i++)
            {
                SyncTargetConfigData target = snapshot[i];
                bool success = TrySendPatch(target, patchJson, out bool reachable, out string error);
                UpdateSyncState(target.id, reachable, success, error);
            }
        }

        private bool TrySendPatch(SyncTargetConfigData target, string patchJson, out bool reachable, out string error)
        {
            reachable = false;
            error = string.Empty;
            if (target == null)
            {
                error = "invalid_target";
                return false;
            }

            if (!EnsureTargetToken(target, out error))
            {
                return false;
            }

            PatchSendResult firstTry = SendPatchCore(target, patchJson, out reachable, out error);
            if (firstTry == PatchSendResult.Success)
            {
                return true;
            }

            bool canRetryWithLogin = firstTry == PatchSendResult.Unauthorized && !string.IsNullOrWhiteSpace(target.password);
            if (!canRetryWithLogin)
            {
                return false;
            }

            if (!TryLogin(target.address, target.password, out string newToken, out string expiresAtUtc, out string loginError))
            {
                error = string.IsNullOrWhiteSpace(loginError) ? "sync_login_failed" : loginError;
                return false;
            }

            target.accessToken = newToken;
            target.tokenExpiresAtUtc = expiresAtUtc;
            UpdateTargetToken(target.id, newToken, expiresAtUtc);
            PatchSendResult secondTry = SendPatchCore(target, patchJson, out reachable, out error);
            return secondTry == PatchSendResult.Success;
        }

        private bool EnsureTargetToken(SyncTargetConfigData target, out string error)
        {
            error = string.Empty;
            bool needsRefresh = string.IsNullOrWhiteSpace(target.accessToken) || IsTokenExpiredSoon(target.tokenExpiresAtUtc);
            if (!needsRefresh)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(target.password))
            {
                return true;
            }

            if (!TryLogin(target.address, target.password, out string newToken, out string expiresAtUtc, out error))
            {
                return false;
            }

            target.accessToken = newToken;
            target.tokenExpiresAtUtc = expiresAtUtc;
            UpdateTargetToken(target.id, newToken, expiresAtUtc);
            return true;
        }

        private PatchSendResult SendPatchCore(SyncTargetConfigData target, string patchJson, out bool reachable, out string error)
        {
            reachable = false;
            error = string.Empty;

            try
            {
                HttpWebRequest request = CreateRequest($"{target.address}/state", "PATCH", PatchTimeoutMs);
                request.ContentType = "application/json; charset=utf-8";
                request.Headers["X-URP-Remote-Relay"] = "1";
                if (!string.IsNullOrWhiteSpace(target.accessToken))
                {
                    request.Headers["Authorization"] = $"Bearer {target.accessToken}";
                }

                byte[] body = Encoding.UTF8.GetBytes(patchJson ?? "{\"entries\":[]}");
                request.ContentLength = body.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int statusCode = (int)response.StatusCode;
                    reachable = true;
                    if (statusCode >= 200 && statusCode < 300)
                    {
                        return PatchSendResult.Success;
                    }

                    error = statusCode == 401 ? "target_unauthorized" : $"target_http_{statusCode}";
                    return statusCode == 401 ? PatchSendResult.Unauthorized : PatchSendResult.Failed;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response == null)
                {
                    error = "target_unreachable";
                    return PatchSendResult.Failed;
                }

                using (response)
                {
                    int statusCode = (int)response.StatusCode;
                    reachable = true;
                    if (statusCode == 401)
                    {
                        error = "target_unauthorized";
                        return PatchSendResult.Unauthorized;
                    }

                    error = $"target_http_{statusCode}";
                    return PatchSendResult.Failed;
                }
            }
            catch
            {
                error = "target_patch_failed";
                return PatchSendResult.Failed;
            }
        }

        private void ProbeLoop()
        {
            while (isRunning)
            {
                SyncTargetConfigData[] snapshot;
                lock (syncRoot)
                {
                    snapshot = targets.ConvertAll(CloneConfig).ToArray();
                }

                for (int i = 0; i < snapshot.Length; i++)
                {
                    UpdateConnectionState(snapshot[i].address, snapshot[i].id);
                }

                int sleep = ProbeIntervalMs;
                while (sleep > 0 && isRunning)
                {
                    int chunk = sleep > 200 ? 200 : sleep;
                    Thread.Sleep(chunk);
                    sleep -= chunk;
                }
            }
        }

        private void UpdateConnectionState(string address, string specificTargetId = "")
        {
            bool connected = TryProbeHealth(address, out string error);
            DateTime now = DateTime.UtcNow;
            List<SyncTargetConfigData> targetsToInitialSync = null;

            lock (syncRoot)
            {
                if (!string.IsNullOrWhiteSpace(specificTargetId))
                {
                    SyncTargetConfigData target = FindByIdNoLock(specificTargetId);
                    SyncTargetRuntimeState state = GetOrCreateRuntimeStateNoLock(specificTargetId);
                    bool wasConnected = state.connected;
                    state.connected = connected;
                    state.connectionError = connected ? string.Empty : error;
                    state.lastCheckedUtc = now;
                    if (connected && !wasConnected && target != null && target.enabled)
                    {
                        targetsToInitialSync = new List<SyncTargetConfigData>(1) { CloneConfig(target) };
                    }
                }
                else
                {
                    for (int i = 0; i < targets.Count; i++)
                    {
                        if (!string.Equals(targets[i].address, address, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        SyncTargetRuntimeState state = GetOrCreateRuntimeStateNoLock(targets[i].id);
                        bool wasConnected = state.connected;
                        state.connected = connected;
                        state.connectionError = connected ? string.Empty : error;
                        state.lastCheckedUtc = now;
                        if (connected && !wasConnected && targets[i].enabled)
                        {
                            if (targetsToInitialSync == null)
                            {
                                targetsToInitialSync = new List<SyncTargetConfigData>();
                            }

                            targetsToInitialSync.Add(CloneConfig(targets[i]));
                        }
                    }
                }
            }

            if (!connected || targetsToInitialSync == null || targetsToInitialSync.Count == 0)
            {
                return;
            }

            for (int i = 0; i < targetsToInitialSync.Count; i++)
            {
                SyncTargetConfigData target = targetsToInitialSync[i];
                ThreadPool.QueueUserWorkItem(_ => SyncFullStateToTarget(target));
            }
        }

        private void SyncFullStateToTarget(SyncTargetConfigData target)
        {
            if (target == null || !target.enabled)
            {
                return;
            }

            if (!TryBuildCurrentStatePatch(out StatePatch patch))
            {
                UpdateSyncState(target.id, true, false, "state_unavailable");
                return;
            }

            if (patch.entries == null)
            {
                patch.entries = new List<StatePatchEntry>();
            }

            string patchJson = JsonUtility.ToJson(patch);
            bool success = TrySendPatch(target, patchJson, out bool reachable, out string error);
            UpdateSyncState(target.id, reachable, success, error);
        }

        private bool TryBuildCurrentStatePatch(out StatePatch patch)
        {
            patch = null;
            if (controller == null)
            {
                return false;
            }

            if (!controller.InvokeOnMainThread(() => controller.BuildCurrentStatePatch(), out patch))
            {
                return false;
            }

            if (patch == null)
            {
                patch = new StatePatch { entries = new List<StatePatchEntry>() };
            }

            return true;
        }

        private void UpdateSyncState(string targetId, bool reachable, bool success, string error)
        {
            DateTime now = DateTime.UtcNow;
            lock (syncRoot)
            {
                SyncTargetRuntimeState state = GetOrCreateRuntimeStateNoLock(targetId);
                if (reachable)
                {
                    state.connected = true;
                    state.connectionError = string.Empty;
                }
                else if (!success)
                {
                    state.connected = false;
                    state.connectionError = string.IsNullOrWhiteSpace(error) ? "target_unreachable" : error;
                }

                state.lastCheckedUtc = now;
                if (success)
                {
                    state.lastSyncedUtc = now;
                    state.syncError = string.Empty;
                }
                else
                {
                    state.syncError = string.IsNullOrWhiteSpace(error) ? "sync_failed" : error;
                }
            }
        }

        private void UpdateTargetToken(string targetId, string token, string expiresAtUtc)
        {
            lock (syncRoot)
            {
                SyncTargetConfigData target = FindByIdNoLock(targetId);
                if (target == null)
                {
                    return;
                }

                target.accessToken = token ?? string.Empty;
                target.tokenExpiresAtUtc = expiresAtUtc ?? string.Empty;
                SaveConfigNoLock();
            }
        }

        private static bool TryProbeHealth(string address, out string error)
        {
            error = string.Empty;
            try
            {
                HttpWebRequest request = CreateRequest($"{address}/health", "GET", HealthTimeoutMs);
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode >= 200 && statusCode < 300)
                    {
                        return true;
                    }

                    error = $"health_http_{statusCode}";
                    return false;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response == null)
                {
                    error = "target_unreachable";
                    return false;
                }

                using (response)
                {
                    error = $"health_http_{(int)response.StatusCode}";
                    return false;
                }
            }
            catch
            {
                error = "health_failed";
                return false;
            }
        }

        private static HttpWebRequest CreateRequest(string url, string method, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.KeepAlive = false;
            return request;
        }

        private static bool IsTokenExpiredSoon(string expiresAtUtc)
        {
            if (string.IsNullOrWhiteSpace(expiresAtUtc))
            {
                return true;
            }

            if (!DateTime.TryParse(
                    expiresAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTime parsed))
            {
                return true;
            }

            return parsed <= DateTime.UtcNow.AddSeconds(20);
        }

        private bool TryNormalizeAddress(string address, out string normalizedAddress, out string error)
        {
            normalizedAddress = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(address))
            {
                error = "address_required";
                return false;
            }

            string candidate = address.Trim();
            if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                candidate = $"http://{candidate}";
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri uri))
            {
                error = "invalid_address";
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "invalid_address";
                return false;
            }

            string scheme = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? Uri.UriSchemeHttps
                : Uri.UriSchemeHttp;
            int port = uri.IsDefaultPort
                ? (scheme == Uri.UriSchemeHttps ? 443 : defaultPort)
                : uri.Port;

            var builder = new UriBuilder(scheme, uri.Host, port);
            normalizedAddress = builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            return true;
        }

        private bool TryLogin(string baseAddress, string password, out string token, out string expiresAtUtc, out string error)
        {
            token = string.Empty;
            expiresAtUtc = string.Empty;
            error = string.Empty;
            try
            {
                HttpWebRequest request = CreateRequest($"{baseAddress}/auth/login", "POST", PatchTimeoutMs);
                request.ContentType = "application/json; charset=utf-8";
                string body = JsonUtility.ToJson(new RemoteAuthLoginRequest { password = password ?? string.Empty });
                byte[] payload = Encoding.UTF8.GetBytes(body);
                request.ContentLength = payload.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(payload, 0, payload.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var responseStream = response.GetResponseStream())
                using (var reader = new StreamReader(responseStream ?? Stream.Null, Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    int statusCode = (int)response.StatusCode;
                    if (statusCode < 200 || statusCode >= 300)
                    {
                        error = $"auth_http_{statusCode}";
                        return false;
                    }

                    RemoteAuthTokenResponse parsed = JsonUtility.FromJson<RemoteAuthTokenResponse>(json);
                    if (parsed == null || string.IsNullOrWhiteSpace(parsed.token))
                    {
                        error = "auth_token_missing";
                        return false;
                    }

                    token = parsed.token;
                    DateTime expiry = DateTime.UtcNow.AddSeconds(Math.Max(30, parsed.expiresInSeconds));
                    expiresAtUtc = expiry.ToString("O");
                    return true;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response == null)
                {
                    error = "auth_target_unreachable";
                    return false;
                }

                using (response)
                using (var responseStream = response.GetResponseStream())
                using (var reader = new StreamReader(responseStream ?? Stream.Null, Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    RemoteAuthErrorResponse parsed = null;
                    try
                    {
                        parsed = JsonUtility.FromJson<RemoteAuthErrorResponse>(json);
                    }
                    catch
                    {
                    }

                    if (parsed != null && !string.IsNullOrWhiteSpace(parsed.error))
                    {
                        error = parsed.error;
                        return false;
                    }

                    error = $"auth_http_{(int)response.StatusCode}";
                    return false;
                }
            }
            catch
            {
                error = "auth_failed";
                return false;
            }
        }

        private void LoadConfig()
        {
            lock (syncRoot)
            {
                try
                {
                    targets.Clear();
                    if (!File.Exists(syncTargetFilePath))
                    {
                        return;
                    }

                    string json = File.ReadAllText(syncTargetFilePath);
                    SyncTargetFileData loaded = JsonUtility.FromJson<SyncTargetFileData>(json);
                    if (loaded?.targets == null)
                    {
                        return;
                    }

                    for (int i = 0; i < loaded.targets.Count; i++)
                    {
                        SyncTargetConfigData item = loaded.targets[i];
                        if (item == null || string.IsNullOrWhiteSpace(item.address))
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(item.id))
                        {
                            item.id = Guid.NewGuid().ToString("N");
                        }

                        item.address = item.address.Trim().TrimEnd('/');
                        targets.Add(item);
                        GetOrCreateRuntimeStateNoLock(item.id);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[URP Remote PP] Failed to load syncTargets.json: {ex.Message}");
                    targets.Clear();
                    runtimeStatesById.Clear();
                }
            }
        }

        private void SaveConfigNoLock()
        {
            string dir = Path.GetDirectoryName(syncTargetFilePath) ?? string.Empty;
            Directory.CreateDirectory(dir);
            var data = new SyncTargetFileData
            {
                targets = new List<SyncTargetConfigData>(targets.Count)
            };

            for (int i = 0; i < targets.Count; i++)
            {
                data.targets.Add(CloneConfig(targets[i]));
            }

            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(syncTargetFilePath, json);
        }

        private SyncTargetRuntimeState GetOrCreateRuntimeStateNoLock(string targetId)
        {
            if (!runtimeStatesById.TryGetValue(targetId, out SyncTargetRuntimeState state))
            {
                state = new SyncTargetRuntimeState();
                runtimeStatesById[targetId] = state;
            }

            return state;
        }

        private SyncTargetConfigData FindByAddressNoLock(string address)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (string.Equals(targets[i].address, address, StringComparison.OrdinalIgnoreCase))
                {
                    return targets[i];
                }
            }

            return null;
        }

        private SyncTargetConfigData FindByIdNoLock(string targetId)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (string.Equals(targets[i].id, targetId, StringComparison.Ordinal))
                {
                    return targets[i];
                }
            }

            return null;
        }

        private static SyncTargetConfigData CloneConfig(SyncTargetConfigData source)
        {
            return new SyncTargetConfigData
            {
                id = source.id,
                address = source.address,
                password = source.password,
                accessToken = source.accessToken,
                tokenExpiresAtUtc = source.tokenExpiresAtUtc,
                enabled = source.enabled
            };
        }

        private enum PatchSendResult
        {
            Success,
            Unauthorized,
            Failed
        }

        [Serializable]
        private sealed class SyncTargetFileData
        {
            public List<SyncTargetConfigData> targets = new List<SyncTargetConfigData>();
        }

        [Serializable]
        private sealed class SyncTargetConfigData
        {
            public string id = string.Empty;
            public string address = string.Empty;
            public string password = string.Empty;
            public string accessToken = string.Empty;
            public string tokenExpiresAtUtc = string.Empty;
            public bool enabled = true;
        }

        private sealed class SyncTargetRuntimeState
        {
            public bool connected;
            public string connectionError = string.Empty;
            public string syncError = string.Empty;
            public DateTime lastCheckedUtc = DateTime.MinValue;
            public DateTime lastSyncedUtc = DateTime.MinValue;
        }

        [Serializable]
        private sealed class RemoteAuthLoginRequest
        {
            public string password = string.Empty;
        }

        [Serializable]
        private sealed class RemoteAuthTokenResponse
        {
            public bool ok = true;
            public string token = string.Empty;
            public int expiresInSeconds;
        }

        [Serializable]
        private sealed class RemoteAuthErrorResponse
        {
            public bool ok = false;
            public string error = string.Empty;
        }
    }

    [Serializable]
    public sealed class SyncTargetSnapshot
    {
        public string id = string.Empty;
        public string address = string.Empty;
        public bool enabled;
        public bool connected;
        public string lastError = string.Empty;
        public string lastCheckedUtc = string.Empty;
        public string lastSyncedUtc = string.Empty;
    }
}
