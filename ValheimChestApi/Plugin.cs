using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ValheimChestApi
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "mathias.valheim.chestapi";
        public const string PluginName = "ValheimChestApi";
        public const string PluginVersion = "0.1.0";

        /// <summary>
        /// Preferred way to set the API token: it never touches disk (no config file, no BepInEx log),
        /// so it's the right choice for a token shared with other services. Always wins over the
        /// ApiKey config entry when both are set.
        /// </summary>
        public const string ApiTokenEnvVar = "VALHEIM_CHEST_API_TOKEN";

        internal static ManualLogSource Log;

        private Harmony _harmony;
        private HttpApiServer _server;

        private ConfigEntry<string> _bindAddress;
        private ConfigEntry<int> _port;
        private ConfigEntry<string> _apiKey;
        private ConfigEntry<bool> _scanUnloadedContainers;
        private ConfigEntry<float> _scanIntervalSeconds;

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

            var token = ResolveApiToken();
            if (string.IsNullOrEmpty(token))
            {
                Log.LogWarning($"[ValheimChestApi] No API token configured - set the {ApiTokenEnvVar} environment " +
                                "variable (recommended) or the ApiKey config entry. Until then, the HTTP API will " +
                                "deny every request (fail closed).");
            }

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            _server = new HttpApiServer(_bindAddress.Value, _port.Value, token);
            _server.Start();

            if (_scanUnloadedContainers.Value)
            {
                InvokeRepeating(nameof(RunUnloadedContainerScan), _scanIntervalSeconds.Value, _scanIntervalSeconds.Value);
            }

            Log.LogInfo($"[ValheimChestApi] Loaded v{PluginVersion}.");
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
            _server?.Stop();
            _harmony?.UnpatchSelf();
        }
    }
}
