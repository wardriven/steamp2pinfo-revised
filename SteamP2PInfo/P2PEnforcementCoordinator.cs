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

        private readonly DispatcherTimer timer;
        private readonly int gameProcessId;
        private readonly Func<IEnumerable<SteamPeerBase>> peerProvider;
        private readonly Func<IFirewallBlockService> firewallFactory;
        private IFirewallBlockService firewall;
        private readonly PeerQuarantineLifecycle quarantinedPeers = new PeerQuarantineLifecycle();
        private readonly HashSet<ulong> returningPeers = new HashSet<ulong>();
        private bool evaluating;
        private bool disposed;
        private bool firewallErrorShown;

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
            Func<IFirewallBlockService> firewallFactory)
        {
            this.gameProcessId = gameProcessId;
            this.peerProvider = peerProvider ?? throw new ArgumentNullException(nameof(peerProvider));
            this.firewallFactory = firewallFactory ?? throw new ArgumentNullException(nameof(firewallFactory));
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
                timer.Start();
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
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
        /// Applies the exact-flow WFP block and then asks Steam to close every
        /// peer currently known by SteamPeerManager. Each peer is isolated so a
        /// failure cannot stop the remaining peers from being attempted.
        /// </summary>
        internal void BlockAllConnectedPeers()
        {
            if (evaluating || disposed)
                return;

            evaluating = true;
            try
            {
                foreach (SteamPeerBase peer in peerProvider().Where(peer => peer != null).ToArray())
                {
                    try
                    {
                        BlockPeerManually(peer);
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteEnforcementLine($"[MANUAL BLOCK ERROR] Failed to process peer {peer.SteamID.m_SteamID}: {ex.Message}");
                    }
                }
            }
            finally
            {
                evaluating = false;
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (evaluating || disposed)
                return;

            evaluating = true;
            try
            {
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

        private void BlockPeerManually(SteamPeerBase peer)
        {
            ulong steamId = peer.SteamID.m_SteamID;
            if (!TryRefreshPeer(peer, "manual block", out PeerNetworkEndpoint endpoint))
                return;

            if (endpoint == null)
            {
                ReportFirewallError($"Cannot manually block {steamId}: Steam did not expose an exact remote UDP endpoint. No broad UDP block was applied.", "Manual Peer Block Error", false, false);
                return;
            }

            if (!BlockAndClose(peer, endpoint, PeerQuarantineOwner.ManualHotkey, "Manual Peer Block Error", false, out FirewallBlockResult blockResult, out bool sessionClosed))
                return;

            string scope = string.IsNullOrWhiteSpace(blockResult.Details) ? "exact UDP flow" : blockResult.Details;
            Logger.WriteEnforcementLine($"[MANUAL BLOCK] Peer {steamId}; WFP blocked the {scope} to {endpoint}; Steam close result: {sessionClosed}");
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

        private void SteamPeerManager_PeerBeginAuthSession(ulong steamId)
        {
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

        private void SteamPeerManager_PeerRemoved(ulong steamId, PeerRemovalReason reason)
        {
            // CloseSession runs in this companion process. Its transport/auth callbacks do
            // not prove that the attached game's independent Steam session has ended; in
            // practice the game can remain connected or recreate the session immediately.
            // Keep enforced rules for the lobby lifetime so the quarantine cannot disappear
            // as a side effect of our own close call.
            if (quarantinedPeers.ShouldRetainAfterPeerRemoval(steamId))
            {
                Logger.WriteEnforcementLine(
                    $"[P2P QUARANTINE] Retaining WFP filters for {steamId} after {reason}; companion IPC removal does not prove the game's connection ended. Waiting for a confirmed new BeginAuthSession before clearing this peer's quarantine.");
                return;
            }

            firewall?.Remove(steamId);
            returningPeers.Remove(steamId);
        }

        private void SteamPeerManager_LobbyLeft()
        {
            if (quarantinedPeers.Count > 0)
            {
                Logger.WriteEnforcementLine(
                    $"[P2P QUARANTINE] Ignoring companion LeaveLobby cleanup for {quarantinedPeers.Count} enforced peer(s); rules remain until the game/tool exits, or a confirmed returning BeginAuthSession clears that peer.");
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
                ReportFirewallError($"Could not initialize Windows Filtering Platform enforcement: {ex.Message}", errorTitle, respectHighPingMute, showErrorNotification);
                return false;
            }
        }
    }
}
