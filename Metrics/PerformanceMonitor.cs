using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValheimHighCap.Patches.Phase1;
using ValheimHighCap.Patches.Phase2;

namespace ValheimHighCap.Metrics
{
    /// <summary>
    /// Periodic server-health snapshot logged to the BepInEx console.
    ///
    /// Emitted every <see cref="HighCapConfig.MetricsIntervalSeconds"/> seconds.
    /// Example output:
    ///
    ///   [HighCap Metrics] 14:32:07 UTC
    ///     Players:     12 / 64
    ///     Total ZDOs:  14 832
    ///     ZDOs/s sent: 2 104
    ///     ZDOs/s recv:   87
    ///     Dirty/cycle:   43
    ///     Per-peer:
    ///       [Ragnar              ]  KnownZDOs= 2 841  QueueKB=  1.2  Pos=(  -420,   880)
    ///       [Freya               ]  KnownZDOs=   312  QueueKB=  0.1  Pos=(  1203,  -210)
    ///       ...
    /// </summary>
    public static class PerformanceMonitor
    {
        // ── Reflection handles ──────────────────────────────────────────────
        private static readonly FieldInfo s_zdoManPeers    = AccessTools.Field(typeof(ZDOMan), "m_peers");
        private static readonly FieldInfo s_zdosSentPerSec = AccessTools.Field(typeof(ZDOMan), "m_zdosSentLastSec");
        private static readonly FieldInfo s_zdosRecvPerSec = AccessTools.Field(typeof(ZDOMan), "m_zdosRecvLastSec");
        private static readonly FieldInfo s_objectsByID    = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");

        private static readonly Type      s_zdoPeerType    = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
        private static readonly FieldInfo s_peerNetPeer    = AccessTools.Field(s_zdoPeerType, "m_peer");
        private static readonly FieldInfo s_peerKnownZdos  = AccessTools.Field(s_zdoPeerType, "m_zdos");

        // ── State ────────────────────────────────────────────────────────────
        private static float s_elapsed;
        private static float s_subTimer;   // sub-timer to sample the vanilla counter once/sec
        // Accumulators for the metric interval — we sum one sample per second so
        // we get a true interval average, not a random last-second snapshot.
        private static long  s_sentAccum;
        private static long  s_recvAccum;
        private static int   s_tickCount;

        // ─────────────────────────────────────────────────────────────────────

        public static void Initialize()
        {
            HighCapPlugin.Log.LogInfo("[Metrics] Performance monitor ready.");
        }

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Hooked into <c>ZDOMan.UpdateStats</c> (called every second by vanilla)
        /// to tick our own interval timer.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), "UpdateStats")]
        internal static class UpdateStatsPatch
        {
            static void Postfix(ZDOMan __instance, float dt)
            {
                if (!HighCapConfig.EnableMetrics.Value) return;

                // Sample the vanilla 1-second counter once per second via a sub-timer.
                // We can't simply accumulate every frame because m_zdosSentLastSec only
                // refreshes once/sec — adding it every frame would multiply it by ~FPS.
                s_subTimer += dt;
                if (s_subTimer >= 1f)
                {
                    s_subTimer -= 1f;
                    s_sentAccum += (int)s_zdosSentPerSec.GetValue(__instance);
                    s_recvAccum += (int)s_zdosRecvPerSec.GetValue(__instance);
                    s_tickCount++;
                }

                s_elapsed += dt;
                if (s_elapsed < HighCapConfig.MetricsIntervalSeconds.Value) return;
                s_elapsed = 0f;

                WriteSnapshot(__instance);

                s_sentAccum = 0;
                s_recvAccum = 0;
                s_tickCount = 0;
            }
        }

        // ─────────────────────────────────────────────────────────────────────

        private static void WriteSnapshot(ZDOMan zdoMan)
        {
            try
            {
                var  peers     = (IList)      s_zdoManPeers   .GetValue(zdoMan);
                var  byId      = (IDictionary) s_objectsByID   .GetValue(zdoMan);
                // Average over the full metric interval (s_tickCount ticks ≈ 1 each second).
                int  ticks     = Math.Max(s_tickCount, 1);
                int  sentAvg   = (int)(s_sentAccum / ticks);
                int  recvAvg   = (int)(s_recvAccum / ticks);

                int intervalSec = Math.Max((int)HighCapConfig.MetricsIntervalSeconds.Value, 1);

                var sb = new StringBuilder();
                sb.AppendLine($"[HighCap Metrics] {DateTime.UtcNow:HH:mm:ss} UTC");
                sb.AppendLine($"  Players      : {peers.Count,3} / {HighCapConfig.MaxPlayers.Value}");
                sb.AppendLine($"  Total ZDOs   : {byId.Count,7:N0}");
                sb.AppendLine($"  ZDOs/s sent  : {sentAvg,7:N0}  (avg/s over {intervalSec}s)");
                sb.AppendLine($"  ZDOs/s recv  : {recvAvg,7:N0}  (avg/s over {intervalSec}s)");
                sb.AppendLine($"  Dirty/cycle  : {DirtyZdoTracker.LiveCount,7:N0}");
                sb.AppendLine($"  Frame EMA    : {MultiPeerSendPatch.AvgFrameMs,7:F1} ms");
                sb.AppendLine($"  Peers/frame  : {MultiPeerSendPatch.EffectivePeers,7}" +
                              (HighCapConfig.AdaptiveMode.Value ? " (adaptive)" : " (fixed)"));
                sb.AppendLine($"  Phase2 dirty : {(HighCapConfig.EnableDirtyTracking.Value ? "ON" : "OFF")}");
                sb.AppendLine($"  Spatial RPC  : {(HighCapConfig.EnableSpatialRpc.Value ? "ON" : "OFF")}");
                sb.AppendLine("  ───────────────────────────────────────────────────────────");

                if (peers.Count == 0)
                {
                    sb.AppendLine("  (no peers connected)");
                }
                else
                {
                    sb.AppendLine(
                        "  " +
                        $"{"Name",-22}" +
                        $"{"KnownZDOs",10}" +
                        $"{"QueueKB",9}" +
                        $"{"World Pos (X,Z)",20}");

                    foreach (object peer in peers)
                    {
                        var    netPeer   = (ZNetPeer)   s_peerNetPeer  .GetValue(peer);
                        var    knownZdos = (IDictionary) s_peerKnownZdos.GetValue(peer);
                        int    queueSize = netPeer.m_socket.GetSendQueueSize();
                        string warning   = queueSize > HighCapConfig.ZdoSendQueueSizeBytes.Value * 0.8f
                                           ? " ⚠ QUEUE HIGH" : "";

                        sb.AppendLine(
                            "  " +
                            $"[{netPeer.m_playerName,-20}]" +
                            $"{knownZdos.Count,10:N0}" +
                            $"{queueSize / 1024f,8:F1}K" +
                            $"  ({netPeer.m_refPos.x,8:F0}, {netPeer.m_refPos.z,8:F0})" +
                            warning);
                    }
                }

                // Guidance line when queue pressure is detected
                float avgQueueKB = AverageQueueKB(peers);
                if (avgQueueKB > 32f)
                {
                    sb.AppendLine(
                        $"  ⚠ Average peer queue {avgQueueKB:F0} KB — " +
                        "consider raising ZdoSendQueueSizeBytes or reducing PeersPerFrame.");
                }

                HighCapPlugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception ex)
            {
                HighCapPlugin.Log.LogWarning($"[Metrics] WriteSnapshot failed: {ex.Message}");
            }
        }

        private static float AverageQueueKB(IList peers)
        {
            if (peers.Count == 0) return 0f;
            float total = 0f;
            foreach (object peer in peers)
            {
                var netPeer = (ZNetPeer)s_peerNetPeer.GetValue(peer);
                total += netPeer.m_socket.GetSendQueueSize();
            }
            return total / peers.Count / 1024f;
        }
    }
}
