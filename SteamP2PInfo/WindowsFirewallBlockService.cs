using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace SteamP2PInfo
{
    internal sealed class FirewallBlockResult
    {
        public bool Success { get; }
        public string Error { get; }

        private FirewallBlockResult(bool success, string error)
        {
            Success = success;
            Error = error;
        }

        public static FirewallBlockResult Ok()
        {
            return new FirewallBlockResult(true, null);
        }

        public static FirewallBlockResult Failed(string error)
        {
            return new FirewallBlockResult(false, error);
        }
    }

    internal interface IFirewallBlockService : IDisposable
    {
        FirewallBlockResult Block(ulong steamId, PeerNetworkEndpoint endpoint);
        void Remove(ulong steamId);
        void RemoveAll();
    }

    internal sealed class WindowsFirewallBlockService : IFirewallBlockService
    {
        private const string RulePrefix = "SteamP2PInfo-";
        private const string RuleGroup = "SteamP2PInfo temporary P2P blocks";
        private const int ActionBlock = 0;
        private const int DirectionInbound = 1;
        private const int DirectionOutbound = 2;
        private const int ProtocolUdp = 17;
        private const int AllProfiles = int.MaxValue;

        private readonly dynamic policy;
        private readonly string executablePath;
        private readonly string sessionId = Guid.NewGuid().ToString("N");
        private readonly Dictionary<ulong, List<string>> peerRuleNames = new Dictionary<ulong, List<string>>();

        public WindowsFirewallBlockService(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentException("A full game executable path is required", nameof(executablePath));

            this.executablePath = System.IO.Path.GetFullPath(executablePath);
            Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true);
            policy = Activator.CreateInstance(policyType);
        }

        public static string RemoveStaleRules()
        {
            try
            {
                Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true);
                dynamic firewallPolicy = Activator.CreateInstance(policyType);
                var names = new List<string>();

                foreach (dynamic rule in (IEnumerable)firewallPolicy.Rules)
                {
                    string name = rule.Name as string;
                    if (!string.IsNullOrEmpty(name) && name.StartsWith(RulePrefix, StringComparison.Ordinal))
                        names.Add(name);
                }

                foreach (string name in names)
                    firewallPolicy.Rules.Remove(name);

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public FirewallBlockResult Block(ulong steamId, PeerNetworkEndpoint endpoint)
        {
            if (endpoint == null || endpoint.Port == 0)
                return FirewallBlockResult.Failed("The peer does not expose a valid remote UDP endpoint.");

            if (peerRuleNames.ContainsKey(steamId))
                return FirewallBlockResult.Ok();

            string outboundName = BuildRuleName(steamId, "out");
            string inboundName = BuildRuleName(steamId, "in");
            var createdNames = new List<string>();

            try
            {
                AddAndVerifyRule(outboundName, DirectionOutbound, endpoint);
                createdNames.Add(outboundName);
                AddAndVerifyRule(inboundName, DirectionInbound, endpoint);
                createdNames.Add(inboundName);
                peerRuleNames.Add(steamId, createdNames);
                return FirewallBlockResult.Ok();
            }
            catch (Exception ex)
            {
                foreach (string name in createdNames)
                    TryRemoveRule(name);
                return FirewallBlockResult.Failed(ex.Message);
            }
        }

        public void Remove(ulong steamId)
        {
            if (!peerRuleNames.TryGetValue(steamId, out List<string> names))
                return;

            foreach (string name in names)
                TryRemoveRule(name);
            peerRuleNames.Remove(steamId);
        }

        public void RemoveAll()
        {
            foreach (ulong steamId in new List<ulong>(peerRuleNames.Keys))
                Remove(steamId);
        }

        public void Dispose()
        {
            RemoveAll();
        }

        private string BuildRuleName(ulong steamId, string direction)
        {
            return $"{RulePrefix}{sessionId}-{steamId}-{direction}";
        }

        private void AddAndVerifyRule(string name, int direction, PeerNetworkEndpoint endpoint)
        {
            Type ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule", true);
            dynamic rule = Activator.CreateInstance(ruleType);
            string remoteAddress = endpoint.Address.ToString();
            string remotePort = endpoint.Port.ToString(CultureInfo.InvariantCulture);

            rule.Name = name;
            rule.Description = "Temporary high-latency Steam P2P block created by SteamP2PInfo";
            rule.Grouping = RuleGroup;
            rule.ApplicationName = executablePath;
            rule.Protocol = ProtocolUdp;
            rule.RemoteAddresses = remoteAddress;
            rule.RemotePorts = remotePort;
            rule.Direction = direction;
            rule.Profiles = AllProfiles;
            rule.Action = ActionBlock;
            rule.Enabled = true;
            policy.Rules.Add(rule);

            dynamic stored = policy.Rules.Item(name);
            bool verified = stored != null
                && (bool)stored.Enabled
                && (int)stored.Action == ActionBlock
                && (int)stored.Direction == direction
                && (int)stored.Protocol == ProtocolUdp
                && string.Equals((string)stored.ApplicationName, executablePath, StringComparison.OrdinalIgnoreCase)
                && ContainsToken((string)stored.RemoteAddresses, remoteAddress)
                && ContainsToken((string)stored.RemotePorts, remotePort);

            if (!verified)
            {
                TryRemoveRule(name);
                throw new InvalidOperationException($"Windows Firewall did not retain the exact scope for rule {name}.");
            }
        }

        private static bool ContainsToken(string value, string expected)
        {
            if (value == null)
                return false;

            foreach (string token in value.Split(','))
            {
                if (string.Equals(token.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void TryRemoveRule(string name)
        {
            try
            {
                policy.Rules.Remove(name);
            }
            catch
            {
                // Startup stale-rule cleanup is the final recovery path after removal failures.
            }
        }
    }
}
