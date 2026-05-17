using System.Collections.Generic;
using HarmonyLib;

namespace ValheimHighCap.Patches.Phase2
{
    /// <summary>
    /// Placeholder for a future optimised <c>ZDOMan.CreateSyncList</c> patch.
    ///
    /// The dirty-tracking approach (skipping <c>ShouldSend</c> for unchanged ZDOs)
    /// is fundamentally incompatible with how the game propagates ZDO changes:
    ///
    ///   • <c>ZDOMan.RPC_ZDOData</c> directly assigns <c>DataRevision</c> and
    ///     <c>OwnerRevision</c> on received ZDOs without calling the private
    ///     <c>IncreaseDataRevision</c> / <c>IncreaseOwnerRevision</c> methods.
    ///     Any ZDO update originating from a client (health, position, inventory,
    ///     etc.) therefore bypasses every Harmony method-level patch and would
    ///     silently fail to be forwarded to other peers.
    ///
    /// Until a reliable revision-change hook exists this patch always defers to
    /// vanilla — O(sector_ZDOs) per peer per cycle but provably correct.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "CreateSyncList")]
    internal static class CreateSyncListPatch
    {
        static bool Prefix(ZDOMan __instance, object peer, List<ZDO> toSync)
        {
            // Always run vanilla — Phase2 optimisation is not yet implemented.
            return true;
        }
    }
}
