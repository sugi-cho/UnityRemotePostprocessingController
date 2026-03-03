using System;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Core
{
    public static class ParameterPathResolver
    {
        public static string BuildPath(string volumeId, Type componentType, string parameterName)
        {
            return $"{volumeId}/{componentType.Name}/{parameterName}";
        }
    }
}
