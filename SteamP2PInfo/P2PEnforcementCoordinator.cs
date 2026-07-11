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
        private IFirewallBlockService firewall;
        private readonly PeerQuarantineLifecycle quarantinedPeers = new PeerQuarantineLifecycle();
        private readonly HashSet<ulong> returningPeers = new HashSet<ulong>();
        private bool evaluating;
        private bool disposed;
        private bool firewallErrorShown;

        public P2PEnforcementCoordinator(int gameProcessId)
        {
            this.gameProcessId = gameProcessId;
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

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (evaluating || disposed)
                return;

            evaluating = true;
            try
            {
                if (GameConfig.Current == null || !GameConfig.Current.DisconnectHighPingEnabled)
                {
                    if (quarantinedPeers.Count > 0 || returningPeers.Count > 0)
                    {
                        firewall?.RemoveAll();
                        quarantinedPeers.Clear();
                        returningPeers.Clear();
                    }
                    return;
                }

                foreach (SteamPeerBase peer in SteamPeerManager.GetPeers().ToArray())
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

            bool connected;
            try
            {
                connected = peer.UpdatePeerInfo();
            }
            catch (Exception ex)
            {
                Logger.WriteEnforcementLine($"[ENFORCEMENT ERROR] Failed to refresh {steamId}: {ex.Message}");
                return;
            }

            if (!connected)
                return;

            bool hasReportedEndpoint = peer.TryGetRemoteEndpoint(out PeerNetworkEndpoint endpoint);
            if (hasReportedEndpoint)
                ETWPingMonitor.WatchUdpEndpoint(endpoint);
            if (isQuarantined)
            {
                // WFP filters are exact tuples. Keep watching an enforced peer so a
                // reconnect or relay migration cannot escape the active quarantine.
                if (hasReportedEndpoint)
                {
                    if (!EnsureFirewall())
                        return;

                    FirewallBlockResult refreshResult = firewall.Block(steamId, endpoint);
                    if (!refreshResult.Success)
                        ReportFirewallError($"Failed to extend the WFP flow quarantine for {steamId}: {refreshResult.Error}");
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

            if (!hasReportedEndpoint)
            {
                ReportFirewallError($"Cannot enforce the high-ping disconnect for {steamId}: Steam did not expose an exact remote UDP endpoint. No broad UDP block was applied.");
                return;
            }

            if (!EnsureFirewall())
                return;

            FirewallBlockResult blockResult = firewall.Block(steamId, endpoint);
            if (!blockResult.Success)
            {
                ReportFirewallError($"Failed to apply the WFP flow quarantine for {steamId}: {blockResult.Error}");
                return;
            }

            // Mark the peer before CloseSession. Steam can synchronously emit an
            // EndAuthSession callback from the companion process; that callback
            // must retain this exact-flow block instead of clearing it.
            quarantinedPeers.Retain(steamId);
            bool wasReturningPeer = returningPeers.Remove(steamId);

            // The exact UDP flow is blocked at WFP transport layers before the
            // companion process asks Steam to release its logical session.
            bool sessionClosed = peer.CloseSession();
            string scope = string.IsNullOrWhiteSpace(blockResult.Details) ? "exact UDP flow" : blockResult.Details;
            string peerDescription = wasReturningPeer ? "Returning peer" : "Peer";
            Logger.WriteEnforcementLine($"[HIGH PING DISCONNECT] {peerDescription} {steamId} measured {ping:F1} ms (limit {thresholdMs:F1} ms); WFP blocked the {scope} to {endpoint}; Steam close result: {sessionClosed}");
        }

        private void SteamPeerManager_PeerBeginAuthSession(ulong steamId)
        {
            if (!quarantinedPeers.Contains(steamId))
                return;

            Logger.WriteEnforcementLine(
                $"[HIGH PING QUARANTINE] Confirmed new BeginAuthSession for returning peer {steamId}; clearing that peer's retained WFP filters before creating the new peer entry.");

            if (!quarantinedPeers.ClearForConfirmedBeginAuthSession(steamId, peerId => firewall?.Remove(peerId)))
                return;

            returningPeers.Add(steamId);
            Logger.WriteEnforcementLine(
                $"[HIGH PING QUARANTINE] Cleared retained WFP filters for {steamId}; the returning session will be re-evaluated under the current ping limit.");
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
                    $"[HIGH PING QUARANTINE] Retaining WFP filters for {steamId} after {reason}; companion IPC removal does not prove the game's connection ended. Waiting for a confirmed new BeginAuthSession before clearing this peer's quarantine.");
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
                    $"[HIGH PING QUARANTINE] Ignoring companion LeaveLobby cleanup for {quarantinedPeers.Count} enforced peer(s); rules remain until enforcement is disabled, the game/tool exits, or a confirmed returning BeginAuthSession clears that peer.");
            }
        }

        private void ReportFirewallError(string message)
        {
            Logger.WriteEnforcementLine($"[ENFORCEMENT ERROR] {message}");
            if (firewallErrorShown || GameConfig.Current?.MuteHighPingEnforcementErrorNotifications == true)
                return;

            firewallErrorShown = true;
            MessageBox.Show(message, "High-Ping Enforcement Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private bool EnsureFirewall()
        {
            if (firewall != null)
                return true;

            try
            {
                firewall = new WfpFlowBlockService(gameProcessId);
                return true;
            }
            catch (Exception ex)
            {
                ReportFirewallError($"Could not initialize Windows Filtering Platform enforcement: {ex.Message}");
                return false;
            }
        }
    }
}
