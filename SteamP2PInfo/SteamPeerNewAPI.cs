using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Steamworks;

namespace SteamP2PInfo
{
    /// <summary>
    /// Represents a Steam P2P peer connected using the ISteamNetworkingMessages API.
    /// </summary>
    class SteamPeerNewAPI : SteamPeerBase
    {
        /// <summary>
        /// Object providing information on the steam P2P connection.
        /// </summary>
        private SteamNetConnectionInfo_t mConnInfo;

        /// <summary>
        /// Object providing realtime information on the steam P2P connection (namely ping)
        /// </summary>
        private SteamNetConnectionRealTimeStatus_t mRealTimeStatus;

        public override bool IsOldAPI { get { return false; } }

        public override string ConnectionTypeName => "SteamNetworkingSockets";

        public override double Ping => mRealTimeStatus.m_nPing;

        public override double ConnectionQuality => mRealTimeStatus.m_flConnectionQualityLocal;

        public SteamPeerNewAPI(CSteamID steamId) : base(steamId) 
        {
            mConnInfo = new SteamNetConnectionInfo_t();
            mRealTimeStatus = new SteamNetConnectionRealTimeStatus_t();
        }

        public override bool UpdatePeerInfo()
        {
            SteamNetworkingIdentity networkingIdentity = new SteamNetworkingIdentity();
            networkingIdentity.SetSteamID(SteamID);

            var connState = SteamNetworkingMessages.GetSessionConnectionInfo(ref networkingIdentity, out mConnInfo, out mRealTimeStatus);
            return IsConnStateOK(connState);
        }

        public static bool IsConnStateOK(ESteamNetworkingConnectionState connState)
        {
            return (connState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting ||
                     connState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected);
        }

        public override bool TryGetRemoteEndpoint(out PeerNetworkEndpoint endpoint)
        {
            endpoint = null;
            if (mConnInfo.m_addrRemote.m_port == 0 || mConnInfo.m_addrRemote.IsIPv6AllZeros() || mConnInfo.m_addrRemote.IsFakeIP())
                return false;

            mConnInfo.m_addrRemote.ToString(out string addressText, false);
            if (!IPAddress.TryParse(addressText, out IPAddress address))
                return false;

            endpoint = new PeerNetworkEndpoint(address, mConnInfo.m_addrRemote.m_port);
            return true;
        }

        public override bool CloseSession()
        {
            SteamNetworkingIdentity networkingIdentity = new SteamNetworkingIdentity();
            networkingIdentity.SetSteamID(SteamID);
            return SteamNetworkingMessages.CloseSessionWithUser(ref networkingIdentity);
        }
    }
}
