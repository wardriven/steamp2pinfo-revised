using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace SteamP2PInfo
{
    internal sealed class PeerConnectionHistoryTracker
    {
        private readonly object syncRoot = new object();
        private readonly Dictionary<ulong, ActiveConnection> activeConnections =
            new Dictionary<ulong, ActiveConnection>();
        private readonly ConnectionHistoryStore store;
        private readonly Func<DateTime> utcNow;

        internal event Action<PeerHistoryEntry> ConnectionCompleted;
        internal event Action<string> ErrorOccurred;

        internal int ActiveConnectionCount
        {
            get
            {
                lock (syncRoot)
                    return activeConnections.Count;
            }
        }

        internal PeerConnectionHistoryTracker(ConnectionHistoryStore store)
            : this(store, () => DateTime.UtcNow)
        {
        }

        internal PeerConnectionHistoryTracker(ConnectionHistoryStore store, Func<DateTime> utcNow)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        internal void Observe(SteamPeerBase peer, long cycleId)
        {
            if (peer == null)
                return;

            ulong steamId;
            try
            {
                steamId = peer.SteamID.m_SteamID;
            }
            catch (Exception ex)
            {
                ReportError("Connection history could not identify a peer: " + ex.Message);
                return;
            }

            if (steamId == 0)
                return;

            string name = null;
            double? ping = null;
            string ipAddress = null;

            try
            {
                string candidateName = peer.Name;
                if (!string.IsNullOrWhiteSpace(candidateName))
                    name = candidateName.Trim();
            }
            catch (Exception ex)
            {
                ReportError("Connection history could not read the peer name: " + ex.Message);
            }

            try
            {
                double candidatePing = peer.Ping;
                if (candidatePing > 0d &&
                    !double.IsNaN(candidatePing) &&
                    !double.IsInfinity(candidatePing))
                {
                    ping = candidatePing;
                }
            }
            catch (Exception ex)
            {
                ReportError("Connection history could not read the peer ping: " + ex.Message);
            }

            try
            {
                if (peer.TryGetRemoteEndpoint(out PeerNetworkEndpoint endpoint) &&
                    endpoint != null &&
                    endpoint.Address != null &&
                    (endpoint.Address.AddressFamily == AddressFamily.InterNetwork ||
                     endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6))
                {
                    ipAddress = endpoint.Address.ToString();
                }
            }
            catch (Exception ex)
            {
                ReportError("Connection history could not read the peer endpoint: " + ex.Message);
            }

            try
            {
                lock (syncRoot)
                {
                    if (!activeConnections.TryGetValue(steamId, out ActiveConnection connection))
                    {
                        connection = new ActiveConnection(steamId, NormalizeUtc(utcNow()));
                        activeConnections.Add(steamId, connection);
                    }

                    if (name != null)
                        connection.SteamName = name;

                    if (ipAddress != null)
                        connection.IpAddress = ipAddress;

                    if (connection.LastSampleCycleId.HasValue &&
                        connection.LastSampleCycleId.Value == cycleId)
                    {
                        return;
                    }

                    if (ping.HasValue)
                    {
                        connection.LastSampleCycleId = cycleId;
                        connection.AddPingSample(ping.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError("Connection history could not observe the peer: " + ex.Message);
            }
        }

        internal PeerHistoryEntry Complete(ulong steamId)
        {
            return Complete(steamId, out string ignoredError);
        }

        internal PeerHistoryEntry Complete(ulong steamId, out string error)
        {
            error = null;
            ActiveConnection connection;
            DateTime disconnectedAtUtc;

            try
            {
                lock (syncRoot)
                {
                    if (!activeConnections.TryGetValue(steamId, out connection))
                        return null;

                    activeConnections.Remove(steamId);
                    disconnectedAtUtc = NormalizeUtc(utcNow());
                }
            }
            catch (Exception ex)
            {
                error = "Connection history could not complete the peer: " + ex.Message;
                ReportError(error);
                return null;
            }

            if (disconnectedAtUtc < connection.ConnectedAtUtc)
                disconnectedAtUtc = connection.ConnectedAtUtc;

            PeerHistoryEntry entry = new PeerHistoryEntry(
                connection.SteamId,
                connection.SteamName,
                connection.AveragePingMs,
                connection.IpAddress,
                connection.ConnectedAtUtc,
                disconnectedAtUtc);

            try
            {
                if (!store.TryAppend(entry, out error))
                {
                    ReportError(error);
                    return null;
                }
            }
            catch (Exception ex)
            {
                error = "Connection history could not save the completed peer: " + ex.Message;
                ReportError(error);
                return null;
            }

            RaiseConnectionCompleted(entry);
            return entry;
        }

        private void RaiseConnectionCompleted(PeerHistoryEntry entry)
        {
            Action<PeerHistoryEntry> handlers = ConnectionCompleted;
            if (handlers == null)
                return;

            foreach (Action<PeerHistoryEntry> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(entry);
                }
                catch (Exception ex)
                {
                    ReportError("A connection-history completion handler failed: " + ex.Message);
                }
            }
        }

        private void ReportError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return;

            Action<string> handlers = ErrorOccurred;
            if (handlers == null)
                return;

            foreach (Action<string> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(error);
                }
                catch
                {
                }
            }
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;

            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();

            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private sealed class ActiveConnection
        {
            private long pingSampleCount;
            private double averagePingMs;

            public ulong SteamId { get; }
            public DateTime ConnectedAtUtc { get; }
            public string SteamName { get; set; }
            public string IpAddress { get; set; }
            public long? LastSampleCycleId { get; set; }

            public double? AveragePingMs
            {
                get { return pingSampleCount == 0 ? (double?)null : averagePingMs; }
            }

            public ActiveConnection(ulong steamId, DateTime connectedAtUtc)
            {
                SteamId = steamId;
                ConnectedAtUtc = connectedAtUtc;
            }

            public void AddPingSample(double pingMs)
            {
                pingSampleCount++;
                averagePingMs += (pingMs - averagePingMs) / pingSampleCount;
            }
        }
    }
}
