> English | [中文](README_zh.md)

# TwilightCore

TwilightCore is the **player-side** BepInEx plugin for the Twilight Cup tournament, used in *Human: Fall Flat*.
It embeds the [LevelCollections](https://github.com/...) collection engine and connects to the existing
`TwilightCupBackend` server (FastAPI + WebSocket), providing: chat, `!ready`, phase/countdown,
server-pushed collection auto-start, ready-phase lock (to prevent early starts), **real-time subsegment tracking**
for multi-level rounds, and a **simulated timer** so the match flow can still run through scoring/judgment
without depending on a real timer.

> The real timer (rules-exact per-level timing + reporting) will be merged later as a separate module.
> For now `SimulatedTimer` is a temporary replacement; the interface (`IRoundReporter`) is already in place,
> so only the implementation needs to be swapped when the real timer is merged.

## Installation / Build

```bash
# Default path points to a macOS Steam install; override with -p on other machines
dotnet build -c Release \
  -p:GAME_MANAGED="/path/Human_Data/Managed" \
  -p:BEPINEX_CORE="/path/BepInEx/core"
cp bin/Release/netstandard2.0/TwilightCore.dll "<game>/BepInEx/plugins/"
```

If you previously installed a standalone `LevelCollections.dll`, remove it (TwilightCore now includes it;
having both would create two collection buttons).

## Configuration

The config is generated at `BepInEx/config/TwilightCore.cfg` on first launch. **The backend address/port is not
stored in the config**; it is passed via `twi connect <host> [port]` in the console (default port **8443**,
matching the public nginx HTTPS entry). nginx same-origin reverse proxy: `/api/...` → backend REST (strip the
`/api` prefix), `/ws/{token}` → backend WebSocket, `/` → frontend. Therefore the plugin uses
`https://<host>:8443/api/auth/login` for login and `wss://<host>:8443/ws/{token}` for WebSocket:

| Section.Key | Default | Description |
| --- | --- | --- |
| `Server.UseTLS` | `true` | Use `wss`/`https` (public nginx uses TLS; host/port come from `twi connect`). Set to `false` for a local plain backend |
| `Account.Username` | *(empty)* | Player account username |
| `Account.Password` | *(empty)* | Player account password (stored in plaintext, used only to exchange for JWT) |
| `Account.Seat` | *(empty)* | `PLAYER_A` / `PLAYER_B`; if left empty, the server assigns it automatically from the session |
| `Net.HeartbeatSecs` | `20` | Heartbeat interval |
| `Net.ReconnectMinBackoffSecs` / `Max` | `1` / `30` | Exponential disconnect backoff (reconnect reuses the address from the last `twi connect`) |
| `Features.EnableReadyLock` | `true` | Lock manual level entry after `!ready` in the prep phase and during countdown (players may practice freely before `!ready`) |
| `Features.EnableSimTimer` | `true` | Enable simulated timer reporting |
| `Features.SameLevelReloadMinDwell` | `1` | Minimum seconds to detour through the `Empty` scene when a collection repeats the same level back-to-back (visually separates attempts); `0` disables the detour |
| `Features.EnableMenuFallLimit` | `true` | Limit the main-menu character's fall speed while connected to a match server (prevents falling out) |
| `Features.EnableScenePreload` | `true` | **Held-scene preload**: after `!ready`, keep the first level of the selected MULTI pick dormant in memory, then swap it in instantly at `round_start` (see "Level Preload" below) |
| `Features.EnableChainedPreload` | `true` | **Chained preload (M3)**: during a collection run, preload the next level in the background and swap it in at frame granularity when the current level completes (see "Level Preload" below). If in-game frame drops are noticeable, disable this (falls back to instant first-level swap only); also works for local `lc` collections |
| `Features.EnableProbeFreeze` | `true` | **Tint fix (only for baked-probe levels such as Halloween/Steam)**: during the hold window, freeze the active light-probe coefficients to the sampled values at the player's position (uniform field), then write the level's real coefficients back on swap-in. Unbaked-probe levels (all other built-ins) cannot be safely written (managed reads and renderer path scaling conventions differ), so they keep a slight next-level tint during the hold (restored on level change; accepted trade-off) |
| `Features.ProbeFreezeUnbakedHolds` | `true` | **Unbaked hold blackness fix (experimental)**: when holding an unbaked-probe level (everything except Halloween/Steam), if the player is inside its probe convex hull, all dynamic objects (including the player model) sample zero coefficients and lose ambient light entirely. This applies the same freeze to the unbaked group — values are sampled from the actual playing-level structure before loading, and are **not written back** on swap-in (the engine refills them when the scene activates, avoiding the historical over-brightening cascade). There may be a slight brightness offset; disabling keeps the hold window fully black |
| `Features.PreloadUnloadUnusedAfterSwap` | `true` | **Post-swap cleanup**: after each swap completes and the old scene unloads, run `Resources.UnloadUnusedAssets()` and wait (~50–145 ms). The additive preload path leaks native assets per distinct scene; long collections (roughly 12+ distinct scenes) eventually hard-crash with OOM during scene loading. This cleanup fixes that (cost: one extra short hitch per swap); disable for A/B comparison |
| `Features.EnableDiskCacheWarmup` | `true` | **Conservative preload (disk cache warmup)**: after the first-level hold completes during PREP, a single background thread pre-reads the collection's remaining level files into the OS page cache — during a match, chained preload disk reads become memory reads, leaving only deserialization CPU work. Pure file I/O (does not touch scenes/lighting, uses a single fixed buffer and adds no process memory); any failure silently degrades to the current behavior. `twi reload` hot-toggles it (disabling only stops new warmups; in-progress warmups finish) |
| `Features.DiskWarmupMode` | `follow-chain` | Warmup strategy: `follow-chain` = PREP only head-warms the two levels after the first, then every completed chained hold warms the "level after next" (fresher pages, smaller steady-state footprint, also works for local `lc` practice runs; mild background reads in-round land in disk-idle windows). `prep-all` = warm all remaining levels once during PREP (zero in-round disk I/O). `twi reload` hot-toggles |
| `Features.DiskWarmupWarmSharedFiles` | `true` | Also warm shared assets during disk-cache warmup: the build-index-adjacent `sharedassets{N}.*` trio + `resources.assets` for each target level (about 600 MB for a whole collection, not the ~4.1 GB of all shared files; falls back to full warming when the scene table fails or with `warm all`). Per-file sizes are logged by `Debug.DebugPreloadLogger`; disable if the machine is memory-constrained (page cache is managed by the kernel and reclaimable) |
| `Features.EnableSubsegment` | `true` | Real-time subsegment gap tracking for multi-level rounds (see below; requires an upgraded server and the TwilightTimer real timer running) |
| `Subsegment.PlaneRadius` | `50` | Detection plane radius (meters): the virtual plane at an opponent's sample point, perpendicular to their movement vector |
| `Subsegment.MinMove` | `0.5` | If the displacement between samples is smaller than this (meters), that sample does not generate a detection plane (nearly stationary) |
| `Subsegment.SampleInterval` | `1` | Sample interval (seconds) |
| `Chat.PopupEnabled` | `true` | Show a log-only chat popup (no input box) when a message is received, then fade out |
| `Chat.PopupSecs` | `5` | Duration in seconds for the popup above (then fades out) |
| `Chat.ToggleHotkey` | `Ctrl+T` | Hotkey to open/close the chat console, format `modifier+key`, e.g. `Ctrl+T`, `Ctrl+Shift+Y`, `Alt+F8`, `F8`. `Ctrl` also matches `Cmd` on macOS. Hot-reloadable with `twi reload` (no restart needed) |
| `HUD.Enabled` | `true` | Show two lines of collection info in the top-right corner during collection runs (styled after the timer: bold plain text, single color, no gradient) |
| `HUD.TextColor` | `FFD94C` | HUD text color, hex encoded (`RRGGBB` or `RRGGBBAA`, `#` optional) |
| `HUD.FontSize` | `18` | HUD font size (same as the default TwilightTimer) |
| `Debug.VerboseNetLog` | `false` | Print every sent/received frame |
| `Debug.DebugPreloadLogger` | `false` | Detailed preload diagnostics: per-hold/swap light probes, lightmap table signatures, memory snapshots, cleanup timing, and other state dumps (for investigating lighting/memory issues). Pure logging switch, behavior is identical; warnings and errors are unaffected, and `twi preload rs` is always available. Hot-switchable with `twi reload` |
| `Debug.DebugSubsegmentLogger` | `false` | Detailed subsegment diagnostics: tracker state transitions, wake detection, sampling/receiving samples, plane creation and crossing detection, hit/completion sync, idle reasons, etc. Pure logging switch, behavior is identical; normal subsegment status lines are unaffected. Hot-switchable with `twi reload` |

## Console Commands

Open the in-game console with `` ` `` (backquote) or `F1`.

**Collections (ported from LevelCollections)**

| Command | Description |
| --- | --- |
| `lc random [seconds]` | Start a random collection from the local config pool (practice) |
| `lc restart [seconds]` | Restart the current collection from the first level |
| `lc skip [seconds]` | Skip the current level (for a single-level pick, this attempt is recorded as N/A) |
| `lc abort` | Cancel a pending delayed command |

**TwilightCore**

| Command | Description |
| --- | --- |
| `twi connect <host> [port]` | Connect (address is given in the command; default port 8443, default `wss`/`https`) |
| `twi disconnect` | Disconnect manually (stops auto-reconnect) |
| `twi disconnect simulate` | Simulate an unexpected disconnect: the connection will auto-reconnect per config, for testing reconnect/resume |
| `twi status` | Connection / match status |
| `twi reload` | Hot-reload `TwilightCore.cfg` + `LevelCollections.json` from disk (no game restart needed after editing config files) |
| `twi sim level_done [ms]` | Simulate the current level/attempt completing (optionally with a duration) |
| `twi sim skip` | Simulate a skip (N/A) |
| `twi sim complete [final_ms]` | Simulate the whole round completing |
| `twi sim forfeit [multi_exit\|single_exit_0_valid]` | Simulate forfeiting |
| `twi sim status` | Simulated timer status |
| `twi subseg status` | Subsegment tracker status (round/level, sample and plane counts, latest gap) |
| `twi preload hold <levelId>` | **M1/M3 validation**: hold the specified level with additive + dormant mode (bypasses match flow); works from the main menu or while inside a level (the latter is the chained preload form). The id follows collection config: built-ins use display names (`Aztec`, `Steam`, …, case-sensitive) or a subscribed workshop id |
| `twi preload swap` | **M1/M3 validation**: swap the held scene in as the current level (GO swap sequence; do not use during ready lock) |
| `twi preload drop` | Drop and unload the held scene |
| `twi preload status` | Preloader status (selected level / held scene / failure reason) |
| `twi preload rs` | Dump the current global lighting state (probes/ambient/fog/lightmap table with per-entry texture IDs/per-scene renderer lightmapIndex histogram/LOD state/process memory) for investigating swap-in lighting and memory issues |
| `twi preload mach` | Dump all machine joint/physics state in the current level (AngularJoint/Lever/Catapult/hinge angles and drive targets, etc.) to run immediately when a machine misbehaves |
| `twi preload warm <levelId\|all>` | **Conservative preload validation**: manually pre-read the specified level (or `all` = all scene files + shared files) into the OS page cache, bypassing match flow (for debugging and cold/hot cache A/B); progress is shown in the `warm:` line of `twi preload status` |

Chat:

- **Ctrl+T** (configurable via `Chat.ToggleHotkey`, e.g. `Ctrl+Shift+Y`, `F8`) opens/closes the full chat console (usable both in menus and in-round). Type text and press Enter to send; `!ready`, `!roll`, etc. are ordinary chat text. While the console is open it captures the keyboard, so the game will not respond to grab/jump (movement keys may still work — stop moving before typing). Hotkey changes take effect via `twi reload`.
- When a message arrives and the console is not open, a **log-only popup (no input box)** appears for `Chat.PopupSecs` (default 5 s) and then fades out; new messages extend it. Can be disabled with `Chat.PopupEnabled`.

## Subsegment Real-Time Gap Tracking

In multi-level (MULTI) rounds, both players run the same collection. Normal reporting only gives whole-level
completion times, so progress cannot be compared within a level. With `Features.EnableSubsegment` enabled:

- **Sampling**: in each level, from the character's **first wake from the play-dead state** (drop in from the sky → 3 seconds unconscious → get up)
  until the level **actually completes**, record once per second: current total time, position, and movement vector, and report to the server.
  Note that touching the pass zone is not the same as completing: in the game flow, the player must die (fall or drown) after touching it
  to trigger completion. Walking/falling time between touching the pass zone and actual completion **counts normally** (same basis as whole-level timing).
  Manually playing dead inside a level or resurrecting at a checkpoint after falling does **not** reset recording (the clock keeps running, so penalties are naturally included).
- **Detection**: the server relays one player's samples to the other. The receiving client creates a **virtual plane perpendicular to the movement vector**
  with radius `Subsegment.PlaneRadius` at each sample point (pure math detection; no Unity colliders are created, so it does not affect level physics and cannot tunnel at high speeds).
  When your character crosses the plane along the movement direction, the hit time is reported.
  The same plane can be **crossed multiple times and reported multiple times** (200 ms debounce per plane): grazing causes back-and-forth crossings, winding routes loop back to old planes — all are reported honestly and settled by the server.
- **Gap**: the server keeps both sides' data (memory only, round-scoped; cleared when the round ends). On each **settlement**, it broadcasts
  `subsegment_gap` to both players/referee/director (`crosser time − sampled time`; positive = crosser is behind), for use by director overlays.
  Settlement rules (settled-event model): a plane is broadcast after about 0.5 s with no further crossing; the effective time is the **last** crossing —
  early grazes are superseded by real crossings. Lower keys before the settled progress (out-of-order late arrivals) are dropped, so the overlay never jumps backward.
  On **failed retry/reversal** (a below-cursor crossing ≥3 s after the seat's last crossing; falling plus waking alone already exceeds that threshold), the below-cursor
  key is reopened and broadcast at the current time — the timer does not reset on falls, so the number already includes the penalty cost, grows monotonically, and the visual updates with real progress instead of freezing for a long time.
  After settlement, re-crossing the same plane (loop-back) causes an amend, and the frontend shows the newest entry.
- **Forced completion sync**: at actual level completion, one extra hit is sent to the opponent's **last sample** (the final sample at their completion time) —
  both endpoints are necessarily the same pass zone, so even if routes are completely different and no plane is crossed in between, every level is guaranteed exactly one
  "finish-vs-finish gap" comparison (sent by the side that completes later; if that sample was already genuinely crossed, the real crossing takes precedence).
- **Real-time timer relay**: on the same beat, every second the current timer reading is also reported to the server
  (`total_ms` = `RoundTotalMs`, `segment_ms` = `CurrentSegmentMs`, current level). If the TwilightTimer provider also implements real-time/wall-clock timing,
  it additionally sends `real_time_ms` (TwilightTimer's Real Time). This is relayed **only to referee/director** seats
  (players cannot perceive each other's timers), with the latest entry kept per seat and replayed immediately to late-connecting referee/director handshakes.
  Reporting continues throughout active rounds (including load/spawn phases), gated by `Features.EnableSubsegment` and dependent on the real timer just like subsegment.
  SINGLE rounds also report `live_time` (the live segment of the current attempt; timer resets on retry), but do not participate in subsegment plane sampling/gaps.
- **Time basis**: time values come directly from **TwilightTimer** (the real timer, registered via `TimerProviderRegistry`)
  `RoundTotalMs` — the **same timeline** as official scoring, so gaps can be compared directly with results. The sampling window is wake-up in each level → actual completion.
  If no real timer is registered, subsegment does not work (it does **not** fall back to the simulated timer — that is an early debug placeholder slated for removal).
- Players see **no in-game display** (to avoid mentally affecting them by seeing the opponent's progress in real time); use `twi subseg status` to inspect local state.
- Samples lost during disconnects are accepted; after reconnecting, the server replays the opponent's stored samples in order and detection planes are rebuilt automatically.
- The backend must be upgraded in sync (new message types); if connected to an un-upgraded server, sending stops after the first 400 until the next round.

## Server Collection Config Format (`collection.raw`)

The server's `CollectionConfig` is an opaque dictionary (`{raw: ...}`); **the format is defined by this plugin**:

```json
{
  "name": "ML1 - Aztec% + Steam%",
  "levels": ["Aztec", "Steam"]
}
```

- `levels` is an array of level ID strings (BuiltIn / EditorPick / Workshop, auto-detected).
- **Single-level picks (`pick.type=SINGLE`)**: put one level in `levels`; the plugin automatically repeats it N times according to `pick.retry_count` (i.e. "automatic arrangement"). You may also list multiple entries manually.
- Multi-level picks (`MULTI`): `levels` is the level order.

When creating a match/map pool, each pick's `collection` field is filled with this structure, for example:

```json
{
  "code": "ML1", "name": "Aztec% + Steam%", "type": 1, "retry_count": null,
  "collection": { "raw": { "name": "Aztec% + Steam%", "levels": ["Aztec", "Steam"] } }
}
```

## Level Preload (held-scene, MULTI only)

When `Features.EnableScenePreload` is enabled, during PREP the plugin loads the **first level scene** of the referee-selected pick
(**MULTI only**) into memory in advance (additive, all root objects dormant: no rendering/no physics/no audio).
At `round_start`, it only performs a lightweight "swap-in" (activate the scene + replicate the game's own `AfterLoad` orchestration),
so the player is **already in the level when the countdown ends**. How it works:

- **Trigger**: receives `pick_announced` from the server (the referee's pick sends the collection early) plus your own `!ready`,
  and the player is at the main menu. After ready-lock is active, manual level entry is locked, so preload cannot be accidentally disrupted.
- **Reporting**: the preload state machine reports `preload_report` to the server (`in_progress` / `done` / `failed` / `na`);
  SINGLE picks always report `na`. The server uses this as the round-start gate (the auto countdown begins only after both players' preloads are done).
- **Degradation**: preload is an optimization, not a dependency — any failure (not subscribed, download failure, scene error, pick change)
  automatically falls back to the existing standard loading path, behaving exactly as if preload were off. If the server does not support
  `pick_announced`, the plugin is fully idle (the WS connection carries `cap=preload1`; old servers ignore it).
- **Chained preload (`Features.EnableChainedPreload`)**: after entering a level (`PlayingLevel`), the plugin preloads the collection's **next level**
  in the background using the same dormant method (low-priority, frame-spread loading), then swaps it in directly on completion —
  transitions between levels go from multi-second loads to frame-level switches, and timer edges/reporting are unaffected.
  Adjacent same levels (including SINGLE repeated attempts) are not preloaded; they use the existing Empty-dwell path. If the next level is not finished preloading at the transition,
  it falls back to standard loading. Local `lc` collections work too (no server needed).
  Hold-window tint: baked-probe levels (e.g. Halloween/Steam) are handled by `Features.EnableProbeFreeze`
  (probe coefficients are frozen to player-sampled values during the hold, written back on swap-in); unbaked levels are handled by
  `Features.ProbeFreezeUnbakedHolds` (if the player falls inside the next level's probe convex hull, dynamic objects go fully black; outside the hull there is a slight tint — both share the same cause and are removed by the freeze; no write-back on swap-in).
  After swap-in, `Resources.UnloadUnusedAssets()` automatically cleans unreferenced assets (`Features.PreloadUnloadUnusedAfterSwap`) —
  the additive preload path leaks native assets per distinct scene, and long collections eventually hard-crash with OOM (confirmed); this cleanup fixes it.
  The full issue list and fix history are in `ignored/M3遗留问题调查-反编译实证.md`.
- **Conservative preload (disk cache warmup, `Features.EnableDiskCacheWarmup`)**: complementary to chained preload —
  a single background thread (low priority, fixed 1 MB reusable buffer) streams each level's disk files through and discards them, filling the
  OS page cache (managed by the kernel, reclaimable, does not consume process memory); after that, chained hold disk reads become memory reads, leaving only
  deserialization/integration CPU work. The strategy is selected by `Features.DiskWarmupMode`: **follow-chain**
  (default) — during PREP (after the first-level hold completes) only head-warm levels 2–3; then after each chained hold of level N+1 completes,
  warm level N+2 in the disk-idle window (previous hold done, next hold not started): pages survive about one level duration before use (rarely evicted), steady-state footprint is small,
  local `lc` practice runs also benefit, at the cost of mild background reads in-round; **prep-all** — warm all remaining levels once during PREP (zero in-round disk I/O).
  At round start (`round_start` swap) or on pick change, warmup stops cooperatively (finishes the current file). Built-in level files are mapped through the engine's
  build-settings scene table (verified on real hardware: 43/43 scenes mapped; on mapping failure it degrades to warming all `level*` files),
  shared assets are trimmed by scene build-index adjacency (`sharedassets{N}.*` trio, about 600 MB per collection rather than the full 4.1 GB),
  workshop levels read only the two files actually used by their load path (`metadata.json` + `data` bundle), uninstalled items are skipped and never trigger downloads.
  Any failure silently degrades to current behavior. Manual trigger and cold/hot cache A/B: `twi preload warm <levelId|all>`; `twi preload status`'s `warm:` line shows mode and progress.
- **Pick change**: if the referee re-picks, `pick_announced` is sent again; the plugin discards the old preload and restarts with the new collection.
- Debug: `twi preload hold/swap/drop/status/rs/mach` can manually validate holding/swap-in/unload without connecting to a server
  (M1 prototype, used for risks 1/2/3 in the design research doc §7; hold+swap inside a level is the minimum reproduction of M3 chained swap-in, e.g. `twi preload hold Siege` then swap to verify the catapult).

> Full design: `ignored/激进预载held-scene方案调研.md`; server side (`pick_announced` push + preload gating): `ignored/需求-合集提前下发与预载门控.md` (takes effect after backend implementation).

## End-to-End Integration (with an existing server)

1. Start the backend: `cd TwilightCupBackend && uv run uvicorn twilightcupbackend.main:app --reload` (requires local MongoDB).
2. Create player/referee/director accounts with an admin account, then create a session (assign people, map pool; each pick's `collection.raw` follows the format above).
3. Player side: edit `TwilightCore.cfg` (Username/Password), start the game → in the console enter `twi connect <public IP>` (default port 8443, default `wss`/`https`, i.e. `https://<public IP>:8443`: login via `/api/auth/login`, WS via `/ws/{token}`) → the console displays login/connecting/connected in sequence → receive `auth_ok`. For a direct local plain backend, use `twi connect <local IP> 8000` and set `Server.UseTLS=false` in the cfg (then the login path must be the bare `/auth/login`, not `/api` — only for local no-nginx setups).
4. Referee runs `referee_mark_prep` → player runs `!ready` → both ready → observe `countdown_tick` → `round_start` is pushed → the player side automatically loads the collection's first level and proceeds through the LC flow.
5. **Simulated timer**: real completions are reported automatically; or manually drive with `twi sim level_done 12345` / `twi sim skip` / `twi sim complete` / `twi sim forfeit` → after both players finish, the server enters `ROUND_JUDGING` → referee runs `referee_verdict` → `round_result` + `cumulative_score` are correct.
