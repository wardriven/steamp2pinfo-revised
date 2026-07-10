using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

using SteamP2PInfo.Config;

namespace SteamP2PInfo
{
    internal sealed class P2PEnforcementCoordinator : IDisposable
    {
        internal const double PingThresholdMs = 100d;
        internal static readonly TimeSpan EnforcementInterval = TimeSpan.FromMilliseconds(250);

        private readonly DispatcherTimer timer;
        private readonly IFirewallBlockService firewall;
        private readonly HashSet<ulong> handledPeers = new HashSet<ulong>();
        private bool evaluating;
        private bool disposed;

        public P2PEnforcementCoordinator(string executablePath)
        {
            firewall = new WindowsFirewallBlockService(executablePath);
            timer = new DispatcherTimer(DispatcherPriority.Send)
            {
                Interval = EnforcementInterval
            };
            timer.Tick += Timer_Tick;
            SteamPeerManager.PeerRemoved += SteamPeerManager_PeerRemoved;
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
            firewall.RemoveAll();
            firewall.Dispose();
            handledPeers.Clear();
        }

        internal static bool ShouldDisconnect(double ping)
        {
            return !double.IsNaN(ping)
                && !double.IsInfinity(ping)
                && ping >= 0d
                && ping > PingThresholdMs;
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
                        firewall.RemoveAll();
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
            if (handledPeers.Contains(steamId))
                return;

            bool connected;
            try
            {
                connected = peer.UpdatePeerInfo();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"[ENFORCEMENT ERROR] Failed to refresh {steamId}: {ex.Message}");
                return;
            }

            if (!connected)
                return;

            double ping = peer.Ping;
            if (!ShouldDisconnect(ping))
                return;

            if (!peer.TryGetRemoteEndpoint(out PeerNetworkEndpoint endpoint))
            {
                Logger.WriteLine($"[ENFORCEMENT LIMITATION] {steamId} measured {ping:F1} ms but has no exact firewall endpoint; requesting Steam session closure only");
                bool closedWithoutFirewall = peer.CloseSession();
                if (closedWithoutFirewall)
                    handledPeers.Add(steamId);
                return;
            }

            FirewallBlockResult blockResult = firewall.Block(steamId, endpoint);
            if (!blockResult.Success)
            {
                Logger.WriteLine($"[ENFORCEMENT ERROR] Failed to block {steamId} at {endpoint}: {blockResult.Error}");
                return;
            }

            // The block has been added and read back at exact scope before this call is allowed.
            bool sessionClosed = peer.CloseSession();
            handledPeers.Add(steamId);
            Logger.WriteLine($"[HIGH PING DISCONNECT] {steamId} measured {ping:F1} ms; firewall blocked UDP {endpoint} for the game executable; Steam close result: {sessionClosed}");
        }

        private void SteamPeerManager_PeerRemoved(ulong steamId)
        {
            firewall.Remove(steamId);
            handledPeers.Remove(steamId);
        }
    }
}
