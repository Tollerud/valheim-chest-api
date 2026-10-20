using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace ChestAPI
{
    internal class HttpApiServer
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly string _apiKey;
        private Thread _thread;
        private volatile bool _running;

        public HttpApiServer(string bindAddress, int port, string apiKey)
        {
            _apiKey = apiKey;
            _listener.Prefixes.Add($"http://{bindAddress}:{port}/");
        }

        public void Start()
        {
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ChestAPI-Http" };
            _thread.Start();
            Plugin.Log?.LogInfo($"[ChestAPI] HTTP API listening on {string.Join(", ", _listener.Prefixes)}");
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => Handle(context));
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        Plugin.Log?.LogWarning($"[ChestAPI] HTTP listener error: {ex.Message}");
                    }
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                if (!CheckAuth(context))
                {
                    WriteJson(context, 401, new ErrorResponse { Error = "Missing or invalid API key." });
                    return;
                }

                var path = context.Request.Url.AbsolutePath.TrimEnd('/');

                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(context, 200, new { status = "ok", utc = DateTime.UtcNow });
                    return;
                }

                if (path.Equals("/api/chests", StringComparison.OrdinalIgnoreCase))
                {
                    var chests = ChestRegistry.Instance.GetAll();
                    WriteJson(context, 200, new ChestListResponse
                    {
                        Count = chests.Count,
                        GeneratedAtUtc = DateTime.UtcNow,
                        Chests = chests,
                    });
                    return;
                }

                if (path.StartsWith("/api/chests/", StringComparison.OrdinalIgnoreCase))
                {
                    var id = Uri.UnescapeDataString(path.Substring("/api/chests/".Length));
                    var chest = ChestRegistry.Instance.GetById(id);
                    if (chest == null)
                    {
                        WriteJson(context, 404, new ErrorResponse { Error = $"No chest with id '{id}'." });
                    }
                    else
                    {
                        WriteJson(context, 200, chest);
                    }
                    return;
                }

                if (path.StartsWith("/api/items/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/count", StringComparison.OrdinalIgnoreCase))
                {
                    var prefab = path.Substring("/api/items/".Length, path.Length - "/api/items/".Length - "/count".Length);
                    prefab = Uri.UnescapeDataString(prefab);

                    var chests = ChestRegistry.Instance.GetAll();
                    var matchingItems = chests
                        .SelectMany(c => c.Items)
                        .Where(i => string.Equals(i.PrefabName, prefab, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    WriteJson(context, 200, new ItemCountResponse
                    {
                        PrefabName = prefab,
                        TotalStack = matchingItems.Sum(i => i.Stack),
                        ChestCount = chests.Count(c => c.Items.Any(i => string.Equals(i.PrefabName, prefab, StringComparison.OrdinalIgnoreCase))),
                        GeneratedAtUtc = DateTime.UtcNow,
                    });
                    return;
                }

                WriteJson(context, 404, new ErrorResponse { Error = "Unknown endpoint." });
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ChestAPI] Request handling error: {ex.Message}");
                try
                {
                    WriteJson(context, 500, new ErrorResponse { Error = "Internal error." });
                }
                catch { /* response may already be closed */ }
            }
        }

        private bool CheckAuth(HttpListenerContext context)
        {
            // Fail closed: with no token configured, nobody gets in - there is no "open" mode.
            if (string.IsNullOrEmpty(_apiKey)) return false;

            var provided = context.Request.Headers["X-Api-Key"];
            if (string.IsNullOrEmpty(provided)) return false;

            return FixedTimeEquals(provided, _apiKey);
        }

        /// <summary>Constant-time string comparison so response timing can't be used to guess the token byte by byte.</summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            var bytesA = Encoding.UTF8.GetBytes(a);
            var bytesB = Encoding.UTF8.GetBytes(b);

            var diff = bytesA.Length ^ bytesB.Length;
            for (var i = 0; i < Math.Min(bytesA.Length, bytesB.Length); i++)
            {
                diff |= bytesA[i] ^ bytesB[i];
            }

            return diff == 0;
        }

        private void WriteJson(HttpListenerContext context, int statusCode, object payload)
        {
            var json = JsonConvert.SerializeObject(payload, Formatting.None);
            var bytes = Encoding.UTF8.GetBytes(json);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }
    }
}
