# WinUI 3 migration plan without feature loss

Planning baseline: 25 July 2026

## Decision

Do not release the existing `SteamP2PInfo-WINUI3` folder as the successor to the WPF application.

Static comparison found:

- the current WPF working tree identifies as **v1.6.0**;
- the WinUI 3 prototype identifies as **v1.4.0**;
- the prototype omits the complete connection-history feature: store, tracker/model, tab, clearing behaviour, and v1.5-and-later data compatibility;
- both variants still carry the unresolved ETW startup, attach-rollback, detection, ping, lifecycle, privacy, and enforcement risks documented in [the upstream issue audit](UPSTREAM_ISSUE_AUDIT.md).

The prototype is valuable research, but WPF v1.6 must remain the behavioural and data-format reference until a written parity matrix and its tests pass.

## Supported foundation

For the migration branch:

- Target **.NET 10 LTS**, rather than shipping a new product on the prototype's .NET 9 target. Microsoft's current policy lists .NET 9 support ending on 10 November 2026 and .NET 10 LTS support ending on 14 November 2028: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).
- Use the latest servicing patch from the stable Windows App SDK channel. As of this plan date, Microsoft lists **Windows App SDK 2.3.1** as current: [Windows App SDK release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels).
- Start with **x64**, matching the game/Steamworks native dependency and the current supported deployment.
- Keep the prototype's **unpackaged, self-contained** model for the parity phase. This reduces simultaneous change around elevation, native library loading, Steam launch behaviour, and installed data. Microsoft documents the trade-offs in [deployment overview](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/deploy-overview), [unpackaged apps](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app), and [self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps).
- Keep `requireAdministrator` for the first parity release because the current ETW/WFP design expects elevation. Investigate least privilege only after parity.

Self-contained output is larger and does not inherit servicing simply because a shared runtime was updated. Treat each relevant .NET/Windows App SDK security servicing release as a trigger to build, test, and publish a new application release.

## Principles

1. **Migrate behaviour, not files.** A WinUI namespace conversion is not a specification.
2. **Fix shared correctness once.** Parser, state, storage, measurement, policy, and lifecycle logic should be framework-neutral and tested by both UIs.
3. **One major risk at a time.** Do not combine UI replacement, privilege separation, packaging, and new features in one release.
4. **WPF remains the oracle until parity.** Keep a runnable stable WPF build and its rollback package through at least one WinUI stable cycle.
5. **No checkmark without evidence.** “Ported” means functional, data-compatible, accessible, clean-machine tested, and covered by an acceptance test.

Microsoft's migration guidance is explicit that WPF concepts need WinUI-specific substitutions and sometimes have no direct equivalent: [WPF to WinUI migration](https://learn.microsoft.com/en-us/windows/apps/develop/ai-assisted/migrate/wpf-to-winui) and [WPF/WinUI pattern mapping](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/wpf-patterns-winui3).

## Feature-parity contract

The matrix below is the minimum release contract. “Prototype finding” is from static inspection, not runtime certification.

| Capability | WPF v1.6 reference | WinUI prototype finding | WinUI release acceptance |
|---|---|---|---|
| Launch/elevation | x64, administrator manifest, one process. | Present; administrator manifest and process-count guard. | UAC accept/deny, rapid relaunch, second-launch activation, crash recovery, and clean exit tested. |
| Game attach | Select target window, enter/store App ID, initialize Steam and monitoring. | Surface is present; attach is still non-transactional. | Explicit state machine, preflight, cancellation, rollback after every injected failure, and actionable error state. |
| Steam console/IPC command | Opens console and requests the required command. | Present. | Works with Steam closed/open/minimized; command remains visible/copyable; actual log activity is verified. |
| Steam IPC path | Configurable full path with fixed default. | Present, same setup risk. | Auto-detection, file picker, directory rejection, delayed file creation, custom drive/Unicode/ACL tests, migration of saved choice. |
| Peer discovery | `BeginAuthSession`, `EndAuthSession`, `LeaveLobby`, read offset, forced flush. | Present. | Shared parser/state fixtures cover create/change/rename/delete/truncate/rotate/partial/duplicate/malformed/restart cases. |
| Old Steam networking peer | ETW-derived endpoint/ping and computed quality. | Present. | Validated correlation, reason-coded unavailability, IPv4/IPv6 and direct/relay tests; never `-1` or false-perfect quality. |
| New Steam networking peer | Steam real-time session status. | Present. | Same fields/refresh/error semantics as WPF and fixtures for state changes. |
| Session list | Steam name, ID, ping, quality and live updates. | Present with different WinUI layout. | Field, sort/display, empty/error, update cadence, selection, and accessibility parity. |
| Recent-player support | Can call Steam recent-player behaviour according to per-game setting. | Present statically. | Setting migrates; supported/unsupported result is visible; no crash when Steam is unavailable. |
| Open Steam profile | Double-click/action opens the correct peer. | Present statically. | Correct profile once, keyboard equivalent, Steam/browser fallback, no malformed URI. |
| Connection history | Per-game completed records, average ping, newest first, 500 records, clear; current model can include IP. | **Missing.** | Backward-compatible v1.5-and-later import, privacy decision implemented, valid-sample averaging, retention, corruption recovery, clear, and redacted export. |
| Per-game configuration | Activity/debug logs, overlay, recent-player, sound, hotkeys, manual block settings and legacy fields. | Mostly present using a different settings implementation. | Every field mapped with default, validation, migration, save, reset, corrupt-file recovery, and unknown-field preservation policy. |
| Activity/diagnostic logs | Per-game logs plus debug diagnostics. | Present in some form. | One LocalAppData policy, bounded retention, early-start diagnostics, atomic writes, and redacted support export. |
| New-session sound | Windows system beep option. | Present. | Exact parity first; configurable accessible notification can follow as an independently tested feature. |
| Main-window update check | Version check/link to Releases. | Present. | Offline, malformed response, newer/equal/older versions, stable/beta policy, and safe external link tested. |
| Overlay content | Live peer name/ID/ping/quality with colour/position settings. | Present as an `AppWindow`/XAML prototype. | Every field and setting retained; unavailable measurement is honest and accessible. |
| Overlay transparency/click-through | Transparent, topmost, no activation/click-through WPF window. | Uses native/AppWindow flags; real per-pixel parity is unproven. | Transparent regions, hit testing, focus, click-through, z-order, no activation, and no taskbar pollution verified. |
| Overlay tracking | Follows target position/focus/minimize; windowed/borderless only. | Equivalent intent, different native integration. | Move/resize/minimize/restore, destroyed/recreated HWND, monitor change, mixed DPI, display reconnect, game/app exit all pass. |
| Overlay rendering | Existing WPF behaviour under SDR/HDR/high refresh/OBS limitations. | New compositor path, unverified. | SDR/HDR/Auto HDR, VRR, 60–240 Hz, mixed displays, and OBS Game/Window/Display Capture results documented. |
| Overlay drag/offset | Current configurable offsets; known drag inconsistency. | No proven complete parity. | Deliberate decision and test for dragging, persisted offsets, scaling, and bounds recovery. |
| Manual hotkey | Low-level global hook and per-game key. | Present with a custom WinUI input surface. | Conflict/error state, immediate callback return, serialized work, focus rules, key-repeat suppression, unhook/rebind/shutdown tests. |
| Exact-flow observation | ETW maps traffic/ownership. | Present with newer dependency. | Clean-machine native load, one owned session, health state, IPv4/IPv6, concurrent traffic, cancellation, teardown. |
| Manual WFP disconnect | Atomic game-and-Steam application-identity UDP policy, dynamic filters, Steam logical close, and process-matched lobby release. | Present; concurrency model differs. | All safety gates from [IMPROVEMENT_ROADMAP.md](IMPROVEMENT_ROADMAP.md), including documented collateral scope, atomic failure, same-lobby reconnect, lobby release, and unrelated-process traffic. |
| Game/lobby exit | Ends monitoring/action and closes app to release Steam App ID. | Present in intent. | Exactly-once cleanup regardless of exit order, exception, shutdown, or forced target loss. |
| Single-instance behaviour | Process-name count and close. | Similar process-count approach. | Use Windows App SDK app-instance redirection; second launch activates/forwards to the owned instance. |
| Data compatibility | Existing WPF settings/config/history/log locations and formats. | No complete WPF migration, history absent. | Documented discovery order, backup, schema migration, atomic commit, rollback, and downgrade policy. |
| Accessibility | WPF baseline not comprehensively certified. | Not certified. | Keyboard-only, Narrator, Automation properties, focus order, high contrast, 200% text, and non-colour-only states pass. |
| Distribution | Current WPF release ZIP/native assets. | Unpackaged self-contained x64 project. | Signed clean artifact, complete native assets, checksums, SBOM/provenance, install/extract/start/exit/upgrade/rollback tests. |

## Migration architecture

### Core

Framework-neutral and fully fixture-testable:

- Steam IPC line parser;
- log-reader state and checkpoints;
- peer/lobby lifecycle;
- ping/quality measurement and availability reason;
- endpoint and ownership evidence;
- enforcement eligibility/policy and state machine;
- per-game configuration and migration;
- history, retention, and redaction;
- update/version comparison.

### Adapters

Owned behind interfaces so tests can substitute controlled failures:

- clock/timers;
- filesystem and atomic storage;
- Steamworks API;
- ETW session and packet source;
- WFP filter/session;
- process/window discovery;
- hotkeys;
- shell/URI/clipboard;
- update transport;
- UI dispatcher.

### Presentation

Two temporary front ends:

- WPF reference shell using the shared Core/adapters;
- WinUI 3 shell using the same contracts and fixtures.

No Steamworks, WFP, ETW, file watcher, or policy decision should live in a click handler or XAML code-behind solely because that was convenient in the prototype.

## Migration phases and exit criteria

### Phase 0 — freeze and record the reference

- Mark the exact WPF commit/config schema/history schema used as the parity baseline.
- Record screenshots and a short interaction capture for every surface.
- Produce a field-by-field configuration/default table.
- Capture sanitized old/new Steam IPC and measurement fixtures.
- Record WPF behaviour for attach, overlay, history, manual action, failures, and shutdown.
- Turn every matrix row above into a tracked item with an owner and an acceptance test.

**Exit:** all current behaviour is either specified as required, intentionally changed with rationale, or explicitly deprecated. No feature can disappear unnoticed.

### Phase 1 — stabilize and extract shared behaviour

- Complete Phase 0 of the [application roadmap](IMPROVEMENT_ROADMAP.md) on the WPF reference.
- Extract parser, peer state, measurements, enforcement policy, configuration, history, and version logic behind shared contracts.
- Add dependency adapters and fault injection.
- Make attach/shutdown stateful, transactional, and idempotent.
- Add fixture/unit tests before moving presentation code.

**Exit:** WPF passes shared tests and remains behaviourally unchanged except for planned safety/reliability fixes.

### Phase 2 — refresh the prototype foundation

- Move to .NET 10 LTS and the then-current Windows App SDK 2.3 servicing patch.
- Remove stale prototype copies of shared logic; reference the shared Core/adapters.
- Preserve x64, unpackaged, self-contained, and elevated deployment for this phase.
- Centralize versioning, architecture, native asset copy, and publish output.
- Move all new writes to the versioned LocalAppData structure, with migration from WPF locations.
- Audit unused native declarations. Remove process-injection-oriented APIs such as remote-thread/memory-write declarations if they truly have no call sites.

**Exit:** clean build/publish in CI, clean-machine start/exit, full native-dependency inventory, no duplicate business logic.

### Phase 3 — rebuild every application surface

- Port the shell, attach dialog, session view, configuration, logs/status, update prompt, and **complete History tab**.
- Replace WPF/MahApps-only controls deliberately. Windows App SDK has no first-party drop-in for every WPF control; choose a maintained control or an accessible native WinUI layout and test it.
- Use `DispatcherQueue`, correct `ContentDialog.XamlRoot`, explicit binding/view models, and WinUI resource patterns.
- Use [Windows App SDK app lifecycle instancing](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing) and [single-instance redirection](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance) instead of counting process names.

**Exit:** every non-overlay parity row is implemented, keyboard accessible, data-compatible, and passes shared tests.

### Phase 4 — prove the overlay as a focused spike

The overlay is the highest UI-specific risk. Do not assume that setting a transparent XAML background recreates WPF per-pixel transparency or capture behaviour.

- Obtain the HWND through the documented WinUI interop route: [retrieve a window handle](https://learn.microsoft.com/en-us/windows/apps/develop/ui/retrieve-hwnd).
- Use `AppWindow` for supported window management and centralize the remaining native flags/hooks: [manage app windows](https://learn.microsoft.com/en-us/windows/apps/develop/ui/manage-app-windows).
- Root delegates, treat destroyed HWNDs as normal, remove hooks on the owning thread, and use safe handle ownership where practical.
- Verify transparent pixels, click-through, no activation, target-relative z-order, taskbar/Alt+Tab behaviour, focus, move/resize/minimize, target restart, mixed DPI, multiple monitors, display reconnect, SDR/HDR, high refresh/VRR, and OBS capture.
- Decide and document whether an optional capture-visible overlay mode is needed.

**Exit:** the full overlay matrix passes on representative GPUs/displays. If not, keep WPF or ship WinUI without claiming replacement.

### Phase 5 — integrate privileged networking safely

- Retain a single explicitly owned ETW session with health reporting and idempotent teardown.
- Keep WFP filters in an owned dynamic session and enforce only after exact-flow validation.
- Serialize Steamworks, peer, ETW, and WFP state transitions.
- Refuse relay/ambiguous/shared ownership rather than guessing.
- Move all expensive work off the low-level keyboard hook.
- Run the complete enforcement safety suite in disposable Windows VMs and controlled live sessions.

Windows documents WFP architecture and dynamic-object lifetime in [About Windows Filtering Platform](https://learn.microsoft.com/en-us/windows/win32/fwp/about-windows-filtering-platform) and [WFP object management](https://learn.microsoft.com/en-us/windows/win32/fwp/object-management).

**Exit:** no unintended network change in monitor-only or targeted tests; filters and native resources always clean up.

### Phase 6 — side-by-side beta and release candidate

- Give preview builds a distinct title/data root so they cannot overwrite stable WPF data.
- Import only from a backup/copy; never mutate the sole WPF data set during beta.
- Publish a parity report showing every matrix row and evidence.
- Test fresh install, upgrade, downgrade/rollback, offline start, Unicode/spaces, no developer tools, supported Win10 and Win11, Steam stable/beta, and every claimed game/mode.
- Run accessibility, startup/idle/load performance, repeated attach/detach, eight-hour soak, sleep/resume, network change, display change, and abrupt termination.
- Have beta users submit only redacted support bundles.

**Exit:** zero unwaived parity gaps, zero P0 safety/correctness defects, and documented rollback succeeds.

### Phase 7 — stable transition

- Publish signed binaries, timestamped signatures, hashes, SBOM/provenance, symbols, known issues, and rollback.
- Keep the previous WPF stable release linked and supported for at least one WinUI stable cycle.
- Change the default download only after real-world beta evidence confirms the clean-machine and live-game gates.
- Archive the WPF UI only after its supported rollback window ends; retain the shared regression fixtures indefinitely.

## Native, lifecycle, and hotkey guidance

- Prefer exact, reviewed native signatures and source-generated `LibraryImport` where practical; follow [.NET native interop guidance](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices).
- Use owned safe handles when ownership semantics permit them.
- Keep callbacks rooted for the exact native lifetime and make unhook/dispose idempotent.
- If the supported hotkey is a normal modifier combination, evaluate [`RegisterHotKey`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey) for built-in conflict detection. If single-key behaviour requires `WH_KEYBOARD_LL`, use a dedicated message-loop thread, enqueue and return immediately, and follow [`LowLevelKeyboardProc`](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) timeout guidance.
- Do not mark an application-level exception handled while leaving an unknown partial attach state. Roll back the owned session or terminate in a controlled, diagnosable way.

## Deployment decision after parity

| Option | Use now? | Conditions |
|---|---|---|
| Unpackaged, self-contained x64 | **Yes for parity.** | Closest to current native/elevated behaviour; accept larger serviced releases. |
| MSIX direct/App Installer | Proof of concept after parity. | Validate elevation/full trust, ETW/WFP, native Steamworks, Steam App ID lifecycle, overlay/hotkey, protected install paths, migration, update, and rollback. |
| Microsoft Store | Later option. | Same proof plus policy/compliance, signing identity, support, and store-managed update behaviour. |
| Normal-user UI + elevated broker | Post-parity architecture. | Narrow authenticated protocol, least privilege, strict request validation, owned lifecycle, threat model, and independent security review. |

If MSIX passes the proof, Microsoft documents [App Installer automatic update and repair](https://learn.microsoft.com/en-us/windows/msix/app-installer/auto-update-and-repair--overview) and [code-signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options). It should not be selected merely because WinUI supports it.

## Final cutover checklist

- [ ] WPF v1.6 reference commit and schemas recorded.
- [ ] Every parity row has an automated or documented manual test.
- [ ] History imports without loss and follows the approved endpoint privacy policy.
- [ ] Every config field/default migrates or has an explicit deprecation.
- [ ] Parser and peer state pass shared fixtures in WPF and WinUI.
- [ ] `-1`/false-perfect measurement states are eliminated.
- [ ] Attach rolls back at every injected failure point.
- [ ] Overlay passes DPI/HDR/high-refresh/OBS/native-lifetime tests.
- [ ] Monitoring makes no network change.
- [ ] Manual enforcement passes documented application-scope, same-lobby reconnect, lobby-release, and cleanup tests.
- [ ] Single-instance redirection replaces process counting.
- [ ] Clean supported Win10 and Win11 artifact tests pass.
- [ ] Accessibility and eight-hour soak pass.
- [ ] Signed release, hashes, SBOM/provenance, symbols, known issues, and rollback are published.
- [ ] Previous WPF stable remains available.

Until every item is checked with evidence, call WinUI 3 a preview, not the replacement.
