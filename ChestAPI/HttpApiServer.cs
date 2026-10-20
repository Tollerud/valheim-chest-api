using System;
using System.Collections.Generic;
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
        private readonly Dictionary<string, Area> _areas;
        private Thread _thread;
        private volatile bool _running;

        public HttpApiServer(string bindAddress, int port, string apiKey, Dictionary<string, Area> areas)
        {
            _apiKey = apiKey;
            _areas = areas ?? new Dictionary<string, Area>(StringComparer.OrdinalIgnoreCase);
            // HttpListener (Mono) rejects any request whose Host header doesn't match the prefix host with
            // "400 Invalid host", so a literal 0.0.0.0 prefix breaks everything behind a reverse proxy.
            // Binding all interfaces needs the "*" wildcard prefix instead.
            var host = bindAddress == "0.0.0.0" || bindAddress == "::" ? "*" : bindAddress;
            _listener.Prefixes.Add($"http://{host}:{port}/");
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
                var query = context.Request.QueryString;

                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(context, 200, new { status = "ok", utc = DateTime.UtcNow });
                    return;
                }

                if (path.Equals("/api/areas", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(context, 200, _areas.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList());
                    return;
                }

                if (path.Equals("/api/players", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(context, 200, ChestAggregates.Players(ChestRegistry.Instance.GetAll(), ChestRegistry.Instance.Names));
                    return;
                }

                // /api/chests* covers every container the server knows about (dungeon and world chests too);
                // /api/playerchests* is the same set of endpoints limited to chests a player built.
                // The old /api/items/{prefab}/count is kept as an alias for /api/chests/items/{prefab}/count.
                string rest;
                bool playerOnly;
                if (TryStrip(path, "/api/playerchests", out rest)) playerOnly = true;
                else if (TryStrip(path, "/api/chests", out rest)) playerOnly = false;
                else if (path.StartsWith("/api/items/", StringComparison.OrdinalIgnoreCase)
                         && path.EndsWith("/count", StringComparison.OrdinalIgnoreCase))
                {
                    rest = "/items/" + path.Substring("/api/items/".Length);
                    playerOnly = false;
                }
                else
                {
                    WriteJson(context, 404, new ErrorResponse { Error = "Unknown endpoint." });
                    return;
                }

                HandleChestRoute(context, rest, playerOnly, query);
                return;
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

        private static bool TryStrip(string path, string prefix, out string rest)
        {
            rest = null;
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)) { rest = string.Empty; return true; }
            if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) { rest = path.Substring(prefix.Length); return true; }
            return false;
        }

        private void HandleChestRoute(HttpListenerContext context, string rest, bool playerOnly, System.Collections.Specialized.NameValueCollection query)
        {
            var registry = ChestRegistry.Instance;

            // GET .../{id}: one chest by its ZDOID. Takes no filters.
            if (rest.Length > 1 && !rest.StartsWith("/items", StringComparison.OrdinalIgnoreCase)
                && !rest.Equals("/check", StringComparison.OrdinalIgnoreCase))
            {
                var id = Uri.UnescapeDataString(rest.Substring(1));
                var chest = registry.GetById(id);
                if (chest == null || (playerOnly && !chest.PlayerPlaced))
                {
                    WriteJson(context, 404, new ErrorResponse { Error = $"No chest with id '{id}'." });
                }
                else
                {
                    WriteJson(context, 200, chest);
                }
                return;
            }

            var isCheck = rest.Equals("/check", StringComparison.OrdinalIgnoreCase);
            var extra = isCheck ? new[] { "need", "where" } : new string[0];

            var filter = ChestFilter.Parse(query, playerOnly, _areas, registry.Names, extra, out var error);
            if (filter == null)
            {
                WriteJson(context, 400, new ErrorResponse { Error = error });
                return;
            }

            var chests = filter.Apply(registry.GetAll());

            // GET .../   the chests themselves
            if (rest.Length == 0)
            {
                WriteJson(context, 200, new ChestListResponse
                {
                    Count = chests.Count,
                    GeneratedAtUtc = DateTime.UtcNow,
                    Meta = ChestAggregates.Meta(chests),
                    Chests = filter.IncludeItems ? chests : chests.Select(c => c.WithoutItems()).ToList(),
                });
                return;
            }

            // GET .../items   totals per item (restrict with ?item=Iron,Wood)
            if (rest.Equals("/items", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 200, new ItemTotalsResponse
                {
                    Meta = ChestAggregates.Meta(chests),
                    Items = ChestAggregates.Totals(chests, filter.Items),
                });
                return;
            }

            // GET .../items/{prefab}/count   one item's total
            if (rest.StartsWith("/items/", StringComparison.OrdinalIgnoreCase) && rest.EndsWith("/count", StringComparison.OrdinalIgnoreCase))
            {
                var prefab = Uri.UnescapeDataString(rest.Substring("/items/".Length, rest.Length - "/items/".Length - "/count".Length));
                var only = new HashSet<string>(new[] { prefab }, StringComparer.OrdinalIgnoreCase);
                var total = ChestAggregates.Totals(chests, only).FirstOrDefault();

                WriteJson(context, 200, new ItemCountResponse
                {
                    PrefabName = prefab,
                    TotalStack = total?.TotalStack ?? 0,
                    ChestCount = total?.ChestCount ?? 0,
                    GeneratedAtUtc = DateTime.UtcNow,
                    Meta = ChestAggregates.Meta(chests),
                });
                return;
            }

            // GET .../check?need=Iron:20,Wood:50[&where=true]   have vs. needed, and what is missing
            if (isCheck)
            {
                var needs = ChestAggregates.ParseNeeds(query.GetValues("need"), out var needError);
                if (needs == null)
                {
                    WriteJson(context, 400, new ErrorResponse { Error = needError });
                    return;
                }

                var includeWhere = string.Equals(query["where"], "true", StringComparison.OrdinalIgnoreCase) || query["where"] == "1";
                WriteJson(context, 200, ChestAggregates.Check(chests, needs, includeWhere));
                return;
            }

            WriteJson(context, 404, new ErrorResponse { Error = "Unknown endpoint." });
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
