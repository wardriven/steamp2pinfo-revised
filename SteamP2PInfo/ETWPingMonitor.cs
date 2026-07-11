using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using System.Net;
using System.Net.Sockets;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace SteamP2PInfo
{
    public static class ETWPingMonitor
    {
        public const int N_SAMPLES = 10;

        private class PingInfo
        {
            public double tFirstSend = -1; // Timestamp of first sent STUN packet
            public double tStunSent = -1; // Timestamp of last sent STUN packet
            public double tLastStunRecv = -1; // Timestamp of last recv STUN packet
            public double ping = -1; // Current ping

            public double avgPing = 0; // average ping over last N_SAMPLES
            public double jitter = 0; // jitter (stdev of ping) over last N_SAMPLES
            public double[] pingSamples;

            public int cnt = 0; // Number of ping packets which came back
            public int stunSentCnt = 0; // Number of STUN packets sent
            public int stunLateCnt = 0; // Number sent after the previous one had not been recieved

            public PingInfo()
            {
                pingSamples = new double[N_SAMPLES];
            }
        }

        private static TraceEventSession kernelSession;
        private static Thread eventThread;
        public static bool Running { get; private set; }
        private static Dictionary<ulong, PingInfo> pings;
        private static readonly Dictionary<PeerNetworkEndpoint, Dictionary<FlowObservationKey, DateTime>> recentUdpFlows;
        private static readonly HashSet<PeerNetworkEndpoint> watchedEndpoints;
        private static int trackedProcessId;
        private static readonly object lockObj;
        private static readonly TimeSpan FlowObservationLifetime = TimeSpan.FromSeconds(20);

        static ETWPingMonitor()
        {
            Running = false;
            lockObj = new object();
            pings = new Dictionary<ulong, PingInfo>();
            recentUdpFlows = new Dictionary<PeerNetworkEndpoint, Dictionary<FlowObservationKey, DateTime>>();
            watchedEndpoints = new HashSet<PeerNetworkEndpoint>();
        }

        /// <summary>
        /// Begin monitoring STUN pings.
        /// </summary>
        public static void Start()
        {
            if (Running) return;

            if (!(TraceEventSession.IsElevated() ?? false))
            {
                throw new Exception("Program must be run in administrator mode");
            }

            kernelSession = new TraceEventSession(KernelTraceEventParser.KernelSessionName);
            kernelSession.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            kernelSession.Source.Kernel.UdpIpSend += Kernel_UdpIpSend;
            kernelSession.Source.Kernel.UdpIpRecv += Kernel_UdpIpRecv;

            Running = true;
            eventThread = new Thread(() => kernelSession.Source.Process());
            eventThread.Start();
        }

        /// <summary>
        /// Stop monitoring STUN pings.
        /// </summary>
        public static void Stop()
        {
            if (!Running) return;

            Running = false;
            kernelSession.Stop();
            lock (lockObj)
            {
                trackedProcessId = 0;
                recentUdpFlows.Clear();
                watchedEndpoints.Clear();
            }
        }

        /// <summary>
        /// Record recently observed UDP tuples for the selected game process. WFP
        /// enforcement uses this evidence to select the socket that is actually
        /// sending to a peer, instead of relying only on Steam's endpoint report.
        /// </summary>
        public static void TrackProcessUdpFlows(int processId)
        {
            lock (lockObj)
            {
                trackedProcessId = processId;
                recentUdpFlows.Clear();
                watchedEndpoints.Clear();
            }
        }

        internal static void WatchUdpEndpoint(PeerNetworkEndpoint endpoint)
        {
            if (endpoint == null || endpoint.Address.AddressFamily != AddressFamily.InterNetwork)
                return;

            lock (lockObj)
                watchedEndpoints.Add(endpoint);
        }

        internal static ushort[] GetRecentLocalUdpPorts(PeerNetworkEndpoint endpoint)
        {
            if (endpoint == null || endpoint.Address.AddressFamily != AddressFamily.InterNetwork)
                return Array.Empty<ushort>();

            lock (lockObj)
            {
                DateTime now = DateTime.UtcNow;
                PurgeExpiredFlows(now);
                if (!recentUdpFlows.TryGetValue(endpoint, out Dictionary<FlowObservationKey, DateTime> ports))
                    return Array.Empty<ushort>();

                return ports.Keys
                    .Where(flow => flow.ProcessId == trackedProcessId)
                    .Select(flow => flow.LocalPort)
                    .OrderBy(port => port)
                    .ToArray();
            }
        }

        internal static string DescribeRecentUdpFlows(PeerNetworkEndpoint endpoint)
        {
            if (endpoint == null || endpoint.Address.AddressFamily != AddressFamily.InterNetwork)
                return "none";

            lock (lockObj)
            {
                DateTime now = DateTime.UtcNow;
                PurgeExpiredFlows(now);
                if (!recentUdpFlows.TryGetValue(endpoint, out Dictionary<FlowObservationKey, DateTime> flows))
                    return "none";

                return string.Join(", ", flows.Keys
                    .OrderBy(flow => flow.ProcessId)
                    .ThenBy(flow => flow.LocalPort)
                    .Select(flow => $"PID {flow.ProcessId}, local UDP {flow.LocalPort}"));
            }
        }

        internal static ObservedUdpFlow[] GetRecentUdpFlows(PeerNetworkEndpoint endpoint)
        {
            if (endpoint == null || endpoint.Address.AddressFamily != AddressFamily.InterNetwork)
                return Array.Empty<ObservedUdpFlow>();

            lock (lockObj)
            {
                DateTime now = DateTime.UtcNow;
                PurgeExpiredFlows(now);
                if (!recentUdpFlows.TryGetValue(endpoint, out Dictionary<FlowObservationKey, DateTime> flows))
                    return Array.Empty<ObservedUdpFlow>();

                return flows.Keys
                    .Select(flow => new ObservedUdpFlow(flow.ProcessId, flow.LocalPort))
                    .OrderBy(flow => flow.ProcessId)
                    .ThenBy(flow => flow.LocalPort)
                    .ToArray();
            }
        }

        /// <summary>
        /// Begin tracking the ping to a remote endpoint. Net ID given by (port << 32) | ipv4.
        /// If netId already is being monitored, will reset average ping.
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        public static void Register(ulong netId)
        {
            lock (lockObj)
            {
                pings[netId] = new PingInfo();
            }
        }

        /// <summary>
        /// Stop tracking the ping to a remote endpoint. Net ID given by (port << 32) | ipv4.
        /// </summary>
        /// <param name="ip"></param>
        /// <param name="port"></param>
        public static void Unregister(ulong netId)
        {
            lock (lockObj)
            {
                pings.Remove(netId);
            }
        }

        /// <summary>
        /// Get the average ping to the provided endpoint, or -1 if none exists.
        /// Net ID given by (port << 32) | ipv4.
        /// </summary>
        /// <param name="netId"></param>
        /// <returns></returns>
        public static double GetPing(ulong netId)
        {
            lock (lockObj)
            {
                return pings.ContainsKey(netId) ? pings[netId].ping : -1;
            }
        }

        /// <summary>
        /// Get the average ping over N_SAMPLES to the provided endpoint, or -1 if none exists.
        /// Net ID given by (port << 32) | ipv4.
        /// </summary>
        /// <param name="ip"></param>
        /// <returns></returns>
        public static double GetAveragePing(ulong netId)
        {
            lock (lockObj)
            {
                return pings.ContainsKey(netId) ? pings[netId].avgPing : -1;
            }
        }

        /// <summary>
        /// Get the jitter to the provided endpoint, or -1 if none exists.
        /// Net ID given by (port << 32) | ipv4.
        /// </summary>
        /// <param name="ip"></param>
        /// <returns></returns>
        public static double GetJitter(ulong netId)
        {
            lock (lockObj)
            {
                return pings.ContainsKey(netId) ? pings[netId].jitter : -1;
            }
        }

        /// <summary>
        /// Get [stun packet sent before reply]/[stun packets sent], as a percentage.
        /// </summary>
        /// <param name="netId"></param>
        /// <returns></returns>
        public static double GetLatePacketRatio(ulong netId)
        {
            lock (lockObj)
            {
                return (pings.ContainsKey(netId) && pings[netId].stunSentCnt > 0) ? pings[netId].stunLateCnt * 100d / pings[netId].stunSentCnt : -1;
            }
        }

        private static void Kernel_UdpIpSend(UdpIpTraceData packet)
        {
            RecordObservedFlow(packet, true);
            if (packet.size == 56)
            {
                uint ipv4 = BitConverter.ToUInt32(packet.daddr.MapToIPv4().GetAddressBytes(), 0);
                ulong netId = (ulong)packet.dport << 32 | ipv4;

                lock (lockObj)
                {
                    if (pings.ContainsKey(netId))
                    {
                        if (pings[netId].tFirstSend == -1)
                            pings[netId].tFirstSend = packet.TimeStampRelativeMSec;

                        pings[netId].stunSentCnt++;
                        // For the first 10 seconds of connection, some STUN packets may be dropped entirely, which messes with the ping
                        // filter. Assuming late packets were dropped at the beginning helps.
                        if (pings[netId].tStunSent == -1 || packet.TimeStampRelativeMSec - pings[netId].tFirstSend < 10000)
                            pings[netId].tStunSent = packet.TimeStampRelativeMSec;
                        else
                            pings[netId].stunLateCnt++;
                    }
                }
            }
        }

        private static void Kernel_UdpIpRecv(UdpIpTraceData packet)
        {
            RecordObservedFlow(packet, false);
            if (packet.size == 68)
            {
                uint ipv4 = BitConverter.ToUInt32(packet.saddr.MapToIPv4().GetAddressBytes(), 0);
                ulong netId = (ulong)packet.sport << 32 | ipv4;

                lock (lockObj)
                {
                    if (pings.ContainsKey(netId))
                    {
                        if (pings[netId].tStunSent != -1)
                        {
                            PingInfo pi = pings[netId];

                            pi.tLastStunRecv = packet.TimeStampRelativeMSec;
                            pi.ping = packet.TimeStampRelativeMSec - pi.tStunSent;

                            pi.pingSamples[pi.cnt++ % N_SAMPLES] = pi.ping;
                            if (pi.cnt >= N_SAMPLES)
                            {   // After N_SAMPLES measurements, compute average ping and jitter
                                pi.avgPing = 0;
                                for (int i = 0; i < N_SAMPLES; i++)
                                    pi.avgPing += pi.pingSamples[i];
                                pi.avgPing /= N_SAMPLES;

                                pi.jitter = 0;
                                for (int i = 0; i < N_SAMPLES; i++)
                                    pi.jitter += Math.Pow(pi.pingSamples[i] - pi.avgPing, 2);
                                pi.jitter = Math.Sqrt(pi.jitter / N_SAMPLES);
                            }

                            pings[netId].tStunSent = -1;
                        }
                    }
                }
            }
        }

        private static void RecordObservedFlow(UdpIpTraceData packet, bool outbound)
        {
            IPAddress remoteAddress = outbound ? packet.daddr : packet.saddr;
            ushort remotePort = unchecked((ushort)(outbound ? packet.dport : packet.sport));
            ushort localPort = unchecked((ushort)(outbound ? packet.sport : packet.dport));
            if (remoteAddress == null || remotePort == 0 || localPort == 0)
                return;

            if (remoteAddress.AddressFamily == AddressFamily.InterNetworkV6 && remoteAddress.IsIPv4MappedToIPv6)
                remoteAddress = remoteAddress.MapToIPv4();
            if (remoteAddress.AddressFamily != AddressFamily.InterNetwork)
                return;

            var endpoint = new PeerNetworkEndpoint(remoteAddress, remotePort);
            lock (lockObj)
            {
                if (packet.ProcessID != trackedProcessId && !watchedEndpoints.Contains(endpoint))
                    return;

                DateTime now = DateTime.UtcNow;
                PurgeExpiredFlows(now);
                if (!recentUdpFlows.TryGetValue(endpoint, out Dictionary<FlowObservationKey, DateTime> ports))
                {
                    ports = new Dictionary<FlowObservationKey, DateTime>();
                    recentUdpFlows.Add(endpoint, ports);
                }
                ports[new FlowObservationKey(packet.ProcessID, localPort)] = now;
            }
        }

        private static void PurgeExpiredFlows(DateTime now)
        {
            foreach (PeerNetworkEndpoint endpoint in recentUdpFlows.Keys.ToArray())
            {
                Dictionary<FlowObservationKey, DateTime> ports = recentUdpFlows[endpoint];
                foreach (FlowObservationKey flow in ports.Where(pair => now - pair.Value > FlowObservationLifetime).Select(pair => pair.Key).ToArray())
                    ports.Remove(flow);
                if (ports.Count == 0)
                    recentUdpFlows.Remove(endpoint);
            }
        }

        private readonly struct FlowObservationKey : IEquatable<FlowObservationKey>
        {
            public int ProcessId { get; }
            public ushort LocalPort { get; }

            public FlowObservationKey(int processId, ushort localPort)
            {
                ProcessId = processId;
                LocalPort = localPort;
            }

            public bool Equals(FlowObservationKey other)
            {
                return ProcessId == other.ProcessId && LocalPort == other.LocalPort;
            }

            public override bool Equals(object obj)
            {
                return obj is FlowObservationKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return (ProcessId * 397) ^ LocalPort.GetHashCode();
            }
        }

        internal sealed class ObservedUdpFlow
        {
            public int ProcessId { get; }
            public ushort LocalPort { get; }

            public ObservedUdpFlow(int processId, ushort localPort)
            {
                ProcessId = processId;
                LocalPort = localPort;
            }
        }
    }
}
