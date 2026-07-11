using System;
using System.Collections.Generic;

namespace SteamP2PInfo
{
    /// <summary>
    /// Owns the per-peer lifecycle for retained WFP quarantine filters.
    /// A peer removal alone is not proof that the game's independent Steam
    /// session ended, so only a confirmed new BeginAuthSession may clear it.
    /// </summary>
    internal sealed class PeerQuarantineLifecycle
    {
        private readonly HashSet<ulong> quarantinedPeers = new HashSet<ulong>();

        public int Count => quarantinedPeers.Count;

        public bool Contains(ulong steamId)
        {
            return quarantinedPeers.Contains(steamId);
        }

        public void Retain(ulong steamId)
        {
            quarantinedPeers.Add(steamId);
        }

        public bool ShouldRetainAfterPeerRemoval(ulong steamId)
        {
            return quarantinedPeers.Contains(steamId);
        }

        /// <summary>
        /// Clears one retained quarantine only after SteamPeerManager confirms
        /// that it is creating a new peer entry for a BeginAuthSession.
        /// </summary>
        public bool ClearForConfirmedBeginAuthSession(ulong steamId, Action<ulong> removePeerFilters)
        {
            if (!quarantinedPeers.Contains(steamId))
                return false;

            removePeerFilters?.Invoke(steamId);
            quarantinedPeers.Remove(steamId);
            return true;
        }

        public void Clear()
        {
            quarantinedPeers.Clear();
        }
    }
}
