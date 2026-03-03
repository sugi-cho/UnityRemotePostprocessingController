using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Core
{
    internal sealed class EnumParameterAdapter : IVolumeParameterAdapter
    {
        public string ParameterType => "enum";

        public bool CanHandle(VolumeParameter parameter)
        {
            return TryGetEnumType(parameter, out _);
        }

        public string ReadCurrentValueJson(VolumeParameter parameter)
        {
            if (!TryGetValueMember(parameter, out MemberInfo member))
            {
                return "0";
            }

            object value = GetMemberValue(member, parameter);
            if (value == null)
            {
                return "0";
            }

            int intValue = (int)System.Convert.ChangeType(value, typeof(int), CultureInfo.InvariantCulture);
            return intValue.ToString(CultureInfo.InvariantCulture);
        }

        public bool TryWriteFromJson(VolumeParameter parameter, string valueJson)
        {
            if (!TryGetEnumType(parameter, out Type enumType))
            {
                return false;
            }

            if (!int.TryParse(valueJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return false;
            }

            if (!TryGetValueMember(parameter, out MemberInfo member))
            {
                return false;
            }

            object enumValue = System.Enum.ToObject(enumType, parsed);
            SetMemberValue(member, parameter, enumValue);
            return true;
        }

        public static bool TryGetEnumType(VolumeParameter parameter, out Type enumType)
        {
            enumType = null;
            if (parameter == null)
            {
                return false;
            }

            Type currentType = parameter.GetType();
            while (currentType != null)
            {
                if (currentType.IsGenericType && currentType.GetGenericTypeDefinition() == typeof(VolumeParameter<>))
                {
                    Type arg = currentType.GetGenericArguments()[0];
                    if (arg.IsEnum)
                    {
                        enumType = arg;
                        return true;
                    }
                }

                currentType = currentType.BaseType;
            }

            return false;
        }

        private static bool TryGetValueMember(VolumeParameter parameter, out MemberInfo member)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            Type currentType = parameter.GetType();
            while (currentType != null)
            {
                FieldInfo field = currentType.GetField("value", flags);
                if (field != null)
                {
                    member = field;
                    return true;
                }

                PropertyInfo property = currentType.GetProperty("value", flags);
                if (property != null && property.CanRead && property.CanWrite)
                {
                    member = property;
                    return true;
                }

                currentType = currentType.BaseType;
            }

            member = null;
            return false;
        }

        private static object GetMemberValue(MemberInfo member, object target)
        {
            if (member is FieldInfo field)
            {
                return field.GetValue(target);
            }

            if (member is PropertyInfo property)
            {
                return property.GetValue(target, null);
            }

            return null;
        }

        private static void SetMemberValue(MemberInfo member, object target, object value)
        {
            if (member is FieldInfo field)
            {
                field.SetValue(target, value);
                return;
            }

            if (member is PropertyInfo property)
            {
                property.SetValue(target, value, null);
            }
        }
    }

    internal sealed class BoolParameterAdapter : IVolumeParameterAdapter
    {
        public string ParameterType => "bool";

        public bool CanHandle(VolumeParameter parameter) => parameter is BoolParameter;

        public string ReadCurrentValueJson(VolumeParameter parameter)
        {
            return ((BoolParameter)parameter).value ? "true" : "false";
        }

        public bool TryWriteFromJson(VolumeParameter parameter, string valueJson)
        {
            if (!(parameter is BoolParameter typed))
            {
                return false;
            }

            if (valueJson == "true")
            {
                typed.value = true;
                return true;
            }

            if (valueJson == "false")
            {
                typed.value = false;
                return true;
            }

            return false;
        }
    }

    internal sealed class IntParameterAdapter : IVolumeParameterAdapter
    {
        public string ParameterType => "int";

        public bool CanHandle(VolumeParameter parameter)
        {
            return parameter is IntParameter || parameter is ClampedIntParameter;
        }

        public string ReadCurrentValueJson(VolumeParameter parameter)
        {
            if (parameter is IntParameter intParam)
            {
                return intParam.value.ToString(CultureInfo.InvariantCulture);
            }

            return "0";
        }

        public bool TryWriteFromJson(VolumeParameter parameter, string valueJson)
        {
            if (!(parameter is IntParameter typed))
            {
                return false;
            }

            if (int.TryParse(valueJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                typed.value = parsed;
                return true;
            }

            return false;
        }
    }

    internal sealed class FloatParameterAdapter : IVolumeParameterAdapter
    {
        public string ParameterType => "float";

        public bool CanHandle(VolumeParameter parameter)
        {
            return parameter is FloatParameter || parameter is ClampedFloatParameter || parameter is MinFloatParameter;
        }

        public string ReadCurrentValueJson(VolumeParameter parameter)
        {
            if (parameter is FloatParameter floatParam)
            {
                return floatParam.value.ToString(CultureInfo.InvariantCulture);
            }

            return "0";
        }

        public bool TryWriteFromJson(VolumeParameter parameter, string valueJson)
        {
            if (!(parameter is FloatParameter typed))
            {
                return false;
            }

            if (float.TryParse(valueJson, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {
                typed.value = parsed;
                return true;
            }

            return false;
        }
    }

    internal sealed class ColorParameterAdapter : IVolumeParameterAdapter
    {
        public string ParameterType => "color";

        public bool CanHandle(VolumeParameter parameter) => parameter is ColorParameter;

        public string ReadCurrentValueJson(VolumeParameter parameter)
        {
            Color value = ((ColorParameter)parameter).value;
            return JsonUtility.ToJson(new ColorDto(value));
        }

        public bool TryWriteFromJson(VolumeParameter parameter, string valueJson)
        {
            if (!(parameter is ColorParameter typed))
            {
                return false;
            }

            try
            {
                ColorDto dto = JsonUtility.FromJson<ColorDto>(valueJson);
                typed.value = new Color(dto.r, dto.g, dto.b, dto.a);
                return true;
            }
            catch
            {
                return false;
            }
        }

        [System.Serializable]
        private struct ColorDto
        {
            public float r;
            public float g;
            public float b;
            public float a;

            public ColorDto(Color color)
            {
                r = color.r;
                g = color.g;
                b = color.b;
                a = color.a;
            }
        }
    }

    internal sealed class Vector2ParameterAdapter : IVolumeParameterAdapter
    {
        public string ParameterType => "vector2";

        public bool CanHandle(VolumeParameter parameter) => parameter is Vector2Parameter;

        public string ReadCurrentValueJson(VolumeParameter parameter)
        {
            Vector2 value = ((Vector2Parameter)parameter).value;
            return JsonUtility.ToJson(new Vector2Dto(value));
        }

        public bool TryWriteFromJson(VolumeParameter parameter, string valueJson)
        {
            if (!(parameter is Vector2Parameter typed))
            {
                return false;
            }

            try
            {
                Vector2Dto dto = JsonUtility.FromJson<Vector2Dto>(valueJson);
                typed.value = new Vector2(dto.x, dto.y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        [System.Serializable]
        private struct Vector2Dto
        {
            public float x;
            public float y;

            public Vector2Dto(Vector2 value)
            {
                x = value.x;
                y = value.y;
            }
        }
    }

    internal sealed class Vector3ParameterAdapter : IVolumeParameterAdapter
    {
        public string ParameterType => "vector3";

        public bool CanHandle(VolumeParameter parameter) => parameter is Vector3Parameter;

        public string ReadCurrentValueJson(VolumeParameter parameter)
        {
            Vector3 value = ((Vector3Parameter)parameter).value;
            return JsonUtility.ToJson(new Vector3Dto(value));
        }

        public bool TryWriteFromJson(VolumeParameter parameter, string valueJson)
        {
            if (!(parameter is Vector3Parameter typed))
            {
                return false;
            }

            try
            {
                Vector3Dto dto = JsonUtility.FromJson<Vector3Dto>(valueJson);
                typed.value = new Vector3(dto.x, dto.y, dto.z);
                return true;
            }
            catch
            {
                return false;
            }
        }

        [System.Serializable]
        private struct Vector3Dto
        {
            public float x;
            public float y;
            public float z;

            public Vector3Dto(Vector3 value)
            {
                x = value.x;
                y = value.y;
                z = value.z;
            }
        }
    }

    internal sealed class Vector4ParameterAdapter : IVolumeParameterAdapter
    {
        public string ParameterType => "vector4";

        public bool CanHandle(VolumeParameter parameter) => parameter is Vector4Parameter;

        public string ReadCurrentValueJson(VolumeParameter parameter)
        {
            Vector4 value = ((Vector4Parameter)parameter).value;
            return JsonUtility.ToJson(new Vector4Dto(value));
        }

        public bool TryWriteFromJson(VolumeParameter parameter, string valueJson)
        {
            if (!(parameter is Vector4Parameter typed))
            {
                return false;
            }

            try
            {
                Vector4Dto dto = JsonUtility.FromJson<Vector4Dto>(valueJson);
                typed.value = new Vector4(dto.x, dto.y, dto.z, dto.w);
                return true;
            }
            catch
            {
                return false;
            }
        }

        [System.Serializable]
        private struct Vector4Dto
        {
            public float x;
            public float y;
            public float z;
            public float w;

            public Vector4Dto(Vector4 value)
            {
                x = value.x;
                y = value.y;
                z = value.z;
                w = value.w;
            }
        }
    }
}
