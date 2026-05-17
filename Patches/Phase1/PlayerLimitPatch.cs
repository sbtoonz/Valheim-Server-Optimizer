using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimHighCap.Patches.Phase1
{
    /// <summary>
    /// Replaces the hardcoded <c>GetNrOfPlayers() >= 10</c> connection-rejection
    /// check in <c>ZNet.RPC_PeerInfo</c> with a runtime-configurable limit.
    ///
    /// Vanilla IL sequence being replaced:
    ///   call     instance int32 ZNet::GetNrOfPlayers()
    ///   ldc.i4.s 10                  ← we target this constant
    ///   bge.s    reject_label
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    internal static class PlayerLimitPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator il)
        {
            var getNrOfPlayers = AccessTools.Method(typeof(ZNet), "GetNrOfPlayers");
            bool patchNext = false;

            foreach (var instr in instructions)
            {
                // Watch for the call to GetNrOfPlayers()
                if (instr.Calls(getNrOfPlayers))
                {
                    patchNext = true;
                    yield return instr;
                    continue;
                }

                // The very next integer constant after GetNrOfPlayers() is the limit
                if (patchNext &&
                    (instr.opcode == OpCodes.Ldc_I4_S ||
                     instr.opcode == OpCodes.Ldc_I4   ||
                     instr.opcode == OpCodes.Ldc_I4_0 ||
                     instr.opcode == OpCodes.Ldc_I4_1))
                {
                    patchNext = false;
                    // Replace the constant with a call to our config-reader so the
                    // limit is re-read at runtime (supports live config reload).
                    var replacement = CodeInstruction.Call(
                        typeof(PlayerLimitPatch), nameof(GetConfiguredMax));
                    // Transfer labels/exception blocks so branch targets remain valid.
                    replacement.labels.AddRange(instr.labels);
                    replacement.blocks.AddRange(instr.blocks);
                    yield return replacement;
                    continue;
                }

                patchNext = false;
                yield return instr;
            }
        }

        // Called from JIT-patched IL — must remain public/static and non-inline.
        public static int GetConfiguredMax() => HighCapConfig.MaxPlayers.Value;
    }
}
