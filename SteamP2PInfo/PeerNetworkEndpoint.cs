using System;
using System.Net;

namespace SteamP2PInfo
{
    internal sealed class PeerNetworkEndpoint : IEquatable<PeerNetworkEndpoint>
    {
        public IPAddress Address { get; }
        public ushort Port { get; }

        public PeerNetworkEndpoint(IPAddress address, ushort port)
        {
            Address = address ?? throw new ArgumentNullException(nameof(address));
            Port = port;
        }

        public bool Equals(PeerNetworkEndpoint other)
        {
            return other != null && Address.Equals(other.Address) && Port == other.Port;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as PeerNetworkEndpoint);
        }

        public override int GetHashCode()
        {
            return (Address.GetHashCode() * 397) ^ Port.GetHashCode();
        }

        public override string ToString()
        {
            return $"{Address}:{Port}";
        }
    }
}
