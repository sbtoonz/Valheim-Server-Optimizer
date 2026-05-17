using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using ValheimHighCap.Patches.Phase2;

namespace ValheimHighCap.Patches.Phase1
{
    /// <summary>
    /// Replaces <c>ZDOMan.SendZDOToPeers2</c> to service multiple peers per frame
    /// instead of one, with an optional adaptive throttler.
    ///
    /// Vanilla behaviour (the bottleneck):
    ///   Every Unity frame, advance m_nextSendPeer by exactly ONE.
    ///   At 20 peers and 60 fps → each peer gets a ZDO update every ~333 ms (3 Hz).
    ///   At 50 peers           → every ~833 ms (1.2 Hz).
    ///   At 100 peers          → every ~1666 ms (0.6 Hz).  Unplayable.
    ///
    /// Patched behaviour:
    ///   Service up to EffectivePeers (adaptive or fixed) peers per frame.
    ///   Adaptive mode: EMA of dt drives auto-scaling between AdaptiveMinPeers and
    ///   PeersPerFrame (ceiling). Adjusts every second, ±1 step at a time.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2")]
    internal static class MultiPeerSendPatch
    {
        // ── Cached reflection handles ─────────────────────────────────────────
        private static readonly FieldInfo  s_peers     = AccessTools.Field(typeof(ZDOMan), "m_peers");
        private static readonly FieldInfo  s_sendTimer = AccessTools.Field(typeof(ZDOMan), "m_sendTimer");
        private static readonly FieldInfo  s_nextPeer  = AccessTools.Field(typeof(ZDOMan), "m_nextSendPeer");
        private static readonly MethodInfo s_sendZDOs  = AccessTools.Method(typeof(ZDOMan), "SendZDOs");

        // ── Adaptive throttle state (main thread only) ────────────────────────
        private static float s_avgFrameMs   = 0f;   // exponential moving average
        private static int   s_adaptPeers   = 0;    // current effective peers/frame
        private static float s_adaptTimer   = 0f;   // time since last adjustment

        /// <summary>
        /// Current effective peers/frame — used by PerformanceMonitor for reporting.
        /// </summary>
        internal static int EffectivePeers => s_adaptPeers > 0
            ? s_adaptPeers
            : HighCapConfig.PeersPerFrame.Value;

        // ─────────────────────────────────────────────────────────────────────

        static bool Prefix(ZDOMan __instance, float dt)
        {
            var peers = (IList)s_peers.GetValue(__instance);
            if (peers.Count == 0)
                return false;

            // ── Always update EMA so adaptive has data even during the wait phase ──
            s_avgFrameMs = s_avgFrameMs * 0.95f + (dt * 1000f) * 0.05f;

            // Adjust effective peers once per second
            s_adaptTimer += dt;
            if (s_adaptTimer >= 1f)
            {
                s_adaptTimer = 0f;
                UpdateAdaptive();
            }

            float sendTimer = (float)s_sendTimer.GetValue(__instance);
            int   nextPeer  = (int)  s_nextPeer .GetValue(__instance);

            sendTimer += dt;

            // ── Waiting phase: accumulate until send interval opens ────────────
            if (nextPeer < 0)
            {
                if (sendTimer > HighCapConfig.SendIntervalSeconds.Value)
                {
                    // New cycle begins — snapshot dirty ZDOs for Phase 2.
                    if (HighCapConfig.EnableDirtyTracking.Value)
                        DirtyZdoTracker.BeginCycle();

                    s_nextPeer .SetValue(__instance, 0);
                    s_sendTimer.SetValue(__instance, 0f);
                }
                else
                {
                    s_sendTimer.SetValue(__instance, sendTimer);
                }
                return false;
            }

            // ── Active phase: service EffectivePeers peers this Unity frame ───
            int limit  = Math.Min(EffectivePeers, peers.Count);
            int served = 0;

            while (served < limit && nextPeer < peers.Count)
            {
                s_sendZDOs.Invoke(__instance, new object[] { peers[nextPeer], false });
                nextPeer++;
                served++;
            }

            // Cycle complete → return to waiting phase.
            if (nextPeer >= peers.Count)
                nextPeer = -1;

            s_nextPeer .SetValue(__instance, nextPeer);
            s_sendTimer.SetValue(__instance, sendTimer);
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────

        private static void UpdateAdaptive()
        {
            int configured = HighCapConfig.PeersPerFrame.Value;

            if (!HighCapConfig.AdaptiveMode.Value)
            {
                s_adaptPeers = configured;
                return;
            }

            // Initialise on first call
            if (s_adaptPeers <= 0)
                s_adaptPeers = configured;

            float target = HighCapConfig.AdaptiveTargetFrameMs.Value;
            int   minP   = HighCapConfig.AdaptiveMinPeers.Value;

            if (s_avgFrameMs > target * 1.2f)
            {
                // Server struggling → step down
                s_adaptPeers = Math.Max(minP, s_adaptPeers - 1);
            }
            else if (s_avgFrameMs < target * 0.7f && s_adaptPeers < configured)
            {
                // Server has headroom → step up toward ceiling
                s_adaptPeers = Math.Min(configured, s_adaptPeers + 1);
            }
        }

        /// <summary>EMA frame time in ms — exposed for metrics.</summary>
        internal static float AvgFrameMs => s_avgFrameMs;
    }
}
