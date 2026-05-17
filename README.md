# ValheimHighCap

BepInEx plugin for the **Valheim dedicated server** that raises the hard-coded 10-player cap to **up to 256** and adds the network throughput optimisations needed to actually support that many players.

> **Server-only.** This plugin does nothing on the game client and clients do **not** need it installed. Connecting clients only need a vanilla, version-matched Valheim install.

---

## Table of contents

1. [Quick install](#1-quick-install)
2. [Requirements](#2-requirements)
3. [Installation (step-by-step)](#3-installation-step-by-step)
4. [Configuration](#4-configuration)
5. [Recommended presets](#5-recommended-presets)
6. [Monitoring & metrics](#6-monitoring--metrics)
7. [Tuning guide](#7-tuning-guide)
8. [Troubleshooting](#8-troubleshooting)
9. [How it works (technical overview)](#9-how-it-works-technical-overview)
10. [Updating Valheim](#10-updating-valheim)
11. [Uninstalling](#11-uninstalling)

---

## 1. Quick install

1. Install **BepInEx 5.4.21+** (server build) on your dedicated server.
2. Drop `ValheimHighCap.dll` into `BepInEx/plugins/`.
3. Start the server once to generate `BepInEx/config/com.valhoom.highcap.cfg`.
4. Edit that file: set `MaxPlayers = 64` (or whatever cap you want, up to 256).
5. Restart the server. Done.

The plugin auto-detects the server process (`valheim_server.exe`) and refuses to load on the game client, so a stray copy on a player's PC won't break anything.

---

## 2. Requirements

| Component | Minimum | Recommended |
|---|---|---|
| **Valheim dedicated server** | Latest stable | Latest stable |
| **BepInEx** | 5.4.21 (server build) | 5.4.22+ |
| **Server CPU** | 4 cores | 8+ cores |
| **Server RAM** | 4 GB | 8–16 GB for 64+ players |
| **Server NIC** | 50 Mbps up | 1 Gbps for 100+ players |
| **OS** | Windows or Linux | Linux (better TCP stack under load) |

**Rough sizing guide:** each additional player above the vanilla 10 costs roughly **15–25 MB RAM** and **1–2 % of one CPU core** of additional server load at steady state.

---

## 3. Installation (step-by-step)

### 3a. Install BepInEx on the server

If you don't already have BepInEx:

1. Download **BepInEx 5.x for Unity Mono x64** from the [official BepInEx releases page](https://github.com/BepInEx/BepInEx/releases). Pick the **`BepInEx_x64_5.x.x.zip`** asset.
2. Extract the contents directly into the **Valheim dedicated server root folder** (the folder containing `valheim_server.exe` on Windows or `valheim_server.x86_64` on Linux).
3. **Start the server once** with your normal launch script so BepInEx initialises and creates its folder structure (`BepInEx/plugins/`, `BepInEx/config/`, etc.). Then shut it back down.

### 3b. Linux only — set the preloader

On Linux, edit your launch script (`start_server.sh`) and add the BepInEx preloader exports **before** the `./valheim_server.x86_64` line:

```bash
export DOORSTOP_ENABLE=TRUE
export DOORSTOP_INVOKE_DLL_PATH=./BepInEx/core/BepInEx.Preloader.dll
export DYLD_LIBRARY_PATH=./doorstop_libs:$DYLD_LIBRARY_PATH
export LD_LIBRARY_PATH=./doorstop_libs:$LD_LIBRARY_PATH
export LD_PRELOAD=libdoorstop_x64.so:$LD_PRELOAD
```

(The official BepInEx README has the full Linux setup; this is the short version.)

### 3c. Install the plugin

1. Copy `ValheimHighCap.dll` into `<server root>/BepInEx/plugins/`.
2. Start the server. Within a few seconds of startup, the console should print:

   ```
   [Info   :   BepInEx] Loading [ValheimHighCap 1.0.0]
   ```

3. Stop the server again — the first run generated `BepInEx/config/com.valhoom.highcap.cfg`. You can now edit it (see next section).

### 3d. Verify it's working

Start the server, connect with one client, and watch the console. You should see, within ~30 seconds:

```
[Info   :ValheimHighCap] [HighCap Metrics] 14:22:01 UTC
  Players      :   1 / 64
  Total ZDOs   :  20,395
  ...
```

If `Players : 1 / 64` shows up, the cap is raised and the plugin is active.

---

## 4. Configuration

The config file lives at `BepInEx/config/com.valhoom.highcap.cfg`. It's a plain INI-style file you can edit in any text editor. **The server reloads it on restart** — you don't need to rebuild anything.

Every option below is explained in the file itself with safe value ranges and defaults.

### Phase 1 — Player cap & basic networking (always on)

| Key | Default | What it does |
|---|---|---|
| `Phase1.PlayerLimit.MaxPlayers` | `64` | Maximum concurrent players. Range 10–256. |
| `Phase1.ZdoSend.PeersPerFrame` | `6` | Peers serviced per server frame for ZDO sync. Vanilla = 1. Roughly `ceil(MaxPlayers / 8)`. |
| `Phase1.ZdoSend.SendIntervalSeconds` | `0.05` | Time between ZDO send cycles. Vanilla = 0.05 (20 Hz). Lower = more responsive, more CPU. |
| `Phase1.ZdoSend.SendQueueSizeBytes` | `65536` | Per-peer outbound ZDO queue ceiling. Vanilla = 10240. Higher prevents starvation for high-latency peers. |
| `Phase1.ZdoSend.SendQueueMinFreeBytes` | `8192` | Minimum free queue bytes before a batch is written. Vanilla = 2048. |
| `Phase1.Steam.SendRateBytesPerSec` | `1048576` | Steam Networking send-rate cap per connection (1 MB/s). Vanilla = 153600. |

### Phase 1 — Adaptive throttling

| Key | Default | What it does |
|---|---|---|
| `Phase1.Adaptive.Enable` | `true` | Auto-scale `PeersPerFrame` based on server frame time. `PeersPerFrame` becomes the ceiling. |
| `Phase1.Adaptive.TargetFrameMs` | `40` | Target server frame time. Adaptive throttles down if exceeded, up when 30 % under. 40 ms = 25 fps server-side. |
| `Phase1.Adaptive.MinPeersPerFrame` | `1` | Floor for adaptive throttling. |

### Phase 2 — Dirty ZDO tracking (opt-in)

| Key | Default | What it does |
|---|---|---|
| `Phase2.DirtyTracking.Enable` | `false` | Use a per-cycle dirty-set sweep to skip clean ZDOs in `CreateSyncList`. Big CPU win on populous servers. |
| `Phase2.DirtyTracking.MinKnownZdos` | `50` | Peers below this acknowledged-ZDO count use the full vanilla scan (initial world load). |
| `Phase2.DirtyTracking.VerboseLogging` | `false` | Emit periodic `[DirtyZdoTracker] cycle=...` diagnostic line. |
| `Phase2.DirtyTracking.LogIntervalSeconds` | `60` | Interval between diagnostic lines when verbose is on. |

### Phase 2 — Spatial RPC culling (on by default)

| Key | Default | What it does |
|---|---|---|
| `Phase2.SpatialRpc.Enable` | `true` | Cull broadcast RPCs (damage numbers, hit effects) to peers within `RadiusUnits` of the source ZDO. Targeted RPCs (door open, item pickup) are never culled. |
| `Phase2.SpatialRpc.RadiusUnits` | `192` | World-space radius for spatial culling (Unity units ≈ metres). 192 ≈ 3 zones. |

### Metrics

| Key | Default | What it does |
|---|---|---|
| `Metrics.Enable` | `true` | Log a per-peer ZDO stats block to the BepInEx console. |
| `Metrics.IntervalSeconds` | `30` | How often to emit the metrics block. Range 5–300. |

---

## 5. Recommended presets

Drop-in starting points. Tune from here based on observed metrics.

### Small group (15–25 players)

```
Phase1.PlayerLimit.MaxPlayers           = 25
Phase1.ZdoSend.PeersPerFrame            = 3
Phase1.ZdoSend.SendQueueSizeBytes       = 32768
Phase1.Adaptive.Enable                  = true
Phase1.Adaptive.TargetFrameMs           = 33
Phase2.DirtyTracking.Enable             = false
Phase2.SpatialRpc.Enable                = true
```

### Mid server (50 players)

```
Phase1.PlayerLimit.MaxPlayers           = 50
Phase1.ZdoSend.PeersPerFrame            = 6
Phase1.ZdoSend.SendQueueSizeBytes       = 65536
Phase1.Adaptive.Enable                  = true
Phase1.Adaptive.TargetFrameMs           = 40
Phase2.DirtyTracking.Enable             = true
Phase2.SpatialRpc.Enable                = true
Phase2.SpatialRpc.RadiusUnits           = 192
```

### Large server (100 players)

```
Phase1.PlayerLimit.MaxPlayers           = 100
Phase1.ZdoSend.PeersPerFrame            = 12
Phase1.ZdoSend.SendQueueSizeBytes       = 131072
Phase1.ZdoSend.SendQueueMinFreeBytes    = 16384
Phase1.Steam.SendRateBytesPerSec        = 2097152
Phase1.Adaptive.Enable                  = true
Phase1.Adaptive.TargetFrameMs           = 50
Phase1.Adaptive.MinPeersPerFrame        = 4
Phase2.DirtyTracking.Enable             = true
Phase2.SpatialRpc.Enable                = true
Phase2.SpatialRpc.RadiusUnits           = 224
```

### Stress test (200+ players)

```
Phase1.PlayerLimit.MaxPlayers           = 256
Phase1.ZdoSend.PeersPerFrame            = 24
Phase1.ZdoSend.SendIntervalSeconds      = 0.066
Phase1.ZdoSend.SendQueueSizeBytes       = 262144
Phase1.ZdoSend.SendQueueMinFreeBytes    = 32768
Phase1.Steam.SendRateBytesPerSec        = 4194304
Phase1.Adaptive.Enable                  = true
Phase1.Adaptive.TargetFrameMs           = 66
Phase2.DirtyTracking.Enable             = true
Phase2.SpatialRpc.Enable                = true
Phase2.SpatialRpc.RadiusUnits           = 160
```

> Numbers above 100 players are **experimental** and depend heavily on your CPU per-core perf and NIC. Always start with a smaller cap and raise gradually while watching the metrics block.

---

## 6. Monitoring & metrics

With `Metrics.Enable = true` (default), every 30 seconds the server prints a block like:

```
[Info   :ValheimHighCap] [HighCap Metrics] 19:42:17 UTC
  Players      :   1 / 64
  Total ZDOs   :  20,395
  ZDOs/s sent  :     156  (avg/s over 30s)
  ZDOs/s recv  :      79  (avg/s over 30s)
  Dirty/cycle  :       7
  Frame EMA    :    33.3 ms
  Peers/frame  :       6 (adaptive)
  Phase2 dirty : ON
  Spatial RPC  : ON
  ───────────────────────────────────────────────────────────
  Name                   KnownZDOs  QueueKB     World Pos (X,Z)
  [Odev                ]     7,723     0.0K  (       4,       -3)
```

### How to read it

| Line | What it tells you | Healthy values |
|---|---|---|
| `Players` | Current / configured cap. | Below cap. |
| `Total ZDOs` | All world entities tracked by the server. | Grows with explored map. 20k–60k typical. |
| `ZDOs/s sent` | Total ZDO updates pushed to clients per second, averaged. | Scales with player count and movement. Can be 0 if no one is moving. |
| `ZDOs/s recv` | ZDO updates received from clients per second. | Scales with player movement. |
| `Dirty/cycle` | Number of ZDOs that changed in the last send cycle. Only meaningful with `DirtyTracking.Enable = true`. | A few dozen is normal; thousands means a chaotic event (boss fight, base destruction). |
| `Frame EMA` | Exponentially smoothed server frame time. | Below `Adaptive.TargetFrameMs`. |
| `Peers/frame` | Currently effective peers serviced per frame (adaptive value, capped by `PeersPerFrame`). | Equal to or near `PeersPerFrame`. If it's pinned at `MinPeersPerFrame`, the server is overloaded. |
| `QueueKB` per peer | How much pending ZDO data is queued for that peer. | < 16 KB. A persistent ⚠ `QUEUE HIGH` warning means the peer's network is too slow or your queue size is too small. |

### Warnings the plugin emits

- `⚠ QUEUE HIGH` (per peer) — That peer is consistently slow to receive. Either their connection is bad, or `SendQueueSizeBytes` is too low.
- `⚠ Average peer queue X KB — consider raising ZdoSendQueueSizeBytes or reducing PeersPerFrame` — Aggregate symptom; either raise the queue ceiling or service fewer peers per frame.

---

## 7. Tuning guide

### Symptom → fix table

| You see… | Most likely fix |
|---|---|
| `Frame EMA` consistently above target | Lower `PeersPerFrame`, raise `SendIntervalSeconds` to 0.066 (15 Hz), enable `Phase2.DirtyTracking`. |
| `Frame EMA` always 50 % below target | You can raise `PeersPerFrame` for snappier ZDO sync. |
| Players see ghost mobs, missing items, or stuck animations | Raise `SendQueueSizeBytes` to 131072. If still bad, lower `PeersPerFrame` so each peer gets serviced more often. |
| Players in PvP fights lose hit feedback | Raise `Phase2.SpatialRpc.RadiusUnits` (e.g. 320). Or disable spatial RPC entirely. |
| Long-distance damage numbers / sound missing for spectators | Same — raise `RadiusUnits` or disable spatial RPC. |
| Server CPU usage way too high | Enable `Phase2.DirtyTracking`. Enable `Phase1.Adaptive`. |
| Aggregate queue warning every cycle | Raise `SendQueueSizeBytes` first; if that doesn't help, lower `PeersPerFrame`. |
| `Dirty/cycle` is always 0 even with many players | `DirtyTrackingMinKnownZdos` may be too high, or sweep auto-disabled (check log for `[DirtyZdoTracker] DISABLING`). |

### Order of operations for tuning a new server

1. Set `MaxPlayers` to your target.
2. Set `PeersPerFrame` to `ceil(MaxPlayers / 8)`.
3. Leave everything else at defaults.
4. Run a stress test at peak expected load.
5. Watch `Frame EMA` and queue warnings for 10 minutes.
6. Adjust one knob at a time based on the table above.

---

## 8. Troubleshooting

### The plugin doesn't load

- Confirm `BepInEx/plugins/ValheimHighCap.dll` exists on the **server** (not the game client).
- Confirm BepInEx itself loaded: the server console should show `[Info   :   BepInEx] BepInEx 5.x.x ... starting` near the top. If not, BepInEx isn't installed correctly — re-do step 3a.
- On Linux, make sure the doorstop environment variables are exported **before** launching the server binary.

### `MaxPlayers` still limited to 10

- Server wasn't restarted after editing the config.
- You edited the client's BepInEx config instead of the server's.
- Some other player-limit mod is also installed and conflicting.

### Clients can connect but immediately disconnect

- This plugin does not change the network protocol, version, or world format. If clients can't connect with this plugin installed but **can** connect without it, file an issue with your full `BepInEx/LogOutput.log`.

### Console is spammed with `[DirtyZdoTracker]` lines

- Set `Phase2.DirtyTracking.VerboseLogging = false` (default). Verbose logging is for debugging only.

### `[DirtyZdoTracker] DISABLING sweep:` or `[CreateSyncListPatch] DISABLING dirty-tracking optimisation:`

- A future Valheim update changed a field name the plugin reflects on. The plugin **safely auto-disables** the optimisation and falls back to vanilla so your server keeps running. Report it as an issue with the log line — the fix is usually a one-line patch on our end. Set `Phase2.DirtyTracking.Enable = false` in the meantime to silence the message.

### Players damage objects but nothing happens

- Verify `Phase2.SpatialRpc.RadiusUnits` is at least 192 — anything smaller can clip damage-effect broadcasts.
- Toggle `Phase2.SpatialRpc.Enable = false` and retest to isolate.
- Toggle `Phase2.DirtyTracking.Enable = false` and retest to isolate.

### Where do I find the logs?

`<server root>/BepInEx/LogOutput.log` — every plugin message goes here as well as the console.

---

## 9. How it works (technical overview)

For server admins who want to understand what's actually being changed.

### Phase 1 (always-on, low risk)

| Patch | Vanilla behaviour | What we change |
|---|---|---|
| `PlayerLimitPatch` | `ZNet.GetServerPeers()` is hard-clamped to 10 in two places. | Returns `MaxPlayers` from config (10–256). |
| `MultiPeerSendPatch` | `SendZDOToPeers2` services exactly **1 peer per Unity frame**. | Services `PeersPerFrame` peers per frame, with an EMA adaptive throttler that scales the effective number based on `Frame EMA` vs `TargetFrameMs`. |
| `SendQueueSizePatch` | Per-peer outbound ZDO queue ceilings hard-coded (10240 / 2048). | Reads `SendQueueSizeBytes` / `SendQueueMinFreeBytes` from config. |
| `SteamSendRatePatch` | Steam Networking send rate hard-coded to 153 600 B/s per connection. | Reads `SendRateBytesPerSec` from config. |

### Phase 2 (opt-in optimisations)

| Patch | Vanilla behaviour | What we change |
|---|---|---|
| `DirtyZdoTracker` | None (helper only). | Once per ZDO send cycle (~50 ms), sweeps `ZDOMan.m_objectsByID` and snapshots which ZDOs' revisions changed since last sweep. |
| `CreateSyncListPatch` | For every peer, scans ALL ZDOs in their active sector area and calls `ShouldSend()` on each. | For peers with ≥ `MinKnownZdos`, only runs `ShouldSend()` on ZDOs that the sweep flagged as dirty. Unknown ZDOs are still discovered normally. Auto-disables and falls back to vanilla on any exception. |
| `SpatialRpcPatch` | `ZRoutedRpc.RouteRPC` broadcasts to every connected peer regardless of distance. | For **broadcast** RPCs that carry a ZDO reference, only forwards to peers within `RadiusUnits` of that ZDO's position. Targeted RPCs (damage, pickup, door open) are never filtered. A whitelist of system RPCs (DestroyZDO, RequestZDO, PeerInfo, GlobalKeys, NetTime, etc.) bypasses the filter. |

### Safety / future-proofing

- Phase 2 patches each have a one-shot exception guard: on first thrown exception they log a clear error and **auto-disable** for the rest of the process, falling back to vanilla behaviour. Your server stays up.
- All reflection uses `AccessTools.Field/Method/Inner` lookups by **name**, not IL offset. The patches survive most Valheim updates that don't change field names.

---

## 10. Updating Valheim

When a new Valheim server update lands:

1. Update the dedicated server normally (`SteamCMD app_update 896660 validate`).
2. **Restart the server with this plugin still installed.** Watch the console output for 30 seconds.
3. If you see:
   - Normal startup + `[HighCap Metrics]` block → ✅ still compatible, done.
   - `[DirtyZdoTracker] DISABLING` or `[CreateSyncListPatch] DISABLING` → Phase 2 optimisation auto-disabled itself. Phase 1 (the player cap) still works. File an issue with the log line; set `Phase2.DirtyTracking.Enable = false` in the meantime.
   - Server fails to start, or `MaxPlayers` reverts to 10 → Phase 1 patch broke. Remove `ValheimHighCap.dll` from `BepInEx/plugins/` so your server is playable, then file an issue with the log.

---

## 11. Uninstalling

1. Stop the server.
2. Delete `BepInEx/plugins/ValheimHighCap.dll`.
3. (Optional) Delete `BepInEx/config/com.valhoom.highcap.cfg`.
4. Start the server. You're back to vanilla 10-player Valheim.

The plugin makes **no** persistent changes to the world save, character files, or `start_server.bat`. It is fully reversible by deleting the DLL.

---

## License

See `LICENSE` in the repository root.

## Bug reports

Include with every report:
- Valheim server version (printed on server startup).
- BepInEx version.
- Your full `com.valhoom.highcap.cfg`.
- `BepInEx/LogOutput.log` from a fresh server start through reproduction of the bug.
