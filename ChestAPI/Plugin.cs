using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ChestAPI
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "mathias.valheim.chestapi";
        public const string PluginName = "ChestAPI";
        public const string PluginVersion = "0.2.0";

        /// <summary>
        /// Preferred way to set the API token: it never touches disk (no config file, no BepInEx log),
        /// so it's the right choice for a token shared with other services. Always wins over the
        /// ApiKey config entry when both are set.
        /// </summary>
        public const string ApiTokenEnvVar = "CHESTAPI_API_TOKEN";

        internal static ManualLogSource Log;

        private Harmony _harmony;
        private HttpApiServer _server;

        private ConfigEntry<string> _bindAddress;
        private ConfigEntry<int> _port;
        private ConfigEntry<string> _apiKey;
        private ConfigEntry<bool> _scanUnloadedContainers;
        private ConfigEntry<float> _scanIntervalSeconds;
        private ConfigEntry<string> _areas;

        private void Awake()
        {
            Log = Logger;

            _bindAddress = Config.Bind("Http", "BindAddress", "127.0.0.1",
                "Address to bind the HTTP API to. Use 127.0.0.1 unless you specifically want it reachable from other machines (then also set ApiKey).");
            _port = Config.Bind("Http", "Port", 8085,
                "TCP port for the HTTP API.");
            _apiKey = Config.Bind("Http", "ApiKey", "",
                $"Clients must send this value in an 'X-Api-Key' header. Fallback only - prefer setting the " +
                $"{ApiTokenEnvVar} environment variable instead, which always takes priority over this value. " +
                "The API denies every request until a token is set by one of these two methods.");

            _scanUnloadedContainers = Config.Bind("Scan", "ScanUnloadedContainers", true,
                "Periodically sweep the server's full persisted world state to find chests outside currently " +
                "loaded zones (e.g. far-away bases with nobody nearby). Without this, those chests only show " +
                "up once a player gets close enough for the zone to load. Experimental - see README.");
            _scanIntervalSeconds = Config.Bind("Scan", "ScanIntervalSeconds", 60f,
                "How often to run the unloaded-container scan. It runs on the main thread, so very low values " +
                "on a large world could cost a noticeable chunk of a server tick.");

            _areas = Config.Bind("Areas", "Definitions", "",
                "Named map regions that API callers can select with ?area=name. Separate entries with ';'. " +
                "Rectangle: name:rect:minX,minZ,maxX,maxZ   Circle: name:circle:x,z,radius   " +
                "Example: base:circle:0,0,150;farm:rect:-300,100,-200,200");

            var areas = AreaParser.Parse(_areas.Value, out var areaProblems);
            foreach (var problem in areaProblems)
            {
                Log.LogWarning($"[ChestAPI] Ignoring area definition {problem}");
            }

            ChestRegistry.Instance.Names = new PlayerNames(Path.Combine(Paths.ConfigPath, "mathias.valheim.chestapi.players.json"));
            try
            {
                ChestRegistry.Instance.Names.Load();
                Log.LogInfo($"[ChestAPI] Loaded {ChestRegistry.Instance.Names.Count} known player name(s).");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[ChestAPI] Could not read the player name file, starting empty: {ex.Message}");
            }

            var token = ResolveApiToken();
            if (string.IsNullOrEmpty(token))
            {
                Log.LogWarning($"[ChestAPI] No API token configured - set the {ApiTokenEnvVar} environment " +
                                "variable (recommended) or the ApiKey config entry. Until then, the HTTP API will " +
                                "deny every request (fail closed).");
            }

            _harmony = new Harmony(PluginGuid);
            // Patch each class on its own so one target that moved in a Valheim update disables only that
            // hook instead of aborting Awake (which would also keep the HTTP server from ever starting).
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception ex)
                {
                    Log.LogError($"[ChestAPI] Patch {type.Name} failed, continuing without it: {ex.Message}");
                }
            }

            _server = new HttpApiServer(_bindAddress.Value, _port.Value, token, areas);
            _server.Start();

            if (_scanUnloadedContainers.Value)
            {
                InvokeRepeating(nameof(RunUnloadedContainerScan), _scanIntervalSeconds.Value, _scanIntervalSeconds.Value);
            }

            Log.LogInfo($"[ChestAPI] Loaded v{PluginVersion}.");
        }

        private void RunUnloadedContainerScan()
        {
            ChestRegistry.Instance.ScanPersistedWorld();
        }

        private string ResolveApiToken()
        {
            var envToken = Environment.GetEnvironmentVariable(ApiTokenEnvVar);
            return !string.IsNullOrEmpty(envToken) ? envToken : _apiKey.Value;
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(RunUnloadedContainerScan));
            try { ChestRegistry.Instance.Names.SaveIfDirty(); } catch { /* shutting down */ }
            _server?.Stop();
            _harmony?.UnpatchSelf();
        }
    }
}
