using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

using SteamP2PInfo.Config;

namespace SteamP2PInfo
{
    internal sealed class P2PEnforcementCoordinator : IDisposable
    {
        internal static readonly TimeSpan EnforcementInterval = TimeSpan.FromMilliseconds(100);
        internal static readonly TimeSpan ManualReconnectFailureBackoff = TimeSpan.FromSeconds(30);
        private const int ManualReconnectFastRetryFailures = 2;

        private readonly DispatcherTimer timer;
        private readonly int gameProcessId;
        private readonly Func<IEnumerable<SteamPeerBase>> peerProvider;
        private readonly Func<IFirewallBlockService> firewallFactory;
        private readonly Func<DateTime> utcNow;
        private IFirewallBlockService firewall;
        private readonly PeerQuarantineLifecycle quarantinedPeers = new PeerQuarantineLifecycle();
        private readonly HashSet<ulong> returningPeers = new HashSet<ulong>();
        private readonly Dictionary<ulong, ManualBlockTarget> manualBlockTargets = new Dictionary<ulong, ManualBlockTarget>();
        private bool manualBlockScanActive;
        private bool manualReconnectGuardActive;
        private bool manualReconnectReleasePending;
        private bool manualReconnectGuardFailureLogged;
        private bool manualReconnectReleaseFailureLogged;
        private int manualReconnectGuardFailureCount;
        private DateTime nextManualReconnectGuardAttemptUtc = DateTime.MinValue;
        private bool evaluating;
        private bool disposed;
        private bool firewallErrorShown;
        private bool firewallErrorNotificationQueued;
        private string lastFirewallInitializationError;

        internal bool IsManualBlockScanActive => manualBlockScanActive;
        internal bool IsManualReconnectGuardActive => manualReconnectGuardActive;
        internal bool IsManualReconnectReleasePending => manualReconnectReleasePending;
        internal bool IsFirewallErrorNotificationQueued => firewallErrorNotificationQueued;

        public P2PEnforcementCoordinator(int gameProcessId)
            : this(
                gameProcessId,
                () => SteamPeerManager.GetPeers(),
                () => new WfpFlowBlockService(gameProcessId))
        {
        }

        internal P2PEnforcementCoordinator(
            int gameProcessId,
            Func<IEnumerable<SteamPeerBase>> peerProvider,
            Func<IFirewallBlockService> firewallFactory,
            Func<DateTime> utcNow = null)
        {
            this.gameProcessId = gameProcessId;
            this.peerProvider = peerProvider ?? throw new ArgumentNullException(nameof(peerProvider));
            this.firewallFactory = firewallFactory ?? throw new ArgumentNullException(nameof(firewallFactory));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            timer = new DispatcherTimer(DispatcherPriority.Send)
            {
                Interval = EnforcementInterval
            };
            timer.Tick += Timer_Tick;
            SteamPeerManager.PeerBeginAuthSession += SteamPeerManager_PeerBeginAuthSession;
            SteamPeerManager.PeerRemoved += SteamPeerManager_PeerRemoved;
            SteamPeerManager.LobbyLeft += SteamPeerManager_LobbyLeft;
        }

        public void Start()
        {
            if (!disposed)
            {
                // Open the dynamic WFP session while attaching rather than inside
                // the low-level keyboard hook. The hotkey then only has to publish
                // the already-prepared policy transaction.
                EnsureFirewall("Manual Peer Block Error", false, false);
                timer.Start();
                DiagnosticLogger.Write("ACTION", "P2P enforcement timer started for game PID " + gameProcessId + ".");
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            DiagnosticLogger.Write("ACTION", "P2P enforcement coordinator is being disposed.");
            timer.Stop();
            timer.Tick -= Timer_Tick;
            SteamPeerManager.PeerBeginAuthSession -= SteamPeerManager_PeerBeginAuthSession;
            SteamPeerManager.PeerRemoved -= SteamPeerManager_PeerRemoved;
            SteamPeerManager.LobbyLeft -= SteamPeerManager_LobbyLeft;
            firewall?.RemoveAll();
            firewall?.Dispose();
            firewall = null;
            quarantinedPeers.Clear();
            returningPeers.Clear();
            manualBlockTargets.Clear();
            manualBlockScanActive = false;
            manualReconnectGuardActive = false;
            manualReconnectReleasePending = false;
            manualReconnectGuardFailureLogged = false;
            manualReconnectReleaseFailureLogged = false;
            manualReconnectGuardFailureCount = 0;
            nextManualReconnectGuardAttemptUtc = DateTime.MinValue;
            firewallErrorNotificationQueued = false;
        }

        internal static bool ShouldDisconnect(double ping, double thresholdMs)
        {
            return !double.IsNaN(ping)
                && !double.IsInfinity(ping)
                && ping >= 0d
                && !double.IsNaN(thresholdMs)
                && !double.IsInfinity(thresholdMs)
                && thresholdMs > 0d
                && ping > thresholdMs;
        }

        /// <summary>
        /// Atomically enables a process-scoped UDP reconnect lock for the attached
        /// game and steam.exe, then closes every logical peer session. The lock
        /// remains active through same-lobby reconnect attempts and is released
        /// when the selected game reports leaving that lobby.
        /// </summary>
        internal void BlockAllConnectedPeers()
        {
            if (disposed)
                return;

            if (manualReconnectReleasePending)
            {
                // A deliberate keypress may immediately retry a failed lobby-exit
                // cleanup, but a partially released policy must be removed before
                // a fresh atomic reconnect guard can be armed.
                nextManualReconnectGuardAttemptUtc = DateTime.MinValue;
                if (!TryReleaseManualReconnectGuard())
                {
                    Logger.WriteEnforcementLine(
                        "[MANUAL BLOCK] A new request was received while lobby-exit cleanup is still pending; the existing UDP lock will be removed before a fresh lock is armed.");
                    return;
                }
            }

            SteamPeerBase[] peers = peerProvider().Where(peer => peer != null).ToArray();
            foreach (SteamPeerBase peer in peers)
                TrackManualBlockTarget(peer);

            manualBlockScanActive = true;
            manualReconnectReleasePending = false;
            manualReconnectReleaseFailureLogged = false;
            // A deliberate new keypress bypasses any failure backoff so the user
            // can retry immediately after correcting permissions or service state.
            nextManualReconnectGuardAttemptUtc = DateTime.MinValue;
            DiagnosticLogger.Write(
                "ACTION",
                string.Format(
                    "Manual block-all-peers hotkey activated for {0} visible peer(s); the game-and-Steam UDP reconnect lock will remain active until the current lobby is left or the game/tool exits.",
                    peers.Length));

            // This call publishes the blocking policy synchronously, before the
            // keyboard hook returns. Steam CloseSession calls are queued so slow
            // Steam callbacks and logging cannot hold the global keyboard hook.
            if (!EnsureManualReconnectGuard())
                return;

            if (timer.IsEnabled)
                timer.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(RetryPendingManualBlocks));
            else
                RetryPendingManualBlocks();
        }

        internal void RetryPendingManualBlocks()
        {
            if (evaluating || disposed || !manualBlockScanActive)
                return;

            evaluating = true;
            try
            {
                ProcessManualBlockScan();
            }
            finally
            {
                evaluating = false;
            }
        }

        private void ProcessManualBlockScan()
        {
            if (!manualBlockScanActive)
                return;
            if (manualReconnectReleasePending)
            {
                TryReleaseManualReconnectGuard();
                return;
            }
            if (!EnsureManualReconnectGuard())
                return;

            SteamPeerBase[] currentPeers = peerProvider()
                .Where(peer => peer != null)
                .ToArray();
            var currentPeersById = currentPeers
                .GroupBy(peer => peer.SteamID.m_SteamID)
                .ToDictionary(group => group.Key, group => group.First());

            // Once the kill switch is armed it covers every later peer as well as
            // the peers visible on the original keypress.
            foreach (SteamPeerBase peer in currentPeers)
                TrackManualBlockTarget(peer);

            foreach (ManualBlockTarget target in manualBlockTargets.Values)
                target.AttachPeer(currentPeersById.TryGetValue(target.SteamId, out SteamPeerBase peer) ? peer : null);

            foreach (ManualBlockTarget target in manualBlockTargets.Values.ToArray())
            {
                if (target.Peer == null)
                    continue;

                try
                {
                    BlockPeerManually(target);
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.WriteException("ENFORCEMENT ERROR", ex, "Manual block failed for peer " + target.SteamId + ".");
                    Logger.WriteEnforcementLine($"[MANUAL BLOCK ERROR] Failed to process peer {target.SteamId}: {ex.Message}");
                }

                if (!manualBlockScanActive)
                    return;
            }
        }

        private void TrackManualBlockTarget(SteamPeerBase peer)
        {
            ulong steamId = peer.SteamID.m_SteamID;
            if (!manualBlockTargets.TryGetValue(steamId, out ManualBlockTarget target))
            {
                target = new ManualBlockTarget(steamId);
                manualBlockTargets.Add(steamId, target);
            }

            target.AttachPeer(peer);
        }

        private ManualBlockTarget TrackManualBlockTarget(ulong steamId)
        {
            if (!manualBlockTargets.TryGetValue(steamId, out ManualBlockTarget target))
            {
                target = new ManualBlockTarget(steamId);
                manualBlockTargets.Add(steamId, target);
            }

            return target;
        }

        private bool EnsureManualReconnectGuard()
        {
            if (manualReconnectGuardActive)
                return true;
            DateTime attemptUtc = utcNow();
            if (attemptUtc < nextManualReconnectGuardAttemptUtc)
                return false;
            if (!EnsureFirewall("Manual Peer Block Error", false, false))
            {
                ScheduleManualReconnectGuardRetry(attemptUtc);
                ReportManualReconnectGuardFailure(
                    string.IsNullOrWhiteSpace(lastFirewallInitializationError)
                        ? "Windows Filtering Platform could not be initialized."
                        : lastFirewallInitializationError);
                return false;
            }

            FirewallBlockResult result;
            try
            {
                result = firewall.BlockManualReconnect();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException(
                    "FIREWALL ERROR",
                    ex,
                    "Failed to activate the manual UDP reconnect lock.");
                ScheduleManualReconnectGuardRetry(attemptUtc);
                ReportManualReconnectGuardFailure(ex.Message);
                return false;
            }

            if (!result.Success)
            {
                ScheduleManualReconnectGuardRetry(attemptUtc);
                ReportManualReconnectGuardFailure(result.Error);
                return false;
            }

            manualReconnectGuardActive = true;
            manualReconnectGuardFailureLogged = false;
            manualReconnectGuardFailureCount = 0;
            nextManualReconnectGuardAttemptUtc = DateTime.MinValue;
            Logger.WriteEnforcementLine(
                "[MANUAL BLOCK] Application-scoped UDP reconnect lock is active for the attached game and steam.exe; it will be removed when the current lobby is left or the game/tool exits.");
            return true;
        }

        private void ReportManualReconnectGuardFailure(string error)
        {
            if (manualReconnectGuardFailureLogged)
                return;

            manualReconnectGuardFailureLogged = true;
            string message =
                "Failed to activate the game-and-Steam UDP reconnect lock: " + error +
                " No Steam session was closed. The manual request remains armed; the WFP lock is not active and will retry.";
            ReportFirewallError(message, "Manual Peer Block Error", false, false);
            QueueFirewallErrorNotification(message, "Manual Peer Block Error", false);
        }

        private bool TryReleaseManualReconnectGuard()
        {
            if (!manualReconnectReleasePending)
                return true;

            if (!manualReconnectGuardActive)
            {
                CompleteManualReconnectRelease(false);
                return true;
            }

            DateTime attemptUtc = utcNow();
            if (attemptUtc < nextManualReconnectGuardAttemptUtc)
                return false;

            FirewallBlockResult result;
            try
            {
                result = firewall == null
                    ? FirewallBlockResult.Failed("The WFP service is not available.")
                    : firewall.RemoveManualReconnect();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException(
                    "FIREWALL ERROR",
                    ex,
                    "Failed to deactivate the manual UDP reconnect lock.");
                ScheduleManualReconnectGuardRetry(attemptUtc);
                ReportManualReconnectReleaseFailure(ex.Message);
                return false;
            }

            if (!result.Success)
            {
                ScheduleManualReconnectGuardRetry(attemptUtc);
                ReportManualReconnectReleaseFailure(result.Error);
                return false;
            }

            manualReconnectGuardActive = false;
            CompleteManualReconnectRelease(true);
            return true;
        }

        private void CompleteManualReconnectRelease(bool removedActiveGuard)
        {
            int releasedPeers = quarantinedPeers.ReleaseOwner(
                PeerQuarantineOwner.ManualHotkey,
                peerId => firewall?.Remove(peerId));

            manualBlockTargets.Clear();
            manualBlockScanActive = false;
            manualReconnectGuardActive = false;
            manualReconnectReleasePending = false;
            manualReconnectGuardFailureLogged = false;
            manualReconnectReleaseFailureLogged = false;
            manualReconnectGuardFailureCount = 0;
            nextManualReconnectGuardAttemptUtc = DateTime.MinValue;

            string action = removedActiveGuard
                ? "the game-and-Steam UDP reconnect lock was removed"
                : "the armed manual request was cancelled before a reconnect lock became active";
            Logger.WriteEnforcementLine(
                $"[MANUAL BLOCK] Game LeaveLobby confirmed; {action}. Future lobby peer connections are allowed. Released manual ownership for {releasedPeers} peer(s).");
        }

        private void ReportManualReconnectReleaseFailure(string error)
        {
            if (manualReconnectReleaseFailureLogged)
                return;

            manualReconnectReleaseFailureLogged = true;
            string message =
                "Failed to deactivate the game-and-Steam UDP reconnect lock after leaving the lobby: " + error +
                " The lock is still treated as active and removal will retry; new lobby connections may remain blocked until cleanup succeeds.";
            ReportFirewallError(message, "Manual Peer Block Error", false, false);
            QueueFirewallErrorNotification(message, "Manual Peer Block Error", false);
        }

        private void ScheduleManualReconnectGuardRetry(DateTime failedAttemptUtc)
        {
            manualReconnectGuardFailureCount++;
            TimeSpan delay = manualReconnectGuardFailureCount <= ManualReconnectFastRetryFailures
                ? EnforcementInterval
                : ManualReconnectFailureBackoff;
            nextManualReconnectGuardAttemptUtc = failedAttemptUtc.Add(delay);
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (evaluating || disposed)
                return;

            evaluating = true;
            try
            {
                ProcessManualBlockScan();

                if (GameConfig.Current == null)
                {
                    ClearAllQuarantines();
                    return;
                }

                if (!GameConfig.Current.DisconnectHighPingEnabled)
                {
                    ClearAutomaticQuarantines();
                    return;
                }

                foreach (SteamPeerBase peer in peerProvider().Where(peer => peer != null).ToArray())
                    Evaluate(peer);
            }
            finally
            {
                evaluating = false;
            }
        }

        private void Evaluate(SteamPeerBase peer)
        {
            ulong steamId = peer.SteamID.m_SteamID;
            bool isQuarantined = quarantinedPeers.Contains(steamId);

            if (!TryRefreshPeer(peer, "high-ping enforcement", out PeerNetworkEndpoint endpoint))
                return;

            if (isQuarantined)
            {
                // WFP filters are exact tuples. Keep watching an enforced peer so a
                // reconnect or relay migration cannot escape the active quarantine.
                if (endpoint != null)
                {
                    if (!EnsureFirewall("High-Ping Enforcement Error", true))
                        return;

                    try
                    {
                        FirewallBlockResult refreshResult = firewall.Block(steamId, endpoint);
                        if (!refreshResult.Success)
                            ReportFirewallError($"Failed to extend the WFP flow quarantine for {steamId}: {refreshResult.Error}", "High-Ping Enforcement Error", true);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.WriteException("FIREWALL ERROR", ex, "Failed to extend the WFP quarantine for peer " + steamId + ".");
                        ReportFirewallError($"Failed to extend the WFP flow quarantine for {steamId}: {ex.Message}", "High-Ping Enforcement Error", true);
                    }
                }
                return;
            }

            double ping = peer.Ping;
            double thresholdMs = GameConfig.Current.DisconnectPingThresholdMs;
            if (!ShouldDisconnect(ping, thresholdMs))
            {
                if (returningPeers.Remove(steamId))
                {
                    Logger.WriteEnforcementLine(
                        $"[HIGH PING QUARANTINE] Returning peer {steamId} re-evaluated at {ping:F1} ms (limit {thresholdMs:F1} ms); no WFP block was applied.");
                }
                return;
            }

            if (endpoint == null)
            {
                ReportFirewallError($"Cannot enforce the high-ping disconnect for {steamId}: Steam did not expose an exact remote UDP endpoint. No broad UDP block was applied.", "High-Ping Enforcement Error", true);
                return;
            }

            if (!BlockAndClose(peer, endpoint, PeerQuarantineOwner.AutomaticHighPing, "High-Ping Enforcement Error", true, out FirewallBlockResult blockResult, out bool sessionClosed))
                return;

            bool wasReturningPeer = returningPeers.Remove(steamId);
            string scope = string.IsNullOrWhiteSpace(blockResult.Details) ? "exact UDP flow" : blockResult.Details;
            string peerDescription = wasReturningPeer ? "Returning peer" : "Peer";
            Logger.WriteEnforcementLine($"[HIGH PING DISCONNECT] {peerDescription} {steamId} measured {ping:F1} ms (limit {thresholdMs:F1} ms); WFP blocked the {scope} to {endpoint}; Steam close result: {sessionClosed}");
        }

        private bool BlockPeerManually(ManualBlockTarget target)
        {
            SteamPeerBase peer = target.Peer;
            ulong steamId = target.SteamId;
            if (!manualReconnectGuardActive || peer == null)
                return false;

            quarantinedPeers.Retain(steamId, PeerQuarantineOwner.ManualHotkey);
            if (target.CloseConfirmedForCurrentPeer)
                return true;

            bool sessionClosed = false;
            try
            {
                sessionClosed = peer.CloseSession();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("ENFORCEMENT ERROR", ex, "WFP blocked peer " + steamId + ", but Steam could not close the session.");
                Logger.WriteEnforcementLine($"[ENFORCEMENT ERROR] WFP blocked {steamId}, but Steam could not close the session: {ex.Message}");
            }

            if (sessionClosed)
                target.MarkCloseConfirmed();
            else
                DiagnosticLogger.Write("ENFORCEMENT", "Steam did not confirm closing peer " + steamId + "; sustained scanning will retry this session.");

            Logger.WriteEnforcementLine(
                $"[MANUAL BLOCK] Peer {steamId}; persistent game-and-Steam UDP reconnect lock active; Steam close result: {sessionClosed}");
            return true;
        }

        private bool TryRefreshPeer(SteamPeerBase peer, string action, out PeerNetworkEndpoint endpoint)
        {
            endpoint = null;
            ulong steamId = peer.SteamID.m_SteamID;
            try
            {
                if (!peer.UpdatePeerInfo())
                    return false;

                if (peer.TryGetRemoteEndpoint(out endpoint))
                    ETWPingMonitor.WatchUdpEndpoint(endpoint);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("ENFORCEMENT ERROR", ex, "Failed to refresh peer " + steamId + " during " + action + ".");
                Logger.WriteEnforcementLine($"[ENFORCEMENT ERROR] Failed to refresh {steamId} during {action}: {ex.Message}");
                endpoint = null;
                return false;
            }
        }

        private bool BlockAndClose(
            SteamPeerBase peer,
            PeerNetworkEndpoint endpoint,
            PeerQuarantineOwner owner,
            string errorTitle,
            bool respectHighPingMute,
            out FirewallBlockResult blockResult,
            out bool sessionClosed)
        {
            ulong steamId = peer.SteamID.m_SteamID;
            blockResult = null;
            sessionClosed = false;

            bool showErrorNotification = owner != PeerQuarantineOwner.ManualHotkey;
            if (!EnsureFirewall(errorTitle, respectHighPingMute, showErrorNotification))
                return false;

            try
            {
                blockResult = firewall.Block(steamId, endpoint);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("FIREWALL ERROR", ex, "Failed to apply the WFP quarantine for peer " + steamId + ".");
                ReportFirewallError($"Failed to apply the WFP flow quarantine for {steamId}: {ex.Message}", errorTitle, respectHighPingMute, showErrorNotification);
                return false;
            }

            if (!blockResult.Success)
            {
                ReportFirewallError($"Failed to apply the WFP flow quarantine for {steamId}: {blockResult.Error}", errorTitle, respectHighPingMute, showErrorNotification);
                return false;
            }

            // Mark the peer before CloseSession. Steam can synchronously emit an
            // EndAuthSession callback from the companion process; that callback
            // must retain this exact-flow block instead of clearing it.
            quarantinedPeers.Retain(steamId, owner);

            // The exact UDP flow is blocked at WFP transport layers before the
            // companion process asks Steam to release its logical session.
            try
            {
                sessionClosed = peer.CloseSession();
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("ENFORCEMENT ERROR", ex, "WFP blocked peer " + steamId + ", but Steam could not close the session.");
                Logger.WriteEnforcementLine($"[ENFORCEMENT ERROR] WFP blocked {steamId}, but Steam could not close the session: {ex.Message}");
                return true;
            }
        }

        private void ClearAutomaticQuarantines()
        {
            int released = quarantinedPeers.ReleaseOwner(PeerQuarantineOwner.AutomaticHighPing, peerId => firewall?.Remove(peerId));
            if (released > 0)
            {
                Logger.WriteEnforcementLine(
                    $"[HIGH PING QUARANTINE] Cleared automatic ownership for {released} peer(s); manually requested quarantines remain active.");
            }

            returningPeers.Clear();
        }

        private void ClearAllQuarantines()
        {
            if (manualBlockScanActive)
            {
                // A transient configuration teardown must not open a reconnect
                // window. LeaveLobby or disposal is the cleanup boundary.
                ClearAutomaticQuarantines();
                return;
            }

            if (quarantinedPeers.Count > 0 || returningPeers.Count > 0)
            {
                firewall?.RemoveAll();
                quarantinedPeers.Clear();
                returningPeers.Clear();
            }
        }

        internal void SteamPeerManager_PeerBeginAuthSession(ulong steamId)
        {
            if (manualReconnectReleasePending)
            {
                TryReleaseManualReconnectGuard();
                if (manualReconnectReleasePending)
                {
                    Logger.WriteEnforcementLine(
                        $"[MANUAL BLOCK] Steam reported BeginAuthSession for {steamId} while lobby-exit cleanup is retrying; no new close request will be issued, but the UDP lock may still prevent the connection until removal succeeds.");
                    return;
                }
            }

            if (manualBlockScanActive)
            {
                ManualBlockTarget target = TrackManualBlockTarget(steamId);
                target.BeginReplacementSession();
                quarantinedPeers.Retain(steamId, PeerQuarantineOwner.ManualHotkey);
                string guardStatus = manualReconnectGuardActive
                    ? "the persistent UDP lock remains active and the logical session will be closed as soon as its peer entry is available."
                    : "the manual request remains armed, but the WFP reconnect lock is not active; no session will be closed unless activation succeeds.";
                Logger.WriteEnforcementLine(
                    $"[MANUAL BLOCK] Steam reported a replacement auth session for {steamId}; {guardStatus}");

                if (timer.IsEnabled)
                    timer.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(RetryPendingManualBlocks));
                return;
            }

            if (!quarantinedPeers.Contains(steamId))
                return;

            Logger.WriteEnforcementLine(
                $"[P2P QUARANTINE] Confirmed new BeginAuthSession for returning peer {steamId}; clearing that peer's retained WFP filters before creating the new peer entry.");

            if (!quarantinedPeers.ClearForConfirmedBeginAuthSession(steamId, peerId => firewall?.Remove(peerId)))
                return;

            returningPeers.Add(steamId);
            Logger.WriteEnforcementLine(
                $"[P2P QUARANTINE] Cleared retained WFP filters for {steamId}; the returning session will be re-evaluated under the current ping limit.");
        }

        internal void SteamPeerManager_PeerRemoved(ulong steamId, PeerRemovalReason reason)
        {
            if (manualBlockTargets.TryGetValue(steamId, out ManualBlockTarget target))
                target.AttachPeer(null);

            // CloseSession runs in this companion process. Its transport/auth callbacks do
            // not prove that the attached game's independent Steam session has ended; in
            // practice the game can remain connected or recreate the session immediately.
            // Keep enforced rules for the lobby lifetime so the quarantine cannot disappear
            // as a side effect of our own close call.
            if (quarantinedPeers.ShouldRetainAfterPeerRemoval(steamId))
            {
                string retention;
                if (manualReconnectReleasePending)
                {
                    retention =
                        $"Retaining WFP protection state for {steamId} after {reason}; removal of the application-scoped UDP lock is pending and will retry.";
                }
                else if (manualBlockScanActive && manualReconnectGuardActive)
                {
                    retention =
                        $"Retaining WFP protection for {steamId} after {reason}; the persistent application-scoped UDP lock remains active and will cover any replacement peer session.";
                }
                else if (manualBlockScanActive)
                {
                    retention =
                        $"Retaining the manual block request for {steamId} after {reason}; the WFP reconnect lock is not active and no replacement session will be closed unless activation succeeds.";
                }
                else
                {
                    retention =
                        $"Retaining WFP filters for {steamId} after {reason}; waiting for a confirmed new BeginAuthSession before clearing this peer's quarantine.";
                }

                Logger.WriteEnforcementLine(
                    $"[P2P QUARANTINE] {retention} Companion IPC removal does not prove the game's connection ended.");
                return;
            }

            firewall?.Remove(steamId);
            returningPeers.Remove(steamId);
        }

        internal void SteamPeerManager_LobbyLeft()
        {
            if (manualBlockScanActive)
            {
                if (!manualReconnectReleasePending)
                {
                    manualReconnectReleasePending = true;
                    nextManualReconnectGuardAttemptUtc = DateTime.MinValue;
                    Logger.WriteEnforcementLine(
                        "[MANUAL BLOCK] Game LeaveLobby observed; deactivating the game-and-Steam UDP reconnect lock before allowing the next lobby.");
                }

                TryReleaseManualReconnectGuard();
                return;
            }

            if (quarantinedPeers.Count > 0)
            {
                Logger.WriteEnforcementLine(
                    $"[P2P QUARANTINE] Game LeaveLobby observed for {quarantinedPeers.Count} enforced peer(s); retained rules remain until the game/tool exits, or a later BeginAuthSession clears a peer after sustained manual scanning has finished.");
            }
        }

        private sealed class ManualBlockTarget
        {
            public ulong SteamId { get; }
            public SteamPeerBase Peer { get; private set; }
            public bool CloseConfirmedForCurrentPeer { get; private set; }
            private SteamPeerBase lastPeerInstance;

            public ManualBlockTarget(ulong steamId)
            {
                SteamId = steamId;
            }

            public void AttachPeer(SteamPeerBase peer)
            {
                if (peer == null)
                {
                    Peer = null;
                    return;
                }

                // SteamPeerManager normally emits BeginAuthSession before replacing
                // its peer object, but IPC log callbacks can be delayed or missed.
                // A new object is therefore also treated as a new logical session,
                // even when Steam reuses the same remote endpoint.
                if (lastPeerInstance != null && !ReferenceEquals(lastPeerInstance, peer))
                    CloseConfirmedForCurrentPeer = false;

                Peer = peer;
                lastPeerInstance = peer;
            }

            public void MarkCloseConfirmed()
            {
                CloseConfirmedForCurrentPeer = true;
            }

            public void BeginReplacementSession()
            {
                Peer = null;
                lastPeerInstance = null;
                CloseConfirmedForCurrentPeer = false;
            }
        }

        private void ReportFirewallError(string message, string title, bool respectHighPingMute, bool showNotification = true)
        {
            Logger.WriteEnforcementLine($"[ENFORCEMENT ERROR] {message}");
            if (!showNotification || firewallErrorShown || (respectHighPingMute && GameConfig.Current?.MuteHighPingEnforcementErrorNotifications == true))
                return;

            firewallErrorShown = true;
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void QueueFirewallErrorNotification(string message, string title, bool respectHighPingMute)
        {
            if (disposed ||
                firewallErrorShown ||
                firewallErrorNotificationQueued ||
                (respectHighPingMute && GameConfig.Current?.MuteHighPingEnforcementErrorNotifications == true))
                return;

            firewallErrorNotificationQueued = true;
            try
            {
                timer.Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(() =>
                    {
                        firewallErrorNotificationQueued = false;
                        if (disposed ||
                            firewallErrorShown ||
                            (respectHighPingMute && GameConfig.Current?.MuteHighPingEnforcementErrorNotifications == true))
                            return;

                        firewallErrorShown = true;
                        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
                    }));
            }
            catch (Exception ex)
            {
                firewallErrorNotificationQueued = false;
                DiagnosticLogger.WriteException(
                    "ENFORCEMENT ERROR",
                    ex,
                    "Could not queue the manual reconnect-lock failure notification.");
            }
        }

        private bool EnsureFirewall(string errorTitle, bool respectHighPingMute, bool showErrorNotification = true)
        {
            if (firewall != null)
                return true;

            try
            {
                firewall = firewallFactory();
                if (firewall == null)
                    throw new InvalidOperationException("The firewall block service factory returned no service.");
                lastFirewallInitializationError = null;
                return true;
            }
            catch (Exception ex)
            {
                lastFirewallInitializationError = ex.Message;
                DiagnosticLogger.WriteException("FIREWALL ERROR", ex, "Could not initialize Windows Filtering Platform enforcement.");
                ReportFirewallError($"Could not initialize Windows Filtering Platform enforcement: {ex.Message}", errorTitle, respectHighPingMute, showErrorNotification);
                return false;
            }
        }
    }
}
