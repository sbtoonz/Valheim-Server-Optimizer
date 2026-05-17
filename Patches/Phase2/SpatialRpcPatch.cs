using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimHighCap.Patches.Phase2
{
    /// <summary>
    /// Filters broadcast RPCs to only peers within a configurable world-space radius
    /// of the target ZDO's position.
    ///
    /// Vanilla behaviour:
    ///   RouteRPC with targetPeerID == 0 (Everybody) sends one serialised packet to
    ///   EVERY connected peer, regardless of position.
    ///   At 100 players a single sword hit generates ~100 packets.
    ///
    /// Patched behaviour (ZDO-targeted broadcasts only):
    ///   Only peers within SpatialRpcRadius of the ZDO receive the packet.
    ///
    /// Whitelist:
    ///   Critical system RPCs that must reach ALL peers (world state, session management)
    ///   bypass the spatial filter entirely, even when they carry a ZDO reference.
    ///   This prevents ghost ZDOs, broken portals, or missed login/logout events.
    ///
    /// What gets culled:
    ///   ✓ Hit effects, damage numbers        (ZDO = victim character)
    ///   ✓ Status effects applied/removed     (ZDO = affected entity)
    ///   ✓ Object destroy/interact sounds     (ZDO = world object)
    ///   ✗ DestroyZDO, RequestZDO             (whitelisted — must reach all peers)
    ///   ✗ Chat, global events, time sync     (no ZDO → passes through)
    ///   ✗ PeerInfo, Disconnect, AdminList    (whitelisted)
    /// </summary>
    [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
    internal static class SpatialRpcPatch
    {
        // ── RPC whitelist ─────────────────────────────────────────────────────
        // Methods whose stable hash codes are in this set always broadcast to all
        // peers, regardless of ZDO position.  Hash is computed the same way the
        // game does: string.GetStableHashCode() (djb2 variant in ExtensionMethods).
        private static readonly HashSet<int> s_whitelist = BuildWhitelist();

        private static HashSet<int> BuildWhitelist()
        {
            var set = new HashSet<int>();
            string[] names =
            {
                // ZDO lifecycle — all peers must stay in sync
                "DestroyZDO",
                "RequestZDO",
                // Session management
                "PeerInfo",
                "Disconnect",
                "Kicked",
                "PlayerList",
                "AdminList",
                // World state
                "GlobalKeys",
                "SetEvent",
                "LocationIcons",
                "NetTime",
                "ServerSyncedPlayerData",
                "SavePlayerProfile",
            };
            foreach (string n in names)
                set.Add(n.GetStableHashCode());
            return set;
        }

        // ─────────────────────────────────────────────────────────────────────

        static bool Prefix(
            ZRoutedRpc __instance,
            ZRoutedRpc.RoutedRPCData rpcData,
            bool ___m_server,
            List<ZNetPeer> ___m_peers)
        {
            // ── Guards ────────────────────────────────────────────────────────
            if (!HighCapConfig.EnableSpatialRpc.Value) return true;
            if (!___m_server)                          return true;
            if (rpcData.m_targetPeerID != 0L)          return true; // targeted, not broadcast

            // Whitelisted RPC → must reach every peer regardless of ZDO position
            if (s_whitelist.Contains(rpcData.m_methodHash)) return true;

            // No ZDO context → no position to cull from → pass through
            if (rpcData.m_targetZDO.IsNone()) return true;

            ZDO? zdo = ZDOMan.instance.GetZDO(rpcData.m_targetZDO);
            if (zdo == null) return true;

            // ── Spatial filter ────────────────────────────────────────────────
            Vector3 origin   = zdo.GetPosition();
            float   radiusSq = HighCapConfig.SpatialRpcRadius.Value *
                               HighCapConfig.SpatialRpcRadius.Value;

            // Serialise once; Invoke calls pkg.GetArray() (no position advance).
            var pkg = new ZPackage();
            rpcData.Serialize(pkg);

            foreach (ZNetPeer peer in ___m_peers)
            {
                if (rpcData.m_senderPeerID == peer.m_uid || !peer.IsReady())
                    continue;

                float distSq = (origin - peer.m_refPos).sqrMagnitude;
                if (distSq <= radiusSq)
                    peer.m_rpc.Invoke("RoutedRPC", pkg);
            }

            return false;
        }
    }
}
