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

        // Sticky dirty map: ZDOID -> remaining cycles before this entry expires.
        // When a ZDO changes, its entry is (re)set to StickyCycles. Each cycle,
        // every entry's countdown decrements; entries that hit 0 are removed.
        // This guarantees that every peer in the round-robin rotation
        // (ceil(MaxPlayers / PeersPerFrame) cycles per pass) sees the dirty mark
        // at least once before it disappears — otherwise peers serviced on a
        // slower rotation than the change cadence silently desync.
        private static Dictionary<ZDOID, int> s_dirty = new Dictionary<ZDOID, int>(1024);

        // Reusable scratch list for entries that hit 0 this cycle.
        private static readonly List<ZDOID> s_expireBuf = new List<ZDOID>(256);
        private static readonly List<ZDOID> s_removeBuf = new List<ZDOID>(256);

        // Reflection handle to ZDOMan.m_objectsByID — resolved lazily on first sweep
        // (ZDOMan.instance may not exist when the plugin's static ctor runs).
        private static FieldInfo? s_objectsByIDField;

        // Diagnostics — periodic log of dirty counts.
        private static int   s_cycleCount;
        private static int   s_lastLoggedTotal;
        private static int   s_lastLoggedDirty;
        private static float s_lastLogTime;
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

                int stickyCycles = HighCapConfig.DirtyTrackingStickyCycles.Value;

                // 1) Decrement all existing sticky entries; collect expired ones.
                //    (Done first so newly-dirtied ZDOs in step 2 don't get decremented.)
                s_expireBuf.Clear();
                foreach (var kv in s_dirty)
                {
                    int remaining = kv.Value - 1;
                    if (remaining <= 0) s_expireBuf.Add(kv.Key);
                }
                foreach (var id in s_expireBuf) s_dirty.Remove(id);

                // Mutate values in-place by re-iterating keys. (Can't mutate
                // dictionary values during foreach, so copy keys to scratch.)
                s_removeBuf.Clear();
                foreach (var kv in s_dirty) s_removeBuf.Add(kv.Key);
                foreach (var id in s_removeBuf) s_dirty[id] = s_dirty[id] - 1;

                // 2) Sweep all live ZDOs; (re)set sticky for any whose revisions
                //    changed since the previous snapshot, and update the snapshot.
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
                            s_dirty[id] = stickyCycles;
                            s_lastSeen[id] = new RevSnapshot(curData, curOwner);
                        }
                    }
                    else
                    {
                        // First time we see this ZDO → treat as dirty so it gets sent.
                        s_dirty[id] = stickyCycles;
                        s_lastSeen[id] = new RevSnapshot(curData, curOwner);
                    }
                }

                // 3) Drop snapshot cache entries for ZDOs that no longer exist (destroyed).
                if (s_lastSeen.Count > objectsByID.Count + 256)
                {
                    s_removeBuf.Clear();
                    foreach (var id in s_lastSeen.Keys)
                        if (!objectsByID.ContainsKey(id))
                            s_removeBuf.Add(id);
                    foreach (var id in s_removeBuf)
                    {
                        s_lastSeen.Remove(id);
                        s_dirty.Remove(id);
                    }
                }

                // Periodic diagnostics — gated by Phase2.DirtyTracking.VerboseLogging,
                // emitted every Phase2.DirtyTracking.LogIntervalSeconds.
                s_cycleCount++;
                if (HighCapConfig.DirtyTrackingVerboseLogging.Value)
                {
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (now - s_lastLogTime >= HighCapConfig.DirtyTrackingLogIntervalSeconds.Value)
                    {
                        s_lastLogTime     = now;
                        s_lastLoggedTotal = objectsByID.Count;
                        s_lastLoggedDirty = s_dirty.Count;
                        HighCapPlugin.Log.LogInfo(
                            $"[DirtyZdoTracker] cycle={s_cycleCount} totalZdos={s_lastLoggedTotal} " +
                            $"dirty={s_lastLoggedDirty} cached={s_lastSeen.Count}");
                    }
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

        /// <summary>True if the ZDO changed within the last StickyCycles cycles.</summary>
        public static bool IsDirty(ZDOID id) => s_dirty.ContainsKey(id);

        /// <summary>Size of the dirty set (count of ZDOs currently within their sticky window).</summary>
        public static int LiveCount => s_dirty.Count;
    }
}


