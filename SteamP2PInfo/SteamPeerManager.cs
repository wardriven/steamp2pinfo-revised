using System.Collections.Generic;
using System.Linq;
using System.IO;
using Steamworks;

using SteamP2PInfo.Config;
using System.Text.RegularExpressions;
using System.Windows;
using System.Diagnostics;
using System;
using System.Reflection;
using System.Threading;

namespace SteamP2PInfo
{
    internal enum PeerRemovalReason
    {
        TransportTimeout,
        AuthSessionEnded,
        LobbyLeft,
        Shutdown
    }

    /// <summary>
    /// Manage a list of active Steam P2P peers. The peers must be in a steam lobby with the current user to be detected.
    /// They will automatically be removed from the list if no packet was sent/recieved for a set amount of time.
    /// </summary>
    static class SteamPeerManager
    {
        private static FileStream fs;
        private static StreamReader sr;
        private static FileSystemWatcher fsWatcher;
        private static bool mustReopenLog = true;
        private static long? lastPosInLog = null;
        private static Stopwatch sw = new Stopwatch();
        private static PeerConnectionHistoryTracker historyTracker;
        private static long updateCycleId;
        private static int updateInProgress;
        private static volatile bool isShuttingDown;

        private static readonly Regex STEAMID3_REGEX = new Regex(@"\[U:1:(?<id>\d+)\]", RegexOptions.Compiled);
        private const long STEAMID64_BASE = 0x0110_0001_0000_0000;

        private const long PEER_TIMEOUT_MS = 5000;

        private static readonly Func<CSteamID, SteamPeerBase>[] PEER_FACTORIES =
            Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsSubclassOf(typeof(SteamPeerBase)))
                .Select(t => new Func<CSteamID, SteamPeerBase>((CSteamID sid) => Activator.CreateInstance(t, sid) as SteamPeerBase))
                .ToArray();
            

        /// <summary>
        /// List of peers mapped by Steam ID.
        /// </summary>
        private static Dictionary<CSteamID, SteamPeerInfo> mPeers = new Dictionary<CSteamID, SteamPeerInfo>();

        public static event Action<ulong, PeerRemovalReason> PeerRemoved;
        public static event Action<ulong> PeerBeginAuthSession;
        public static event Action LobbyLeft;

        public static void Init(PeerConnectionHistoryTracker peerHistoryTracker = null)
        {
            DiagnosticLogger.Write("ACTION", "Initializing Steam peer monitoring from " + Settings.Default.SteamLogPath + ".");
            historyTracker = peerHistoryTracker;
            isShuttingDown = false;
            if (!sw.IsRunning)
                sw.Start();

            fsWatcher = new FileSystemWatcher(Path.GetDirectoryName(Settings.Default.SteamLogPath));
            fsWatcher.Filter = Path.GetFileName(Settings.Default.SteamLogPath);
            fsWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            fsWatcher.Changed += (e, s) => mustReopenLog = true;
            fsWatcher.EnableRaisingEvents = true;
            DiagnosticLogger.Write("ACTION", "Steam peer monitoring initialized.");
        }

        public static void Shutdown()
        {
            DiagnosticLogger.Write("ACTION", "Steam peer monitoring shutdown started.");
            isShuttingDown = true;
            foreach (CSteamID steamId in mPeers.Keys.ToArray())
                RemovePeer(steamId, "SteamP2PInfo is shutting down", PeerRemovalReason.Shutdown);

            sr?.Dispose();
            fs?.Dispose();
            fsWatcher?.Dispose();
            sr = null;
            fs = null;
            fsWatcher = null;
            mustReopenLog = true;
            lastPosInLog = null;
            historyTracker = null;
            DiagnosticLogger.Write("ACTION", "Steam peer monitoring shutdown completed.");
        }

        private static void LogDisconnect(SteamPeerBase peer, CSteamID steamId, string reason)
        {
            if (peer is null)
                Logger.WriteLine($"[PEER DISCONNECT] (https://steamcommunity.com/profiles/{(ulong)steamId}): {reason}");
            else
                Logger.WriteLine($"[PEER DISCONNECT] \"{peer.Name}\" (https://steamcommunity.com/profiles/{(ulong)steamId}): {reason}");
        }

        private static void RemovePeer(CSteamID steamId, string reason, PeerRemovalReason removalReason)
        {
            if (!mPeers.TryGetValue(steamId, out SteamPeerInfo peerInfo))
                return;

            mPeers.Remove(steamId);
            FinalizePeerRemoval(
                peerInfo,
                steamId,
                removalReason,
                historyTracker,
                PeerRemoved,
                () => LogDisconnect(peerInfo.peer, steamId, reason));
        }

        internal static void FinalizePeerRemoval(
            SteamPeerInfo peerInfo,
            CSteamID steamId,
            PeerRemovalReason removalReason,
            PeerConnectionHistoryTracker peerHistoryTracker,
            Action<ulong, PeerRemovalReason> peerRemovedCallback,
            Action disconnectLogAction = null)
        {
            try
            {
                string historyError = null;
                if (peerHistoryTracker != null)
                    peerHistoryTracker.Complete(steamId.m_SteamID, out historyError);
                if (!string.IsNullOrWhiteSpace(historyError))
                    DiagnosticLogger.Write("ERROR", "Could not save connection history for peer " + steamId.m_SteamID + ": " + historyError);
            }
            catch (Exception ex)
            {
                try
                {
                    DiagnosticLogger.WriteException("ERROR", ex, "Could not finalize connection history for peer " + steamId.m_SteamID + ".");
                }
                catch
                {
                }
            }

            try
            {
                disconnectLogAction?.Invoke();
            }
            catch (Exception ex)
            {
                try
                {
                    DiagnosticLogger.WriteException("ERROR", ex, "Could not write the disconnect log for peer " + steamId.m_SteamID + ".");
                }
                catch
                {
                }
            }

            peerInfo?.peer?.Dispose();
            peerRemovedCallback?.Invoke(steamId.m_SteamID, removalReason);
        }

        private static void TryObserveHistory(SteamPeerBase peer, long cycleId)
        {
            try
            {
                historyTracker?.Observe(peer, cycleId);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("ERROR", ex, "Could not observe connection history for peer " + peer?.SteamID.m_SteamID + ".");
            }
        }

        private static CSteamID ExtractUser(string str)
        {
            Match m = STEAMID3_REGEX.Match(str);
            if (m.Success)
            {
                return new CSteamID(ulong.Parse(m.Groups["id"].Value) + STEAMID64_BASE);
            }
            else
            {
                return new CSteamID(0);
            }
        }

        private static SteamPeerBase GetPeer(CSteamID player, long cycleId)
        {
            SteamPeerBase peer = null;
            foreach (var factory in PEER_FACTORIES)
            {
                try
                {
                    peer = factory(player);
                    if (peer.UpdatePeerInfo())
                    {
                        Logger.WriteLine($"[PEER CONNECT] \"{peer.Name}\" (https://steamcommunity.com/profiles/{(ulong)peer.SteamID}) has connected via {peer.ConnectionTypeName}");
                        string endpointDescription = peer.TryGetRemoteEndpoint(out PeerNetworkEndpoint endpoint)
                            ? endpoint.ToString()
                            : "unavailable";
                        DiagnosticLogger.Write(
                            "PEER",
                            string.Format(
                                "Connected peer {0} via {1}; endpoint {2}; ping {3:F1} ms; connection quality {4:F3}.",
                                peer.SteamID.m_SteamID,
                                peer.ConnectionTypeName,
                                endpointDescription,
                                peer.Ping,
                                peer.ConnectionQuality));
                        if (GameConfig.Current.SetPlayedWith)
                        {
                            SteamFriends.SetPlayedWith(player);
                            DiagnosticLogger.Write("ACTION", "Added peer " + peer.SteamID.m_SteamID + " to Steam Recent Players.");
                        }

                        TryObserveHistory(peer, cycleId);
                        return peer;
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.WriteException("ERROR", ex, "Failed to query peer " + player.m_SteamID + ".");
                    peer?.Dispose();
                }
            }
            return null;
        }

        public async static void UpdatePeerList()
        {
            if (isShuttingDown || Interlocked.CompareExchange(ref updateInProgress, 1, 0) != 0)
                return;

            long cycleId = Interlocked.Increment(ref updateCycleId);
            try
            {
                // Make sure we're constantly writing to the IPC log to force Steam to eventually flush
                // This call was chosen because it's not something a game will call often
                // Thus we avoid blowing up the IPC log with dummy calls
                SteamFriends.SendClanChatMessage(new CSteamID(0), "");

                if (mustReopenLog)
                {
                    sr?.Dispose();
                    fs?.Close();
                    fs?.Dispose();

                    try
                    {
                        fs = new FileStream(Settings.Default.SteamLogPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
                        sr = new StreamReader(fs);
                        // If the file had to be reopened, read from the last position we were at before
                        if (lastPosInLog is null)
                            fs.Seek(0, SeekOrigin.End);
                        else
                            fs.Seek((long)lastPosInLog, SeekOrigin.Begin);
                        mustReopenLog = false;
                    }
                    catch (DirectoryNotFoundException ex)
                    {
                        DiagnosticLogger.WriteException("ERROR", ex, "Steam IPC log directory was not found.");
                        MessageBox.Show("Steam IPC log file directory does not exist", "Directory Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                while (!mustReopenLog && !isShuttingDown)
                {
                    string line = await sr.ReadLineAsync();
                    if (isShuttingDown)
                        return;

                    if (line == null)
                    {
                        lastPosInLog = fs.Position;
                        break;
                    }

                    if (!line.Contains(GameConfig.Current.ProcessName))
                        continue;

                    bool begin;
                    if (line.Contains("BeginAuthSession"))
                    {
                        begin = true;
                    }
                    else if (line.Contains("EndAuthSession"))
                    {
                        begin = false;
                    }
                    else if (line.Contains("LeaveLobby"))
                    {
                        DiagnosticLogger.Write("PEER", "Steam IPC reported that the local user left the lobby.");
                        foreach (var sid in mPeers.Keys.ToArray())
                            RemovePeer(sid, "Player left Steam lobby", PeerRemovalReason.LobbyLeft);
                        LobbyLeft?.Invoke();
                        continue;
                    }
                    else continue;

                    CSteamID steamID = ExtractUser(line);

                    if (steamID.m_SteamID != 0)
                    {
                        if (steamID.BIndividualAccount())
                        {
                            if (begin)
                            {
                                if (!mPeers.TryGetValue(steamID, out SteamPeerInfo peer))
                                {
                                    // This is a genuinely new manager entry. Notify
                                    // enforcement before GetPeer can accept or the
                                    // timer can evaluate the returning session.
                                    PeerBeginAuthSession?.Invoke(steamID.m_SteamID);

                                    var newPeerInfo = new SteamPeerInfo(GetPeer(steamID, cycleId));
                                    if (newPeerInfo.peer is null)
                                    {
                                        Logger.WriteLine($"[PEER CONNECT] Player \"{steamID}\" was detected, but we don't have a P2P connection to them yet");
                                        newPeerInfo.lastDisconnectTimeMS = sw.ElapsedMilliseconds;
                                    }
                                    mPeers.Add(steamID, newPeerInfo);
                                }
                            }
                            else
                            {
                                // peer just disconnected
                                if (mPeers.ContainsKey(steamID))
                                    RemovePeer(steamID, "Auth session with peer ended", PeerRemovalReason.AuthSessionEnded);
                                else
                                    PeerRemoved?.Invoke(steamID.m_SteamID, PeerRemovalReason.AuthSessionEnded);
                            }
                        }
                        else
                        {
                            Logger.WriteLine($"[PARSE ERROR] \"{steamID}\" was not a valid steam user");
                        }
                    }
                }

                // clean up old peers.
                foreach (var sid in mPeers.Keys.ToArray())
                {
                    var pInfo = mPeers[sid];
                    bool isP2PConnected = false;
                    if (pInfo.peer is null)
                        isP2PConnected = (pInfo.peer = GetPeer(sid, cycleId)) != null;
                    else
                    {
                        isP2PConnected = pInfo.peer.UpdatePeerInfo();
                        if (isP2PConnected)
                            TryObserveHistory(pInfo.peer, cycleId);
                    }

                    if (pInfo.isConnected && !isP2PConnected)
                        pInfo.lastDisconnectTimeMS = sw.ElapsedMilliseconds;
                    pInfo.isConnected = isP2PConnected;

                    if (!isP2PConnected && sw.ElapsedMilliseconds - pInfo.lastDisconnectTimeMS > PEER_TIMEOUT_MS)
                    {
                        RemovePeer(sid, pInfo.peer is null ? "P2P connection was not established" : "Peer disconnected from P2P session", PeerRemovalReason.TransportTimeout);
                    }
                }
            }
            catch (ObjectDisposedException) when (isShuttingDown)
            {
                // The IPC reader may be disposed while an asynchronous update is yielding during shutdown.
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("ERROR", ex, "Steam peer update failed.");
            }
            finally
            {
                Interlocked.Exchange(ref updateInProgress, 0);
            }
        }

        public static IEnumerable<SteamPeerBase> GetPeers()
        {
            return mPeers.Values.Where(info => info.peer != null).Select(info => info.peer);
        }
    }
}
