using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimHighCap.Patches.Phase2
{
    /// <summary>
    /// Sweep-based dirty-ZDO tracker.
    ///
    /// Why a sweep instead of Harmony method patches:
    ///   <c>ZDO.DataRevision</c> and <c>ZDO.OwnerRevision</c> are auto-properties
    ///   (<c>{ get; set; }</c>).  The compiler emits trivial 1-line setters that the
    ///   JIT eagerly inlines into every caller within the same assembly.  Harmony
    ///   patches on <c>set_DataRevision</c> / <c>set_OwnerRevision</c> (or on
    ///   <c>IncreaseDataRevision</c> / <c>IncreaseOwnerRevision</c>) silently fail
    ///   to fire for the inlined call sites — including all the places in
    ///   <c>ZDOMan.RPC_ZDOData</c> that directly assign the revision values.
    ///
    ///   Sweeping the global ZDO dictionary once per send cycle and comparing each
    ///   ZDO's current revision to a cached snapshot catches EVERY mutation
    ///   regardless of how it happened.  Bulletproof, no false negatives.
    ///
    /// Cost analysis (100-player server):
    ///   • Sweep:  ~50 k ZDOs × 1 dict lookup + 2 compares ≈ 150 k ops / 50 ms
    ///                                                     ≈ 3 M ops / sec.
    ///   • Saved:  ~190 k peer.ShouldSend dictionary lookups per cycle
    ///             (sector_zdos × peers × clean_ratio).
    ///   Net: large win — the sweep is much cheaper than the saved per-peer work.
    ///
    /// Thread safety:
    ///   ZDOMan.Update and ZDO mutations are main-thread only.  No locks needed.
    /// </summary>
    public static class DirtyZdoTracker
    {
        // Cached revision snapshot per ZDO from the previous sweep.
        private static readonly Dictionary<ZDOID, RevSnapshot> s_lastSeen =
            new Dictionary<ZDOID, RevSnapshot>(8192);

        // ZDOs whose revisions changed in the most recent sweep.
        private static HashSet<ZDOID> s_dirty = new HashSet<ZDOID>(1024);

        // Reusable scratch sets — avoid per-cycle allocation.
        private static HashSet<ZDOID> s_scratchDirty = new HashSet<ZDOID>(1024);
        private static readonly List<ZDOID> s_removeBuf = new List<ZDOID>(256);

        // Reflection handle to ZDOMan.m_objectsByID — resolved lazily on first sweep
        // (ZDOMan.instance may not exist when the plugin's static ctor runs).
        private static FieldInfo? s_objectsByIDField;

        // Diagnostics — periodic log of dirty counts.
        private static int   s_cycleCount;
        private static int   s_lastLoggedTotal;
        private static int   s_lastLoggedDirty;
        private static bool  s_disabled;

        private readonly struct RevSnapshot
        {
            public readonly uint   Data;
            public readonly ushort Owner;
            public RevSnapshot(uint d, ushort o) { Data = d; Owner = o; }
        }

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called once per ZDO send cycle (~50 ms) by <see cref="MultiPeerSendPatch"/>
        /// at the very start of a new cycle, BEFORE any peer is serviced.
        /// Sweeps the global ZDO dictionary and produces the dirty set used by
        /// <see cref="CreateSyncListPatch"/> for the entire cycle.
        /// </summary>
        public static void BeginCycle()
        {
            if (s_disabled) return;

            try
            {
                var zdoMan = ZDOMan.instance;
                if (zdoMan == null) return;

                if (s_objectsByIDField == null)
                {
                    s_objectsByIDField =
                        AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
                    if (s_objectsByIDField == null)
                    {
                        Disable("m_objectsByID field not found");
                        return;
                    }
                }

                var objectsByID =
                    (Dictionary<ZDOID, ZDO>)s_objectsByIDField.GetValue(zdoMan);
                if (objectsByID == null) return;

                // Build the new dirty set into the scratch buffer, then swap.
                s_scratchDirty.Clear();

                foreach (var kv in objectsByID)
                {
                    ZDOID id = kv.Key;
                    ZDO   z  = kv.Value;

                    uint   curData  = z.DataRevision;
                    ushort curOwner = z.OwnerRevision;

                    if (s_lastSeen.TryGetValue(id, out var prev))
                    {
                        if (curData != prev.Data || curOwner != prev.Owner)
                        {
                            s_scratchDirty.Add(id);
                            s_lastSeen[id] = new RevSnapshot(curData, curOwner);
                        }
                    }
                    else
                    {
                        // First time we see this ZDO → treat as dirty so it gets sent.
                        s_scratchDirty.Add(id);
                        s_lastSeen[id] = new RevSnapshot(curData, curOwner);
                    }
                }

                // Drop cached entries for ZDOs that no longer exist (destroyed).
                if (s_lastSeen.Count > objectsByID.Count + 256)
                {
                    s_removeBuf.Clear();
                    foreach (var id in s_lastSeen.Keys)
                        if (!objectsByID.ContainsKey(id))
                            s_removeBuf.Add(id);
                    foreach (var id in s_removeBuf)
                        s_lastSeen.Remove(id);
                }

                // Swap scratch → live dirty set.
                var tmp = s_dirty;
                s_dirty = s_scratchDirty;
                s_scratchDirty = tmp;

                // Periodic diagnostics — every ~5 s (100 cycles @ 50 ms).
                s_cycleCount++;
                if (s_cycleCount % 100 == 0)
                {
                    s_lastLoggedTotal = objectsByID.Count;
                    s_lastLoggedDirty = s_dirty.Count;
                    HighCapPlugin.Log.LogInfo(
                        $"[DirtyZdoTracker] cycle={s_cycleCount} totalZdos={s_lastLoggedTotal} " +
                        $"dirty={s_lastLoggedDirty} cached={s_lastSeen.Count}");
                }
            }
            catch (Exception ex)
            {
                Disable($"exception in BeginCycle: {ex.GetType().Name}: {ex.Message}");
                HighCapPlugin.Log.LogError($"[DirtyZdoTracker] stack: {ex.StackTrace}");
            }
        }

        private static void Disable(string reason)
        {
            s_disabled = true;
            // Empty the dirty set so CreateSyncListPatch (if still active) skips every
            // known ZDO — but CreateSyncListPatch also has its own self-disable path.
            s_dirty.Clear();
            HighCapPlugin.Log.LogError(
                $"[DirtyZdoTracker] DISABLING sweep: {reason}");
        }

        /// <summary>True if the ZDO changed (or is new) since the previous cycle.</summary>
        public static bool IsDirty(ZDOID id) => s_dirty.Contains(id);

        /// <summary>Size of the dirty set produced by the most recent <see cref="BeginCycle"/>.</summary>
        public static int LiveCount => s_dirty.Count;

        /// <summary>The dirty ZDO set from the most recent cycle. Stable for the duration of the cycle.</summary>
        public static HashSet<ZDOID> CycleSnapshot => s_dirty;
    }
}


