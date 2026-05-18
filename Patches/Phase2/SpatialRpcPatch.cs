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
    /// ─── BLACKLIST APPROACH (v2) ────────────────────────────────────────────
    ///
    ///   v1 used a WHITELIST — any RPC not explicitly allowed was spatially culled.
    ///   This broke interactions because many gameplay RPCs (door use, chest open,
    ///   item pickup, damage) are sent as ZDO-targeted broadcasts or to owner=0
    ///   (unowned ZDOs), and the whitelist couldn't cover all of them.
    ///
    ///   v2 inverts to a BLACKLIST — only RPCs that are KNOWN to be high-volume
    ///   and purely cosmetic (damage numbers, hit effects, footsteps) get spatially
    ///   culled. Everything else passes through to all peers (vanilla behaviour).
    ///
    ///   Missing a blacklist entry = a cosmetic RPC leaks to distant peers (minor
    ///   perf hit, harmless). Missing a whitelist entry = a gameplay RPC gets
    ///   dropped (broken interactions). Blacklist is strictly safer.
    ///
    /// What gets culled:
    ///   ✓ Damage numbers / damage text display
    ///   ✓ Hit effects and impact visuals
    ///   ✓ Footstep and movement effects
    ///   ✓ Alert/noise/aggro notifications (visual only on other clients)
    ///   ✗ Everything else passes through unchanged (safe default)
    /// </summary>
    [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
    internal static class SpatialRpcPatch
    {
        // ── RPC blacklist ─────────────────────────────────────────────────────
        // Methods whose stable hash codes are in this set get spatially culled.
        // These must be purely cosmetic / visual-feedback RPCs that have no
        // gameplay state impact. When in doubt, do NOT add to this set.
        private static readonly HashSet<int> s_blacklist = BuildBlacklist();

        private static HashSet<int> BuildBlacklist()
        {
            var set = new HashSet<int>();
            string[] names =
            {
                // ── Damage / combat visuals ──────────────────────────────────
                "DamageText",          // floating damage numbers
                "Stagger",             // stagger visual sync
                "AddNoise",            // AI noise radius (visual aggro indicator)
                "Alert",               // AI alert state visual

                // ── Hit / impact effects ─────────────────────────────────────
                "Hit",                 // melee hit effect
                "OnHit",               // projectile hit effect
                "Poke",                // stab hit feedback

                // ── Movement / footstep effects ──────────────────────────────
                "Step",                // footstep sync
                "Step2",               // alternate footstep

                // ── Status effect visuals ────────────────────────────────────
                "FlashShield",         // shield flash VFX
                "ResetCloth",          // cloth physics reset (visual only)
            };
            foreach (string n in names)
                set.Add(n.GetStableHashCode());
            return set;
        }

        // One-shot diagnostic logging
        private static int s_loggedActive;

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

            // No ZDO context → no position to cull from → pass through
            if (rpcData.m_targetZDO.IsNone()) return true;

            // ── Blacklist check: only cull known-cosmetic RPCs ────────────────
            // If the RPC is NOT in the blacklist, let vanilla handle it (pass through).
            if (!s_blacklist.Contains(rpcData.m_methodHash)) return true;

            ZDO? zdo = ZDOMan.instance.GetZDO(rpcData.m_targetZDO);
            if (zdo == null) return true;

            // ── Spatial filter (only for blacklisted cosmetic RPCs) ───────────
            Vector3 origin   = zdo.GetPosition();
            float   radiusSq = HighCapConfig.SpatialRpcRadius.Value *
                               HighCapConfig.SpatialRpcRadius.Value;

            // Serialise once; ZRpc.Invoke calls pkg.GetArray() which doesn't
            // advance the stream position, so the same package is safe for
            // multiple peers.
            var pkg = new ZPackage();
            rpcData.Serialize(pkg);

            int sent = 0;
            int culled = 0;
            foreach (ZNetPeer peer in ___m_peers)
            {
                if (rpcData.m_senderPeerID == peer.m_uid || !peer.IsReady())
                    continue;

                float distSq = (origin - peer.m_refPos).sqrMagnitude;
                if (distSq <= radiusSq)
                {
                    peer.m_rpc.Invoke("RoutedRPC", pkg);
                    sent++;
                }
                else
                {
                    culled++;
                }
            }

            // One-shot activation log
            if (System.Threading.Interlocked.Exchange(ref s_loggedActive, 1) == 0)
            {
                HighCapPlugin.Log.LogInfo(
                    $"[SpatialRpcPatch] ACTIVE (v2 blacklist mode). " +
                    $"Radius={HighCapConfig.SpatialRpcRadius.Value}u, " +
                    $"blacklisted RPCs={s_blacklist.Count}.");
            }

            return false; // skip vanilla for this blacklisted RPC
        }
    }
}
