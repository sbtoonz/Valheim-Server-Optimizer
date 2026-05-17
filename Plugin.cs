using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ValheimHighCap.Metrics;

namespace ValheimHighCap
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInProcess("valheim_server")]
    public sealed class HighCapPlugin : BaseUnityPlugin
    {
        public const string PluginGUID    = "com.valhoom.highcap";
        public const string PluginName    = "ValheimHighCap";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log     = null!;
        private         Harmony         _harmony = null!;

        private void Awake()
        {
            Log = Logger;

            HighCapConfig.Bind(Config);

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(HighCapPlugin).Assembly);

            PerformanceMonitor.Initialize();

            Log.LogInfo(
                $"[ValheimHighCap v{PluginVersion}] Loaded. " +
                $"MaxPlayers={HighCapConfig.MaxPlayers.Value}  " +
                $"PeersPerFrame={HighCapConfig.PeersPerFrame.Value}  " +
                $"SteamSendRate={HighCapConfig.SteamSendRateBytesPerSec.Value / 1024}KB/s  " +
                $"Phase2={HighCapConfig.EnableDirtyTracking.Value}  " +
                $"SpatialRPC={HighCapConfig.EnableSpatialRpc.Value}");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
