using System.Text;
using System.Collections.Generic;
using UnityEngine;

namespace Cc.Sugi.UrpRemotePostprocess.Runtime.Web
{
    public sealed class WebUiAssetProvider
    {
        private readonly Dictionary<string, CachedAsset> assets = new Dictionary<string, CachedAsset>();

        public WebUiAssetProvider()
        {
            CacheTextAsset("/index.html", "UrpRemoteWebUI/index_html", "text/html; charset=utf-8");
            CacheTextAsset("/style.css", "UrpRemoteWebUI/style_css", "text/css; charset=utf-8");
            CacheTextAsset("/app.js", "UrpRemoteWebUI/app_js", "application/javascript; charset=utf-8");
        }

        public bool TryGetAsset(string requestPath, out byte[] payload, out string contentType)
        {
            string normalizedPath = string.IsNullOrEmpty(requestPath) ? "/" : requestPath;
            if (normalizedPath == "/")
            {
                normalizedPath = "/index.html";
            }

            if (assets.TryGetValue(normalizedPath, out CachedAsset cached))
            {
                payload = cached.payload;
                contentType = cached.contentType;
                return true;
            }

            payload = null;
            contentType = "text/plain";
            return false;
        }

        private void CacheTextAsset(string requestPath, string resourcePath, string assetContentType)
        {
            TextAsset asset = Resources.Load<TextAsset>(resourcePath);
            if (asset == null)
            {
                assets[requestPath] = new CachedAsset(
                    Encoding.UTF8.GetBytes($"Missing resource: {resourcePath}"),
                    "text/plain; charset=utf-8");
                return;
            }

            assets[requestPath] = new CachedAsset(Encoding.UTF8.GetBytes(asset.text), assetContentType);
        }

        private readonly struct CachedAsset
        {
            public CachedAsset(byte[] payload, string contentType)
            {
                this.payload = payload;
                this.contentType = contentType;
            }

            public readonly byte[] payload;
            public readonly string contentType;
        }
    }
}
