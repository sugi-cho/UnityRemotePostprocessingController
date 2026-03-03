using System.Collections.Generic;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Model;
using UnityEngine;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Core
{
    public sealed class VolumeStateApplier
    {
        private readonly VolumeParameterAdapterRegistry adapterRegistry;
        private readonly Dictionary<string, VolumeParameterBinding> bindingsByPath;

        public VolumeStateApplier(
            VolumeParameterAdapterRegistry adapterRegistry,
            Dictionary<string, VolumeParameterBinding> bindingsByPath)
        {
            this.adapterRegistry = adapterRegistry;
            this.bindingsByPath = bindingsByPath;
        }

        public void Apply(StatePatch patch)
        {
            if (patch == null || patch.entries == null)
            {
                return;
            }

            for (int i = 0; i < patch.entries.Count; i++)
            {
                ApplyEntry(patch.entries[i]);
            }
        }

        private void ApplyEntry(StatePatchEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.path))
            {
                return;
            }

            if (!bindingsByPath.TryGetValue(entry.path, out VolumeParameterBinding binding))
            {
                Debug.LogWarning($"[URP Remote PP] Unknown path: {entry.path}");
                return;
            }

            if (binding.isComponentBinding)
            {
                if (entry.hasOverrideState)
                {
                    binding.component.active = entry.overrideState;
                }
                return;
            }

            if (entry.hasOverrideState)
            {
                binding.parameter.overrideState = entry.overrideState;
            }

            IVolumeParameterAdapter adapter = adapterRegistry.Resolve(binding.parameter);
            if (adapter == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(entry.valueJson) || entry.valueJson == "null")
            {
                return;
            }

            if (!adapter.TryWriteFromJson(binding.parameter, entry.valueJson))
            {
                Debug.LogWarning($"[URP Remote PP] Invalid value for path: {entry.path}");
            }
        }
    }
}
