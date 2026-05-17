using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimHighCap.Patches.Phase1
{
    /// <summary>
    /// Replaces the hardcoded 10 240 B (10 KB) send-queue threshold in
    /// <c>ZDOMan.SendZDOs</c> with the configured values.
    ///
    /// Vanilla logic (the two relevant constants):
    ///   if (!flush &amp;&amp; sendQueueSize > 10240) return false;   ← skip congested peer
    ///   int num = 10240 - sendQueueSize;
    ///   if (num &lt; 2048) return false;                          ← insufficient headroom
    ///
    /// With 10 KB queues and 100 players each sending player-ZDOs every ~200 ms,
    /// the queues fill in milliseconds and peers stop receiving ZDO updates entirely.
    /// Raising to 64 KB / 8 KB gives each peer a deeper buffer before starvation.
    ///
    /// NOTE: 10240 and 2048 are the only occurrences of these exact values in SendZDOs,
    /// so the transpiler patch is unambiguous without needing context-sensitive matching.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
    internal static class SendQueueSizePatch
    {
        static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instr in instructions)
            {
                // 10240 → configured queue ceiling
                if (instr.opcode == OpCodes.Ldc_I4 && (int)instr.operand == 10240)
                {
                    var replacement = CodeInstruction.Call(
                        typeof(SendQueueSizePatch), nameof(GetQueueCeiling));
                    // Transfer labels/exception blocks so branch targets remain valid.
                    replacement.labels.AddRange(instr.labels);
                    replacement.blocks.AddRange(instr.blocks);
                    yield return replacement;
                    continue;
                }

                // 2048 → configured minimum headroom
                if (instr.opcode == OpCodes.Ldc_I4 && (int)instr.operand == 2048)
                {
                    var replacement = CodeInstruction.Call(
                        typeof(SendQueueSizePatch), nameof(GetMinHeadroom));
                    replacement.labels.AddRange(instr.labels);
                    replacement.blocks.AddRange(instr.blocks);
                    yield return replacement;
                    continue;
                }

                yield return instr;
            }
        }

        public static int GetQueueCeiling()  => HighCapConfig.ZdoSendQueueSizeBytes.Value;
        public static int GetMinHeadroom()   => HighCapConfig.ZdoSendQueueMinFreeBytes.Value;
    }
}
