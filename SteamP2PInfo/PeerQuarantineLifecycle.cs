using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamP2PInfo
{
    [Flags]
    internal enum PeerQuarantineOwner
    {
        None = 0,
        AutomaticHighPing = 1,
        ManualHotkey = 2
    }

    /// <summary>
    /// Owns the per-peer lifecycle for retained WFP quarantine filters.
    /// A peer removal alone is not proof that the game's independent Steam
    /// session ended, so only a confirmed new BeginAuthSession may clear it.
    /// </summary>
    internal sealed class PeerQuarantineLifecycle
    {
        private readonly Dictionary<ulong, PeerQuarantineOwner> quarantinedPeers = new Dictionary<ulong, PeerQuarantineOwner>();

        public int Count => quarantinedPeers.Count;

        public bool Contains(ulong steamId)
        {
            return quarantinedPeers.ContainsKey(steamId);
        }

        public PeerQuarantineOwner GetOwners(ulong steamId)
        {
            return quarantinedPeers.TryGetValue(steamId, out PeerQuarantineOwner owners)
                ? owners
                : PeerQuarantineOwner.None;
        }

        public void Retain(ulong steamId, PeerQuarantineOwner owner)
        {
            if (owner == PeerQuarantineOwner.None)
                return;

            quarantinedPeers[steamId] = GetOwners(steamId) | owner;
        }

        public bool ShouldRetainAfterPeerRemoval(ulong steamId)
        {
            return quarantinedPeers.ContainsKey(steamId);
        }

        /// <summary>
        /// Clears one retained quarantine only after SteamPeerManager confirms
        /// that it is creating a new peer entry for a BeginAuthSession.
        /// </summary>
        public bool ClearForConfirmedBeginAuthSession(ulong steamId, Action<ulong> removePeerFilters)
        {
            if (!quarantinedPeers.ContainsKey(steamId))
                return false;

            removePeerFilters?.Invoke(steamId);
            quarantinedPeers.Remove(steamId);
            return true;
        }

        /// <summary>
        /// Removes one ownership reason from every quarantine. Filters are
        /// removed only for peers that have no remaining owner.
        /// </summary>
        public int ReleaseOwner(PeerQuarantineOwner owner, Action<ulong> removePeerFilters)
        {
            if (owner == PeerQuarantineOwner.None)
                return 0;

            int released = 0;
            foreach (ulong steamId in quarantinedPeers.Keys.ToArray())
            {
                PeerQuarantineOwner owners = quarantinedPeers[steamId];
                if ((owners & owner) == PeerQuarantineOwner.None)
                    continue;

                released++;
                owners &= ~owner;
                if (owners == PeerQuarantineOwner.None)
                {
                    removePeerFilters?.Invoke(steamId);
                    quarantinedPeers.Remove(steamId);
                }
                else
                {
                    quarantinedPeers[steamId] = owners;
                }
            }

            return released;
        }

        public void Clear()
        {
            quarantinedPeers.Clear();
        }
    }
}
