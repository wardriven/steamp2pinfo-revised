using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SteamP2PInfo
{
    internal sealed class FirewallBlockResult
    {
        public bool Success { get; }
        public string Error { get; }
        public string Details { get; }

        private FirewallBlockResult(bool success, string error, string details)
        {
            Success = success;
            Error = error;
            Details = details;
        }

        public static FirewallBlockResult Ok(string details = null)
        {
            return new FirewallBlockResult(true, null, details);
        }

        public static FirewallBlockResult Failed(string error)
        {
            return new FirewallBlockResult(false, error, null);
        }
    }

    internal interface IFirewallBlockService : IDisposable
    {
        FirewallBlockResult Block(ulong steamId, PeerNetworkEndpoint endpoint);
        FirewallBlockResult BlockAllGameUdp(ulong steamId);
        FirewallBlockResult BlockGameOwnedUdpPorts(ulong steamId);
        void Remove(ulong steamId);
        void RemoveAll();
    }

    internal sealed class WindowsFirewallBlockService : IFirewallBlockService
    {
        private const string RulePrefix = "SteamP2PInfo-";
        private const int DirectionInbound = 1;
        private const int DirectionOutbound = 2;

        private readonly dynamic policy;
        private readonly string executablePath;
        private readonly int gameProcessId;
        private readonly string sessionId = Guid.NewGuid().ToString("N");
        private readonly Dictionary<ulong, List<string>> peerRuleNames = new Dictionary<ulong, List<string>>();

        public WindowsFirewallBlockService(string executablePath, int gameProcessId)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentException("A full game executable path is required", nameof(executablePath));

            this.executablePath = System.IO.Path.GetFullPath(executablePath);
            if (gameProcessId <= 0)
                throw new ArgumentOutOfRangeException(nameof(gameProcessId));
            this.gameProcessId = gameProcessId;
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
                {
                    try
                    {
                        firewallPolicy.Rules.Remove(name);
                    }
                    catch
                    {
                        RemoveRuleWithPowerShell(name);
                    }
                }

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

            return BlockInternal(steamId, endpoint);
        }

        public FirewallBlockResult BlockAllGameUdp(ulong steamId)
        {
            return BlockInternal(steamId, null);
        }

        public FirewallBlockResult BlockGameOwnedUdpPorts(ulong steamId)
        {
            if (peerRuleNames.ContainsKey(steamId))
                return FirewallBlockResult.Ok();

            string outboundName = BuildRuleName(steamId, "ports-out");
            string inboundName = BuildRuleName(steamId, "ports-in");
            var names = new List<string> { outboundName, inboundName };

            try
            {
                AddGamePortRulePairWithPowerShell(outboundName, inboundName);
                peerRuleNames.Add(steamId, names);
                return FirewallBlockResult.Ok();
            }
            catch (Exception ex)
            {
                foreach (string name in names)
                    TryRemoveRule(name);
                return FirewallBlockResult.Failed(ex.Message);
            }
        }

        private FirewallBlockResult BlockInternal(ulong steamId, PeerNetworkEndpoint endpoint)
        {

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
            string remoteAddress = endpoint?.Address.ToString() ?? "*";
            string remotePort = endpoint == null ? "*" : endpoint.Port.ToString(CultureInfo.InvariantCulture);
            string description = endpoint == null
                ? "Temporary high-latency shared-relay UDP block created by SteamP2PInfo"
                : "Temporary high-latency Steam P2P block created by SteamP2PInfo";

            // On some Windows installations the in-process HNetCfg add operation records
            // an attempted rule but returns ERROR_FILE_NOT_FOUND when SteamP2PInfo is the
            // modifying application. NetSecurity uses the same Windows Firewall policy
            // store through its supported provider and is then independently read back below.
            AddRuleWithPowerShell(name, description, direction, remoteAddress, remotePort);
        }

        private void TryRemoveRule(string name)
        {
            try
            {
                policy.Rules.Remove(name);
            }
            catch
            {
                try
                {
                    RemoveRuleWithPowerShell(name);
                }
                catch
                {
                    // Startup stale-rule cleanup is the final recovery path after removal failures.
                }
            }
        }

        private void AddRuleWithPowerShell(
            string name,
            string description,
            int direction,
            string remoteAddress,
            string remotePort)
        {
            string directionName = direction == DirectionInbound ? "Inbound" : "Outbound";
            string netSecurityAddress = remoteAddress == "*" ? "Any" : remoteAddress;
            string netSecurityPort = remotePort == "*" ? "Any" : remotePort;
            string quotedName = QuotePowerShell(name);
            string quotedExecutable = QuotePowerShell(executablePath);
            string quotedAddress = QuotePowerShell(netSecurityAddress);
            string quotedPort = QuotePowerShell(netSecurityPort);
            string command =
                "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" +
                "New-NetFirewallRule" +
                " -Name " + quotedName +
                " -DisplayName " + quotedName +
                " -Description " + QuotePowerShell(description) +
                " -Direction " + directionName +
                " -Action Block -Enabled True -Profile Any" +
                " -Program " + quotedExecutable +
                " -Protocol UDP" +
                " -RemoteAddress " + quotedAddress +
                " -RemotePort " + quotedPort +
                " | Out-Null;" +
                "$r=Get-NetFirewallRule -Name " + quotedName + " -ErrorAction Stop;" +
                "$a=$r|Get-NetFirewallApplicationFilter;" +
                "$n=$r|Get-NetFirewallAddressFilter;" +
                "$p=$r|Get-NetFirewallPortFilter;" +
                "if([string]$r.Enabled -ne 'True' -or [string]$r.Direction -ne '" + directionName +
                "' -or [string]$r.Action -ne 'Block' -or $a.Program -ine " + quotedExecutable +
                " -or [string]$p.Protocol -ne 'UDP' -or $n.RemoteAddress -inotcontains " + quotedAddress +
                " -or $p.RemotePort -inotcontains " + quotedPort + "){throw 'Windows Firewall did not retain the exact requested scope.'}";

            RunPowerShell(command, $"create firewall rule {name}");
        }

        private void AddGamePortRulePairWithPowerShell(string outboundName, string inboundName)
        {
            string quotedOutboundName = QuotePowerShell(outboundName);
            string quotedInboundName = QuotePowerShell(inboundName);
            string command =
                "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" +
                "$all=@(Get-NetUDPEndpoint -ErrorAction Stop);" +
                "$owned=@($all|Where-Object OwningProcess -eq " + gameProcessId.ToString(CultureInfo.InvariantCulture) +
                "|Select-Object -ExpandProperty LocalPort -Unique);" +
                "$safe=@($owned|Where-Object{$port=$_;-not($all|Where-Object{$_.LocalPort -eq $port -and $_.OwningProcess -ne " +
                gameProcessId.ToString(CultureInfo.InvariantCulture) + "})});" +
                "if($safe.Count -eq 0){throw 'No exclusive UDP local ports are currently owned by the selected game process.'};" +
                "$specs=@(@{Name=" + quotedOutboundName + ";Direction='Outbound'},@{Name=" + quotedInboundName + ";Direction='Inbound'});" +
                "foreach($spec in $specs){" +
                "New-NetFirewallRule -Name $spec.Name -DisplayName $spec.Name" +
                " -Description 'Temporary game-owned UDP port quarantine created by SteamP2PInfo'" +
                " -Direction $spec.Direction -Action Block -Enabled True -Profile Any -Protocol UDP" +
                " -LocalPort $safe -RemoteAddress Any -RemotePort Any|Out-Null;" +
                "$r=Get-NetFirewallRule -Name $spec.Name -ErrorAction Stop;" +
                "$a=$r|Get-NetFirewallApplicationFilter;$n=$r|Get-NetFirewallAddressFilter;$p=$r|Get-NetFirewallPortFilter;" +
                "$stored=@($p.LocalPort|ForEach-Object{[int]$_}|Sort-Object -Unique);" +
                "$expected=@($safe|ForEach-Object{[int]$_}|Sort-Object -Unique);" +
                "if([string]$r.Enabled -ne 'True' -or [string]$r.Action -ne 'Block'" +
                " -or [string]$r.Direction -ne $spec.Direction -or [string]$p.Protocol -ne 'UDP'" +
                " -or $n.RemoteAddress -inotcontains 'Any' -or $p.RemotePort -inotcontains 'Any'" +
                " -or (Compare-Object $stored $expected)){throw 'Windows Firewall did not retain the exact game-owned UDP port scope.'}" +
                "}";

            RunPowerShell(command, "create and verify game-owned UDP port quarantine");
        }

        private static void RemoveRuleWithPowerShell(string name)
        {
            string command =
                "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" +
                "Remove-NetFirewallRule -Name " + QuotePowerShell(name) + " -ErrorAction SilentlyContinue";
            RunPowerShell(command, $"remove firewall rule {name}");
        }

        private static string QuotePowerShell(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private static void RunPowerShell(string command, string operation)
        {
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encodedCommand,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using (Process process = Process.Start(startInfo))
            {
                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException($"Timed out while attempting to {operation}.");
                }

                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                if (process.ExitCode != 0)
                {
                    string details = string.IsNullOrWhiteSpace(error) ? output : error;
                    throw new InvalidOperationException(
                        $"Windows NetSecurity failed to {operation}: {details.Trim()}");
                }
            }
        }
    }
}
