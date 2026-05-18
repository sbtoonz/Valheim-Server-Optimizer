using BepInEx.Configuration;

namespace ValheimHighCap
{
    /// <summary>
    /// All tunable parameters, loaded from BepInEx config at startup.
    /// Config file: BepInEx/config/com.valhoom.highcap.cfg
    /// </summary>
    public static class HighCapConfig
    {
        // ── Phase 1 ──────────────────────────────────────────────────────────

        /// <summary>Maximum concurrent players. Vanilla hardcodes 10.</summary>
        public static ConfigEntry<int> MaxPlayers = null!;

        /// <summary>
        /// Peers serviced per ZDO send frame.
        /// Vanilla services 1 peer/frame; at 50 players that means 0.4 Hz per peer.
        /// Set to 4–8 for 50 players, 8–16 for 100 players.
        /// Each extra peer adds CPU work proportional to their active-area ZDO count.
        /// </summary>
        public static ConfigEntry<int> PeersPerFrame = null!;

        /// <summary>
        /// Steam Networking send-rate cap per connection (bytes/sec).
        /// Vanilla = 153 600 (150 KB/s). At 100 players × 150 KB/s = 14.6 MB/s theoretical max.
        /// 1 048 576 = 1 MB/s per peer cap. Total egress = MaxPlayers × actual utilisation.
        /// Raising this has no effect unless the OS NIC can sustain the total.
        /// </summary>
        public static ConfigEntry<int> SteamSendRateBytesPerSec = null!;

        /// <summary>
        /// Per-peer ZDO send-queue ceiling (bytes).
        /// When a peer's queue exceeds this, ZDO sends to them are skipped for that tick.
        /// Vanilla = 10 240 (10 KB). Raise to 65 536 for high-latency or high-density peers.
        /// </summary>
        public static ConfigEntry<int> ZdoSendQueueSizeBytes = null!;

        /// <summary>
        /// Minimum free bytes required in the send queue before a ZDO batch is written.
        /// Vanilla = 2 048. Raising this gives slow peers a larger cushion before being starved.
        /// </summary>
        public static ConfigEntry<int> ZdoSendQueueMinFreeBytes = null!;

        // ── Phase 2 ──────────────────────────────────────────────────────────

        /// <summary>
        /// Replace full sector scan with dirty-set lookup for established peers.
        /// Reduces CreateSyncList from O(sector_ZDOs) to O(dirty_ZDOs) for peers that
        /// have already received an initial world snapshot.
        /// Disable if you observe missed ZDO updates after long sessions.
        /// </summary>
        public static ConfigEntry<bool> EnableDirtyTracking = null!;

        /// <summary>
        /// Minimum number of ZDOs a peer must have acknowledged before dirty-tracking
        /// activates for them. Prevents skipping the initial world-load full scan.
        /// </summary>
        public static ConfigEntry<int> DirtyTrackingMinKnownZdos = null!;

        /// <summary>Log periodic dirty-tracker sweep statistics.</summary>
        public static ConfigEntry<bool> DirtyTrackingVerboseLogging = null!;

        /// <summary>How often to emit the dirty-tracker stats line (seconds).</summary>
        public static ConfigEntry<float> DirtyTrackingLogIntervalSeconds = null!;

        /// <summary>
        /// Number of consecutive send cycles a ZDO stays in the dirty set after
        /// changing. MUST be ≥ ceil(MaxPlayers / PeersPerFrame) + safety margin,
        /// otherwise peers serviced on a slower rotation than the change cadence
        /// will miss updates and silently desync (chests don't open, doors don't
        /// toggle, item pickups don't reflect). Default 16 = safe for 64 players
        /// at PeersPerFrame=6 (full rotation ≈ 11 cycles).
        /// </summary>
        public static ConfigEntry<int> DirtyTrackingStickyCycles = null!;

        /// <summary>
        /// Filter ZDO-targeted broadcast RPCs (damage numbers, hit effects, status changes)
        /// to only peers within SpatialRpcRadius. Reduces broadcast fan-out from O(N) to
        /// O(peers_in_range). Recommended for servers where players spread across the map.
        /// </summary>
        public static ConfigEntry<bool> EnableSpatialRpc = null!;

        /// <summary>
        /// World-space radius for spatial RPC culling (Unity units ≈ metres).
        /// 64 u ≈ 1 zone, 192 u ≈ 3 zones. Combat is rarely visible past 200 u.
        /// </summary>
        public static ConfigEntry<float> SpatialRpcRadius = null!;

        // ── Phase 3 ──────────────────────────────────────────────────────────

        /// <summary>
        /// Replace <c>ZPackage.Write(ZPackage)</c> with a no-copy variant that
        /// streams the inner package's internal MemoryStream buffer directly
        /// into the outer package instead of calling <c>GetArray()</c> (which
        /// allocates a fresh byte[] and memcpys the entire buffer on every call).
        /// At 64 players this saves ~10–20 MB/sec of GC allocation. Pure managed
        /// code — no native interop. Auto-disables on any reflection mismatch.
        /// </summary>
        public static ConfigEntry<bool> EnableZPackageFastWrite = null!;

        // ── Metrics ──────────────────────────────────────────────────────────

        // ── Phase 1 — Adaptive ────────────────────────────────────────────

        /// <summary>
        /// Interval between ZDO send cycles (seconds). Vanilla = 0.05 (20 Hz).
        /// Lower values increase ZDO update frequency at the cost of CPU and bandwidth.
        /// </summary>
        public static ConfigEntry<float> SendIntervalSeconds = null!;

        /// <summary>
        /// Automatically adjust effective PeersPerFrame up/down based on server
        /// frame time. Protects potato hardware from overload; lets beast hardware
        /// serve more peers. PeersPerFrame becomes the manual override max.
        /// </summary>
        public static ConfigEntry<bool> AdaptiveMode = null!;

        /// <summary>
        /// Target server frame time (ms). Adaptive mode reduces peers/frame when
        /// the EMA of frame time exceeds this, and raises it when below 70% of it.
        /// 40 ms = 25 fps server-side (safe for most hardware).
        /// </summary>
        public static ConfigEntry<float> AdaptiveTargetFrameMs = null!;

        /// <summary>Floor: adaptive mode will not go below this many peers/frame.</summary>
        public static ConfigEntry<int> AdaptiveMinPeers = null!;

        // ── Metrics ──────────────────────────────────────────────────────────

        /// <summary>Log per-peer ZDO stats periodically to the BepInEx console.</summary>
        public static ConfigEntry<bool>  EnableMetrics          = null!;

        /// <summary>How often to emit the metrics snapshot (seconds).</summary>
        public static ConfigEntry<float> MetricsIntervalSeconds = null!;

        // ─────────────────────────────────────────────────────────────────────

        public static void Bind(ConfigFile cfg)
        {
            // Phase 1
            MaxPlayers = cfg.Bind(
                "Phase1.PlayerLimit", "MaxPlayers", 64,
                new ConfigDescription(
                    "Maximum concurrent players. Vanilla = 10. " +
                    "Each additional player costs ~15-25 MB RAM and ~1-2% CPU @ 8 cores.",
                    new AcceptableValueRange<int>(10, 256)));

            PeersPerFrame = cfg.Bind(
                "Phase1.ZdoSend", "PeersPerFrame", 6,
                new ConfigDescription(
                    "ZDO-bearing peers serviced per Unity frame. " +
                    "Vanilla = 1. Recommended: ceil(MaxPlayers / 8). " +
                    "Higher values flatten per-peer ZDO latency at the cost of main-thread CPU.",
                    new AcceptableValueRange<int>(1, 64)));

            SteamSendRateBytesPerSec = cfg.Bind(
                "Phase1.Steam", "SendRateBytesPerSec", 1_048_576,
                new ConfigDescription(
                    "Steam networking send-rate cap per connection (bytes/sec). " +
                    "Vanilla = 153600. Recommended = 1048576 (1 MB/s). " +
                    "Ensure your server NIC can sustain MaxPlayers × typical utilisation.",
                    new AcceptableValueRange<int>(153_600, 10_485_760)));

            ZdoSendQueueSizeBytes = cfg.Bind(
                "Phase1.ZdoSend", "SendQueueSizeBytes", 65_536,
                new ConfigDescription(
                    "Per-peer ZDO send-queue ceiling (bytes). Vanilla = 10240. " +
                    "Increasing this prevents ZDO starvation for high-latency peers.",
                    new AcceptableValueRange<int>(10_240, 524_288)));

            ZdoSendQueueMinFreeBytes = cfg.Bind(
                "Phase1.ZdoSend", "SendQueueMinFreeBytes", 8_192,
                new ConfigDescription(
                    "Minimum free bytes required before writing a ZDO batch. Vanilla = 2048.",
                    new AcceptableValueRange<int>(1_024, 65_536)));

            // Phase 2
            EnableDirtyTracking = cfg.Bind(
                "Phase2.DirtyTracking", "Enable", false,
                "Use dirty-set based ZDO sync for established peers. " +
                "Reduces per-tick CPU from O(sector_ZDOs × peers) to O(dirty_ZDOs × peers). " +
                "Disabled by default — enable only after confirming vanilla ZDO sync is stable.");

            DirtyTrackingMinKnownZdos = cfg.Bind(
                "Phase2.DirtyTracking", "MinKnownZdos", 50,
                new ConfigDescription(
                    "Minimum acknowledged ZDOs before dirty-tracking activates for a peer. " +
                    "Peers below this threshold use the full sector scan (initial world load).",
                    new AcceptableValueRange<int>(1, 5_000)));

            DirtyTrackingVerboseLogging = cfg.Bind(
                "Phase2.DirtyTracking", "VerboseLogging", false,
                "Emit a periodic '[DirtyZdoTracker] cycle=...' line with sweep statistics. " +
                "Useful for debugging; leave off in production.");

            DirtyTrackingStickyCycles = cfg.Bind(
                "Phase2.DirtyTracking", "StickyCycles", 16,
                new ConfigDescription(
                    "Number of cycles a changed ZDO stays in the dirty set so every peer in " +
                    "the round-robin rotation gets at least one chance to receive the update. " +
                    "Must be ≥ ceil(MaxPlayers / PeersPerFrame). Too low → missed updates " +
                    "(chest doesn't open, door doesn't toggle). Too high → more dirty entries " +
                    "per cycle (cheap, harmless).",
                    new AcceptableValueRange<int>(1, 256)));

            DirtyTrackingLogIntervalSeconds = cfg.Bind(
                "Phase2.DirtyTracking", "LogIntervalSeconds", 60f,
                new ConfigDescription(
                    "Interval between dirty-tracker stat log lines (seconds). " +
                    "Only used when VerboseLogging = true.",
                    new AcceptableValueRange<float>(1f, 3600f)));

            EnableSpatialRpc = cfg.Bind(
                "Phase2.SpatialRpc", "Enable", false,
                "Spatially cull known-cosmetic broadcast RPCs (damage numbers, hit effects, " +
                "footsteps) to peers within SpatialRpcRadius. Uses a blacklist approach: " +
                "only explicitly listed cosmetic RPCs get culled, all other RPCs pass through " +
                "to all peers unchanged. Safe to enable — does NOT affect gameplay interactions " +
                "(doors, chests, pickups, damage). Reduces cosmetic RPC fan-out at scale.");

            SpatialRpcRadius = cfg.Bind(
                "Phase2.SpatialRpc", "RadiusUnits", 192f,
                new ConfigDescription(
                    "World-space radius for spatial RPC culling (Unity units). " +
                    "192 ≈ 3 zones. Increase for large-scale siege/pvp scenarios.",
                    new AcceptableValueRange<float>(64f, 640f)));

            // Phase 3
            EnableZPackageFastWrite = cfg.Bind(
                "Phase3.ZPackageFastWrite", "Enable", false,
                "EXPERIMENTAL — disabled by default. " +
                "Replace ZPackage.Write(ZPackage) GetArray()+memcpy with a direct " +
                "stream-to-stream write using MemoryStream.GetBuffer(). On paper " +
                "writes byte-identical wire format, but empirically observed to break " +
                "client interaction RPCs (door open, item pickup, chest use) — root " +
                "cause unknown. Leave OFF unless investigating.");

            // Phase 1 — Adaptive
            SendIntervalSeconds = cfg.Bind(
                "Phase1.ZdoSend", "SendIntervalSeconds", 0.05f,
                new ConfigDescription(
                    "Interval between ZDO send cycles (seconds). Vanilla = 0.05 (20 Hz). " +
                    "Lower = more responsive ZDO updates, higher CPU/bandwidth cost.",
                    new AcceptableValueRange<float>(0.02f, 0.2f)));

            AdaptiveMode = cfg.Bind(
                "Phase1.Adaptive", "Enable", true,
                "Auto-scale PeersPerFrame based on server frame time. " +
                "Scales down when the server is struggling, up when it has headroom. " +
                "PeersPerFrame acts as the ceiling.");

            AdaptiveTargetFrameMs = cfg.Bind(
                "Phase1.Adaptive", "TargetFrameMs", 40f,
                new ConfigDescription(
                    "Target server frame time (ms) for adaptive scaling. " +
                    "40 ms = 25 fps server-side. Reduce on fast hardware.",
                    new AcceptableValueRange<float>(16f, 100f)));

            AdaptiveMinPeers = cfg.Bind(
                "Phase1.Adaptive", "MinPeersPerFrame", 1,
                new ConfigDescription(
                    "Minimum peers/frame adaptive mode will throttle down to.",
                    new AcceptableValueRange<int>(1, 8)));

            // Metrics
            EnableMetrics = cfg.Bind(
                "Metrics", "Enable", true,
                "Write per-peer ZDO stats to the BepInEx console.");

            MetricsIntervalSeconds = cfg.Bind(
                "Metrics", "IntervalSeconds", 30f,
                new ConfigDescription(
                    "How often to write the metrics snapshot (seconds).",
                    new AcceptableValueRange<float>(5f, 300f)));
        }
    }
}
