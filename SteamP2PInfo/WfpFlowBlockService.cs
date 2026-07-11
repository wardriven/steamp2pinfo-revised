using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SteamP2PInfo.Config;

namespace SteamP2PInfo
{
    /// <summary>
    /// Experimental user-mode WFP transport filter.
    ///
    /// This blocks an observed UDP 5-tuple in both directions at the transport
    /// layers. It is deliberately endpoint/port scoped and uses a dynamic WFP
    /// session so the filters are removed when the owning engine session closes.
    /// It does not identify a Steam ID and is therefore shared when the endpoint
    /// is a Steam Datagram Relay address.
    /// </summary>
    internal sealed class WfpFlowBlockService : IFirewallBlockService
    {
        private const uint FwpSessionFlagDynamic = 0x00000001;
        private const uint FwpDataTypeUint8 = 1;
        private const uint FwpDataTypeUint16 = 2;
        private const uint FwpDataTypeUint32 = 3;
        private const uint FwpDataTypeByteArray16 = 11;
        private const uint FwpMatchEqual = 0;
        // FWP_ACTION_BLOCK is the low-order action code combined with the
        // required FWP_ACTION_FLAG_TERMINATING flag (0x00001000).
        private const uint FwpActionBlock = 0x00001001;

        private static readonly Guid ConditionIpProtocol = new Guid("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
        private static readonly Guid ConditionIpLocalPort = new Guid("0c1ba1af-5765-453f-af22-a8f791ac775b");
        private static readonly Guid ConditionIpRemoteAddress = new Guid("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
        private static readonly Guid ConditionIpRemotePort = new Guid("c35a604d-d22b-4e1a-91b4-68f674ee674b");

        private static readonly Guid LayerInboundTransportV4 = new Guid("5926dfc8-e3cf-4426-a283-dc393f5d0f9d");
        private static readonly Guid LayerInboundTransportV6 = new Guid("634a869f-fc23-4b90-b0c1-bf620a36ae6f");
        private static readonly Guid LayerOutboundTransportV4 = new Guid("09e61aea-d214-46e2-9b21-b26b0b2f28c8");
        private static readonly Guid LayerOutboundTransportV6 = new Guid("e1735bde-013f-4655-b351-a49e15762df0");

        private readonly IntPtr engine;
        private readonly Guid subLayerKey = Guid.NewGuid();
        private readonly int gameProcessId;
        private readonly Dictionary<ulong, PeerFilterSet> peerFilters = new Dictionary<ulong, PeerFilterSet>();
        private bool disposed;

        public WfpFlowBlockService() : this(0)
        {
        }

        public WfpFlowBlockService(int gameProcessId)
        {
            this.gameProcessId = gameProcessId;
            IntPtr sessionName = Marshal.StringToHGlobalUni("SteamP2PInfo experimental WFP flow session");
            try
            {
                var session = new FwpmSession0
                {
                    displayData = new FwpmDisplayData { name = sessionName },
                    flags = FwpSessionFlagDynamic
                };

                ThrowIfFailed(
                    FwpmEngineOpen0(null, 10, IntPtr.Zero, ref session, out IntPtr openedEngine),
                    "open WFP engine session");
                engine = openedEngine;
            }
            finally
            {
                Marshal.FreeHGlobal(sessionName);
            }

            IntPtr subLayerName = Marshal.StringToHGlobalUni("SteamP2PInfo experimental WFP flows");
            try
            {
                var subLayer = new FwpmSubLayer0
                {
                    subLayerKey = subLayerKey,
                    displayData = new FwpmDisplayData { name = subLayerName },
                    providerData = new FwpByteBlob(),
                    weight = ushort.MaxValue
                };

                try
                {
                    ThrowIfFailed(
                        FwpmSubLayerAdd0(engine, ref subLayer, IntPtr.Zero),
                        "add WFP sublayer");
                }
                catch
                {
                    FwpmEngineClose0(engine);
                    throw;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(subLayerName);
            }
        }

        /// <summary>
        /// Add inbound and outbound filters for each supplied local UDP port.
        /// The remote address and port are exact; local address remains
        /// unrestricted to tolerate interface selection changes.
        /// </summary>
        public FirewallBlockResult BlockFlow(ulong steamId, PeerNetworkEndpoint endpoint, IEnumerable<ushort> localPorts)
        {
            if (endpoint == null)
                return FirewallBlockResult.Failed("An endpoint is required when local UDP ports are supplied directly.");

            IEnumerable<GameUdpSocket> sockets = (localPorts ?? Enumerable.Empty<ushort>())
                .Where(port => port != 0)
                .Distinct()
                .Select(port => new GameUdpSocket(endpoint.Address.AddressFamily, port));
            return BlockFlowInternal(steamId, endpoint, sockets);
        }

        public FirewallBlockResult Block(ulong steamId, PeerNetworkEndpoint endpoint)
        {
            if (gameProcessId <= 0)
                return FirewallBlockResult.Failed("A selected game process is required for WFP flow enforcement.");
            if (endpoint == null)
                return FirewallBlockResult.Failed("The peer does not expose a valid remote UDP endpoint.");

            try
            {
                ETWPingMonitor.WatchUdpEndpoint(endpoint);
                ETWPingMonitor.ObservedUdpFlow[] observedFlows = ETWPingMonitor.GetRecentUdpFlows(endpoint);
                var gameObservedPorts = new HashSet<ushort>(observedFlows
                    .Where(flow => flow.ProcessId == gameProcessId)
                    .Select(flow => flow.LocalPort));
                GameUdpSocket[] matchingGameSockets = GetExclusiveGameUdpSockets()
                    .Where(socket => socket.AddressFamily == endpoint.Address.AddressFamily)
                    .Where(socket => gameObservedPorts.Contains(socket.LocalPort))
                    .ToArray();
                if (matchingGameSockets.Length > 0)
                {
                    FirewallBlockResult gameResult = BlockFlowInternal(steamId, endpoint, matchingGameSockets);
                    return gameResult.Success
                        ? FirewallBlockResult.Ok("exact game-owned UDP flow")
                        : gameResult;
                }

                if (GameConfig.Current?.AllowSteamOwnedExactFlowFallback != true)
                {
                    string observedDescription = ETWPingMonitor.DescribeRecentUdpFlows(endpoint);
                    return FirewallBlockResult.Failed(
                        $"No recent UDP packet from the selected game process was observed for {endpoint}. " +
                        $"Observed packets for that endpoint: {observedDescription}. " +
                        "Enable the Steam-owned exact-flow fallback to permit a verified steam.exe tuple.");
                }

                GameUdpSocket[] matchingSteamSockets = GetMatchingSteamSockets(endpoint, observedFlows);
                if (matchingSteamSockets.Length == 0)
                {
                    string observedDescription = ETWPingMonitor.DescribeRecentUdpFlows(endpoint);
                    return FirewallBlockResult.Failed(
                        $"No exclusive steam.exe UDP socket is currently available for {endpoint}. " +
                        $"Observed packets for that endpoint: {observedDescription}.");
                }

                FirewallBlockResult steamResult = BlockFlowInternal(steamId, endpoint, matchingSteamSockets);
                return steamResult.Success
                    ? FirewallBlockResult.Ok("exact Steam-owned UDP flow")
                    : steamResult;
            }
            catch (Exception ex)
            {
                return FirewallBlockResult.Failed(ex.Message);
            }
        }

        public FirewallBlockResult BlockAllGameUdp(ulong steamId)
        {
            return BlockGameOwnedUdpPorts(steamId);
        }

        public FirewallBlockResult BlockGameOwnedUdpPorts(ulong steamId)
        {
            if (gameProcessId <= 0)
                return FirewallBlockResult.Failed("A selected game process is required for WFP port enforcement.");

            try
            {
                return BlockFlowInternal(steamId, null, GetExclusiveGameUdpSockets());
            }
            catch (Exception ex)
            {
                return FirewallBlockResult.Failed(ex.Message);
            }
        }

        private FirewallBlockResult BlockFlowInternal(ulong steamId, PeerNetworkEndpoint endpoint, IEnumerable<GameUdpSocket> localSockets)
        {
            if (disposed)
                return FirewallBlockResult.Failed("The WFP flow service has been disposed.");
            if (endpoint != null && endpoint.Port == 0)
                return FirewallBlockResult.Failed("The flow has no valid remote UDP endpoint.");
            if (endpoint != null && endpoint.Address.AddressFamily != AddressFamily.InterNetwork && endpoint.Address.AddressFamily != AddressFamily.InterNetworkV6)
                return FirewallBlockResult.Failed("Only IPv4 and IPv6 endpoints are supported.");

            GameUdpSocket[] sockets = (localSockets ?? Enumerable.Empty<GameUdpSocket>())
                .Where(socket => socket.LocalPort != 0)
                .Where(socket => endpoint == null || socket.AddressFamily == endpoint.Address.AddressFamily)
                .GroupBy(socket => new { socket.AddressFamily, socket.LocalPort })
                .Select(group => group.First())
                .ToArray();
            if (sockets.Length == 0)
                return FirewallBlockResult.Failed("No local UDP ports were supplied for the flow.");

            if (!peerFilters.TryGetValue(steamId, out PeerFilterSet peerFilterSet))
                peerFilterSet = new PeerFilterSet();

            var created = new List<ulong>();
            var createdScopes = new List<string>();
            try
            {
                foreach (GameUdpSocket socket in sockets)
                {
                    string scope = BuildScope(endpoint, socket);
                    if (peerFilterSet.Scopes.Contains(scope))
                        continue;

                    created.Add(AddFilter(steamId, endpoint, socket, false));
                    created.Add(AddFilter(steamId, endpoint, socket, true));
                    createdScopes.Add(scope);
                }

                if (createdScopes.Count == 0)
                    return FirewallBlockResult.Ok();

                peerFilterSet.FilterIds.AddRange(created);
                foreach (string scope in createdScopes)
                    peerFilterSet.Scopes.Add(scope);
                peerFilters[steamId] = peerFilterSet;
                return FirewallBlockResult.Ok();
            }
            catch (Exception ex)
            {
                foreach (ulong id in created)
                    TryDeleteFilter(id);
                return FirewallBlockResult.Failed(ex.Message);
            }
        }

        public void Remove(ulong steamId)
        {
            if (!peerFilters.TryGetValue(steamId, out PeerFilterSet peerFilterSet))
                return;

            foreach (ulong id in peerFilterSet.FilterIds)
                TryDeleteFilter(id);
            peerFilters.Remove(steamId);
        }

        public void RemoveAll()
        {
            foreach (ulong steamId in peerFilters.Keys.ToArray())
                Remove(steamId);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            RemoveAll();
            try
            {
                Guid key = subLayerKey;
                FwpmSubLayerDeleteByKey0(engine, ref key);
            }
            catch { }
            try { FwpmEngineClose0(engine); } catch { }
        }

        private ulong AddFilter(ulong steamId, PeerNetworkEndpoint endpoint, GameUdpSocket socket, bool inbound)
        {
            var conditions = new List<FwpmFilterCondition0>
            {
                CreateScalarCondition(ConditionIpProtocol, FwpDataTypeUint8, 17),
                CreateScalarCondition(ConditionIpLocalPort, FwpDataTypeUint16, socket.LocalPort)
            };

            if (endpoint != null)
            {
                conditions.Add(CreateAddressCondition(endpoint.Address));
                conditions.Add(CreateScalarCondition(ConditionIpRemotePort, FwpDataTypeUint16, endpoint.Port));
            }

            int conditionSize = Marshal.SizeOf(typeof(FwpmFilterCondition0));
            IntPtr conditionMemory = Marshal.AllocHGlobal(conditionSize * conditions.Count);
            IntPtr displayName = Marshal.StringToHGlobalUni(
                $"SteamP2PInfo-WFP-{steamId}-{(inbound ? "in" : "out")}-{socket.LocalPort}-{(endpoint == null ? "any" : "flow")}");

            try
            {
                for (int i = 0; i < conditions.Count; i++)
                    Marshal.StructureToPtr(conditions[i], IntPtr.Add(conditionMemory, i * conditionSize), false);

                var filter = new FwpmFilter0
                {
                    filterKey = Guid.NewGuid(),
                    displayData = new FwpmDisplayData { name = displayName },
                    layerKey = GetTransportLayer(socket.AddressFamily, inbound),
                    subLayerKey = subLayerKey,
                    // FWP_UINT8 is a weight-range index, not an arbitrary byte.
                    // The documented range is 0 through 15; use the highest range
                    // so this block wins within our dedicated sublayer.
                    weight = new FwpValue { type = FwpDataTypeUint8, value = new FwpValueUnion { uint8 = 15 } },
                    numFilterConditions = (uint)conditions.Count,
                    filterCondition = conditionMemory,
                    action = new FwpAction { type = FwpActionBlock }
                };

                ThrowIfFailed(
                    FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out ulong filterId),
                    "add WFP flow filter");
                return filterId;
            }
            finally
            {
                foreach (FwpmFilterCondition0 condition in conditions)
                {
                    if (condition.conditionValue.value.pointer != IntPtr.Zero &&
                        condition.conditionValue.type == FwpDataTypeByteArray16)
                        Marshal.FreeHGlobal(condition.conditionValue.value.pointer);
                }

                Marshal.FreeHGlobal(conditionMemory);
                Marshal.FreeHGlobal(displayName);
            }
        }

        private static FwpmFilterCondition0 CreateScalarCondition(Guid fieldKey, uint type, ushort value)
        {
            return new FwpmFilterCondition0
            {
                fieldKey = fieldKey,
                matchType = FwpMatchEqual,
                conditionValue = new FwpConditionValue
                {
                    type = type,
                    value = new FwpValueUnion { uint16 = value }
                }
            };
        }

        private static FwpmFilterCondition0 CreateScalarCondition(Guid fieldKey, uint type, byte value)
        {
            return new FwpmFilterCondition0
            {
                fieldKey = fieldKey,
                matchType = FwpMatchEqual,
                conditionValue = new FwpConditionValue
                {
                    type = type,
                    value = new FwpValueUnion { uint8 = value }
                }
            };
        }

        private static FwpmFilterCondition0 CreateAddressCondition(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return new FwpmFilterCondition0
                {
                    fieldKey = ConditionIpRemoteAddress,
                    matchType = FwpMatchEqual,
                    conditionValue = new FwpConditionValue
                    {
                        type = FwpDataTypeUint32,
                        // WFP expects FWP_UINT32 IPv4 values in host byte order.
                        value = new FwpValueUnion
                        {
                            uint32 = unchecked((uint)IPAddress.NetworkToHostOrder(
                                unchecked((int)BitConverter.ToUInt32(bytes, 0))))
                        }
                    }
                };
            }

            IntPtr bytesMemory = Marshal.AllocHGlobal(16);
            Marshal.Copy(bytes, 0, bytesMemory, 16);
            return new FwpmFilterCondition0
            {
                fieldKey = ConditionIpRemoteAddress,
                matchType = FwpMatchEqual,
                conditionValue = new FwpConditionValue
                {
                    type = FwpDataTypeByteArray16,
                    value = new FwpValueUnion { pointer = bytesMemory }
                }
            };
        }

        private IEnumerable<GameUdpSocket> GetExclusiveGameUdpSockets()
        {
            return GetExclusiveUdpSockets(gameProcessId);
        }

        private IEnumerable<GameUdpSocket> GetExclusiveUdpSockets(int owningProcessId)
        {
            var rows = new List<UdpOwnerRow>();
            rows.AddRange(ReadUdpTable(AddressFamily.InterNetwork));
            rows.AddRange(ReadUdpTable(AddressFamily.InterNetworkV6));

            return rows
                .GroupBy(row => new { row.AddressFamily, row.LocalPort })
                .Where(group => group.Any(row => row.OwningProcessId == owningProcessId)
                    && group.All(row => row.OwningProcessId == owningProcessId))
                .Select(group => new GameUdpSocket(group.Key.AddressFamily, group.Key.LocalPort))
                .Where(socket => socket.LocalPort != 0)
                .OrderBy(socket => socket.AddressFamily)
                .ThenBy(socket => socket.LocalPort)
                .ToArray();
        }

        private GameUdpSocket[] GetMatchingSteamSockets(
            PeerNetworkEndpoint endpoint,
            IEnumerable<ETWPingMonitor.ObservedUdpFlow> observedFlows)
        {
            var sockets = new List<GameUdpSocket>();
            foreach (IGrouping<int, ETWPingMonitor.ObservedUdpFlow> processGroup in observedFlows.GroupBy(flow => flow.ProcessId))
            {
                if (!IsSteamProcess(processGroup.Key))
                    continue;

                var observedPorts = new HashSet<ushort>(processGroup.Select(flow => flow.LocalPort));
                sockets.AddRange(GetExclusiveUdpSockets(processGroup.Key)
                    .Where(socket => socket.AddressFamily == endpoint.Address.AddressFamily)
                    .Where(socket => observedPorts.Contains(socket.LocalPort)));
            }

            return sockets
                .GroupBy(socket => new { socket.AddressFamily, socket.LocalPort })
                .Select(group => group.First())
                .ToArray();
        }

        private static bool IsSteamProcess(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                    return string.Equals(process.ProcessName, "steam", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<UdpOwnerRow> ReadUdpTable(AddressFamily addressFamily)
        {
            const uint ErrorSuccess = 0;
            const uint ErrorInsufficientBuffer = 122;
            const int UdpTableOwnerPid = 1;
            const int Ipv4RowSize = 12;
            const int Ipv6RowSize = 28;
            int bufferSize = 0;

            uint status = GetExtendedUdpTable(
                IntPtr.Zero,
                ref bufferSize,
                false,
                (int)addressFamily,
                UdpTableOwnerPid,
                0);

            if (status != ErrorInsufficientBuffer && status != ErrorSuccess)
                ThrowIfFailed(status, $"query {(addressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} UDP ownership");
            if (bufferSize <= 0)
                return Enumerable.Empty<UdpOwnerRow>();

            IntPtr table = Marshal.AllocHGlobal(bufferSize);
            try
            {
                status = GetExtendedUdpTable(
                    table,
                    ref bufferSize,
                    false,
                    (int)addressFamily,
                    UdpTableOwnerPid,
                    0);
                ThrowIfFailed(status, $"read {(addressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} UDP ownership");

                int rowCount = Marshal.ReadInt32(table);
                int rowSize = addressFamily == AddressFamily.InterNetwork ? Ipv4RowSize : Ipv6RowSize;
                var rows = new List<UdpOwnerRow>(rowCount);
                for (int i = 0; i < rowCount; i++)
                {
                    IntPtr row = IntPtr.Add(table, sizeof(uint) + i * rowSize);
                    int portOffset = addressFamily == AddressFamily.InterNetwork ? sizeof(uint) : 20;
                    int processOffset = addressFamily == AddressFamily.InterNetwork ? 8 : 24;
                    uint rawPort = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(row, portOffset)));
                    ushort port = unchecked((ushort)IPAddress.NetworkToHostOrder((short)(rawPort & ushort.MaxValue)));
                    uint processId = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(row, processOffset)));
                    rows.Add(new UdpOwnerRow(addressFamily, port, processId));
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        private sealed class UdpOwnerRow
        {
            public AddressFamily AddressFamily { get; }
            public ushort LocalPort { get; }
            public uint OwningProcessId { get; }

            public UdpOwnerRow(AddressFamily addressFamily, ushort localPort, uint owningProcessId)
            {
                AddressFamily = addressFamily;
                LocalPort = localPort;
                OwningProcessId = owningProcessId;
            }
        }

        private sealed class GameUdpSocket
        {
            public AddressFamily AddressFamily { get; }
            public ushort LocalPort { get; }

            public GameUdpSocket(AddressFamily addressFamily, ushort localPort)
            {
                AddressFamily = addressFamily;
                LocalPort = localPort;
            }
        }

        private sealed class PeerFilterSet
        {
            public List<ulong> FilterIds { get; } = new List<ulong>();
            public HashSet<string> Scopes { get; } = new HashSet<string>(StringComparer.Ordinal);
        }

        private static string BuildScope(PeerNetworkEndpoint endpoint, GameUdpSocket socket)
        {
            return endpoint == null
                ? $"{socket.AddressFamily}:{socket.LocalPort}:any"
                : $"{socket.AddressFamily}:{socket.LocalPort}:{endpoint.Address}:{endpoint.Port}";
        }

        private static Guid GetTransportLayer(AddressFamily family, bool inbound)
        {
            if (family == AddressFamily.InterNetwork)
                return inbound ? LayerInboundTransportV4 : LayerOutboundTransportV4;
            return inbound ? LayerInboundTransportV6 : LayerOutboundTransportV6;
        }

        private void TryDeleteFilter(ulong filterId)
        {
            try { FwpmFilterDeleteById0(engine, filterId); } catch { }
        }

        private static void ThrowIfFailed(uint status, string operation)
        {
            if (status == 0)
                return;

            string systemMessage;
            try { systemMessage = new Win32Exception(unchecked((int)status)).Message; }
            catch { systemMessage = "unknown system error"; }
            throw new InvalidOperationException(
                $"{operation} failed with WFP status 0x{status:X8}: {systemMessage}");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FwpmDisplayData
        {
            public IntPtr name;
            public IntPtr description;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FwpmSession0
        {
            public Guid sessionKey;
            public FwpmDisplayData displayData;
            public uint flags;
            public uint txnWaitTimeoutInMSec;
            public uint processId;
            public IntPtr sid;
            public IntPtr username;
            public int kernelMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FwpmSubLayer0
        {
            public Guid subLayerKey;
            public FwpmDisplayData displayData;
            public uint flags;
            public IntPtr providerKey;
            public FwpByteBlob providerData;
            public ushort weight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FwpmFilter0
        {
            public Guid filterKey;
            public FwpmDisplayData displayData;
            public uint flags;
            public IntPtr providerKey;
            public FwpByteBlob providerData;
            public Guid layerKey;
            public Guid subLayerKey;
            public FwpValue weight;
            public uint numFilterConditions;
            public IntPtr filterCondition;
            public FwpAction action;
            public ulong rawContext;
            public IntPtr providerContextKey;
            public IntPtr reserved;
            public ulong filterId;
            public FwpValue effectiveWeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FwpmFilterCondition0
        {
            public Guid fieldKey;
            public uint matchType;
            public FwpConditionValue conditionValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FwpConditionValue
        {
            public uint type;
            public FwpValueUnion value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FwpValue
        {
            public uint type;
            public FwpValueUnion value;
        }

        [StructLayout(LayoutKind.Explicit, Size = 8)]
        private struct FwpValueUnion
        {
            [FieldOffset(0)] public byte uint8;
            [FieldOffset(0)] public ushort uint16;
            [FieldOffset(0)] public uint uint32;
            [FieldOffset(0)] public IntPtr pointer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FwpAction
        {
            public uint type;
            public Guid calloutKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FwpByteBlob
        {
            public uint size;
            public IntPtr data;
        }

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern uint FwpmEngineOpen0(
            string serverName,
            uint authnService,
            IntPtr authIdentity,
            ref FwpmSession0 session,
            out IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmEngineClose0(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmSubLayerAdd0(
            IntPtr engineHandle,
            ref FwpmSubLayer0 subLayer,
            IntPtr securityDescriptor);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid subLayerKey);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmFilterAdd0(
            IntPtr engineHandle,
            ref FwpmFilter0 filter,
            IntPtr securityDescriptor,
            out ulong filterId);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong filterId);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(
            IntPtr udpTable,
            ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool order,
            int addressFamily,
            int tableClass,
            uint reserved);
    }
}
