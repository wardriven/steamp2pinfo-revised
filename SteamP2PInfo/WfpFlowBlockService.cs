using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SteamP2PInfo.Config;

namespace SteamP2PInfo
{
    /// <summary>
    /// User-mode WFP enforcement for Steam P2P traffic.
    ///
    /// Automatic high-ping enforcement uses observed, endpoint-scoped UDP
    /// transport tuples. Strict manual enforcement uses ALE application identity
    /// to block all UDP for the attached game and steam.exe before their logical
    /// peer sessions are closed. Both policies use a dynamic WFP session, so
    /// filters are removed when the owning engine session closes.
    /// </summary>
    internal sealed class WfpFlowBlockService : IFirewallBlockService
    {
        internal static readonly TimeSpan VanishedSocketEvidenceLifetime = TimeSpan.FromSeconds(1);

        private const uint FwpSessionFlagDynamic = 0x00000001;
        private const uint FwpDataTypeUint8 = 1;
        private const uint FwpDataTypeUint16 = 2;
        private const uint FwpDataTypeUint32 = 3;
        private const uint FwpDataTypeByteArray16 = 11;
        private const uint FwpDataTypeByteBlob = 12;
        private const uint FwpMatchEqual = 0;
        private const uint FwpEFilterNotFound = 0x80320003;
        // FWP_ACTION_BLOCK is the low-order action code combined with the
        // required FWP_ACTION_FLAG_TERMINATING flag (0x00001000).
        private const uint FwpActionBlock = 0x00001001;

        private static readonly Guid ConditionIpProtocol = new Guid("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
        private static readonly Guid ConditionIpLocalPort = new Guid("0c1ba1af-5765-453f-af22-a8f791ac775b");
        private static readonly Guid ConditionIpRemoteAddress = new Guid("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
        private static readonly Guid ConditionIpRemotePort = new Guid("c35a604d-d22b-4e1a-91b4-68f674ee674b");
        private static readonly Guid ConditionAleAppId = new Guid("d78e1e87-8644-4ea5-9437-d809ecefc971");

        private static readonly Guid LayerInboundTransportV4 = new Guid("5926dfc8-e3cf-4426-a283-dc393f5d0f9d");
        private static readonly Guid LayerInboundTransportV6 = new Guid("634a869f-fc23-4b90-b0c1-bf620a36ae6f");
        private static readonly Guid LayerOutboundTransportV4 = new Guid("09e61aea-d214-46e2-9b21-b26b0b2f28c8");
        private static readonly Guid LayerOutboundTransportV6 = new Guid("e1735bde-013f-4655-b351-a49e15762df0");
        private static readonly Guid LayerAleAuthRecvAcceptV4 = new Guid("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
        private static readonly Guid LayerAleAuthRecvAcceptV6 = new Guid("a3b42c97-9f04-4672-b87e-cee9c483257f");
        private static readonly Guid LayerAleAuthConnectV4 = new Guid("c38d57d1-05a7-4c33-904f-7fbceee60e82");
        private static readonly Guid LayerAleAuthConnectV6 = new Guid("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");

        private readonly IntPtr engine;
        private readonly Guid subLayerKey = Guid.NewGuid();
        private readonly int gameProcessId;
        private string gameExecutablePath;
        private readonly string[] steamExecutablePaths;
        private readonly Dictionary<ulong, PeerFilterSet> peerFilters = new Dictionary<ulong, PeerFilterSet>();
        private readonly List<ulong> manualReconnectFilterIds = new List<ulong>();
        private bool disposed;

        public WfpFlowBlockService() : this(0)
        {
        }

        public WfpFlowBlockService(int gameProcessId)
        {
            if (!HasExpectedNativeInteropLayout())
                throw new PlatformNotSupportedException("The WFP native interop layout is not valid for this process architecture.");

            this.gameProcessId = gameProcessId;
            gameExecutablePath = TryGetProcessExecutablePath(gameProcessId);
            steamExecutablePaths = FindSteamExecutablePaths();
            DiagnosticLogger.Write("FIREWALL", "Attempting to open a dynamic WFP session for game PID " + gameProcessId + ".");
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
            DiagnosticLogger.Write("FIREWALL", "Dynamic WFP session and sublayer created successfully.");
        }

        internal static bool HasExpectedNativeInteropLayout()
        {
            return IntPtr.Size == 8
                && Marshal.SizeOf(typeof(FwpByteBlob)) == 16
                && Marshal.SizeOf(typeof(FwpValue)) == 16
                && Marshal.SizeOf(typeof(FwpConditionValue)) == 16
                && Marshal.SizeOf(typeof(FwpAction)) == 20
                && Marshal.SizeOf(typeof(FwpFilterContextUnion)) == 16
                && Marshal.SizeOf(typeof(FwpmFilterCondition0)) == 40
                && Marshal.SizeOf(typeof(FwpmSession0)) == 72
                && Marshal.SizeOf(typeof(FwpmSubLayer0)) == 72
                && Marshal.SizeOf(typeof(FwpmFilter0)) == 200;
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

                GameUdpSocket[] matchingSteamSockets = GetMatchingSteamSockets(
                    endpoint,
                    observedFlows,
                    out bool usedVanishedSocketEvidence);
                if (matchingSteamSockets.Length == 0)
                {
                    string observedDescription = ETWPingMonitor.DescribeRecentUdpFlows(endpoint);
                    return FirewallBlockResult.Failed(
                        $"No verified steam.exe UDP flow is currently available for {endpoint}. " +
                        $"Observed packets for that endpoint: {observedDescription}.");
                }

                FirewallBlockResult steamResult = BlockFlowInternal(steamId, endpoint, matchingSteamSockets);
                return steamResult.Success
                    ? FirewallBlockResult.Ok(
                        usedVanishedSocketEvidence
                            ? "fresh ETW-verified Steam-owned UDP flow"
                            : "exact Steam-owned UDP flow")
                    : steamResult;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("FIREWALL ERROR", ex, "Failed while identifying and blocking the peer's exact UDP flow.");
                return FirewallBlockResult.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Atomically blocks UDP connection creation and reauthorisation for both
        /// the attached game and steam.exe. Unlike an exact transport tuple, this
        /// ALE application-identity guard also covers a replacement socket, relay,
        /// address, or port that did not exist when the hotkey was pressed.
        /// </summary>
        public FirewallBlockResult BlockManualReconnect()
        {
            if (disposed)
                return FirewallBlockResult.Failed("The WFP flow service has been disposed.");
            if (manualReconnectFilterIds.Count > 0)
                return FirewallBlockResult.Ok("application-scoped game and Steam UDP reconnect lock");

            string currentGameExecutablePath = gameExecutablePath;
            if (string.IsNullOrWhiteSpace(currentGameExecutablePath))
            {
                currentGameExecutablePath = TryGetProcessExecutablePath(gameProcessId);
                if (!string.IsNullOrWhiteSpace(currentGameExecutablePath))
                {
                    gameExecutablePath = currentGameExecutablePath;
                    DiagnosticLogger.Write(
                        "FIREWALL",
                        "Recovered the selected game's executable path while activating the reconnect lock.");
                }
            }

            if (string.IsNullOrWhiteSpace(currentGameExecutablePath))
                return FirewallBlockResult.Failed("The selected game's executable path could not be resolved.");

            string[] currentSteamExecutablePaths = steamExecutablePaths.Length > 0
                ? steamExecutablePaths
                : FindSteamExecutablePaths();
            if (currentSteamExecutablePaths.Length == 0)
                return FirewallBlockResult.Failed("steam.exe is not running or its executable path could not be resolved.");

            string[] applicationPaths = new[] { currentGameExecutablePath }
                .Concat(currentSteamExecutablePaths)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var applicationIds = new List<ApplicationIdentity>(applicationPaths.Length);
            var createdFilterIds = new List<ulong>(applicationPaths.Length * 4);
            bool transactionStarted = false;

            try
            {
                foreach (string applicationPath in applicationPaths)
                {
                    ThrowIfFailed(
                        FwpmGetAppIdFromFileName0(applicationPath, out IntPtr applicationId),
                        "resolve WFP application identity for " + applicationPath);
                    applicationIds.Add(new ApplicationIdentity(applicationPath, applicationId));
                }

                ThrowIfFailed(FwpmTransactionBegin0(engine, 0), "begin atomic WFP reconnect-lock transaction");
                transactionStarted = true;

                foreach (ApplicationIdentity application in applicationIds)
                {
                    createdFilterIds.Add(AddApplicationUdpFilter(
                        application,
                        LayerAleAuthConnectV4,
                        "outbound IPv4"));
                    createdFilterIds.Add(AddApplicationUdpFilter(
                        application,
                        LayerAleAuthConnectV6,
                        "outbound IPv6"));
                    createdFilterIds.Add(AddApplicationUdpFilter(
                        application,
                        LayerAleAuthRecvAcceptV4,
                        "inbound IPv4"));
                    createdFilterIds.Add(AddApplicationUdpFilter(
                        application,
                        LayerAleAuthRecvAcceptV6,
                        "inbound IPv6"));
                }

                ThrowIfFailed(FwpmTransactionCommit0(engine), "commit atomic WFP reconnect-lock transaction");
                transactionStarted = false;
                manualReconnectFilterIds.AddRange(createdFilterIds);
                DiagnosticLogger.Write(
                    "FIREWALL",
                    string.Format(
                        "Activated the persistent manual UDP reconnect lock with {0} ALE filter(s) for: {1}.",
                        createdFilterIds.Count,
                        string.Join(", ", applicationPaths)));
                return FirewallBlockResult.Ok("application-scoped game and Steam UDP reconnect lock");
            }
            catch (Exception ex)
            {
                if (transactionStarted)
                {
                    try { FwpmTransactionAbort0(engine); } catch { }
                }

                DiagnosticLogger.WriteException(
                    "FIREWALL ERROR",
                    ex,
                    "Failed to activate the atomic game-and-Steam UDP reconnect lock.");
                return FirewallBlockResult.Failed(ex.Message);
            }
            finally
            {
                foreach (ApplicationIdentity application in applicationIds)
                {
                    IntPtr applicationId = application.ApplicationId;
                    if (applicationId != IntPtr.Zero)
                        FwpmFreeMemory0(ref applicationId);
                }
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
                DiagnosticLogger.WriteException("FIREWALL ERROR", ex, "Failed while blocking game-owned UDP ports.");
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

            DiagnosticLogger.Write(
                "FIREWALL",
                string.Format(
                    "Attempting WFP block for peer {0}; endpoint {1}; local UDP socket(s): {2}.",
                    steamId,
                    endpoint == null ? "any" : endpoint.ToString(),
                    string.Join(", ", sockets.Select(socket => socket.AddressFamily + ":" + socket.LocalPort))));

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
                DiagnosticLogger.Write(
                    "FIREWALL",
                    string.Format("Created {0} WFP filter(s) successfully for peer {1}: {2}.", created.Count, steamId, string.Join(", ", created)));
                return FirewallBlockResult.Ok();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("FIREWALL ERROR", ex, "Failed to create WFP filters for peer " + steamId + ".");
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

        /// <summary>
        /// Removes only the application-scoped manual reconnect policy. Deleting
        /// all of its filters in one WFP transaction prevents a partial unlock
        /// from being reported as successful.
        /// </summary>
        public FirewallBlockResult RemoveManualReconnect()
        {
            if (manualReconnectFilterIds.Count == 0)
                return FirewallBlockResult.Ok("manual reconnect lock already inactive");

            bool transactionStarted = false;
            try
            {
                ThrowIfFailed(
                    FwpmTransactionBegin0(engine, 0),
                    "begin atomic WFP reconnect-lock removal transaction");
                transactionStarted = true;

                foreach (ulong filterId in manualReconnectFilterIds)
                {
                    uint status = FwpmFilterDeleteById0(engine, filterId);
                    if (status != 0 && status != FwpEFilterNotFound)
                    {
                        ThrowIfFailed(
                            status,
                            "remove WFP reconnect-lock filter " + filterId);
                    }
                }

                ThrowIfFailed(
                    FwpmTransactionCommit0(engine),
                    "commit atomic WFP reconnect-lock removal transaction");
                transactionStarted = false;

                int removedFilterCount = manualReconnectFilterIds.Count;
                manualReconnectFilterIds.Clear();
                DiagnosticLogger.Write(
                    "FIREWALL",
                    string.Format(
                        "Deactivated the manual UDP reconnect lock by removing {0} ALE filter(s).",
                        removedFilterCount));
                return FirewallBlockResult.Ok("manual reconnect lock removed");
            }
            catch (Exception ex)
            {
                if (transactionStarted)
                {
                    try { FwpmTransactionAbort0(engine); } catch { }
                }

                DiagnosticLogger.WriteException(
                    "FIREWALL ERROR",
                    ex,
                    "Failed to remove the atomic game-and-Steam UDP reconnect lock.");
                return FirewallBlockResult.Failed(ex.Message);
            }
        }

        public void RemoveAll()
        {
            RemoveManualReconnect();

            foreach (ulong steamId in peerFilters.Keys.ToArray())
                Remove(steamId);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            DiagnosticLogger.Write("FIREWALL", "Disposing the dynamic WFP session and all remaining filters.");
            RemoveAll();
            try
            {
                Guid key = subLayerKey;
                FwpmSubLayerDeleteByKey0(engine, ref key);
            }
            catch { }
            try { FwpmEngineClose0(engine); } catch { }
        }

        private ulong AddApplicationUdpFilter(
            ApplicationIdentity application,
            Guid layerKey,
            string layerDescription)
        {
            DiagnosticLogger.Write(
                "FIREWALL",
                string.Format(
                    "Preparing {0} UDP reconnect-lock filter for {1}.",
                    layerDescription,
                    application.ExecutablePath));

            var conditions = new[]
            {
                CreateScalarCondition(ConditionIpProtocol, FwpDataTypeUint8, 17),
                new FwpmFilterCondition0
                {
                    fieldKey = ConditionAleAppId,
                    matchType = FwpMatchEqual,
                    conditionValue = new FwpConditionValue
                    {
                        type = FwpDataTypeByteBlob,
                        value = new FwpValueUnion { pointer = application.ApplicationId }
                    }
                }
            };

            int conditionSize = Marshal.SizeOf(typeof(FwpmFilterCondition0));
            IntPtr conditionMemory = Marshal.AllocHGlobal(conditionSize * conditions.Length);
            IntPtr displayName = Marshal.StringToHGlobalUni(
                string.Format(
                    "SteamP2PInfo strict UDP lock - {0} - {1}",
                    Path.GetFileName(application.ExecutablePath),
                    layerDescription));

            try
            {
                for (int i = 0; i < conditions.Length; i++)
                    Marshal.StructureToPtr(conditions[i], IntPtr.Add(conditionMemory, i * conditionSize), false);

                var filter = new FwpmFilter0
                {
                    filterKey = Guid.NewGuid(),
                    displayData = new FwpmDisplayData { name = displayName },
                    layerKey = layerKey,
                    subLayerKey = subLayerKey,
                    weight = new FwpValue
                    {
                        type = FwpDataTypeUint8,
                        value = new FwpValueUnion { uint8 = 15 }
                    },
                    numFilterConditions = (uint)conditions.Length,
                    filterCondition = conditionMemory,
                    action = new FwpAction { type = FwpActionBlock }
                };

                ThrowIfFailed(
                    FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out ulong filterId),
                    "add WFP application UDP reconnect-lock filter");
                return filterId;
            }
            finally
            {
                Marshal.FreeHGlobal(conditionMemory);
                Marshal.FreeHGlobal(displayName);
            }
        }

        private ulong AddFilter(ulong steamId, PeerNetworkEndpoint endpoint, GameUdpSocket socket, bool inbound)
        {
            string direction = inbound ? "inbound" : "outbound";
            DiagnosticLogger.Write(
                "FIREWALL",
                string.Format(
                    "Attempting to create {0} WFP filter for peer {1}; local UDP {2}; remote {3}.",
                    direction,
                    steamId,
                    socket.LocalPort,
                    endpoint == null ? "any" : endpoint.ToString()));
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
                DiagnosticLogger.Write(
                    "FIREWALL",
                    string.Format("Created {0} WFP filter {1} successfully for peer {2}.", direction, filterId, steamId));
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
            IEnumerable<ETWPingMonitor.ObservedUdpFlow> observedFlows,
            out bool usedVanishedSocketEvidence)
        {
            ETWPingMonitor.ObservedUdpFlow[] observations = (observedFlows ?? Enumerable.Empty<ETWPingMonitor.ObservedUdpFlow>())
                .Where(flow => flow != null)
                .ToArray();
            var ownerRows = new List<UdpOwnerRow>();
            ownerRows.AddRange(ReadUdpTable(AddressFamily.InterNetwork));
            ownerRows.AddRange(ReadUdpTable(AddressFamily.InterNetworkV6));
            DateTime nowUtc = DateTime.UtcNow;

            GameUdpSocket[] sockets = observations
                .Where(flow => CanUseObservedSteamFlow(
                    flow,
                    observations,
                    ownerRows
                        .Where(row => row.AddressFamily == endpoint.Address.AddressFamily)
                        .Where(row => row.LocalPort == flow.LocalPort)
                        .Select(row => row.OwningProcessId),
                    nowUtc,
                    IsSteamProcess))
                .Select(flow => new GameUdpSocket(endpoint.Address.AddressFamily, flow.LocalPort))
                .GroupBy(socket => new { socket.AddressFamily, socket.LocalPort })
                .Select(group => group.First())
                .ToArray();

            GameUdpSocket[] vanishedSockets = sockets.Where(socket =>
                !ownerRows.Any(row =>
                    row.AddressFamily == socket.AddressFamily &&
                    row.LocalPort == socket.LocalPort))
                .ToArray();
            usedVanishedSocketEvidence = vanishedSockets.Length > 0;

            foreach (GameUdpSocket socket in vanishedSockets)
            {
                DiagnosticLogger.Write(
                    "FIREWALL",
                    $"Accepting fresh steam.exe ETW evidence for {endpoint} on vanished local UDP socket {socket.LocalPort}; the WFP filter remains scoped to that exact tuple.");
            }

            return sockets;
        }

        /// <summary>
        /// Verifies a Steam-owned exact-flow observation without widening its
        /// local-port/remote-address/remote-port scope. A live socket must still
        /// own its port exclusively. If the transient socket has already vanished
        /// from the UDP table, only sub-second ETW evidence is accepted.
        /// </summary>
        internal static bool CanUseObservedSteamFlow(
            ETWPingMonitor.ObservedUdpFlow candidate,
            IEnumerable<ETWPingMonitor.ObservedUdpFlow> endpointObservations,
            IEnumerable<uint> currentPortOwnerProcessIds,
            DateTime nowUtc,
            Func<int, bool> isSteamProcess)
        {
            if (candidate == null ||
                candidate.ProcessId <= 0 ||
                candidate.LocalPort == 0 ||
                isSteamProcess == null ||
                !isSteamProcess(candidate.ProcessId))
                return false;

            ETWPingMonitor.ObservedUdpFlow[] observations =
                (endpointObservations ?? Enumerable.Empty<ETWPingMonitor.ObservedUdpFlow>())
                    .Where(flow => flow != null)
                    .ToArray();
            bool hasFreshConflictingObservation = observations.Any(flow =>
                flow.LocalPort == candidate.LocalPort &&
                flow.ProcessId != candidate.ProcessId &&
                IsFreshVanishedSocketEvidence(flow.LastObservedUtc, nowUtc));
            if (hasFreshConflictingObservation)
                return false;

            uint[] currentOwners = (currentPortOwnerProcessIds ?? Enumerable.Empty<uint>())
                .Distinct()
                .ToArray();
            uint candidateProcessId = unchecked((uint)candidate.ProcessId);
            if (currentOwners.Length > 0)
                return currentOwners.All(processId => processId == candidateProcessId);

            return IsFreshVanishedSocketEvidence(candidate.LastObservedUtc, nowUtc);
        }

        private static bool IsFreshVanishedSocketEvidence(DateTime observedUtc, DateTime nowUtc)
        {
            TimeSpan age = nowUtc - observedUtc;
            return age >= TimeSpan.Zero && age <= VanishedSocketEvidenceLifetime;
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

        internal static string TryGetProcessExecutablePath(int processId)
        {
            if (processId <= 0)
                return null;

            Exception limitedQueryFailure = null;
            try
            {
                string path = WinAPI.Kernel32.GetProcessImagePath(processId);
                if (!string.IsNullOrWhiteSpace(path))
                    return Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                limitedQueryFailure = ex;
            }

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    string path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path))
                        return Path.GetFullPath(path);
                }
            }
            catch (Exception ex)
            {
                Exception pathFailure = limitedQueryFailure == null
                    ? ex
                    : new AggregateException(
                        "Both limited-information and module-based process path queries failed.",
                        limitedQueryFailure,
                        ex);
                DiagnosticLogger.WriteException(
                    "FIREWALL ERROR",
                    pathFailure,
                    "Could not resolve executable path for process " + processId + ".");
                return null;
            }

            if (limitedQueryFailure != null)
            {
                DiagnosticLogger.WriteException(
                    "FIREWALL ERROR",
                    limitedQueryFailure,
                    "The limited-information path query failed and the module-based fallback returned no path for process " + processId + ".");
            }

            return null;
        }

        private static string[] FindSteamExecutablePaths()
        {
            var paths = new List<string>();
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("steam");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException(
                    "FIREWALL ERROR",
                    ex,
                    "Could not enumerate steam.exe processes for the strict reconnect lock.");
                return Array.Empty<string>();
            }

            foreach (Process process in processes)
            {
                try
                {
                    string path = TryGetProcessExecutablePath(process.Id);
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(path);
                }
                finally
                {
                    process.Dispose();
                }
            }

            return paths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
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

        private sealed class ApplicationIdentity
        {
            public string ExecutablePath { get; }
            public IntPtr ApplicationId { get; }

            public ApplicationIdentity(string executablePath, IntPtr applicationId)
            {
                ExecutablePath = executablePath;
                ApplicationId = applicationId;
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
            try
            {
                uint status = FwpmFilterDeleteById0(engine, filterId);
                if (status == 0)
                    DiagnosticLogger.Write("FIREWALL", "Removed WFP filter " + filterId + " successfully.");
                else
                    DiagnosticLogger.Write("FIREWALL ERROR", string.Format("Could not remove WFP filter {0}; status 0x{1:X8}.", filterId, status));
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("FIREWALL ERROR", ex, "Could not remove WFP filter " + filterId + ".");
            }
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
            public FwpFilterContextUnion context;
            public IntPtr reserved;
            public ulong filterId;
            public FwpValue effectiveWeight;
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct FwpFilterContextUnion
        {
            [FieldOffset(0)] public ulong rawContext;
            [FieldOffset(0)] public Guid providerContextKey;
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

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern uint FwpmGetAppIdFromFileName0(
            string fileName,
            out IntPtr applicationId);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern void FwpmFreeMemory0(ref IntPtr memory);

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
