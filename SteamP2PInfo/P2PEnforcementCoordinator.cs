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
        private readonly HashSet<ulong> handledPeers = new HashSet<ulong>();
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
            SteamPeerManager.PeerRemoved -= SteamPeerManager_PeerRemoved;
            SteamPeerManager.LobbyLeft -= SteamPeerManager_LobbyLeft;
            firewall?.RemoveAll();
            firewall?.Dispose();
            firewall = null;
            handledPeers.Clear();
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
                    if (handledPeers.Count > 0)
                    {
                        firewall?.RemoveAll();
                        handledPeers.Clear();
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
            bool isQuarantined = handledPeers.Contains(steamId);

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
                return;

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

            // The exact UDP flow is blocked at WFP transport layers before the
            // companion process asks Steam to release its logical session.
            bool sessionClosed = peer.CloseSession();
            handledPeers.Add(steamId);
            string scope = string.IsNullOrWhiteSpace(blockResult.Details) ? "exact UDP flow" : blockResult.Details;
            Logger.WriteEnforcementLine($"[HIGH PING DISCONNECT] {steamId} measured {ping:F1} ms (limit {thresholdMs:F1} ms); WFP blocked the {scope} to {endpoint}; Steam close result: {sessionClosed}");
        }

        private void SteamPeerManager_PeerRemoved(ulong steamId, PeerRemovalReason reason)
        {
            // CloseSession runs in this companion process. Its transport/auth callbacks do
            // not prove that the attached game's independent Steam session has ended; in
            // practice the game can remain connected or recreate the session immediately.
            // Keep enforced rules for the lobby lifetime so the quarantine cannot disappear
            // as a side effect of our own close call.
            if (handledPeers.Contains(steamId))
            {
                Logger.WriteEnforcementLine(
                    $"[HIGH PING QUARANTINE] Retaining firewall rules for {steamId} after {reason}; companion IPC removal does not prove the game's connection ended.");
                return;
            }

            firewall?.Remove(steamId);
            handledPeers.Remove(steamId);
        }

        private void SteamPeerManager_LobbyLeft()
        {
            if (handledPeers.Count > 0)
            {
                Logger.WriteEnforcementLine(
                    $"[HIGH PING QUARANTINE] Ignoring companion LeaveLobby cleanup for {handledPeers.Count} enforced peer(s); rules remain until enforcement is disabled or the game/tool exits.");
            }
        }

        private void ReportFirewallError(string message)
        {
            Logger.WriteEnforcementLine($"[ENFORCEMENT ERROR] {message}");
            if (firewallErrorShown)
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
