using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Core;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Model;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Serialization;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Transport;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Bootstrap
{
    public sealed class RemotePostprocessController : MonoBehaviour
    {
        private const string SelectedPresetPlayerPrefsKey = "cc.sugi.urp-remote-postprocess.selectedPreset";

        [Header("Target Volumes")]
        [SerializeField] private List<Volume> targetVolumes = new List<Volume>();

        [Header("Server Settings")]
        [SerializeField] private int port = 8080;
        [SerializeField] private string defaultPresetName = "default";
        [SerializeField] private bool autoStartOnAwake = true;
        [SerializeField] private bool forceRunInBackground = true;

        private VolumeParameterAdapterRegistry adapterRegistry;
        private VolumeProfileScanner scanner;
        private VolumeStateApplier stateApplier;
        private PresetRepository presetRepository;
        private HttpApiServer httpApiServer;
        private WsEventHub wsEventHub;
        private ScanResult scanResult;
        private string cachedSchemaJson = "{}";
        private readonly Queue<Action> mainThreadActions = new Queue<Action>();
        private readonly object actionLock = new object();
        private int mainThreadId;
        private bool isCoreInitialized;
        private string selectedPresetNameCache = string.Empty;
        private string remotePostprocessRootPath = string.Empty;

        public RemoteSchema CurrentSchema => scanResult != null ? scanResult.schema : null;
        public int Port => port;

        private void Awake()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            if (autoStartOnAwake)
            {
                Initialize();
            }
        }

        private void OnDestroy()
        {
            httpApiServer?.Stop();
            wsEventHub?.Dispose();
            httpApiServer = null;
            wsEventHub = null;
            isCoreInitialized = false;
        }

        private void Update()
        {
            ExecutePendingActions();
        }

        public void Initialize()
        {
            EnsureCoreInitialized(startServer: true);
        }

        public void ApplyPatch(StatePatch patch)
        {
            EnsureCoreInitialized(startServer: false);
            stateApplier.Apply(patch);
            wsEventHub.PublishStateUpdated(patch);
        }

        public bool SavePreset(string presetName)
        {
            EnsureCoreInitialized(startServer: false);
            presetName = SanitizePresetName(presetName);
            if (string.IsNullOrWhiteSpace(presetName) || scanResult == null)
            {
                return false;
            }

            var data = new PresetData
            {
                schemaVersion = scanResult.schema.version,
                createdAtUtc = DateTime.UtcNow.ToString("O"),
                updatedAtUtc = DateTime.UtcNow.ToString("O"),
                entries = BuildCurrentEntries()
            };

            bool saved = presetRepository.Save(data, presetName);
            if (saved)
            {
                SetSelectedPresetName(presetName);
            }

            return saved;
        }

        public bool LoadPreset(string presetName)
        {
            EnsureCoreInitialized(startServer: false);
            presetName = SanitizePresetName(presetName);
            PresetData data = presetRepository.Load(presetName);
            if (data == null)
            {
                return false;
            }

            var patch = new StatePatch { entries = data.entries ?? new List<StatePatchEntry>() };
            ApplyPatch(patch);
            SetSelectedPresetName(presetName);
            wsEventHub.PublishPresetLoaded(presetName);
            return true;
        }

        public bool DeletePreset(string presetName)
        {
            EnsureCoreInitialized(startServer: false);
            presetName = SanitizePresetName(presetName);
            if (string.IsNullOrEmpty(presetName))
            {
                return false;
            }

            bool deleted = presetRepository.Delete(presetName);
            if (!deleted)
            {
                return false;
            }

            if (string.Equals(selectedPresetNameCache, presetName, StringComparison.Ordinal))
            {
                string fallback = FindFallbackPresetName();
                if (!string.IsNullOrEmpty(fallback))
                {
                    SetSelectedPresetName(fallback);
                }
                else
                {
                    selectedPresetNameCache = string.Empty;
                    PlayerPrefs.DeleteKey(SelectedPresetPlayerPrefsKey);
                    PlayerPrefs.Save();
                }
            }

            return true;
        }

        public bool RenamePreset(string fromPresetName, string toPresetName)
        {
            EnsureCoreInitialized(startServer: false);
            fromPresetName = SanitizePresetName(fromPresetName);
            toPresetName = SanitizePresetName(toPresetName);
            if (string.IsNullOrEmpty(fromPresetName) || string.IsNullOrEmpty(toPresetName))
            {
                return false;
            }

            bool renamed = presetRepository.Rename(fromPresetName, toPresetName);
            if (!renamed)
            {
                return false;
            }

            if (string.Equals(selectedPresetNameCache, fromPresetName, StringComparison.Ordinal))
            {
                SetSelectedPresetName(toPresetName);
            }

            return true;
        }

        public IReadOnlyList<string> ListPresets()
        {
            EnsureCoreInitialized(startServer: false);
            return presetRepository.ListPresets();
        }

        public StatePatch BuildCurrentStatePatch()
        {
            EnsureCoreInitialized(startServer: false);
            return new StatePatch { entries = BuildCurrentEntries() };
        }

        public string GetSchemaJson(bool prettyPrint = false)
        {
            EnsureCoreInitialized(startServer: false);
            if (!prettyPrint)
            {
                return cachedSchemaJson ?? "{}";
            }

            return SchemaSerializer.ToJson(CurrentSchema, true);
        }

        public string GetSelectedPresetName()
        {
            return selectedPresetNameCache ?? string.Empty;
        }

        public void SetSelectedPresetName(string presetName)
        {
            EnsureCoreInitialized(startServer: false);
            presetName = SanitizePresetName(presetName);
            if (string.IsNullOrEmpty(presetName))
            {
                return;
            }

            selectedPresetNameCache = presetName;
            PlayerPrefs.SetString(SelectedPresetPlayerPrefsKey, presetName);
            PlayerPrefs.Save();
        }

#if UNITY_EDITOR
        [ContextMenu("Apply Selected Preset To Current Profile (Edit Mode)")]
        public void ApplySelectedPresetToCurrentProfileInEditMode()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[URP Remote PP] Use this action in EditMode.");
                return;
            }

            string targetPreset = ResolveStartupPresetName();
            bool applied = LoadPreset(targetPreset);
            if (!applied)
            {
                Debug.LogWarning($"[URP Remote PP] Preset not found: {targetPreset}");
                return;
            }

            MarkProfilesDirtyAndSave();
            Debug.Log($"[URP Remote PP] Applied preset in EditMode: {targetPreset}");
        }
#endif

        private void EnsureCoreInitialized(bool startServer)
        {
            if (isCoreInitialized)
            {
                if (startServer && (httpApiServer == null || !httpApiServer.IsRunning))
                {
                    if (httpApiServer == null)
                    {
                        if (string.IsNullOrEmpty(remotePostprocessRootPath))
                        {
                            remotePostprocessRootPath = GetRemotePostprocessRootPath();
                        }
                        httpApiServer = new HttpApiServer(port, this, wsEventHub, remotePostprocessRootPath);
                    }
                    httpApiServer.Start();
                }
                return;
            }

            if (mainThreadId == 0)
            {
                mainThreadId = Thread.CurrentThread.ManagedThreadId;
            }

            if (forceRunInBackground && Application.isPlaying)
            {
                Application.runInBackground = true;
            }

            adapterRegistry = new VolumeParameterAdapterRegistry();
            scanner = new VolumeProfileScanner(adapterRegistry);
            scanResult = scanner.Scan(targetVolumes);
            cachedSchemaJson = SchemaSerializer.ToJson(scanResult.schema, false);
            stateApplier = new VolumeStateApplier(adapterRegistry, scanResult.bindingsByPath);
            presetRepository = new PresetRepository();
            wsEventHub = new WsEventHub();
            selectedPresetNameCache = SanitizePresetName(PlayerPrefs.GetString(SelectedPresetPlayerPrefsKey, string.Empty));
            remotePostprocessRootPath = GetRemotePostprocessRootPath();
            isCoreInitialized = true;

            TryLoadStartupPreset();
            if (startServer)
            {
                httpApiServer = new HttpApiServer(port, this, wsEventHub, remotePostprocessRootPath);
                httpApiServer.Start();
            }
        }

        public bool InvokeOnMainThread(Action action, int timeoutMs = 3000)
        {
            if (action == null)
            {
                return false;
            }

            if (IsMainThread())
            {
                action();
                return true;
            }

            using (var waitHandle = new ManualResetEventSlim(false))
            {
                Exception capturedException = null;
                EnqueueMainThreadAction(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        capturedException = ex;
                    }
                    finally
                    {
                        waitHandle.Set();
                    }
                });

                if (!waitHandle.Wait(timeoutMs))
                {
                    return false;
                }

                if (capturedException != null)
                {
                    Debug.LogException(capturedException);
                    return false;
                }

                return true;
            }
        }

        public bool InvokeOnMainThread<T>(Func<T> func, out T result, int timeoutMs = 3000)
        {
            result = default;
            if (func == null)
            {
                return false;
            }

            if (IsMainThread())
            {
                result = func();
                return true;
            }

            using (var waitHandle = new ManualResetEventSlim(false))
            {
                Exception capturedException = null;
                T localResult = default;
                EnqueueMainThreadAction(() =>
                {
                    try
                    {
                        localResult = func();
                    }
                    catch (Exception ex)
                    {
                        capturedException = ex;
                    }
                    finally
                    {
                        waitHandle.Set();
                    }
                });

                if (!waitHandle.Wait(timeoutMs))
                {
                    return false;
                }

                if (capturedException != null)
                {
                    Debug.LogException(capturedException);
                    return false;
                }

                result = localResult;
                return true;
            }
        }

        private List<StatePatchEntry> BuildCurrentEntries()
        {
            var entries = new List<StatePatchEntry>();
            foreach (KeyValuePair<string, VolumeParameterBinding> pair in scanResult.bindingsByPath)
            {
                if (pair.Value.isComponentBinding)
                {
                    entries.Add(new StatePatchEntry
                    {
                        path = pair.Key,
                        hasOverrideState = true,
                        overrideState = pair.Value.component.active,
                        valueJson = "null"
                    });
                    continue;
                }

                IVolumeParameterAdapter adapter = adapterRegistry.Resolve(pair.Value.parameter);
                if (adapter == null)
                {
                    continue;
                }

                entries.Add(new StatePatchEntry
                {
                    path = pair.Key,
                    valueJson = adapter.ReadCurrentValueJson(pair.Value.parameter),
                    hasOverrideState = true,
                    overrideState = pair.Value.parameter.overrideState
                });
            }

            return entries;
        }

        private void TryLoadStartupPreset()
        {
            string presetName = ResolveStartupPresetName();
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return;
            }

            bool loaded = LoadPreset(presetName);
            if (!loaded)
            {
                string fallback = SanitizePresetName(defaultPresetName);
                if (!string.IsNullOrEmpty(fallback) && !string.Equals(fallback, presetName, StringComparison.Ordinal))
                {
                    LoadPreset(fallback);
                }
            }
        }

        private string ResolveStartupPresetName()
        {
            string selected = SanitizePresetName(selectedPresetNameCache);
            if (!string.IsNullOrEmpty(selected))
            {
                return selected;
            }

            return SanitizePresetName(defaultPresetName);
        }

        private string FindFallbackPresetName()
        {
            IReadOnlyList<string> names = presetRepository.ListPresets();
            if (names.Count == 0)
            {
                return string.Empty;
            }

            string fallback = SanitizePresetName(defaultPresetName);
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], fallback, StringComparison.Ordinal))
                {
                    return fallback;
                }
            }

            return names[0];
        }

        private static string SanitizePresetName(string presetName)
        {
            return string.IsNullOrWhiteSpace(presetName) ? string.Empty : presetName.Trim();
        }

        private static string GetRemotePostprocessRootPath()
        {
            return Path.Combine(Application.persistentDataPath, "RemotePostprocess");
        }

#if UNITY_EDITOR
        private void MarkProfilesDirtyAndSave()
        {
            for (int i = 0; i < targetVolumes.Count; i++)
            {
                Volume volume = targetVolumes[i];
                VolumeProfile profile = volume != null ? (volume.sharedProfile != null ? volume.sharedProfile : volume.profile) : null;
                if (profile == null)
                {
                    continue;
                }

                EditorUtility.SetDirty(profile);
            }

            AssetDatabase.SaveAssets();
        }
#endif

        private bool IsMainThread()
        {
            return Thread.CurrentThread.ManagedThreadId == mainThreadId;
        }

        private void EnqueueMainThreadAction(Action action)
        {
            lock (actionLock)
            {
                mainThreadActions.Enqueue(action);
            }
        }

        private void ExecutePendingActions()
        {
            List<Action> actions = null;
            lock (actionLock)
            {
                while (mainThreadActions.Count > 0)
                {
                    if (actions == null)
                    {
                        actions = new List<Action>(mainThreadActions.Count);
                    }

                    actions.Add(mainThreadActions.Dequeue());
                }
            }

            if (actions == null)
            {
                return;
            }

            for (int i = 0; i < actions.Count; i++)
            {
                actions[i]?.Invoke();
            }
        }
    }
}
