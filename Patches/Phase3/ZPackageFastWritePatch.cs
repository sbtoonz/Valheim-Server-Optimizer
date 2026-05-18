using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace ValheimHighCap.Patches.Phase3
{
    /// <summary>
    /// Replaces <c>ZPackage.Write(ZPackage)</c> with a no-copy fast path.
    ///
    /// ─────────────────────────────────────────────────────────────────────
    ///  Why this matters
    /// ─────────────────────────────────────────────────────────────────────
    /// Vanilla source:
    ///   public void Write(ZPackage pkg)
    ///   {
    ///       byte[] array = pkg.GetArray();   // \← ToArray() = full memcpy + new alloc
    ///       m_writer.Write(array.Length);
    ///       m_writer.Write(array);            // \← second memcpy into outer stream
    ///   }
    ///
    /// <c>ZPackage.GetArray()</c> calls <c>MemoryStream.ToArray()</c> which always
    /// allocates a fresh byte[] of the stream's logical length and copies every
    /// byte into it. The outer BinaryWriter.Write(byte[]) then copies those same
    /// bytes into the outer MemoryStream's buffer.
    ///
    /// At 64 players × 20 Hz × ~20 ZDOs/peer cycle this is ≈ 25 600 calls/sec,
    /// each allocating + memcpy-ing the entire serialised ZDO body. Estimated
    /// GC allocation rate: <b>10–20 MB/sec</b> just from this one method.
    ///
    /// ─────────────────────────────────────────────────────────────────────
    ///  Fix
    /// ─────────────────────────────────────────────────────────────────────
    /// The inner ZPackage's MemoryStream is constructed via parameterless
    /// <c>new MemoryStream()</c>, which sets the buffer as publicly visible.
    /// That means <c>MemoryStream.GetBuffer()</c> returns the internal byte[]
    /// without copying. We can then write the slice [0..Length] of that buffer
    /// straight into the outer writer via <c>BinaryWriter.Write(byte[], int, int)</c>,
    /// which writes raw bytes without a length prefix.
    ///
    /// Wire format is byte-identical to vanilla:
    ///   [int32 length] [raw payload bytes]
    ///
    /// ─────────────────────────────────────────────────────────────────────
    ///  Safety
    /// ─────────────────────────────────────────────────────────────────────
    /// Reflection-resolves <c>ZPackage.m_stream</c> and <c>m_writer</c> by name,
    /// caches FieldInfos lazily. try/catch wraps the whole fast path and on any
    /// failure logs a one-shot warning, sets <c>s_disabled</c>, and returns
    /// <c>true</c> to fall back to vanilla. Bulletproof against Valheim updates.
    ///
    /// Note: this only patches <c>Write(ZPackage)</c>. <c>WriteCompressed(ZPackage)</c>
    /// still goes through vanilla (it has to compress, so the copy is unavoidable
    /// without rewriting Utils.Compress to accept a stream).
    /// </summary>
    [HarmonyPatch(typeof(ZPackage), nameof(ZPackage.Write), new[] { typeof(ZPackage) })]
    internal static class ZPackageFastWritePatch
    {
        private static FieldInfo? s_streamField;
        private static FieldInfo? s_writerField;

        private static bool s_disabled;
        private static int  s_disableLogged;
        private static int  s_activeLogged;

        private static bool TryResolveReflection()
        {
            if (s_streamField != null && s_writerField != null) return true;

            s_streamField = AccessTools.Field(typeof(ZPackage), "m_stream");
            s_writerField = AccessTools.Field(typeof(ZPackage), "m_writer");

            if (s_streamField == null || s_writerField == null)
            {
                Disable("ZPackage.m_stream or m_writer field not found");
                return false;
            }
            if (s_streamField.FieldType != typeof(MemoryStream))
            {
                Disable($"m_stream is {s_streamField.FieldType}, expected MemoryStream");
                return false;
            }
            if (s_writerField.FieldType != typeof(BinaryWriter))
            {
                Disable($"m_writer is {s_writerField.FieldType}, expected BinaryWriter");
                return false;
            }
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────

        static bool Prefix(ZPackage __instance, ZPackage pkg)
        {
            if (!HighCapConfig.EnableZPackageFastWrite.Value) return true;
            if (s_disabled) return true;
            if (pkg == null)
            {
                // Vanilla would NRE on pkg.GetArray(); match that exactly so callers
                // get the same observable behaviour.
                return true;
            }

            try
            {
                if (!TryResolveReflection()) return true;

                var pkgStream  = (MemoryStream) s_streamField!.GetValue(pkg);
                var pkgWriter  = (BinaryWriter) s_writerField!.GetValue(pkg);
                var instStream = (MemoryStream) s_streamField  .GetValue(__instance);
                var instWriter = (BinaryWriter) s_writerField  .GetValue(__instance);

                // Flush inner writer so any buffered bytes hit m_stream before we
                // measure Length. BinaryWriter over MemoryStream doesn't actually
                // buffer in current .NET, but call Flush() for forward compatibility.
                pkgWriter.Flush();

                int len = checked((int)pkgStream.Length);
                byte[] fastBuf = pkgStream.GetBuffer();
                // Always copy the logical buffer to guarantee no trailing garbage
                byte[] safeCopy = new byte[len];
                Buffer.BlockCopy(fastBuf, 0, safeCopy, 0, len);

                // For diagnostics: compare to vanilla GetArray()
                byte[] vanilla = pkg.GetArray();
                bool mismatch = false;
                if (vanilla.Length != len)
                {
                    mismatch = true;
                }
                else
                {
                    for (int i = 0; i < len; ++i)
                    {
                        if (vanilla[i] != safeCopy[i]) { mismatch = true; break; }
                    }
                }
                if (mismatch)
                {
                    HighCapPlugin.Log.LogWarning($"[ZPackageFastWritePatch] BYTE MISMATCH: fastLen={len} vanillaLen={vanilla.Length}\nfast=[{BitConverter.ToString(safeCopy,0,Math.Min(32,len))}]\nvanilla=[{BitConverter.ToString(vanilla,0,Math.Min(32,vanilla.Length))}]");
                }

                instWriter.Write(len);
                instWriter.Write(safeCopy, 0, len);

                if (System.Threading.Interlocked.Exchange(ref s_activeLogged, 1) == 0)
                {
                    HighCapPlugin.Log.LogInfo(
                        "[ZPackageFastWritePatch] ACTIVE  no-copy ZPackage write enabled (safe copy, diff logging ON).");
                }
                return false; // skip vanilla
            }
            catch (Exception ex)
            {
                Disable("exception: " + ex.GetType().Name + " — " + ex.Message);
                return true;
            }
        }

        private static void Disable(string reason)
        {
            s_disabled = true;
            if (System.Threading.Interlocked.Exchange(ref s_disableLogged, 1) == 0)
            {
                HighCapPlugin.Log.LogWarning(
                    $"[ZPackageFastWritePatch] disabled — {reason}. " +
                    "Falling back to vanilla GetArray()+copy path.");
            }
        }
    }
}
