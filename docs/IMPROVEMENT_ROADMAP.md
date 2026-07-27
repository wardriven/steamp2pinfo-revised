# Application improvement and stability roadmap

Planning baseline: 25 July 2026

This roadmap is based on a static review of the current revised WPF working tree, the separate WinUI 3 prototype, and every issue in the [original SteamP2PInfo tracker](UPSTREAM_ISSUE_AUDIT.md). It deliberately separates conclusions visible in source from behaviour that still needs a live Steam/game/Windows test.

## Product direction

SteamP2PInfo should be a trustworthy, Windows-only diagnostic companion for Elden Ring and compatible Steam P2P games:

- show which Steam peers the game authenticates;
- show ping and connection quality only when they can be measured honestly;
- provide a readable window and optional overlay;
- retain privacy-safe connection history;
- add peers to Steam's recent-player list and open profiles;
- permit an explicitly chosen, tightly scoped manual disconnect only when the exact flow can be proven;
- fail safely and explain which stage is unavailable.

It should not claim to lower latency, fix lag, identify every Steam connection, support every Steam game, or make an unsafe best guess about a peer's traffic.

## Current baseline

| Area | Static finding | Consequence |
|---|---|---|
| Main application | The current WPF working tree identifies itself as v1.5.0. | Use this behaviour and its data formats as the reference until WinUI parity is proven. |
| WinUI 3 | The prototype identifies as v1.4.0 and lacks the complete History feature. | It is a useful prototype, not a replacement build. |
| Peer discovery | Still reads Steam's IPC log for `BeginAuthSession`, `EndAuthSession`, and `LeaveLobby`, with a dummy call used to encourage flushing. | Recurring blank/missed-peer reports remain a release risk. |
| Old networking API ping | Depends on ETW packet observation and narrow STUN packet-size assumptions. | Open `-1` ping/false-quality reports are structurally plausible. |
| Attach | Starts configuration, Steam, parser, overlay, hooks, ETW, and enforcement sequentially. | A mid-attach failure can close the app or leave partial state unless rollback is made explicit. |
| Enforcement | Can install dynamic WFP filters and call Steam session-close APIs. | Network scope and cleanup are safety-critical, unlike the observation-only upstream app. |
| Storage | Config, logs, history, and settings use inconsistent locations; history can persist IP addresses. | Data migration, retention, atomicity, permissions, and privacy need one design. |
| Testing/release | A custom lifecycle harness exists, but there is no visible CI workflow or clean-machine release gate. | Regressions in native dependencies, ETW, WFP, packaging, and lifecycle can escape. |

## Definition of a stable release

A stable release is not merely one that compiles. It must meet all of these gates:

1. **Network safety:** monitoring is passive; manual enforcement is exact, bounded, attributable, and cleaned up on every exit path.
2. **Crash-free setup:** a bad path, corrupt config, missing native DLL, UAC denial, unavailable ETW session, dead game window, or Steam launch failure never causes an unhandled exit.
3. **Honest measurements:** unavailable ping/quality has a reason; an invalid value can never look like a successful or perfect measurement.
4. **Reliable detection:** fixture and live-session tests cover log creation, partial writes, flush delay, truncation, rotation, restart, rapid lobbies, and each supported Steam API path.
5. **Data responsibility:** users can understand, locate, delete, and safely share the data the app creates.
6. **Deterministic lifecycle:** hooks, timers, overlays, Steam API, ETW sessions, WFP filters, file watchers, and background threads are acquired and released exactly once.
7. **Reproducible distribution:** a clean tagged commit produces a signed, checksummed artifact that starts and exits on a clean supported Windows machine.

## Phase 0 — safety and correctness before the next stable release

### P0.1 Prove and constrain network enforcement

Relevant upstream issues: [#3](https://github.com/tremwil/SteamP2PInfo/issues/3), [#5](https://github.com/tremwil/SteamP2PInfo/issues/5), [#31](https://github.com/tremwil/SteamP2PInfo/issues/31), [#44](https://github.com/tremwil/SteamP2PInfo/issues/44), and [#54](https://github.com/tremwil/SteamP2PInfo/issues/54).

Plan:

- Write a formal enforcement invariant: no validated exact flow means no filter and no Steam close call.
- Keep automatic high-ping enforcement disabled, and add a regression test proving that legacy configuration cannot reactivate it. This is the safe resolution for [revised issue #6](https://github.com/wardriven/steamp2pinfo-revised/issues/6).
- Separate **game-owned exact flow**, **Steam-owned exact-flow fallback**, **relayed/unavailable**, and **ambiguous ownership** in the model and UI.
- Bind filters to application identity where Windows supports it, as well as direction, protocol, address family, local port, remote address, and remote port.
- Observe and test IPv6 before claiming IPv6 enforcement.
- Time-bound the action and its filters; make lobby transition, app/game exit, attach failure, and process termination definitive cleanup boundaries.
- Keep the WFP session dynamic, and also perform idempotent explicit cleanup plus a defensive startup audit.
- Move work out of the low-level keyboard callback: enqueue one edge-triggered request, return immediately, and serialize enforcement on an owned worker.
- Add a visible pre-use warning covering session timeout, multiplayer penalties, shared Steam-flow risk, and appropriate use.
- Record a privacy-redacted audit event for each refusal, filter, close attempt, timeout, and cleanup.
- Close [revised whitelist request #3](https://github.com/wardriven/steamp2pinfo-revised/issues/3) as obsolete with automatic enforcement, unless a distinct manual-exclusion workflow is specified and tested.

Acceptance:

- A test matrix covers IPv4/IPv6, game-owned/Steam-owned sockets, direct/relay transport, endpoint replacement, shared ports, multiple peers, repeated key presses, lobby exit, game exit, ordinary close, crash, forced termination, sleep/resume, and the next startup.
- Steam store, login, downloads, chat, voice, Remote Play, and unrelated games are unaffected in every monitor-only and targeted test.
- Ambiguity always produces a refusal, never a broader rule.

### P0.2 Minimize and protect endpoint data

Relevant upstream issues: [#44](https://github.com/tremwil/SteamP2PInfo/issues/44) and [#55](https://github.com/tremwil/SteamP2PInfo/issues/55).

Plan:

- Inventory every stored or displayed field in settings, per-game config, activity logs, diagnostics, history, and enforcement records.
- Decide whether a raw IP address provides enough user value to justify persistence. Prefer not to persist it; otherwise make retention short, explicit, and opt-in.
- Move user data to one versioned directory under `%LOCALAPPDATA%\SteamP2PInfo`, not the executable/current directory.
- Use atomic replace, schema versioning, bounded retention, corruption quarantine, and a tested migration from existing locations.
- Create **Export support bundle** with endpoint/IP redaction on by default.
- Put a plain-language privacy section in the app and README, including what Clear History does and does not delete.

Acceptance:

- A fixture containing IPs, Steam IDs, usernames, file paths, and tokens exports with all configured sensitive fields removed.
- Clear/delete, retention, corrupt-file recovery, upgrade, downgrade, and uninstall guidance are tested.
- Raw diagnostic files are never silently attached to an issue or opened for sharing.

### P0.3 Make setup and attach transactional

Relevant upstream issues: [#11](https://github.com/tremwil/SteamP2PInfo/issues/11), [#16](https://github.com/tremwil/SteamP2PInfo/issues/16), [#22](https://github.com/tremwil/SteamP2PInfo/issues/22), [#24](https://github.com/tremwil/SteamP2PInfo/issues/24), [#26](https://github.com/tremwil/SteamP2PInfo/issues/26), [#27](https://github.com/tremwil/SteamP2PInfo/issues/27), [#32](https://github.com/tremwil/SteamP2PInfo/issues/32), [#33](https://github.com/tremwil/SteamP2PInfo/issues/33), [#35](https://github.com/tremwil/SteamP2PInfo/issues/35), [#50](https://github.com/tremwil/SteamP2PInfo/issues/50), [#51](https://github.com/tremwil/SteamP2PInfo/issues/51), [#56](https://github.com/tremwil/SteamP2PInfo/issues/56), [#57](https://github.com/tremwil/SteamP2PInfo/issues/57), and [#60](https://github.com/tremwil/SteamP2PInfo/issues/60).

Plan:

- Model attach as explicit states: `Idle → Preflight → Steam initialized → Parser ready → ETW ready/degraded → Overlay ready → Attached`, with rollback to `Idle` from every failure.
- Validate the target process/window, Steam App ID, configuration schema, write directories, exact IPC file path, Steam native library, ETW native dependencies, elevation, hotkey, and overlay prerequisites before committing the session.
- Auto-detect Steam's install/log location from supported Windows/Steam metadata, present candidates, and add a real file picker.
- Label the input **Steam IPC log file** and reject a directory before attempting to open it.
- Tolerate the file not existing until the Steam command creates it; show creation/last-write/parser state without repeated modal alerts.
- Treat Steam console opening as a convenience. Always keep the exact command visible and copyable.
- Preflight ETW and offer a useful degraded state where possible; never let a missing module escape as an unhandled exception.
- Parse configuration defensively, quarantine a corrupt file, restore defaults, and preserve the bad file for support.
- Replace the close-on-dispatcher-exception policy with component-scoped recovery wherever safe.

Acceptance:

- A failure injected after every attach step rolls back all previously acquired resources.
- Bad JSON, bad App ID, directory-as-file, nonexistent/locked/read-only/Unicode/custom-drive paths, missing native dependency, UAC denial, unavailable ETW, dead HWND, Steam closed, protocol-launch denial, and hotkey conflict all leave the main app responsive.

### P0.4 Make peer and ping state observable and testable

Relevant upstream issues: [#4](https://github.com/tremwil/SteamP2PInfo/issues/4), [#10](https://github.com/tremwil/SteamP2PInfo/issues/10), [#13](https://github.com/tremwil/SteamP2PInfo/issues/13), [#17](https://github.com/tremwil/SteamP2PInfo/issues/17), [#20](https://github.com/tremwil/SteamP2PInfo/issues/20), [#30](https://github.com/tremwil/SteamP2PInfo/issues/30), [#36](https://github.com/tremwil/SteamP2PInfo/issues/36), [#40](https://github.com/tremwil/SteamP2PInfo/issues/40), [#41](https://github.com/tremwil/SteamP2PInfo/issues/41), [#43](https://github.com/tremwil/SteamP2PInfo/issues/43), [#46](https://github.com/tremwil/SteamP2PInfo/issues/46), [#48](https://github.com/tremwil/SteamP2PInfo/issues/48), [#52](https://github.com/tremwil/SteamP2PInfo/issues/52), [#53](https://github.com/tremwil/SteamP2PInfo/issues/53), and [#58](https://github.com/tremwil/SteamP2PInfo/issues/58).

Plan:

- Extract the IPC reader/parser and peer/lobby state machine from UI and filesystem callbacks.
- Capture sanitized real samples for supported old/new Steam API paths and commit them as deterministic fixtures.
- Handle `Created`, `Changed`, `Renamed`, deletion, truncation, rotation, partial lines, duplicate notifications, delayed flush, Unicode, malformed lines, and Steam restart.
- Make process-name matching intentionally case-insensitive and tested.
- Instrument a compact health model: command expected, file exists, file changing, bytes/lines read, process lines matched, auth/lobby events parsed, peers resolved, ETW samples observed, UI rows displayed.
- Rate-limit and monitor the dummy flush action. Investigate a supported alternative, but preserve known behaviour until a replacement proves equivalent.
- Represent ping and quality as a measurement with source, timestamp, value, and unavailable reason. Never calculate quality from invalid samples.
- Replace exact-size-only packet inference with validated correlation and add IPv6 support before depending on it.
- Publish a tested compatibility matrix. Games that do not emit required auth calls must be described as unsupported or experimental.

Acceptance:

- Fixture tests deterministically cover the full reader/parser/state matrix.
- At least 100 expected joins across each claimed Elden Ring role and rapid rematches produce no missing or stale peers.
- A 60-minute soak recovers from endpoint/log changes without raw `-1`, false `1.0`, or a silently blank overlay.

### P0.5 Make the release artifact independently verifiable

Plan:

- Choose one SemVer source for assembly, file, package, update check, and `version.md`.
- Pin/lock dependencies and review security/servicing updates on a fixed cadence.
- Build Release x64 from a clean tagged commit in CI.
- Run unit/fixture tests, analyzers, a startup/exit smoke test, and artifact-content validation.
- Run privileged ETW/WFP integration tests only in disposable Windows VMs or a tightly controlled self-hosted runner.
- Test the exact ZIP/install artifact on clean supported Windows 10 and Windows 11 machines without Visual Studio or developer runtimes.
- Authenticode-sign executable/native binaries and timestamp signatures.
- Publish SHA-256 checksums, an SBOM/provenance record, separate symbols, known issues, upgrade instructions, and rollback instructions.
- Keep the previous stable release available. Use separate stable and beta channels.
- Do not let an elevated process silently download and execute an unsigned update.

Acceptance:

- A user can verify origin and integrity, extract/install, start, attach or receive a clear prerequisite error, exit, upgrade, and roll back using only the published assets.
- The artifact includes every TraceEvent/Steamworks/native dependency required by a clean supported machine.

## Phase 1 — reliability and maintainability

### P1.1 Establish framework-neutral core contracts

Create clear boundaries for:

- IPC reading and parsing;
- peer/lobby state;
- ping/quality measurements;
- endpoint ownership;
- enforcement decisions and lifecycle;
- configuration/history serialization;
- version comparison;
- clocks, file systems, Steam API, ETW, WFP, window tracking, hotkeys, and dispatching.

The WPF UI should use these contracts before the WinUI UI replaces it. This lets the same fixture and policy tests certify both front ends.

### P1.2 Deterministic lifecycle and concurrency

- Give the app/session one cancellation owner.
- Make `Start`, `Stop`, `Dispose`, attach rollback, and shutdown idempotent.
- Join owned workers with bounded timeouts and report those that fail to stop.
- Dispose native handles, hooks, watchers, timers, fonts/graphics resources, ETW sessions, and Steam API state exactly once and on the correct thread.
- Serialize Steamworks and enforcement operations instead of mixing UI, timer, hook, and thread-pool callbacks.
- Add an eight-hour soak plus repeated attach/detach, game restart, sleep/resume, display change, and network change tests.

### P1.3 Consistent data and diagnostics

- One LocalAppData root with subdirectories for config, history, logs, diagnostics, and migration backup.
- Schema version and atomic save for every JSON/settings file.
- Bounded, privacy-redacted diagnostic ring buffer available from startup, including failures before per-game logging is enabled.
- A status page and copyable support summary that does not reveal IPs by default.
- Explicit retention and size limits for activity and diagnostic logs.

### P1.4 Deliberate single-instance behaviour

Retain one process unless multi-game use becomes a real product requirement.

- WPF: use a per-user owned mutex/IPC activation design.
- WinUI 3: use Windows App SDK app lifecycle/instance redirection.
- A second launch should activate the existing window and optionally forward the requested game, never merely show a false process-count error.

### P1.5 Overlay, display, and accessibility

- Centralize target-window/HWND ownership and stale-handle checks.
- Use target-window per-monitor DPI transforms.
- Test position, scale, click-through, focus, minimize/restore, monitor disconnect, mixed DPI, high refresh, VRR, SDR/HDR, and OBS capture.
- Provide accessible names, keyboard navigation, high-contrast behaviour, 200% text, Narrator verification, and a non-colour-only state representation.
- Define a separate OBS/capture mode only if testing shows it is required.

### P1.6 Notification choices

Replace the single Windows beep with:

- off, system sound, or a user-selected WAV;
- preview;
- new-session versus each-new-peer semantics;
- rate limiting and volume/mute;
- missing/inaccessible-file fallback;
- backward-compatible migration from the existing boolean.

## Phase 2 — user value and adoption

### P2.1 Privacy-safe history and export

- Preserve WPF v1.5 history semantics during migration.
- Add CSV/JSON export with endpoint fields excluded by default.
- Let users filter by game, date, Steam ID/name, and measurement availability.
- Explain average calculation, invalid-sample handling, 500-entry limit, and Clear History behaviour.

### P2.2 Compatibility and first-run diagnostics

- Maintain a versioned matrix of game, executable, Steam API path, tested role/mode, direct/relay result, and last verified version.
- Add a guided health check for Steam command, IPC file, parser, Steam API, elevation, ETW, overlay target, and enforcement capability.
- Keep “monitor works” separate from “manual enforcement available.”

### P2.3 Trustworthy distribution and support

- Improve the README and GitHub metadata as described in [DISCOVERABILITY_PLAN.md](DISCOVERABILITY_PLAN.md).
- Add issue forms that request app/Windows/Steam/game versions and a redacted support bundle.
- Publish a security policy, privacy statement, support boundaries, changelog, and compatibility page.
- Use a current screenshot and social-preview image that show the real application rather than an upstream-only state.

## Phase 3 — later architectural options

These are separate projects, not conditions to start the WinUI migration:

- **Least privilege:** normal-user UI plus a narrow, authenticated elevated broker for ETW/WFP. First prove parity with the current elevated model.
- **MSIX/Store distribution:** pursue only after a proof of concept validates elevation/full trust, native loading, Steam App ID lifecycle, overlay, hotkey, data location, update, and rollback.
- **Multiple game contexts:** one process with explicit independent sessions, only if demand justifies the Steam/ETW/hotkey/filter complexity.
- **Notification-area mode:** a new feature with explicit user opt-in and Exit; it must not keep Steam believing a game is running.
- **Linux:** a separate native monitoring/enforcement backend and UI effort, not a WinUI port.
- **Character-name mapping or process injection:** remain non-goals without a separate anti-cheat, privacy, security, and abuse assessment.

## Test portfolio

| Layer | Runs where | Purpose |
|---|---|---|
| Pure unit tests | Every change | Parser, peer/lobby state, ping/quality state, policy, version comparison, migrations. |
| Recorded-fixture tests | Every change | Steam IPC partial writes/rotation and sanitized ETW correlation without Steam or elevation. |
| Component tests | Every change where possible | Atomic stores, watcher behaviour, corrupt files, cancellation, UI view-model behaviour. |
| Unprivileged Windows UI tests | Pull requests/nightly | Attach validation, dialogs, accessibility, overlay state without real enforcement. |
| Privileged disposable-VM tests | Nightly/release candidate | ETW startup, native dependency load, WFP scope/cleanup, UAC, exact release artifact. |
| Manual game matrix | Release candidate | Real Steam client versions, Elden Ring roles, direct/relay, old/new API games, OBS/HDR/high refresh. |
| Soak and fault injection | Nightly/release candidate | Eight-hour run, repeated sessions, abrupt termination, sleep/resume, network/display changes. |

## Release sequence

1. **WPF safety release:** finish Phase 0 on the current reference UI.
2. **WPF reliability release:** complete the highest-risk Phase 1 foundations and capture parity fixtures.
3. **WinUI preview:** side-by-side beta only after every row in the migration matrix has an owner and test.
4. **WinUI release candidate:** no missing v1.5 features; clean-machine, accessibility, performance, native, and rollback gates pass.
5. **WinUI stable:** retain the last WPF stable build and documented rollback for at least one full stable cycle.
6. **Post-parity redesigns:** least privilege, packaging changes, multi-context, tray, and other new features happen one at a time.

No calendar promise should replace these exit criteria. Progress can be reported as completed gates and published evidence.

## Success measures

- Crash-free attach rate on supported configurations.
- Percentage of failures that include a reason-coded actionable state rather than a blank UI.
- Expected-peer detection rate in the maintained game matrix.
- Valid ping availability rate, separated by direct/relay and API path.
- Zero unintended WFP/Steam traffic changes in the enforcement safety suite.
- Zero sensitive endpoints in default support exports.
- Clean shutdown/relaunch rate across lifecycle tests.
- Release download-to-successful-first-attach funnel, measured without invasive telemetry.
- Issue recurrence for path, blank overlay, `-1` ping, native dependency, and stale-process clusters.

The feature-by-feature WinUI sequence is defined in [WINUI3_MIGRATION_PLAN.md](WINUI3_MIGRATION_PLAN.md).
