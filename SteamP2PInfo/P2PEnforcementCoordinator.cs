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
        internal static readonly TimeSpan EnforcementInterval = TimeSpan.FromMilliseconds(250);
        internal static readonly TimeSpan ManualBlockMinimumScanWindow = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan ManualBlockPeerAbsenceGracePeriod = TimeSpan.FromSeconds(2);
        internal static readonly TimeSpan ManualBlockMaximumScanWindow = TimeSpan.FromSeconds(60);

        private readonly DispatcherTimer timer;
        private readonly int gameProcessId;
        private readonly Func<IEnumerable<SteamPeerBase>> peerProvider;
        private readonly Func<IFirewallBlockService> firewallFactory;
        private readonly Func<DateTime> utcNow;
        private IFirewallBlockService firewall;
        private readonly PeerQuarantineLifecycle quarantinedPeers = new PeerQuarantineLifecycle();
        private readonly HashSet<ulong> returningPeers = new HashSet<ulong>();
        private readonly Dictionary<ulong, ManualBlockTarget> manualBlockTargets = new Dictionary<ulong, ManualBlockTarget>();
        private DateTime manualBlockMinimumEndUtc;
        private DateTime manualBlockMaximumEndUtc;
        private DateTime? manualAllTargetsAbsentSinceUtc;
        private bool manualBlockScanActive;
        private bool manualMinimumBoundaryScanCompleted;
        private bool manualMinimumElapsedLogged;
        private bool evaluating;
        private bool disposed;
        private bool firewallErrorShown;

        internal bool IsManualBlockScanActive => manualBlockScanActive;

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
        /// Begins sustained exact-flow WFP enforcement for every peer currently
        /// known by SteamPeerManager. The action follows replacement peer
        /// instances and endpoint migrations throughout a twenty-second minimum
        /// scan window. It then follows only those tracked Steam IDs until they
        /// disappear, the game reports that it left, or a safety timeout expires.
        /// </summary>
        internal void BlockAllConnectedPeers()
        {
            if (disposed)
                return;

            SteamPeerBase[] peers = peerProvider().Where(peer => peer != null).ToArray();
            if (!manualBlockScanActive)
                manualBlockTargets.Clear();

            foreach (SteamPeerBase peer in peers)
                TrackManualBlockTarget(peer);

            manualBlockScanActive = true;
            manualAllTargetsAbsentSinceUtc = null;
            manualMinimumBoundaryScanCompleted = false;
            manualMinimumElapsedLogged = false;
            DateTime startedUtc = utcNow();
            manualBlockMinimumEndUtc = startedUtc.Add(ManualBlockMinimumScanWindow);
            manualBlockMaximumEndUtc = startedUtc.Add(ManualBlockMaximumScanWindow);
            DiagnosticLogger.Write(
                "ACTION",
                string.Format(
                    "Manual block-all-peers hotkey action started for {0} peer(s); exact-flow enforcement will scan for at least {1:F1} seconds, follow those peers until the lobby ends, and stop after {2:F1} seconds if Steam IPC misses the lobby exit.",
                    manualBlockTargets.Count,
                    ManualBlockMinimumScanWindow.TotalSeconds,
                    ManualBlockMaximumScanWindow.TotalSeconds));
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

            DateTime currentUtc = utcNow();
            if (currentUtc >= manualBlockMaximumEndUtc)
            {
                CompleteManualBlockScan("the maximum safety window elapsed without a reliable lobby-exit signal");
                return;
            }

            SteamPeerBase[] currentPeers = peerProvider()
                .Where(peer => peer != null)
                .ToArray();
            var currentPeersById = currentPeers
                .GroupBy(peer => peer.SteamID.m_SteamID)
                .ToDictionary(group => group.Key, group => group.First());

            bool minimumElapsed = currentUtc >= manualBlockMinimumEndUtc;
            bool acceptingNewTargets = !minimumElapsed || !manualMinimumBoundaryScanCompleted;
            if (acceptingNewTargets)
            {
                foreach (SteamPeerBase peer in currentPeers)
                    TrackManualBlockTarget(peer);
            }
            if (minimumElapsed)
                manualMinimumBoundaryScanCompleted = true;

            foreach (ManualBlockTarget target in manualBlockTargets.Values)
                target.Peer = currentPeersById.TryGetValue(target.SteamId, out SteamPeerBase peer) ? peer : null;

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

            if (currentUtc < manualBlockMinimumEndUtc)
                return;

            if (manualBlockTargets.Count == 0)
            {
                CompleteManualBlockScan("the minimum scan window elapsed without detecting a peer");
                return;
            }

            if (!manualBlockTargets.Values.Any(target => target.Peer != null))
            {
                if (!manualAllTargetsAbsentSinceUtc.HasValue)
                    manualAllTargetsAbsentSinceUtc = currentUtc;

                if (currentUtc - manualAllTargetsAbsentSinceUtc.Value < ManualBlockPeerAbsenceGracePeriod)
                    return;

                CompleteManualBlockScan("the minimum scan window elapsed and no tracked peer session remained visible through the replacement-session grace period");
                return;
            }

            manualAllTargetsAbsentSinceUtc = null;

            if (!manualMinimumElapsedLogged)
            {
                manualMinimumElapsedLogged = true;
                DiagnosticLogger.Write(
                    "ACTION",
                    "Manual block-all-peers minimum scan window elapsed; enforcement remains active for the originally tracked Steam IDs until they disappear, the game leaves the lobby, or the safety timeout expires.");
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

            target.Peer = peer;
        }

        private void CompleteManualBlockScan(string reason)
        {
            int targetCount = manualBlockTargets.Count;
            manualBlockTargets.Clear();
            manualBlockScanActive = false;
            manualAllTargetsAbsentSinceUtc = null;
            manualMinimumBoundaryScanCompleted = false;
            manualMinimumElapsedLogged = false;
            DiagnosticLogger.Write(
                "ACTION",
                string.Format(
                    "Manual block-all-peers action completed because {0}; stopped tracking {1} peer(s). Retained WFP filters remain active.",
                    reason,
                    targetCount));
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
            if (!TryRefreshPeer(peer, "manual block", out PeerNetworkEndpoint endpoint))
                return false;

            if (endpoint == null)
            {
                ReportFirewallError($"Cannot manually block {steamId}: Steam did not expose an exact remote UDP endpoint. No broad UDP block was applied.", "Manual Peer Block Error", false, false);
                return false;
            }

            if (!EnsureFirewall("Manual Peer Block Error", false, false))
                return false;

            FirewallBlockResult blockResult;
            try
            {
                blockResult = firewall.Block(steamId, endpoint);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("FIREWALL ERROR", ex, "Failed to apply the manual WFP quarantine for peer " + steamId + ".");
                ReportFirewallError($"Failed to apply the WFP flow quarantine for {steamId}: {ex.Message}", "Manual Peer Block Error", false, false);
                return false;
            }

            if (!blockResult.Success)
            {
                ReportFirewallError($"Failed to apply the WFP flow quarantine for {steamId}: {blockResult.Error}", "Manual Peer Block Error", false, false);
                return false;
            }

            quarantinedPeers.Retain(steamId, PeerQuarantineOwner.ManualHotkey);

            string endpointKey = endpoint.ToString();
            if (target.ClosedEndpoints.Contains(endpointKey))
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
                target.ClosedEndpoints.Add(endpointKey);
            else
                DiagnosticLogger.Write("ENFORCEMENT", "Steam did not confirm closing peer " + steamId + "; sustained scanning will retry this session.");

            string scope = string.IsNullOrWhiteSpace(blockResult.Details) ? "exact UDP flow" : blockResult.Details;
            Logger.WriteEnforcementLine($"[MANUAL BLOCK] Peer {steamId}; WFP blocked the {scope} to {endpoint}; Steam close result: {sessionClosed}");
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
            if (quarantinedPeers.Count > 0 || returningPeers.Count > 0)
            {
                firewall?.RemoveAll();
                quarantinedPeers.Clear();
                returningPeers.Clear();
            }
        }

        internal void SteamPeerManager_PeerBeginAuthSession(ulong steamId)
        {
            if (!quarantinedPeers.Contains(steamId))
                return;

            if (manualBlockScanActive && manualBlockTargets.ContainsKey(steamId))
            {
                manualBlockTargets[steamId].BeginReplacementSession();
                Logger.WriteEnforcementLine(
                    $"[MANUAL BLOCK] Returning peer {steamId} began a replacement auth session while sustained scanning is active; retained filters remain and the replacement peer will be scanned.");
                return;
            }

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
                target.Peer = null;

            // CloseSession runs in this companion process. Its transport/auth callbacks do
            // not prove that the attached game's independent Steam session has ended; in
            // practice the game can remain connected or recreate the session immediately.
            // Keep enforced rules for the lobby lifetime so the quarantine cannot disappear
            // as a side effect of our own close call.
            if (quarantinedPeers.ShouldRetainAfterPeerRemoval(steamId))
            {
                string continuation = manualBlockScanActive && manualBlockTargets.ContainsKey(steamId)
                    ? "Sustained manual scanning remains active and will reacquire any replacement peer session."
                    : "Waiting for a confirmed new BeginAuthSession before clearing this peer's quarantine.";
                Logger.WriteEnforcementLine(
                    $"[P2P QUARANTINE] Retaining WFP filters for {steamId} after {reason}; companion IPC removal does not prove the game's connection ended. {continuation}");
                return;
            }

            firewall?.Remove(steamId);
            returningPeers.Remove(steamId);
        }

        internal void SteamPeerManager_LobbyLeft()
        {
            if (manualBlockScanActive)
            {
                CompleteManualBlockScan("the game left the lobby");
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
            public SteamPeerBase Peer { get; set; }
            public HashSet<string> ClosedEndpoints { get; } = new HashSet<string>(StringComparer.Ordinal);

            public ManualBlockTarget(ulong steamId)
            {
                SteamId = steamId;
            }

            public void BeginReplacementSession()
            {
                Peer = null;
                ClosedEndpoints.Clear();
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

        private bool EnsureFirewall(string errorTitle, bool respectHighPingMute, bool showErrorNotification = true)
        {
            if (firewall != null)
                return true;

            try
            {
                firewall = firewallFactory();
                if (firewall == null)
                    throw new InvalidOperationException("The firewall block service factory returned no service.");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("FIREWALL ERROR", ex, "Could not initialize Windows Filtering Platform enforcement.");
                ReportFirewallError($"Could not initialize Windows Filtering Platform enforcement: {ex.Message}", errorTitle, respectHighPingMute, showErrorNotification);
                return false;
            }
        }
    }
}
