# Upstream issue audit

Audit date: 25 July 2026

Source: [all issues in tremwil/SteamP2PInfo](https://github.com/tremwil/SteamP2PInfo/issues)

## Executive conclusion

The original tracker contains 52 issues: 20 open and 32 closed. Eight missing numbers in the sequence are pull requests, not issues. Every issue was reviewed together with its discussion and compared with the current revised WPF application and the separate WinUI 3 prototype.

Static inspection can establish that a mechanism exists or that a defect is still possible; it cannot prove live Steam, game, overlay, ETW, or Windows Filtering Platform behaviour. Accordingly, this audit uses these terms:

- **Confirmed by static inspection** means the relevant implementation is present or absent in the current source.
- **Likely persists** means the same risky mechanism remains and no conclusive fix is visible.
- **Partly addressed** means the revision contains related work but still needs the stated acceptance test.
- **Runtime gate** means the result cannot responsibly be claimed until it is exercised in a clean, controlled environment.
- **Out of scope** means it should be documented as a product boundary rather than silently promised.

No code was changed and no build or live game test was run during this audit.

## Revised tracker context

The revised repository had two open issues at the audit date:

- [revised #6 — disconnecting low-ping players](https://github.com/wardriven/steamp2pinfo-revised/issues/6) confirms the severity of automatic enforcement choosing or retaining the wrong target. Automatic high-ping disconnect has since been removed/disabled in v1.4.0. Add a regression test proving that legacy configuration cannot reactivate it, then close the issue with the removal rationale.
- [revised #3 — whitelist players](https://github.com/wardriven/steamp2pinfo-revised/issues/3) was intended to protect players from that automatic path. With automatic enforcement removed, close it as obsolete unless a separately specified manual-exclusion use case is still valuable. A whitelist must not be used to make unsafe automatic targeting appear reliable.

## Release-blocking findings

### 1. Network enforcement changes the meaning of upstream issues #3, #5, #31, and #54

The original maintainer could reasonably dismiss [#54](https://github.com/tremwil/SteamP2PInfo/issues/54) on the basis that the original application only observed ETW events and could not modify network traffic. That reasoning does **not** apply to this fork: the revised application can create WFP filters and close Steam peer sessions.

Before another stable release, the application needs a repeatable test proving that:

- monitoring alone never installs a filter or closes a session;
- a manual action atomically installs the complete application-scoped UDP lock before any Steam close call;
- Steam login/store/web traffic remains available, while the UI and README explicitly warn that Steam voice, Remote Play, other games, and all other `steam.exe` UDP can be interrupted while locked;
- missing application identity or partial WFP policy creation is refused without closing a Steam session;
- every filter is removed when the selected game's process-matched `LeaveLobby` ends the affected lobby, and on ordinary exit, game exit, attach failure, crash, forced termination, and the next startup;
- once activated, the action remains armed through peer removal and same-lobby reconnect attempts, then releases on the process-matched `LeaveLobby` event so a later lobby or player can connect;
- the UI states the multiplayer timeout/penalty and misuse risks before the feature is enabled.

This is the first release gate, not a normal backlog item.

### 2. Persistent endpoint data changes the meaning of upstream issue #55

The original maintainer declined [#55](https://github.com/tremwil/SteamP2PInfo/issues/55), which requested access to player IP addresses. The current WPF v1.6 history model stores IP addresses, while diagnostics and enforcement logs can also contain endpoints.

Before release:

- publish a clear data inventory and retention policy;
- default to the least identifying persisted data needed by the product;
- consider omitting or one-way redacting IP addresses from persistent history;
- keep files under the current user's application-data directory with appropriate access;
- provide a redacted diagnostic export and warn users not to post raw logs/history;
- test corruption recovery, deletion, retention limits, upgrades, and uninstall behaviour.

### 3. Setup and attach remain a recurring crash/onboarding risk

Issues [#11](https://github.com/tremwil/SteamP2PInfo/issues/11), [#16](https://github.com/tremwil/SteamP2PInfo/issues/16), [#22](https://github.com/tremwil/SteamP2PInfo/issues/22), [#24](https://github.com/tremwil/SteamP2PInfo/issues/24), [#26](https://github.com/tremwil/SteamP2PInfo/issues/26), [#27](https://github.com/tremwil/SteamP2PInfo/issues/27), [#33](https://github.com/tremwil/SteamP2PInfo/issues/33), [#35](https://github.com/tremwil/SteamP2PInfo/issues/35), [#50](https://github.com/tremwil/SteamP2PInfo/issues/50), [#51](https://github.com/tremwil/SteamP2PInfo/issues/51), [#56](https://github.com/tremwil/SteamP2PInfo/issues/56), [#57](https://github.com/tremwil/SteamP2PInfo/issues/57), and [#60](https://github.com/tremwil/SteamP2PInfo/issues/60) form one repeated failure family.

The current configuration still defaults to a fixed `C:\Program Files (x86)\Steam\logs\ipc_SteamClient.log` path. A custom path is editable, but static inspection shows that a directory value can pass the early parent-directory check. Attach also initializes several native, file, overlay, Steam, ETW, history, and enforcement resources as one operation, so failure part-way through needs transactional rollback.

The fix plan is to auto-detect Steam, use a file picker, distinguish a directory from the exact log file, preflight every dependency, return one actionable error without closing the app, and undo every resource already acquired when attach fails.

### 4. Detection and ping correctness are not yet proven

The current parser retains the `BeginAuthSession`, `EndAuthSession`, `LeaveLobby`, read-position, file-watcher, and dummy `SendClanChatMessage` flush approach introduced by upstream releases 1.1 and 1.2. This addresses important earlier defects, but later reports [#52](https://github.com/tremwil/SteamP2PInfo/issues/52), [#53](https://github.com/tremwil/SteamP2PInfo/issues/53), and [#58](https://github.com/tremwil/SteamP2PInfo/issues/58) remain open.

For the older Steam networking path, ping sampling still depends on narrow ETW/STUN packet assumptions. An unavailable ping can also lead to a plausible-looking quality value. The stable release must use a reason-coded state such as **Unavailable — no packet samples**, **Unavailable — relay**, or **Unavailable — ETW failed**, never `-1` or an apparently perfect quality.

## Complete issue ledger

| Issue | Upstream result | Revised-application finding | Priority and acceptance test |
|---|---|---|---|
| [#2 Ghost process on close](https://github.com/tremwil/SteamP2PInfo/issues/2) | Open; overlay shutdown suspected. | Cleanup code exists, but timers, hooks, ETW, WFP, and background threads still require runtime proof. | **High, runtime gate.** Run at least 50 app-first and game-first shutdown cycles, overlay on/off. Exit promptly, release all resources, and allow immediate relaunch. |
| [#3 Unexpected disconnect](https://github.com/tremwil/SteamP2PInfo/issues/3) | Closed as likely game/EAC instability. | That conclusion predates this fork's network enforcement. | **Critical.** A/B-test monitor-only, armed, used, and crash paths. Monitoring must remain passive; strict mode must affect only the documented game/steam.exe UDP application scope and clean up on exit. |
| [#4 IPC parsing stops](https://github.com/tremwil/SteamP2PInfo/issues/4) | Closed as fixed in 1.0.2. | The revision still depends on file notifications, log flush timing, and a saved read offset. | **High.** Replay partial writes, duplicate notifications, delayed flush, truncation, rotation, restart, and malformed lines with no missed/duplicate peer events. |
| [#5 Disconnect button](https://github.com/tremwil/SteamP2PInfo/issues/5) | Declined because of timeout, penalty, and abuse risk. | The fork implements adjacent manual block/close behaviour. | **Critical product-safety gate.** Explicit opt-in, application-scope warning, visible armed state, exit cleanup, audit record, and penalty warning are required. |
| [#6 False “already running”](https://github.com/tremwil/SteamP2PInfo/issues/6) | Closed unreproduced. | Both variants use process enumeration, which is weaker than an owned single-instance primitive. | **Medium.** Test rapid relaunch, crash, rename, separate Windows sessions, and real second launch; activate the live instance and never let a dead one block startup. |
| [#7 Hard-coded Elden Ring process](https://github.com/tremwil/SteamP2PInfo/issues/7) | Fixed by PR #8. | Current parser uses the selected process name. | **Regression gate.** A non-Elden-Ring fixture/game must work while unrelated game IPC lines are ignored. |
| [#9 OBS-friendly overlay](https://github.com/tremwil/SteamP2PInfo/issues/9) | Open. | The overlay is still a separate transparent/tool window. | **Medium.** Test OBS Game, Window, and Display Capture. If Window Capture cannot see it, plan an explicit capture mode rather than claiming compatibility. |
| [#10 Rare peer detection](https://github.com/tremwil/SteamP2PInfo/issues/10) | Closed without a demonstrated fix in the thread. | Later parser work is relevant, but not proof. | **High.** Establish a detection target with fixtures and at least 100 expected live peer joins across host/client roles with zero misses. |
| [#11 Access-denied crash after attach](https://github.com/tremwil/SteamP2PInfo/issues/11) | Closed after correcting a custom Steam path. | Editable path partly addresses it; failure handling is not proven. | **High.** Nonexistent, locked, read-only, custom-drive, Unicode, removable, and inaccessible paths must produce one actionable message and leave the app usable. |
| [#12 How to find recent players](https://github.com/tremwil/SteamP2PInfo/issues/12) | Closed with instructions. | Primarily documentation. | **Low.** Verify the route against the current Steam client before publishing help or screenshots. |
| [#13 Ping `-1` or blank after time](https://github.com/tremwil/SteamP2PInfo/issues/13) | Closed without a maintainer root cause. | Same symptom later appears in #53/#58. | **High.** Soak for 60+ minutes in each display/network mode; report reason-coded unavailable states and recover after endpoint changes. |
| [#14 Automatically port old Steam API games](https://github.com/tremwil/SteamP2PInfo/issues/14) | Open placeholder. | Separate old/new peer adapters exist; automatic porting does not. | **Out of scope.** Preserve both supported paths and reject injection/automatic porting until separately designed and threat-modelled. |
| [#16 IPC log missing](https://github.com/tremwil/SteamP2PInfo/issues/16) | Open; repeated custom-path confusion. | Fixed default path remains; path is editable. | **High.** Auto-detect Steam, require a full file path, tolerate the file appearing after the command, offer Browse/Reset, and avoid repeated alerts. |
| [#17 Peers rarely detected](https://github.com/tremwil/SteamP2PInfo/issues/17) | Marked fixed by `BeginAuthSession` in 1.1. | Current parser retains that approach. | **High regression gate.** Cover host, client, invader, phantom, arena, rapid rematch, and abrupt exit. |
| [#19 Overlay tearing/high refresh](https://github.com/tremwil/SteamP2PInfo/issues/19) | Reporter resolved it using G-Sync. | That is not an application fix; WinUI composition will differ again. | **Medium.** Compare overlay off/on at 60/120/144/240 Hz, VRR on/off, mixed DPI, and multiple monitors. |
| [#20 Blank session info](https://github.com/tremwil/SteamP2PInfo/issues/20) | Closed as probably covered by later detection work. | Not conclusively verified. | **High.** Diagnostics must identify command inactive, file not changing, parser mismatch, Steam API failure, or UI binding failure as separate stages. |
| [#21 Write ping to a log](https://github.com/tremwil/SteamP2PInfo/issues/21) | Open; maintainer welcomed a contribution. | WPF v1.5 and later persist average-ping history; the WinUI prototype omits it. | **Medium/partly addressed.** Test sampling, invalid-value rejection, 500-record retention, corruption, deletion, export, per-game isolation, and WinUI parity. |
| [#22 Null reference during attach](https://github.com/tremwil/SteamP2PInfo/issues/22) | Closed after an anecdotal Defender attribution. | No conclusive code fix follows from that explanation. | **High.** Test Defender/Controlled Folder Access, denied URI launch, missing Steam components, cancelled selection, null window, and partial initialization. |
| [#23 Recent players appears empty](https://github.com/tremwil/SteamP2PInfo/issues/23) | Closed after locating the right Steam surface. | Documentation/UX. | **Low.** Explain that local peer detection and Steam's recent-player population are separate and verify both current Steam routes. |
| [#24 Unauthorized access on custom drive](https://github.com/tremwil/SteamP2PInfo/issues/24) | Closed without a demonstrated app fix. | Same path family remains relevant. | **High.** Valid files must work regardless of app/Steam drive; bare directories must be rejected before any open call. |
| [#25 Add/open a peer profile](https://github.com/tremwil/SteamP2PInfo/issues/25) | Closed with double-click/Steam-overlay guidance. | Profile opening is present. | **Low regression gate.** Open the correct profile once with Steam available/unavailable and provide a safe browser fallback. |
| [#26 Invalid window handle on focus loss](https://github.com/tremwil/SteamP2PInfo/issues/26) | Open and unreproduced upstream. | Overlay/native-window lifetime remains a risk. | **High.** Hammer Alt+Tab, minimize, display disconnect, target-window destruction, game restart, and app shutdown; stale handles must be safe no-ops. |
| [#27 Custom Steam directory not found](https://github.com/tremwil/SteamP2PInfo/issues/27) | Closed by pointing to Config. | Setting exists but discovery is weak. | **Medium.** On failure, focus the setting, show detected candidates and an example ending in `ipc_SteamClient.log`, then retest immediately. |
| [#28 Show in-game character name](https://github.com/tremwil/SteamP2PInfo/issues/28) | Open; not available from the used Steam API. | Would require a substantially riskier game-specific mechanism. | **Out of scope.** Do not add memory/proxy access during migration; require a separate anti-cheat, privacy, and abuse review. |
| [#29 Cannot find the executable](https://github.com/tremwil/SteamP2PInfo/issues/29) | User downloaded source instead of a release. | README adoption problem. | **Medium.** The first call to action must say “Download the app” and link Releases, while explicitly warning that GitHub source archives are not the app. |
| [#30 Misses arena players](https://github.com/tremwil/SteamP2PInfo/issues/30) | Marked fixed by 1.1 detection rewrite. | Retained but not re-proven in the fork. | **High regression gate.** Run 100 alternating host/non-host arena sessions including immediate rematches. |
| [#31 Automatic ping filter](https://github.com/tremwil/SteamP2PInfo/issues/31) | Declined due to abuse and re-query limitations. | Automatic high-ping disconnect was added then removed in v1.4; manual action remains. | **Safety decision.** Keep automatic enforcement disabled. Any reconsideration requires a separate product/abuse review. |
| [#32 Steam console does not open](https://github.com/tremwil/SteamP2PInfo/issues/32) | Closed after a manual URI workaround. | URI launch is still environment-dependent. | **Medium.** Test Steam closed/open/minimized and privilege mismatch; keep a visible copyable command and manual-console fallback. |
| [#33 Non-default Steam path](https://github.com/tremwil/SteamP2PInfo/issues/33) | Open. | Editable path exists, but hard-coded default and discoverability remain. | **High.** Auto-detect registry/config candidates, migrate old settings, persist the selection, and never suggest a directory junction. |
| [#35 Is the IPC setting a folder or file?](https://github.com/tremwil/SteamP2PInfo/issues/35) | Open. | UI can still allow this ambiguity. | **High.** Label it “IPC log file,” use a file picker, validate filename/type, and show existence plus last-write status. |
| [#36 Blank view although logs exist](https://github.com/tremwil/SteamP2PInfo/issues/36) | Closed as probably covered by other fixes. | Closure was inferential. | **High.** Expose diagnostic counters for lines read, process matches, parsed IDs, peer objects, and displayed rows. |
| [#37 HDR becomes washed out](https://github.com/tremwil/SteamP2PInfo/issues/37) | Reporter said a later Elden Ring patch fixed it. | WinUI migration changes the compositor/window path. | **Medium.** Test SDR, HDR10, Auto HDR, borderless/windowed, capture, and focus transitions on representative GPUs. |
| [#40 Upgrade detects nothing](https://github.com/tremwil/SteamP2PInfo/issues/40) | Required command changed; Steam restart and new command fixed it. | Version/command checks exist. | **High upgrade gate.** Upgrade from every supported version with Steam warm/cold; prompt once and verify actual log activity, not merely URI/clipboard success. |
| [#41 Partial players and stale ghosts](https://github.com/tremwil/SteamP2PInfo/issues/41) | Reporter confirmed the 1.2 prerelease fix. | Current parser retains `LeaveLobby`, forced flush, and read position. | **High regression gate.** Multi-peer joins/exits in every order must appear promptly and clear correctly without leaving enforcement armed. |
| [#43 Red “too many calls” console output](https://github.com/tremwil/SteamP2PInfo/issues/43) | Explained as an intentional dummy flush call. | The invalid dummy call remains in static code. | **Medium trust/stability.** Rate-limit it, document expected output, verify continued IPC writes, and investigate a cleaner trigger. |
| [#44 Map Steam-owned connections](https://github.com/tremwil/SteamP2PInfo/issues/44) | Open technical discussion. | Mapping now feeds enforcement as well as display. | **High enforcement/privacy gate.** Validate relay/direct changes, endpoint reuse, NAT, IPv4/IPv6, concurrent games, and shared ownership; refuse ambiguity. |
| [#45 Linux support](https://github.com/tremwil/SteamP2PInfo/issues/45) | Open; ETW cannot simply run under Wine. | The product relies on Windows, ETW, Win32, WFP, and Steam native APIs. | **Out of scope.** State Windows-only support; a Linux edition would be a separate backend/product effort. |
| [#46 A game stopped being detected](https://github.com/tremwil/SteamP2PInfo/issues/46) | Closed; game likely does not call `BeginAuthSession`. | Current parser still depends on the required auth calls. | **High if claiming general compatibility.** Publish a tested matrix and say “compatible games that emit the required calls,” or design/test a bounded fallback. |
| [#47 Source archive mistaken for app](https://github.com/tremwil/SteamP2PInfo/issues/47) | Closed by directing user to Releases. | README adoption problem. | **Medium.** Same acceptance criteria as #29, plus a screenshot/asset name and supported-Windows section. |
| [#48 Red clan-account assertion](https://github.com/tremwil/SteamP2PInfo/issues/48) | Intentional side effect of dummy flush call. | Same mechanism remains. | **Medium.** Prove no side effect, document it, monitor Steam changes, and replace it if a supported flush/health method becomes available. |
| [#49 Custom notification sound](https://github.com/tremwil/SteamP2PInfo/issues/49) | Open. | Current revision still uses a Windows system beep. | **Medium/accessibility.** Plan previewable system/WAV choices, per-session/per-peer semantics, rate limiting, mute, missing-file fallback, and setting migration. |
| [#50 IPC log missing again](https://github.com/tremwil/SteamP2PInfo/issues/50) | Closed with custom-path advice. | No demonstrated product fix. | **High.** Consolidate with #16/#27/#33/#35/#51/#56 into one setup flow and test suite. |
| [#51 Directory entered instead of IPC file](https://github.com/tremwil/SteamP2PInfo/issues/51) | Closed after correcting it to the exact file. | **Likely persists:** early validation checks the parent, so a directory value may advance. | **High.** Reject directories immediately, normalize safely, catch access errors, and keep the app running. |
| [#52 Intermittently empty overlay](https://github.com/tremwil/SteamP2PInfo/issues/52) | Open; startup timing affected success. | Suggests command/log lifecycle health, not only parsing. | **High.** Test every Steam/app/game startup order, beta/stable Steam, stale logs, warm restarts, and command entered once/twice. Surface live logging health. |
| [#53 Elden Ring ping always `-1`](https://github.com/tremwil/SteamP2PInfo/issues/53) | Open. | Older networking path still depends on ETW/STUN observations. | **High.** Cover direct/relay, old/new APIs, IPv4/IPv6, endpoint change, and ETW availability; show a reason, never raw `-1`. |
| [#54 App causes Steam disconnect](https://github.com/tremwil/SteamP2PInfo/issues/54) | Closed because the original app did not alter traffic. | **Upstream conclusion invalid for this fork** because WFP filters and session close calls now exist. | **Critical.** Exact-scope, unrelated-Steam-traffic, crash cleanup, power-loss, next-start cleanup, and ambiguity-refusal tests block release. |
| [#55 Expose player IP addresses](https://github.com/tremwil/SteamP2PInfo/issues/55) | Declined by the original maintainer. | WPF v1.5 and later persist IPs in history and logs can contain endpoints. | **High privacy gate.** Define minimization, retention, access, deletion, redaction, export, and sharing-warning behaviour before release. |
| [#56 App closes after entering command](https://github.com/tremwil/SteamP2PInfo/issues/56) | Open; a commenter fixed a bad path. | Same path/attach transaction family. | **High.** Malformed, directory, inaccessible, missing, stale, and locked paths must never cause app shutdown. |
| [#57 AC6 attach null reference](https://github.com/tremwil/SteamP2PInfo/issues/57) | Open. | No evidence that the anecdotal #22 explanation covers it. | **High.** Test clean/existing config, invalid App ID, target-window loss, Steam API failure, overlay failure, and rollback. |
| [#58 Ping `-1`, quality `1`](https://github.com/tremwil/SteamP2PInfo/issues/58) | Open. | Unavailable data can appear as a valid-looking perfect value. | **High correctness gate.** Use nullable/reason-coded measurements and never calculate quality from an invalid ping series. |
| [#59 Two instances](https://github.com/tremwil/SteamP2PInfo/issues/59) | Open title-only request. | Both variants intentionally reject a second process. | **Low/product decision.** Retain and explain one instance, or design one process with multiple contexts; do not allow competing ETW, Steam API, settings, hotkeys, or filters. |
| [#60 Missing ETW native module](https://github.com/tremwil/SteamP2PInfo/issues/60) | Open; clean Windows 10 failure during ETW startup. | The prototype has newer dependencies but no clean-machine proof. | **Release blocker for affected systems.** Test the exact release artifact on clean supported Win10/Win11 VMs, audit native assets/architecture, preflight ETW, and fail with repair guidance. |

## Pull requests occupying the missing numbers

GitHub issues and pull requests share one number sequence. These are not omitted issues:

| Pull request | Relevance |
|---|---|
| [#1 Create LICENSE](https://github.com/tremwil/SteamP2PInfo/pull/1) | Added the license. |
| [#8 Remove hard-coded process name](https://github.com/tremwil/SteamP2PInfo/pull/8) | Fixed issue #7. |
| [#15 Skip Steam console button](https://github.com/tremwil/SteamP2PInfo/pull/15) | Earlier console-flow improvement. |
| [#18 Play sound on new session](https://github.com/tremwil/SteamP2PInfo/pull/18) | Origin of the current Windows beep; relevant to #49. |
| [#34 Project cleanup](https://github.com/tremwil/SteamP2PInfo/pull/34) | Repaired project references/build problems. |
| [#38 `BeginAuthSession` detection rewrite](https://github.com/tremwil/SteamP2PInfo/pull/38) | Intended to fix #17/#30; also explains the compatibility boundary in #46. |
| [#39 Merge community improvements](https://github.com/tremwil/SteamP2PInfo/pull/39) | Brought #34/#38 into the main branch. |
| [#42 Detection/reliability fixes](https://github.com/tremwil/SteamP2PInfo/pull/42) | Added `LeaveLobby`, forced flushing, and read-position preservation for #41. |

## Recommended tracker actions

Create focused revised-repository issues rather than copying all 52 upstream threads verbatim:

1. **P0 — Prove WFP/session-close scope and cleanup** (#3, #5, #31, #44, #54).
2. **P0 — Define endpoint-data privacy and redacted support bundles** (#44, #55).
3. **P0 — Make attach transactional and validate/autodetect the IPC file** (#11, #16, #22, #24, #27, #32, #33, #35, #50, #51, #56, #57, #60).
4. **P0 — Add parser fixtures and logging-health diagnostics** (#4, #10, #17, #20, #30, #36, #40, #41, #43, #46, #48, #52).
5. **P0 — Replace `-1`/false-quality output with reason-coded measurement state** (#13, #53, #58).
6. **P1 — Make shutdown and overlay/native lifetimes idempotent** (#2, #19, #26, #37).
7. **P1 — Finish and export privacy-safe history** (#21, #55).
8. **P1 — Replace process counting with deliberate single-instance activation** (#6, #59).
9. **P2 — Add configurable, accessible notifications** (#49).
10. **Documentation — improve downloads, compatibility, recent-player help, Windows-only scope, and capture limitations** (#9, #12, #23, #29, #45, #46, #47).

The detailed implementation sequence and release gates are in [IMPROVEMENT_ROADMAP.md](IMPROVEMENT_ROADMAP.md). The migration-specific parity plan is in [WINUI3_MIGRATION_PLAN.md](WINUI3_MIGRATION_PLAN.md).
