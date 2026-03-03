using Cc.Sugi.UrpRemotePostprocess.Runtime.Model;
using UnityEngine;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Serialization
{
    public static class SchemaSerializer
    {
        public static string ToJson(RemoteSchema schema, bool prettyPrint = false)
        {
            if (schema == null)
            {
                return "{}";
            }

            return JsonUtility.ToJson(schema, prettyPrint);
        }
    }
}
