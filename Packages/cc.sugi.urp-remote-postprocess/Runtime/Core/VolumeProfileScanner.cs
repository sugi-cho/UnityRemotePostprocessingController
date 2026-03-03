using System;
using System.Collections.Generic;
using System.Reflection;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Model;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Core
{
    public sealed class VolumeProfileScanner
    {
        private readonly VolumeParameterAdapterRegistry adapterRegistry;

        public VolumeProfileScanner(VolumeParameterAdapterRegistry adapterRegistry)
        {
            this.adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
        }

        public ScanResult Scan(IReadOnlyList<Volume> volumes)
        {
            var result = new ScanResult();
            if (volumes == null)
            {
                return result;
            }

            for (int i = 0; i < volumes.Count; i++)
            {
                Volume volume = volumes[i];
                VolumeProfile profile = GetProfileForContext(volume);
                if (volume == null || profile == null)
                {
                    continue;
                }

                RemoteVolumeSchema volumeSchema = BuildVolumeSchema(volume, profile, result.bindingsByPath);
                result.schema.volumes.Add(volumeSchema);
            }

            return result;
        }

        private RemoteVolumeSchema BuildVolumeSchema(Volume volume, VolumeProfile profile, Dictionary<string, VolumeParameterBinding> bindingsByPath)
        {
            var volumeSchema = new RemoteVolumeSchema
            {
                volumeId = string.IsNullOrEmpty(volume.gameObject.name) ? volume.GetInstanceID().ToString() : volume.gameObject.name,
                displayName = volume.gameObject.name
            };

            IReadOnlyList<VolumeComponent> components = profile.components;
            for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
            {
                VolumeComponent component = components[componentIndex];
                if (component == null)
                {
                    continue;
                }

                var componentSchema = new RemoteComponentSchema
                {
                    componentPath = BuildComponentPath(volumeSchema.volumeId, component.GetType()),
                    typeName = component.GetType().Name,
                    displayName = BuildDisplayName(component.GetType().Name),
                    overrideState = component.active
                };
                bindingsByPath[componentSchema.componentPath] = new VolumeParameterBinding(componentSchema.componentPath, component);

                IReadOnlyList<VolumeParameter> parameters = component.parameters;
                for (int parameterIndex = 0; parameterIndex < parameters.Count; parameterIndex++)
                {
                    VolumeParameter parameter = parameters[parameterIndex];
                    if (parameter == null)
                    {
                        continue;
                    }

                    IVolumeParameterAdapter adapter = adapterRegistry.Resolve(parameter);
                    if (adapter == null)
                    {
                        continue;
                    }

                    string parameterName = FindParameterName(component, parameter, parameterIndex);
                    string path = ParameterPathResolver.BuildPath(volumeSchema.volumeId, component.GetType(), parameterName);

                    var paramSchema = new RemoteParameterSchema
                    {
                        path = path,
                        name = BuildDisplayName(parameterName),
                        type = adapter.ParameterType,
                        overrideState = parameter.overrideState,
                        defaultValueJson = adapter.ReadCurrentValueJson(parameter),
                        currentValueJson = adapter.ReadCurrentValueJson(parameter)
                    };

                    FillRangeInfo(parameter, paramSchema);
                    FillEnumInfo(parameter, paramSchema);
                    componentSchema.parameters.Add(paramSchema);
                    bindingsByPath[path] = new VolumeParameterBinding(path, component, parameter, parameterName);
                }

                if (componentSchema.parameters.Count > 0)
                {
                    volumeSchema.components.Add(componentSchema);
                }
            }

            return volumeSchema;
        }

        private static string FindParameterName(VolumeComponent component, VolumeParameter target, int indexFallback)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo[] fields = component.GetType().GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!typeof(VolumeParameter).IsAssignableFrom(fields[i].FieldType))
                {
                    continue;
                }

                if (ReferenceEquals(fields[i].GetValue(component), target))
                {
                    return fields[i].Name;
                }
            }

            return $"param_{indexFallback}";
        }

        private static string BuildComponentPath(string volumeId, Type componentType)
        {
            return $"{volumeId}/{componentType.Name}/__component";
        }

        private static VolumeProfile GetProfileForContext(Volume volume)
        {
            if (volume == null)
            {
                return null;
            }

            if (Application.isPlaying)
            {
                return volume.profile;
            }

            return volume.sharedProfile != null ? volume.sharedProfile : volume.profile;
        }

        private static void FillRangeInfo(VolumeParameter parameter, RemoteParameterSchema schema)
        {
            if (parameter is ClampedFloatParameter clampedFloat)
            {
                schema.hasMin = true;
                schema.min = clampedFloat.min;
                schema.hasMax = true;
                schema.max = clampedFloat.max;
                schema.hasStep = true;
                schema.step = 0.01f;
                return;
            }

            if (parameter is ClampedIntParameter clampedInt)
            {
                schema.hasMin = true;
                schema.min = clampedInt.min;
                schema.hasMax = true;
                schema.max = clampedInt.max;
                schema.hasStep = true;
                schema.step = 1f;
                return;
            }

            if (parameter is MinFloatParameter minFloat)
            {
                schema.hasMin = true;
                schema.min = minFloat.min;
                schema.hasStep = true;
                schema.step = 0.01f;
            }
        }

        private static void FillEnumInfo(VolumeParameter parameter, RemoteParameterSchema schema)
        {
            if (schema.type != "enum")
            {
                return;
            }

            if (!EnumParameterAdapter.TryGetEnumType(parameter, out Type enumType))
            {
                return;
            }

            Array values = Enum.GetValues(enumType);
            string[] names = Enum.GetNames(enumType);
            var options = new EnumOptionsContainer { options = new List<EnumOption>(values.Length) };
            for (int i = 0; i < values.Length; i++)
            {
                int value = Convert.ToInt32(values.GetValue(i));
                options.options.Add(new EnumOption
                {
                    value = value,
                    label = BuildDisplayName(names[i])
                });
            }

            schema.enumOptionsJson = JsonUtility.ToJson(options);
        }

        private static string BuildDisplayName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName))
            {
                return string.Empty;
            }

            var chars = new List<char>(rawName.Length + 4);
            for (int i = 0; i < rawName.Length; i++)
            {
                char c = rawName[i];
                bool addSpace = i > 0 && char.IsUpper(c) && !char.IsUpper(rawName[i - 1]);
                if (addSpace)
                {
                    chars.Add(' ');
                }

                if (i == 0)
                {
                    chars.Add(char.ToUpperInvariant(c));
                }
                else
                {
                    chars.Add(c);
                }
            }

            return new string(chars.ToArray());
        }
    }

    public sealed class ScanResult
    {
        public readonly RemoteSchema schema = new RemoteSchema();
        public readonly Dictionary<string, VolumeParameterBinding> bindingsByPath = new Dictionary<string, VolumeParameterBinding>();
    }

    public sealed class VolumeParameterBinding
    {
        public VolumeParameterBinding(string path, VolumeComponent component)
        {
            this.path = path;
            this.component = component;
            parameter = null;
            parameterName = string.Empty;
            isComponentBinding = true;
        }

        public VolumeParameterBinding(string path, VolumeComponent component, VolumeParameter parameter, string parameterName)
        {
            this.path = path;
            this.component = component;
            this.parameter = parameter;
            this.parameterName = parameterName;
            isComponentBinding = false;
        }

        public readonly string path;
        public readonly VolumeComponent component;
        public readonly VolumeParameter parameter;
        public readonly string parameterName;
        public readonly bool isComponentBinding;
    }

    [Serializable]
    internal sealed class EnumOptionsContainer
    {
        public List<EnumOption> options = new List<EnumOption>();
    }

    [Serializable]
    internal sealed class EnumOption
    {
        public int value;
        public string label = string.Empty;
    }
}
