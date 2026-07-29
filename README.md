# SteamP2PInfo — Elden Ring and Steam P2P Ping Monitor for Windows

SteamP2PInfo is an open-source Windows tool that shows Steam P2P peers, ping, and connection quality for **Elden Ring** and compatible Steam Networking games. It provides an optional overlay, Steam recent-player support, local connection history, and an advanced lobby-scoped manual UDP disconnect.

It can help you inspect an Elden Ring or Steam P2P connection; it does **not** lower ping, fix lag, repair an ISP route, solve a Steam outage, or detect every connection in every Steam game.

## Download the app

**[Download the latest SteamP2PInfo release](https://github.com/wardriven/steamp2pinfo-revised/releases/latest)**

Download the named Windows x64 release asset and extract the entire archive before running it. GitHub's automatically generated **Source code (zip)** and **Source code (tar.gz)** files contain source, not the ready-to-run application.

The source tree is prepared for **v1.6.0**. Check the selected GitHub release notes and download the named Windows x64 asset rather than an automatically generated source archive.

> **Project status:** active work in progress. Read [requirements and limitations](#requirements-and-compatibility) and [network and privacy safety](#network-and-privacy-safety) before relying on the overlay, history, or manual disconnect.

![Elden Ring peer ping and connection-quality overlay](https://raw.githubusercontent.com/tremwil/SteamP2PInfo/master/overlay_er.PNG)

<img width="800" height="450" alt="SteamP2PInfo PingGuard main session view on Windows" src="https://github.com/user-attachments/assets/2a679b45-e9ea-4107-9acf-9092a568184a" />

<img width="800" height="500" alt="SteamP2PInfo per-game configuration and manual hotkey settings" src="https://github.com/user-attachments/assets/d5c7df61-8745-415e-bc94-041bc3722295" />

## What it shows

- Detected Steam peers with Steam name and Steam ID.
- Ping and connection quality when the active Steam networking path exposes enough information.
- An optional in-game-adjacent overlay for windowed and borderless play.
- Steam recent-player integration for games that do not populate it themselves.
- Per-game activity/debug logging.
- In v1.5 and later, completed per-game connection history with average valid ping.
- A manual hotkey that atomically blocks game-and-Steam UDP reconnects for the current lobby before closing visible peer sessions.

## What it does not do

- It does not reduce latency, improve routing, prioritize packets, or repair Wi-Fi.
- It does not diagnose every Elden Ring network issue or Steam connection problem.
- It cannot promise a ping for relayed, unsupported, ambiguous, or unobserved traffic.
- It does not support every Steam game. Peer discovery currently depends on the game authenticating peers through `ISteamUser::BeginAuthSession`.
- It does not show in-game character names.
- The overlay is not supported in exclusive fullscreen.
- OBS Window/Game Capture compatibility is not guaranteed; Display Capture may be required.
- Linux and Wine are not supported because the application depends on Windows APIs, ETW, and Windows Filtering Platform.

## Requirements and compatibility

| Requirement | Current position |
|---|---|
| Operating system | Windows 10 or Windows 11, x64. Clean-machine compatibility remains part of the stability test plan. |
| Permission | Administrator permission is required for ETW network events and manual WFP filtering. |
| Steam | Steam desktop client with the IPC logging command enabled. |
| Game | Elden Ring or another Steam P2P game that emits the required authentication calls. Compatibility should be treated as game/version-specific until tested. |
| Overlay mode | Windowed or borderless. Exclusive fullscreen is not supported. |
| Network path | Direct and observable connections provide the most information. Steam Datagram Relay or ambiguous ownership can make ping or manual enforcement unavailable. |
| Architecture | Windows x64 release asset with all bundled native files kept together. |

Elden Ring was the original target. “Compatible Steam game” means the game uses a Steam networking/authentication path the application understands; it is not a claim that every Steam multiplayer title works.

## Quick start

1. Open [Releases](https://github.com/wardriven/steamp2pinfo-revised/releases/latest), download the Windows x64 application archive, and extract all files to a normal folder. Do not run the executable from inside the ZIP.
2. Start Steam and the game. Use windowed or borderless mode if you want the overlay.
3. Start `SteamP2PInfo.exe`, accept the administrator prompt, and choose **Attach Game**.
4. Select the correct game window. On first use for that game, enter its numeric **Steam App ID**. [SteamDB](https://steamdb.info/) can help locate it.
5. When Steam's console opens, enter:

   ```text
   log_ipc "BeginAuthSession,EndAuthSession,LeaveLobby,SendClanChatMessage"
   ```

6. Return to SteamP2PInfo. Use the **Config** tab for that game's overlay, recent-player, logging, sound, and hotkey settings.

The command makes Steam write the IPC events used to associate authenticated peers with the selected game. Steam may need to be restarted and the command entered again after a Steam/app upgrade or when logging is inactive.

## Features

### Live Steam P2P session information

The main view displays the peers the selected game authenticates. Depending on the Steam networking API and network path, it shows:

- Steam name;
- Steam ID;
- ping in milliseconds;
- connection quality;
- session/log activity.

Double-click a peer in the main list to open the corresponding Steam profile. The per-game recent-player option can also call Steam's recent-player function, but Steam controls where and when that entry appears.

### Elden Ring ping overlay

The customizable overlay follows the selected game window and can display live peer data without taking input focus. It is intended for windowed and borderless modes.

Overlay position, content, colour, and related options are configured per game. Mixed-DPI, HDR, high-refresh-rate, drag, and capture behaviour vary by system and remain explicit stability-test areas.

### Connection history (v1.5 and later)

The **History** tab records completed connections for the attached game, newest first. It keeps the latest 500 records and can show:

- Steam name;
- Steam ID;
- average valid ping;
- IP address when a direct endpoint was available.

Average ping uses valid samples collected during the connection. Missing ping or direct endpoint data appears as `Unavailable`.

History is saved separately for each game in the local `history\` folder. **Raw IP addresses may be stored there.** Treat that folder as private and read [network and privacy safety](#network-and-privacy-safety) before sharing files. **Clear History** removes completed history for the attached game; it does not promise to remove matching data from separate activity or diagnostic logs.

### Lobby-scoped manual UDP disconnect

Automatic high-ping disconnect was removed and disabled in v1.4.0 after stability and functionality problems. Ping remains visible so the user can decide whether to use the separate manual hotkey.

Configure **Manual block all peers hotkey** in the game's **Config** tab. With SteamP2PInfo attached and the game in the foreground, one press activates a UDP reconnect lock for the current lobby.

The current action:

1. opens the dynamic Windows Filtering Platform session while the game is attached, before the hotkey is needed;
2. atomically installs application-identity UDP deny filters for the attached game and `steam.exe`, inbound and outbound over IPv4 and IPv6;
3. lets WFP reauthorize and stop existing UDP flows while preventing new sockets, endpoints, relay routes, or ports from reconnecting;
4. only after WFP accepts the complete filter transaction, calls Steam's logical close-session API for every visible peer;
5. parses Steam IPC changes every second and retries logical session closes every 100 ms;
6. covers peers and replacement sessions discovered after the original keypress;
7. remains active through peer removal and same-lobby reconnect attempts;
8. removes the dynamic filters when Steam IPC reports that the selected game left the lobby, allowing the next lobby or player connection; game or SteamP2PInfo exit remains a fallback cleanup boundary.

This strict scope is intentional. Steam P2P traffic can be owned by `steam.exe`, and a new connection can use a different direct or relay address and port. Blocking only the previously observed five-tuple—or only Valve's documented port ranges—cannot guarantee that the UDP session stays disconnected.

The tradeoff is that this is not a per-Steam-ID firewall rule. While the lock is active, **all UDP traffic from the attached game and `steam.exe` is blocked**, so Steam voice, Remote Play, another concurrently running Steam game, and other Steam UDP features can be interrupted. TCP and web traffic are outside this lock. The lock is released when the selected game's process-matched `LeaveLobby` event is observed; close SteamP2PInfo to remove it sooner if the effect is unexpected.

Successfully installed filters use a dynamic WFP session and should disappear if SteamP2PInfo terminates unexpectedly as well as during normal shutdown.

## Troubleshooting

### Elden Ring peers or ping are not detected

Check each stage in order:

1. Steam was running when the IPC command was entered.
2. The command exactly matches the [quick-start command](#quick-start).
3. The configured **Steam IPC log file** is the real `ipc_SteamClient.log` file for this Steam installation.
4. The file exists and its modified time changes while Steam/game activity occurs.
5. SteamP2PInfo is attached to the correct game window and App ID.
6. The session has another authenticated Steam peer.
7. The game/version uses a supported authentication/networking path.

If logging appears stale, fully exit Steam, restart it, enter the command soon after Steam starts, then launch/attach the game. This is a workaround reported in upstream issue [#52](https://github.com/tremwil/SteamP2PInfo/issues/52), not a guaranteed fix.

Do not repeatedly post raw logs publicly. They can contain Steam IDs, file paths, IP addresses, and network endpoints.

### Steam IPC log file is not found

The setting must be a **file**, not the `logs` folder.

Typical default:

```text
C:\Program Files (x86)\Steam\logs\ipc_SteamClient.log
```

For a custom Steam installation, change the drive/folder but keep the final filename:

```text
D:\Steam\logs\ipc_SteamClient.log
```

The file may not exist until Steam accepts the `log_ipc` command. A bare value such as `D:\Steam\logs` is incorrect and has caused access-denied or attach failures in past versions.

### Ping shows `Unavailable` or `-1`

Peer identity and ping are separate measurements. A peer can be detected while ping remains unavailable because:

- ETW failed to start or its native dependency is missing;
- no matching network samples were observed;
- Steam/game changed the packet pattern used by the older API path;
- the connection uses Steam Datagram Relay;
- the endpoint changed;
- the networking API does not expose a usable measurement.

Do not interpret `-1` as real latency or a quality value of `1` as proof of a perfect connection. These states are part of the active correctness roadmap: [upstream issue audit](docs/UPSTREAM_ISSUE_AUDIT.md).

### Overlay is not visible, cannot be dragged, or OBS cannot capture it

- Use windowed or borderless mode; exclusive fullscreen is unsupported.
- Confirm the overlay is enabled in that game's Config tab.
- Set **X Offset** and **Y Offset** directly if dragging does not work.
- Restore the game and move it to the expected monitor.
- With OBS, try Display Capture. The separate overlay may not be available to Window or Game Capture.
- HDR, mixed-DPI, high-refresh, and multi-monitor behaviour are being retained as explicit migration tests.

### Manual disconnect is unavailable

The manual action requires:

- administrator permission;
- a resolvable executable path for the selected game and a running `steam.exe`;
- successful atomic WFP filter creation;
- a registered hotkey while the game has focus.

Protected and anti-cheat game processes are resolved with Windows' limited-information image-path API rather than module enumeration. If the path is temporarily unavailable during attach, the manual action resolves it again before each eligible activation attempt.

No currently detected peer or exposed direct endpoint is required. If WFP cannot create the complete process-scoped lock, the app visibly reports the failure, leaves Steam sessions open, and retries without creating a partial policy. It makes two 100 ms recovery attempts before backing off to a 30-second retry interval so a persistent Windows service or permission failure cannot stall the UI or flood the log; pressing the hotkey again always requests an immediate retry.

### Hotkey did not register

Choose a key or combination not already owned by Steam, ShareX, screenshot software, overlays, accessibility tools, or another global-hotkey application. Restart SteamP2PInfo after changing it if the UI reports that registration failed.

### The app closes or errors while attaching

First verify that the IPC setting ends in `ipc_SteamClient.log`, not a folder. Also keep all release files together and use the correct x64 release asset.

Bad paths, missing ETW modules, dead game windows, and partial attach failures are recognized stability priorities. If the problem persists, open an issue with app/Windows/Steam/game versions, the exact stage, and a **redacted** error. Do not publish raw history or diagnostic logs.

### Steam console shows red clan or “too many calls” messages

Current detection periodically makes a deliberately invalid `SendClanChatMessage` call to encourage Steam to flush its IPC log. Steam can show this as red console output. It should not be treated as evidence that peer detection is healthy; if the log stops changing or peers remain blank, follow the staged checks above.

## Network and privacy safety

This fork is not observation-only:

- ETW observes Windows network events.
- Manual enforcement can install WFP filters and call Steam session-close APIs.
- Activity/diagnostic records can contain Steam IDs and endpoints.
- WPF v1.5-and-later history can store direct peer IP addresses locally.
- The app checks GitHub Releases for updates when it starts, which is a network request.

Practical safeguards:

- Leave automatic high-ping disconnect disabled; current versions ignore legacy attempts to enable it.
- Use the manual action only when you understand game timeout/penalty consequences and that it blocks all UDP for the attached game and `steam.exe` until that game reports leaving the current lobby, or until the tool/game exits.
- Close SteamP2PInfo if filtering appears to affect anything unexpected.
- Never share `history\`, activity logs, diagnostic logs, or screenshots containing endpoints without reviewing/redacting them.
- Treat Steam IDs and usernames as personal data in support reports even when profiles are public.
- Download binaries only from this repository's Releases and verify published signatures/hashes when they become available.

The next stable-release plan makes exact-flow scope/cleanup and endpoint-data minimization release gates: [application roadmap](docs/IMPROVEMENT_ROADMAP.md).

## FAQ

### Why does SteamP2PInfo require administrator permission?

The newer `SteamNetworkingMessages` API can provide detailed connection information, but the older `SteamNetworking` path does not. For that path, the app estimates ping by monitoring relevant network events through Event Tracing for Windows (ETW). Kernel networking events require elevation. The manual disconnect also needs permission to create temporary Windows Filtering Platform filters.

### Why is the Steam console and IPC command required?

The application needs to learn which Steam IDs the selected game authenticates. Competing for Steam callbacks from a second process is not reliable because the game can consume them. Current versions therefore read selected Steam IPC log calls:

```text
BeginAuthSession, EndAuthSession, LeaveLobby, SendClanChatMessage
```

`BeginAuthSession`/`EndAuthSession` identify peer authentication. A process-matched `LeaveLobby` ends stale lobby state and releases an active manual reconnect lock so the next lobby or player can connect. The dummy chat call encourages periodic log flushing.

The application does not need to read game memory or inject code into the game for this workflow.

### Which Steam games work?

Elden Ring is the primary target. Another game may work if it authenticates peers through the required Steam calls and uses a networking path supported by the old/new peer adapters. Some games do not call `BeginAuthSession`; those games can remain blank even though they use Steam.

A tested compatibility matrix is planned. Until then, treat non-Elden-Ring support as version-specific and experimental.

### How is connection quality calculated?

For `SteamNetworkingMessages`, the displayed value roughly corresponds to `1 - packet loss` using Steam's session data.

For the deprecated `SteamNetworking` path, the application derives a value from recent ping jitter:

```text
1 / (0.01 × jitter + 1)
```

where `jitter` is the standard deviation of the last ten valid ping values. Invalid/unavailable pings must not be interpreted as a valid quality measurement.

### Why does the program close when the game closes?

The tool initializes Steam using the game's App ID. Leaving it running can make Steam think that game is still active even after `SteamAPI_Shutdown`, so the current lifecycle closes the tool with the game.

### How do I find a detected player again?

Double-click the peer in SteamP2PInfo to open the Steam profile. If **Set played with** is enabled for that game, also check Steam's current recent-player surface. Steam changes its UI over time, and local detection does not guarantee Steam will populate that list.

### Does this fix Elden Ring lag or Steam network issues?

No. It helps observe peer identity, ping/quality availability, and some connection state. High latency can originate in local Wi-Fi, ISP routing, congestion, geographic distance, Steam relay/direct selection, the game, or another system. SteamP2PInfo is a monitor and diagnostic aid, not a network optimizer.

### How should I report a bug?

Open an issue in [steamp2pinfo-revised](https://github.com/wardriven/steamp2pinfo-revised/issues) and include:

- SteamP2PInfo version;
- Windows version and x64 architecture;
- Steam stable or beta;
- game, version, executable, App ID, and host/client role;
- direct/relay status if known;
- the failing stage and reproducible steps;
- a redacted error/support excerpt.

Remove IP addresses, endpoints, Steam IDs/usernames you do not intend to publish, account details, and personal file paths.

## Roadmap and engineering plans

- [Original GitHub issue audit](docs/UPSTREAM_ISSUE_AUDIT.md) — all 52 issues mapped to the revised application.
- [Application improvement roadmap](docs/IMPROVEMENT_ROADMAP.md) — safety, stability, testing, releases, and future updates.
- [WinUI 3 migration plan](docs/WINUI3_MIGRATION_PLAN.md) — feature-parity matrix and phased cutover.
- [Discoverability and website plan](docs/DISCOVERABILITY_PLAN.md) — GitHub metadata, search intent, content, measurement, and website decision.

The WinUI 3 folder is currently a prototype, not a feature-complete replacement. It lags behind the WPF working tree and omits connection history. WPF v1.6 remains the migration reference until every parity gate passes.

## Development and contributions

Before proposing a feature:

- check the [revised issue tracker](https://github.com/wardriven/steamp2pinfo-revised/issues);
- read the relevant safety, privacy, compatibility, and migration plan;
- keep parser/policy behaviour testable without Steam or a live game;
- keep any broad network control explicit, application-bound, temporary, and backed by a maintained test matrix;
- never commit real player endpoints or unredacted IPC/diagnostic fixtures.

For binaries, use Releases. Source archives are for building/development and do not contain a ready-to-run application.

## Origin, licence, and affiliation

This project is a fork of [tremwil/SteamP2PInfo](https://github.com/tremwil/SteamP2PInfo). It preserves the original goal of displaying Steam P2P connection information and adds revised history, diagnostics, and carefully scoped manual network-control work.

See [LICENSE](LICENSE) for licence terms.

Steam, Steamworks, and Valve are trademarks of Valve Corporation. Elden Ring is a trademark of its respective owners. This community project is not affiliated with, endorsed by, or sponsored by Valve, Bandai Namco, or FromSoftware.
