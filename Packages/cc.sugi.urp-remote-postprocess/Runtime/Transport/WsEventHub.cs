using System;
using Cc.Sugi.UrpRemotePostprocess.Runtime.Model;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Transport
{
    public sealed class WsEventHub : IDisposable
    {
        public event Action<StatePatch> StateUpdated;
        public event Action<string> PresetLoaded;
        public event Action<string> Error;

        public void PublishStateUpdated(StatePatch patch)
        {
            StateUpdated?.Invoke(patch);
        }

        public void PublishPresetLoaded(string presetName)
        {
            PresetLoaded?.Invoke(presetName);
        }

        public void PublishError(string message)
        {
            Error?.Invoke(message ?? string.Empty);
        }

        public void Dispose()
        {
            StateUpdated = null;
            PresetLoaded = null;
            Error = null;
        }
    }
}
