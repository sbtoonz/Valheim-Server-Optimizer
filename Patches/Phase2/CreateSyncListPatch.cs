using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimHighCap.Patches.Phase2
{
    /// <summary>
    /// Optimises <c>ZDOMan.CreateSyncList</c> for established peers using a dirty-ZDO set.
    ///
    /// Vanilla:
    ///   For every peer, iterate ALL ZDOs in the active sector area (1000–5000) and call
    ///   <c>ShouldSend(zdo)</c> (one dictionary lookup each) to find the ~5–20 that need
    ///   updating.  O(sector_ZDOs × peers) per cycle.
    ///
    /// Optimised path (gated by Phase2.DirtyTracking):
    ///   <c>FindSectorObjects</c> still runs — required to discover ZDOs the peer has never
    ///   seen.  For each sector ZDO:
    ///     • Unknown to peer            → always add  (discovery; same as vanilla)
    ///     • Known + dirty              → ShouldSend  (may have changed)
    ///     • Known + NOT dirty          → SKIP        (provably unchanged)
    ///
    /// Safety:
    ///   The Prefix is wrapped in try/catch.  Any exception (reflection failure,
    ///   game-version drift, etc.) auto-disables the optimisation for the remainder
    ///   of the process and reverts to vanilla — guaranteeing the server stays
    ///   playable even if a future Valheim patch breaks our reflection handles.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "CreateSyncList")]
    internal static class CreateSyncListPatch
    {
        // ── Cached reflection handles ─────────────────────────────────────────
        private static readonly Type s_zdoPeerType =
            AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
        private static readonly FieldInfo  s_peerNetPeer   = AccessTools.Field(s_zdoPeerType, "m_peer");
        private static readonly FieldInfo  s_peerKnownZdos = AccessTools.Field(s_zdoPeerType, "m_zdos");
        private static readonly MethodInfo s_shouldSend    = AccessTools.Method(s_zdoPeerType, "ShouldSend");
        private static readonly MethodInfo s_addForceSend  = AccessTools.Method(typeof(ZDOMan), "AddForceSendZdos");
        private static readonly MethodInfo s_serverSort    = AccessTools.Method(typeof(ZDOMan), "ServerSortSendZDOS");

        // Reusable sector-scan buffers — main-thread only.
        private static readonly List<ZDO> s_nearBuf    = new List<ZDO>(1024);
        private static readonly List<ZDO> s_distantBuf = new List<ZDO>(256);

        // Auto-disable latch — flips on first exception, never re-enables.
        private static bool s_disabled = false;

        // One-shot sanity log on first invocation.
        private static bool s_firstCallLogged = false;

        // ─────────────────────────────────────────────────────────────────────

        static bool Prefix(ZDOMan __instance, object peer, List<ZDO> toSync)
        {
            if (s_disabled)                                return true;
            if (!HighCapConfig.EnableDirtyTracking.Value)  return true;
            if (!ZNet.instance.IsServer())                 return true;

            try
            {
                if (!s_firstCallLogged)
                {
                    s_firstCallLogged = true;
                    HighCapPlugin.Log.LogInfo(
                        $"[CreateSyncListPatch] ACTIVE  peerType={s_zdoPeerType?.FullName}  " +
                        $"knownZdosField={s_peerKnownZdos != null}  shouldSendMethod={s_shouldSend != null}");
                }

                if (s_peerKnownZdos == null || s_shouldSend == null ||
                    s_peerNetPeer   == null || s_serverSort == null || s_addForceSend == null)
                {
                    Disable("reflection handle is null");
                    return true;
                }

                var knownZdos = (IDictionary)s_peerKnownZdos.GetValue(peer);
                if (knownZdos == null)
                {
                    Disable("knownZdos was null for peer");
                    return true;
                }

                if (knownZdos.Count < HighCapConfig.DirtyTrackingMinKnownZdos.Value)
                    return true; // new peer → vanilla full scan

                ZNetPeer netPeer = (ZNetPeer)s_peerNetPeer.GetValue(peer);
                if (netPeer == null) return true;

                Vector3  refPos = netPeer.GetRefPos();
                Vector2i zone   = ZoneSystem.GetZone(refPos);

                s_nearBuf.Clear();
                s_distantBuf.Clear();
                __instance.FindSectorObjects(
                    zone,
                    ZoneSystem.instance.m_activeArea,
                    ZoneSystem.instance.m_activeDistantArea,
                    s_nearBuf,
                    s_distantBuf);

                ProcessZdoList(s_nearBuf, knownZdos, peer, toSync);

                s_serverSort.Invoke(__instance, new object[] { toSync, refPos, peer });

                if (toSync.Count < 10)
                    ProcessZdoList(s_distantBuf, knownZdos, peer, toSync);

                s_addForceSend.Invoke(__instance, new object[] { peer, toSync });
                return false;
            }
            catch (Exception ex)
            {
                Disable($"exception: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    HighCapPlugin.Log.LogError($"[CreateSyncListPatch] inner: {ex.InnerException}");
                HighCapPlugin.Log.LogError($"[CreateSyncListPatch] stack: {ex.StackTrace}");
                toSync.Clear();
                return true; // fall back to vanilla for this call too
            }
        }

        private static void ProcessZdoList(
            List<ZDO>   zdos,
            IDictionary knownZdos,
            object      peer,
            List<ZDO>   toSync)
        {
            foreach (ZDO zdo in zdos)
            {
                if (!knownZdos.Contains(zdo.m_uid))
                {
                    toSync.Add(zdo);
                }
                else if (DirtyZdoTracker.IsDirty(zdo.m_uid))
                {
                    if ((bool)s_shouldSend.Invoke(peer, new object[] { zdo }))
                        toSync.Add(zdo);
                }
            }
        }

        private static void Disable(string reason)
        {
            s_disabled = true;
            HighCapPlugin.Log.LogError(
                $"[CreateSyncListPatch] DISABLING dirty-tracking optimisation: {reason}. " +
                "Vanilla CreateSyncList will be used for the rest of this process. " +
                "Set Phase2.DirtyTracking.Enable=false in config to silence.");
        }
    }
}


