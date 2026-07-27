using System;
using System.Globalization;

using Newtonsoft.Json;

namespace SteamP2PInfo
{
    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class PeerHistoryEntry
    {
        private const string UnavailableText = "Unavailable";

        [JsonProperty("steamName")]
        public string SteamName { get; private set; }

        [JsonProperty("steamId")]
        public string SteamId { get; private set; }

        [JsonProperty("averagePingMs")]
        public double? AveragePingMs { get; private set; }

        [JsonProperty("ipAddress")]
        internal string IpAddress { get; private set; }

        [JsonProperty("connectedAtUtc")]
        public DateTime ConnectedAtUtc { get; private set; }

        [JsonProperty("disconnectedAtUtc")]
        public DateTime DisconnectedAtUtc { get; private set; }

        [JsonIgnore]
        public string AveragePingDisplay
        {
            get
            {
                return AveragePingMs.HasValue
                    ? AveragePingMs.Value.ToString("N0", CultureInfo.CurrentCulture)
                    : UnavailableText;
            }
        }

        [JsonIgnore]
        public string IpAddressDisplay
        {
            get
            {
                return string.IsNullOrWhiteSpace(IpAddress) ? UnavailableText : IpAddress;
            }
        }

        [JsonIgnore]
        internal ulong SteamIdValue
        {
            get
            {
                ulong.TryParse(
                    SteamId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out ulong value);
                return value;
            }
        }

        private PeerHistoryEntry()
        {
        }

        internal PeerHistoryEntry(
            ulong steamId,
            string steamName,
            double? averagePingMs,
            string ipAddress,
            DateTime connectedAtUtc,
            DateTime disconnectedAtUtc)
        {
            SteamId = steamId.ToString(CultureInfo.InvariantCulture);
            SteamName = string.IsNullOrWhiteSpace(steamName) ? null : steamName.Trim();
            AveragePingMs = IsValidPing(averagePingMs) ? averagePingMs : null;
            IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress;
            ConnectedAtUtc = NormalizeUtc(connectedAtUtc);
            DisconnectedAtUtc = NormalizeUtc(disconnectedAtUtc);
        }

        internal bool IsValid()
        {
            if (SteamIdValue == 0)
                return false;

            if (!IsValidPing(AveragePingMs))
                return false;

            if (ConnectedAtUtc == default(DateTime) ||
                DisconnectedAtUtc == default(DateTime) ||
                DisconnectedAtUtc < ConnectedAtUtc)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(IpAddress) &&
                !System.Net.IPAddress.TryParse(IpAddress, out System.Net.IPAddress address))
            {
                return false;
            }

            return true;
        }

        internal void NormalizeAfterLoad()
        {
            SteamName = string.IsNullOrWhiteSpace(SteamName) ? null : SteamName.Trim();
            IpAddress = string.IsNullOrWhiteSpace(IpAddress) ? null : IpAddress;
            ConnectedAtUtc = NormalizeUtc(ConnectedAtUtc);
            DisconnectedAtUtc = NormalizeUtc(DisconnectedAtUtc);
        }

        private static bool IsValidPing(double? ping)
        {
            return !ping.HasValue ||
                   (ping.Value > 0d && !double.IsNaN(ping.Value) && !double.IsInfinity(ping.Value));
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;

            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();

            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
