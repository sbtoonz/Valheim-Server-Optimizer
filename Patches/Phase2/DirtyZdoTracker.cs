using System.Collections.Generic;
using HarmonyLib;

namespace ValheimHighCap.Patches.Phase2
{
    /// <summary>
    /// Maintains a per-cycle snapshot of ZDOs that changed since the last ZDO send cycle.
    ///
    /// Design:
    ///   s_live   — accumulates ZDOIDs as ZDO.IncreaseDataRevision OR
    ///              ZDO.IncreaseOwnerRevision is called (main thread).
    ///   s_snapshot — frozen copy used by ALL peers during the current send cycle.
    ///
    ///   At the start of each 50 ms ZDO cycle (<see cref="BeginCycle"/>):
    ///     1. The live set is swapped into the snapshot.
    ///     2. The live set is cleared for the next cycle.
    ///
    ///   Each peer's CreateSyncList (Phase2 path) queries CycleSnapshot instead of
    ///   scanning every ZDO in the sector.  peer.ShouldSend(zdo) still handles
    ///   per-peer revision tracking so multiple peers can share the same snapshot
    ///   without interfering with each other.
    ///
    /// Thread safety:
    ///   IncreaseDataRevision and SendZDOToPeers2 both run on the Unity main thread.
    ///   No cross-thread mutation occurs — no lock required.
    /// </summary>
    public static class DirtyZdoTracker
    {
        private static HashSet<ZDOID> s_live     = new HashSet<ZDOID>(512);
        private static HashSet<ZDOID> s_snapshot = new HashSet<ZDOID>(512);

        /// <summary>
        /// Mark a ZDO as changed.  Called from the ZDO.IncreaseDataRevision and
        /// ZDO.IncreaseOwnerRevision Postfixes.
        /// </summary>
        public static void MarkDirty(ZDOID id)
        {
            s_live.Add(id);
        }

        /// <summary>
        /// Called once per ZDO send cycle (every ~50 ms) by MultiPeerSendPatch.
        /// Swaps the live and snapshot sets so CreateSyncListPatch can read a stable
        /// snapshot while new dirty entries continue to accumulate.
        /// </summary>
        public static void BeginCycle()
        {
            // Swap references — no allocation.
            var temp   = s_snapshot;
            s_snapshot = s_live;
            s_live     = temp;
            s_live.Clear();
        }

        /// <summary>
        /// The ZDOs that changed in the previous send cycle.
        /// Valid from <see cref="BeginCycle"/> until the next call to BeginCycle.
        /// </summary>
        public static HashSet<ZDOID> CycleSnapshot => s_snapshot;

        /// <summary>Current size of the live (in-progress) dirty set.</summary>
        public static int LiveCount => s_live.Count;
    }

    // Harmony patches removed: the dirty-tracking optimization cannot reliably
    // intercept all ZDO revision changes.  RPC_ZDOData directly assigns
    // DataRevision / OwnerRevision on received ZDOs without calling
    // IncreaseDataRevision or IncreaseOwnerRevision, so those updates would be
    // silently dropped for all other peers.  CreateSyncList now always runs
    // vanilla — see CreateSyncListPatch.cs.
}
