using System;
using System.Collections.Generic;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Model
{
    [Serializable]
    public sealed class RemoteSchema
    {
        public int version = 1;
        public List<RemoteVolumeSchema> volumes = new List<RemoteVolumeSchema>();
    }

    [Serializable]
    public sealed class RemoteVolumeSchema
    {
        public string volumeId = string.Empty;
        public string displayName = string.Empty;
        public string profileAssetGuid = string.Empty;
        public List<RemoteComponentSchema> components = new List<RemoteComponentSchema>();
    }

    [Serializable]
    public sealed class RemoteComponentSchema
    {
        public string componentPath = string.Empty;
        public string typeName = string.Empty;
        public string displayName = string.Empty;
        public bool overrideState;
        public List<RemoteParameterSchema> parameters = new List<RemoteParameterSchema>();
    }

    [Serializable]
    public sealed class RemoteParameterSchema
    {
        public string path = string.Empty;
        public string name = string.Empty;
        public string type = string.Empty;
        public bool overrideState;
        public bool hasMin;
        public float min;
        public bool hasMax;
        public float max;
        public bool hasStep;
        public float step;
        public string enumOptionsJson = "[]";
        public string defaultValueJson = "null";
        public string currentValueJson = "null";
    }
}
