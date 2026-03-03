using System;
using System.Collections.Generic;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Model
{
    [Serializable]
    public sealed class StatePatch
    {
        public List<StatePatchEntry> entries = new List<StatePatchEntry>();
    }

    [Serializable]
    public sealed class StatePatchEntry
    {
        public string path = string.Empty;
        public string valueJson = "null";
        public bool hasOverrideState;
        public bool overrideState;
    }

    [Serializable]
    public sealed class PresetData
    {
        public int schemaVersion = 1;
        public string createdAtUtc = string.Empty;
        public string updatedAtUtc = string.Empty;
        public List<StatePatchEntry> entries = new List<StatePatchEntry>();
    }
}
