# P2P latency firewall-disconnect plan

## Goal and acceptance criteria

Add an opt-in enforcement mode that disconnects a detected Steam P2P peer when a valid measured ping is strictly greater than 100 ms. Direct connections should be isolated to the peer; relayed connections may disconnect other peers sharing the same relay path, but must leave unrelated Steam services working.

The feature is complete only when all of the following are demonstrated:

1. A ping of 100 ms remains connected; the first authoritative sample above 100 ms starts enforcement.
2. A direct P2P peer is blocked using rules scoped to the selected game executable, UDP, and the peer's exact remote IP and port.
3. Steam client services and non-P2P Steam traffic continue to work while enforcement is active. Other P2P peers may be disconnected when they share the same Steam Datagram Relay path.
4. The Steam logical session is closed after the firewall block is active, and automatic reconnection cannot bypass the block during the same auth/lobby session.
5. Rules are removed on `EndAuthSession`, `LeaveLobby`, detach/game exit, normal application exit, and on the next startup after an unclean exit.
6. A relayed connection may be blocked at its exact observed relay endpoint even when that endpoint is shared by other peers. The rule must remain scoped to the selected game executable and must not target `steam.exe`, a Valve address range, or a broad Steam port range.

## Current-state findings

- The application already requires administrator rights in `app.manifest`, which is sufficient to manage local Windows Firewall rules.
- `MainWindow.Timer_Tick` runs every second, but calls `SteamPeerManager.UpdatePeerList` only every sixth tick. That six-second refresh is too slow for a strict latency cutoff.
- `SteamPeerOldAPI` already reads the direct IPv4 address and UDP port from `P2PSessionState_t` and encodes them in `mNetIdentity`, but does not expose the endpoint.
- `SteamPeerNewAPI` stores `SteamNetConnectionInfo_t`, whose bundled Steamworks.NET type exposes `m_addrRemote`. Steam documents this address as zero when a concrete remote address is unknown or inapplicable, including most non-direct connections.
- The bundled Steamworks.NET assembly exposes both `SteamNetworking.CloseP2PSessionWithUser` and `SteamNetworkingMessages.CloseSessionWithUser`.
- `SteamPeerManager` currently removes peers in several places without consistently calling `Dispose`; enforcement cleanup must be centralized rather than added independently to each branch.
- The selected window provides a process ID, but only the process name is persisted. Firewall application rules require the selected game's full executable path.
- `UpdatePeerList` is `async void`; a separate serialized enforcement loop is needed so rule mutations cannot overlap.

## Proposed design

### 1. Configuration and user-visible state

Extend `GameConfig` with:

- `disconnect_high_ping_enabled`, default `false`.
- `disconnect_ping_threshold_ms`, default `100`, validated as a positive finite value.
- A read-only per-peer enforcement status for the grid/overlay: `Monitoring`, `Firewall blocked (direct)`, `Firewall blocked (shared relay)`, `Session closed (no firewall endpoint)`, or `Error`.

The comparison is `Ping > threshold`; `Ping == threshold` is allowed. Ignore unavailable, negative, NaN, or infinite samples. The strict mode acts on the first valid over-threshold sample. Any future debounce option must be separate and opt-in so it does not weaken this requirement.

### 2. Normalize the peer network endpoint

Add an immutable `PeerNetworkEndpoint` value containing address family, IP address, UDP port, and whether the endpoint is direct and firewall-safe.

Expose from `SteamPeerBase`:

- `TryGetRemoteEndpoint(out PeerNetworkEndpoint endpoint)`.
- `CloseSession()` using the API appropriate to the peer type.

Implementations:

- Old API: decode the existing `P2PSessionState_t.m_nRemoteIP` and `m_nRemotePort`; accept only nonzero IPv4 and port values.
- New API: read `mConnInfo.m_addrRemote`; use a nonzero direct IPv4/IPv6 endpoint when available. If it is zero because SDR is in use, classify the peer as relayed and obtain the game's currently active exact relay IP/UDP-port tuple from the process-scoped ETW flow tracker. A relay endpoint is firewall-usable under the relaxed policy, but is explicitly marked as shared rather than peer-unique.

Endpoint changes must be observable so a pending rule is removed and recreated atomically for the new endpoint.

### 3. Windows Firewall boundary

Create `IFirewallBlockService` and a production `WindowsFirewallBlockService`. Use the Windows Firewall COM API (`HNetCfg.FwPolicy2`/`HNetCfg.FWRule`) directly rather than shelling out to PowerShell.

For each blocked direct peer, create two explicit block rules (outbound and inbound) with all of these constraints:

- `ApplicationName`: canonical full path of the selected game executable.
- `Protocol`: UDP only.
- `RemoteAddresses`: one exact peer address.
- `RemotePorts`: one exact peer port.
- `Profiles`: all active profiles.
- `Action`: block; `Enabled`: true.
- Deterministic internal name and group containing a per-run session ID and Steam ID, for example `SteamP2PInfo:{session}:{steamId}:{direction}`.

Do not create an address-only rule, a port-only rule, a rule for `steam.exe`, a Valve subnet/range rule, or a broad Steam port-range rule. A rule for one exact relay IP and UDP port is permitted even if multiple P2P peers share it. Explicit Windows Firewall block rules override conflicting allow rules, so executable and tuple scoping remains mandatory.

The service must be idempotent, own only its named rule group, verify the created rule by reading it back, and return a structured result rather than swallowing COM errors.

### 4. Enforcement order and lifecycle

At attach time, resolve and canonicalize the selected process's `MainModule.FileName`, then start one serialized enforcement loop with a target cadence of 250 ms. The loop updates peer connection info and evaluates valid ping samples independently of the existing six-second IPC/log maintenance.

When `Ping > 100`:

1. Atomically mark the peer as `EnforcementPending` so duplicate ticks cannot act.
2. If a direct or exact observed relay endpoint exists, create and verify both block rules. A relay block may intentionally affect every peer sharing that relay tuple.
3. Call the peer's Steam close-session API only after the block is active.
4. Keep the rules in place for the rest of that auth/lobby session, preventing immediate automatic reconnection.
5. Log threshold, measured ping, Steam ID, endpoint classification, rule IDs, close result, and any error.

If SDR is detected but no exact relay endpoint can be attributed to the game process, call the Steam close-session API, mark `Closed without firewall endpoint`, and report that firewall enforcement could not be applied. Do not substitute a Valve address range, all UDP traffic, or all Steam ports.

Centralize peer removal in one method that disposes the peer and releases its rules. Call it for `EndAuthSession`, timeout, and errors; use it for every peer on `LeaveLobby`. On window close/detach, stop and await enforcement, remove all rules for the current run, then stop ETW. At application startup, remove stale rules in the SteamP2PInfo group from previous unclean exits before allowing attachment.

### 5. Failure policy

- Fail closed with respect to claims, not to all game networking: if either rule cannot be created or verified, do not create a broader substitute rule.
- Still attempt the Steam close-session call, surface `Error` or `Session closed (no firewall endpoint)`, and log the exact failure.
- If local firewall policy merge is disabled or a third-party firewall prevents rule creation, show a one-time actionable message and disable firewall enforcement for the run.
- Removal failures are retried during shutdown and again by stale-rule cleanup on next startup.

## Implementation sequence

1. Refactor peer removal/disposal and replace overlapping `async void` updates with serialized tasks.
2. Add endpoint and close-session abstractions to both peer implementations.
3. Capture the selected game's canonical executable path and add validated configuration.
4. Implement and unit-test the firewall rule service behind `IFirewallBlockService`.
5. Add the 250 ms enforcement coordinator, state transitions, logging, UI status, and lifecycle cleanup.
6. Add startup stale-rule recovery and user-facing diagnostics.
7. Run the automated and Windows integration verification matrix below.

## Verification plan

### Automated tests

- Threshold policy: `-1`, NaN, and infinity ignored; 99 and 100 allowed; 100.001 blocked.
- Idempotency: repeated high samples create one rule pair and call close once.
- Endpoint mutation: old rules are removed before rules for the new endpoint become active.
- Peer lifecycle: all removal paths dispose the peer and release rules.
- Rule projection: exact executable, UDP, exact remote IP/port, both directions, correct group/name.
- Failure paths: one-rule failure rolls back the other rule and never broadens scope.
- Relay path: an exact game-process relay tuple creates a rule pair marked as shared; no endpoint falls back to Steam close with explicit status; no broad rule is created.

### Elevated Windows integration harness

Use a small UDP sender/receiver fixture with two remote endpoints. Verify packet capture and firewall enumeration before, during, and after enforcement:

1. Traffic to endpoint A and B succeeds initially.
2. Blocking A stops A in both directions within one enforcement interval.
3. B remains functional for a direct-peer test. In the shared-relay test, other P2P flows on A may stop, but unrelated TCP/HTTPS traffic and traffic belonging to a separate Steam-service fixture remain functional.
4. Rule removal restores A.
5. Forced process termination leaves rules that the next startup cleanup removes.

### Manual Steam matrix

- Legacy `ISteamNetworking`, direct IPv4.
- `SteamNetworkingMessages`, direct endpoint.
- `SteamNetworkingMessages` through Steam Datagram Relay/no `m_addrRemote`.
- Two simultaneous peers, with only one above 100 ms.
- Endpoint migration while connected.
- `EndAuthSession`, `LeaveLobby`, game exit, normal tool exit, and forced tool termination.

Capture logs plus `Get-NetFirewallRule`/address-filter/port-filter output for each case. Direct mode must isolate one peer. Relay mode may disconnect all peers sharing the blocked tuple, but must demonstrate that the Steam client remains signed in and that friends/chat, overlay, store/library traffic, and a fresh matchmaking/control request still function. Relay mode must be documented as shared-path blocking, not peer-specific isolation.

## Feasibility boundary

Windows Firewall filters OS-visible application/IP/protocol/port traffic, not Steam IDs or logical Steam channels. Exact peer isolation is feasible for a direct connection with a unique endpoint. Under the relaxed requirement, an SDR connection can instead be blocked at the exact relay tuple observed for the selected game process; this may disconnect every peer sharing that path. The guarantee is that the scoped game/relay traffic is blocked, not that only one Steam ID is affected. Blocking remains unavailable if no exact process-owned relay tuple can be identified safely.

### Why Steam Datagram Relay changes the guarantee

For a direct connection, Windows and Steam describe the same useful network tuple:

```text
game.exe -> peer public IP:peer UDP port
```

The firewall can match that tuple together with the full path to `game.exe`. Blocking it is narrow enough to stop that peer while permitting another peer at a different endpoint and permitting unrelated game traffic.

With Steam Datagram Relay, the peer is a logical identity inside Steam's encrypted relay transport:

```text
game.exe -> Valve relay IP:relay UDP port -> Steam peer A
                                          -> Steam peer B
```

Windows Firewall sees the first hop, not the Steam identity behind it. Steam's `SteamNetConnectionInfo_t.m_addrRemote` is documented as all zeroes when a direct remote address is unknown or not applicable, which includes most connections other than direct UDP. Packet capture may reveal the relay IP and port, but that is not proof of a peer-unique endpoint: multiple peers or Steam control traffic can use the same relay path, and the path can migrate.

Creating a rule against one exact relay tuple can disconnect multiple peers, which is acceptable for this use case. It must still be scoped to the selected game's full executable path so Steam client processes and their services are outside the rule. The implementation must track relay migration and must never broaden the rule to `steam.exe`, a Valve network range, all UDP, or a generic Steam port range.

Executable scoping protects services hosted by other processes, but it cannot prove that the game itself never sends non-P2P Steam traffic to the same relay tuple. For that reason, the exact tuple must be derived from process-scoped network events, the rule must be reversible, and the manual release gate must verify Steam sign-in, friends/chat, overlay, and matchmaking/control behavior while the block is active.

### What session closure provides instead

`SteamNetworkingMessages.CloseSessionWithUser` still complements the shared relay block by targeting the offending logical Steam identity. It is also the only available fallback when no exact relay tuple is observable, but it is not equivalent to a persistent firewall block:

- It requests immediate closure and resource cleanup for the current logical session.
- New traffic from the remote user can cause another session request, and the game may accept or recreate the session.
- Because SteamP2PInfo is a companion process using the same App ID, testing must prove that its close call changes the attached game's effective session, not merely the companion process's Steam API context.

For a relayed high-ping peer, enforcement should therefore:

1. Mark the Steam ID as denied for the remainder of the current auth/lobby session.
2. Resolve the exact relay tuple from network events owned by the selected game process.
3. If resolved, create the program/UDP/exact-tuple firewall rule pair and record that the scope is shared across peers.
4. Call `CloseSessionWithUser`.
5. Poll `GetSessionConnectionInfo` for a bounded interval and confirm that the state leaves `Connecting`/`Connected`.
6. If the session or relay path reappears, update the exact tuple rules and issue a rate-limited close while the Steam ID remains denied.
7. Remove the deny state and all associated relay rules on `EndAuthSession` or `LeaveLobby`.

The UI and logs must distinguish evidence levels:

- `Firewall blocked (direct endpoint)`: both exact firewall rules were created and read back successfully, then the Steam close call ran.
- `Firewall blocked (shared relay)`: exact rules scoped to the game executable and observed relay tuple were verified; other peers on that path may also be disconnected.
- `Session closed (no firewall endpoint)`: the close call ran and a subsequent Steam state query confirmed closure, but no exact relay tuple was available.
- `Close requested but unconfirmed`: Steam did not expose a unique endpoint and the state transition could not be verified.
- `Isolation unavailable`: testing shows that closing from the companion process does not close the attached game's relayed session.

The direct state guarantees peer-specific firewall isolation. The shared-relay state guarantees only that traffic from the game executable to that exact relay tuple is blocked; it deliberately does not guarantee preservation of other P2P peers. The session-only state is a confirmed logical closure, while the final two are explicit failure/limitation states. Shipping relay enforcement is gated on real-game tests proving both the companion-process close behavior and continued operation of unrelated Steam services.
