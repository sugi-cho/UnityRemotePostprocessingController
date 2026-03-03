using System;
using System.Collections.Generic;
using UnityEngine.Rendering;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Core
{
    public interface IVolumeParameterAdapter
    {
        bool CanHandle(VolumeParameter parameter);
        string ParameterType { get; }
        string ReadCurrentValueJson(VolumeParameter parameter);
        bool TryWriteFromJson(VolumeParameter parameter, string valueJson);
    }

    public sealed class VolumeParameterAdapterRegistry
    {
        private readonly List<IVolumeParameterAdapter> adapters = new List<IVolumeParameterAdapter>();

        public VolumeParameterAdapterRegistry()
        {
            RegisterBuiltInAdapters();
        }

        public void Register(IVolumeParameterAdapter adapter)
        {
            if (adapter == null)
            {
                throw new ArgumentNullException(nameof(adapter));
            }

            adapters.Add(adapter);
        }

        public IVolumeParameterAdapter Resolve(VolumeParameter parameter)
        {
            for (int i = 0; i < adapters.Count; i++)
            {
                if (adapters[i].CanHandle(parameter))
                {
                    return adapters[i];
                }
            }

            return null;
        }

        private void RegisterBuiltInAdapters()
        {
            Register(new BoolParameterAdapter());
            Register(new EnumParameterAdapter());
            Register(new IntParameterAdapter());
            Register(new FloatParameterAdapter());
            Register(new ColorParameterAdapter());
            Register(new Vector2ParameterAdapter());
            Register(new Vector3ParameterAdapter());
            Register(new Vector4ParameterAdapter());
        }
    }
}
