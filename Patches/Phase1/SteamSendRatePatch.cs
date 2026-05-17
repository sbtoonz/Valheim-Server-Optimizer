using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Steamworks;

namespace ValheimHighCap.Patches.Phase1
{
    /// <summary>
    /// Raises the Steam Networking per-connection send-rate cap from the vanilla
    /// 153 600 B/s (150 KB/s) to the configured value.
    ///
    /// Context:
    ///   Vanilla calls RegisterGlobalCallbacks() once (guarded by null check).
    ///   We Postfix it so our values overwrite the vanilla values immediately after.
    ///   If the callback was already registered (server reloaded, etc.) our postfix
    ///   still fires and refreshes the rate, which is harmless.
    ///
    /// Total theoretical egress = MaxPlayers × SteamSendRateBytesPerSec.
    ///   100 players × 1 MB/s = 100 MB/s peak (unrealistic; actual ~2–5 MB/s typical).
    ///   Ensure your NIC/hosting plan can handle the real-world average.
    /// </summary>
    [HarmonyPatch(typeof(ZSteamSocket), "RegisterGlobalCallbacks")]
    internal static class SteamSendRatePatch
    {
        static void Postfix()
        {
            int rate = HighCapConfig.SteamSendRateBytesPerSec.Value;

            // SteamNetworkingUtils requires a pinned value for the pointer argument.
            var handle = GCHandle.Alloc(rate, GCHandleType.Pinned);
            try
            {
                IntPtr pRate = handle.AddrOfPinnedObject();

                SteamGameServerNetworkingUtils.SetConfigValue(
                    ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    pRate);

                SteamGameServerNetworkingUtils.SetConfigValue(
                    ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    pRate);
            }
            finally
            {
                handle.Free();
            }

            HighCapPlugin.Log.LogInfo(
                $"[SteamSendRate] Per-connection rate set to " +
                $"{rate / 1024} KB/s  " +
                $"(vanilla was 150 KB/s).");
        }
    }
}
