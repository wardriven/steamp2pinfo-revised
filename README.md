# SteamP2PInfo /w PingGuard
Simple C# application displaying active Steam P2P connection info, namely SteamID / ping / connection quality. This was specifically made with Elden Ring in mind, but it should work for any Steam Networking game that authenticates peers using `ISteamUser::BeginAuthSession`. Also comes with a customizable overlay (**windowed / borderless mode only!**) and logging.

This is a fork from the original SteamP2PInfo, with the additional tweaks to allow players the choice of who they play with based of ping.
Please check known issues, this is very much a WIP with no strict timeline for releases.

**It also supports adding peers to the Steam recent players list, if the game does not support this.**
## [Releases](https://github.com/wardriven/steamp2pinfo-revised/releases)

SteamP2PInfo checks for updates when it opens and shows a link to GitHub Releases, so you never miss improvements, fixes, or new features.

![](https://raw.githubusercontent.com/tremwil/SteamP2PInfo/master/overlay_er.PNG)
<img width="800" height="450" alt="6855270e533e1fba49393b9484607fed" src="https://github.com/user-attachments/assets/2a679b45-e9ea-4107-9acf-9092a568184a" />
<img width="800" height="500" alt="a874f533002e86ec3df2dedead9b1eb8" src="https://github.com/user-attachments/assets/d5c7df61-8745-415e-bc94-041bc3722295" />

# How to Use
Download the latest release from the Releases tab and extract the ZIP file in any folder on your computer. Once the game is running, start `SteamP2PInfo.exe` and click on "Attach Game". Select the appropriate game window in the dialog. If this game has never been opened before, you will be prompted to enter the game's **Steam AppId**. This can be queried on websites like [steamdb](https://steamdb.info/). The Steam console will then open. **You must enter the following command in the console for the tool to work:**
```
log_ipc "BeginAuthSession,EndAuthSession,LeaveLobby,SendClanChatMessage"
```
The program should now be ready! You can then go in the "Config" tab to customize game-specific settings.

## Disconnecting high-ping players

### Manual hotkey disconnect (preferred)

For a manual, immediate disconnect, configure **Manual block hotkey** in the game's **Config** tab. Keep SteamP2PInfo attached to the game, bring the game to the foreground, and press the configured hotkey. The tool applies an exact-flow Windows Filtering Platform block and closes the Steam P2P session for the currently detected peer(s), preventing an immediate reconnection.

The hotkey is configured inside the application for each game. It is the preferred way to disconnect a high-ping player when you want to act immediately rather than wait for automatic enforcement.

### Automatic high-ping disconnect

SteamP2PInfo can automatically disconnect a peer whose measured ping exceeds a per-game limit. This feature is disabled by default. In the **Config** tab, enable **Automatically disconnect high-ping players** and set **High-ping limit (ms)**; the default limit is 100 ms.

For Elden Ring, **Allow Steam-owned exact-flow fallback** is enabled by default. Live testing showed that the active P2P transport can be owned by `steam.exe`, rather than `eldenring.exe`. The fallback can affect Steam traffic that shares the same exact UDP flow.

### How it works

1. Every 250 ms, SteamP2PInfo evaluates valid peer ping samples. The first sample strictly greater than the configured limit triggers enforcement; a ping equal to the limit is allowed. Missing, negative, `NaN`, and infinite values are ignored.
2. The tool obtains Steam's exact remote IP/UDP-port tuple and uses ETW to confirm which local UDP port and process are actually sending packets to that tuple.
3. It adds inbound and outbound Windows Filtering Platform (WFP) transport filters for that exact local-port/remote-IP/remote-port flow.
4. Only after WFP accepts the filters does the companion process call Steam's logical close-session API for the peer.
5. The filters remain active for the current tool/game lifetime, preventing an immediate reconnection. The peer is sampled again so a newly observed endpoint or local port can be added to the quarantine without issuing another close request. A confirmed new `BeginAuthSession` for that Steam ID clears its old filters, allowing the peer to be evaluated afresh.

WFP transport filtering applies to packets in an already-active UDP flow; creating an ordinary Windows Firewall rule alone did not reliably stop this game's effective P2P path during testing. WFP filters are created in a dynamic session and are removed automatically if SteamP2PInfo exits unexpectedly. They are also removed when the feature is disabled or the tool/game closes.

### Flow ownership and safety boundaries

The normal path requires the observed UDP port to be owned exclusively by the selected game process. If ETW instead shows that `steam.exe` owns the exact flow, the optional Steam-owned fallback may use it only when all of the following are true:

- The flow is an ETW-observed local UDP port to the peer's exact remote IP and port.
- The observed process is `steam.exe`.
- No other process owns that local port in the same address family at the time of enforcement.

The fallback still does **not** block all Steam networking, a Valve address range, or a Steam port range. It blocks one exact network tuple. Windows can see IP addresses and ports, not Steam IDs, so any traffic sharing that tuple can be affected.

An exact endpoint is required. If Steam exposes no usable remote IP/port—common with Steam Datagram Relay—the tool records an enforcement error and does not broaden the block. It also refuses a port that is shared with another process.

### Notifications and logs

Enforcement results are always written to the per-game log, including the peer Steam ID, measured ping, endpoint, selected flow type, and errors. **Mute high-ping enforcement error notifications** suppresses only modal pop-ups; it does not suppress log entries.

# Known Issues
### Peers not getting detected in rare circumstances (versions < 1.2.0)
This is due to the very naive Steam IPC log file parsing. The program can "miss" a Steam lobby, preventing the detection of P2P peers in this lobby. I plan to improve the log file parsing to make this rarer or completely eliminate it in the future.

### Disconnecting low ping players
Known bug, turn off automatical and switch to hot key.

### Hotkey did not register
Ensure that key isn't used by another program (eg: ShareX, Steam screenshot etc).

### Overlay cannot be dragged around
I'm not sure what the cause for this is yet. Please modify the "X Offset" and "Y Offset" settings directly for now.

### Cannot customize ping color thresholds
Not really an issue, but I plan to implement a GUI editor for this in the future. For now the colors can be changed by directly editing the game's json configuration file. 

# FAQ
### Why does it require administrator privileges?
While the `SteamNetworkingMessages` API provides detailed connection information, the old API `SteamNetworking` does not do this. Hence the pings are computed by monitoring STUN packets that are sent to and received from the players' IPs. To capture these packets I use Event Tracing for Windows (ETW), which requires administrator privileges for "kernel" events like networking. Administrator privileges are also required to create the temporary Windows Filtering Platform filters used by the optional high-ping disconnect feature.

### Why do I have to use the Steam console / IPC logging? Isn't there a cleaner way to monitor lobbies?

Sadly, this is the only way I found to reliably detect lobby joining and creation when running two processes using the same game ID. I cannot use Steam callbacks, because if the game "consumes" them my tool's callbacks will not be called, and vice versa. I also do not want to rely on reading game memory or injecting code into the game in order to support anti-cheat protected games. In the future, I plan to move from IPC log file parsing to an internal `steam.exe` hook to make peer detection 100% reliable. Since some VAC games might not like this, an option will be available to use the legacy system if needed. This new method will take quite a bit of work to implement, however.

*(1.1.0 and above)* [zkxs](https://github.com/zkxs) had the smart idea to log `ISteamUser::BeginAuthSession` calls instead of lobby joining. Since Steam Networking P2P games must use this method to authenticate peers. This is a much more robust choice than the old logic which was using `IClientMatchmaking` `CreateLobby` and `JoinLobby`. In 1.2.0, I combine this with `IClientMatchmaking::LeaveLobby` to eliminate improperly terminated connections and additionally log dummy calls to `IClientFriends::SendClanChatMessage` to periodically force Steam to flush the IPC log. This now makes the tool very reliable.

### How is the "Connection Quality" computed?
When a peer is connected using the `SteamNetworkingMessages` API, this roughly corresponds to `1 - packet_loss`. When connected using the deprecated API `SteamNetworking`, the value is computed using the formula `1 / (0.01 * jitter + 1)`, where `jitter` is the standard deviation of the last 10 ping values. This is done instead of showing `jitter` directly so that the value is on the same scale as the `SteamNetworkingMessages` one.

### Why does the program close with the game?
Since the tool is loaded with the game's Steam AppId, letting the program run after the game closes would make Steam think the game is still running. Calling `SteamAPI_Shutdown` does not seem to fix the problem, so we have to close the process.

### I found a bug / I have something to say about the tool
Feel free to open an issue on this GitHub repo.

