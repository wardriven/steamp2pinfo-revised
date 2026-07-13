using System;
using System.Collections.Generic;
using System.Net;
using SteamP2PInfo;
using SteamP2PInfo.Config;
using Steamworks;

namespace SteamP2PInfo.LifecycleTests
{
    internal static class Program
    {
        private const ulong ReturningPeer = 76561198000000001;
        private const ulong OtherQuarantinedPeer = 76561198000000002;

        private static int Main()
        {
            var tests = new Action[]
            {
                HighPingPeerIsQuarantined,
                ImmediateCloseCallbacksRetainQuarantine,
                ConfirmedReturningBeginAuthClearsOnlyThatPeersFilters,
                ReturningPeerBelowThresholdIsNotBlocked,
                ManualBlockHotkeyDefaultsToUnassigned,
                ManualQuarantineSurvivesAutomaticCleanup,
                ManualBlockWorkflowContinuesAfterPeerFailure
            };

            int failures = 0;
            foreach (Action test in tests)
            {
                try
                {
                    test();
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + ex.Message);
                }
            }

            Console.WriteLine($"{tests.Length - failures}/{tests.Length} lifecycle tests passed.");
            return failures == 0 ? 0 : 1;
        }

        private static void HighPingPeerIsQuarantined()
        {
            var lifecycle = new PeerQuarantineLifecycle();

            AssertTrue(P2PEnforcementCoordinator.ShouldDisconnect(150d, 100d), "The high-ping peer should require enforcement.");
            lifecycle.Retain(ReturningPeer, PeerQuarantineOwner.AutomaticHighPing);

            AssertTrue(lifecycle.Contains(ReturningPeer), "The high-ping peer should be quarantined after its WFP block is applied.");
        }

        private static void ImmediateCloseCallbacksRetainQuarantine()
        {
            var lifecycle = new PeerQuarantineLifecycle();
            lifecycle.Retain(ReturningPeer, PeerQuarantineOwner.AutomaticHighPing);

            AssertTrue(lifecycle.ShouldRetainAfterPeerRemoval(ReturningPeer), "The companion CloseSession callback must retain the quarantine.");
            AssertTrue(lifecycle.Contains(ReturningPeer), "The immediate peer-removal callback must not clear the quarantined Steam ID.");
        }

        private static void ConfirmedReturningBeginAuthClearsOnlyThatPeersFilters()
        {
            var lifecycle = new PeerQuarantineLifecycle();
            var removedPeerFilters = new List<ulong>();
            lifecycle.Retain(ReturningPeer, PeerQuarantineOwner.AutomaticHighPing);
            lifecycle.Retain(OtherQuarantinedPeer, PeerQuarantineOwner.AutomaticHighPing);

            AssertTrue(
                lifecycle.ClearForConfirmedBeginAuthSession(ReturningPeer, removedPeerFilters.Add),
                "A confirmed new BeginAuthSession should clear the returning peer's old filters.");
            AssertEqual(1, removedPeerFilters.Count, "Exactly one peer's filters should be removed.");
            AssertEqual(ReturningPeer, removedPeerFilters[0], "Only the returning Steam ID should have its filters removed.");
            AssertFalse(lifecycle.Contains(ReturningPeer), "The returning Steam ID should leave the quarantine set.");
            AssertTrue(lifecycle.Contains(OtherQuarantinedPeer), "Another peer's quarantine must remain intact.");
        }

        private static void ReturningPeerBelowThresholdIsNotBlocked()
        {
            var lifecycle = new PeerQuarantineLifecycle();
            lifecycle.Retain(ReturningPeer, PeerQuarantineOwner.AutomaticHighPing);
            lifecycle.ClearForConfirmedBeginAuthSession(ReturningPeer, _ => { });

            AssertFalse(lifecycle.Contains(ReturningPeer), "The returning peer should be fresh after its confirmed BeginAuthSession.");
            AssertFalse(P2PEnforcementCoordinator.ShouldDisconnect(45d, 100d), "A returning peer below the ping threshold must not be blocked.");
        }

        private static void ManualBlockHotkeyDefaultsToUnassigned()
        {
            var config = new GameConfig();

            AssertEqual(0, config.ManualBlockHotkey, "The manual block hotkey must be unassigned by default.");
        }

        private static void ManualQuarantineSurvivesAutomaticCleanup()
        {
            var lifecycle = new PeerQuarantineLifecycle();
            var removedPeerFilters = new List<ulong>();
            lifecycle.Retain(ReturningPeer, PeerQuarantineOwner.AutomaticHighPing);
            lifecycle.Retain(ReturningPeer, PeerQuarantineOwner.ManualHotkey);
            lifecycle.Retain(OtherQuarantinedPeer, PeerQuarantineOwner.AutomaticHighPing);

            int released = lifecycle.ReleaseOwner(PeerQuarantineOwner.AutomaticHighPing, removedPeerFilters.Add);

            AssertEqual(2, released, "Automatic cleanup should release every automatic owner.");
            AssertEqual(PeerQuarantineOwner.ManualHotkey, lifecycle.GetOwners(ReturningPeer), "Manual ownership must remain after automatic enforcement is disabled.");
            AssertTrue(lifecycle.Contains(ReturningPeer), "A manually quarantined peer must keep its filters.");
            AssertFalse(lifecycle.Contains(OtherQuarantinedPeer), "A peer owned only by automatic enforcement must be cleared.");
            AssertEqual(1, removedPeerFilters.Count, "Only the automatic-only peer's filters should be removed.");
            AssertEqual(OtherQuarantinedPeer, removedPeerFilters[0], "Cleanup must not remove the manually retained peer's filters.");
        }

        private static void ManualBlockWorkflowContinuesAfterPeerFailure()
        {
            var firstPeer = new FakePeer(ReturningPeer, throwOnClose: true);
            var secondPeer = new FakePeer(OtherQuarantinedPeer);
            var firewall = new FakeFirewallBlockService();
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => new SteamPeerBase[] { firstPeer, secondPeer },
                () => firewall);

            try
            {
                coordinator.BlockAllConnectedPeers();
            }
            finally
            {
                coordinator.Dispose();
            }

            AssertEqual(2, firewall.BlockedPeerIds.Count, "The exact-flow block should be attempted for every current peer.");
            AssertEqual(ReturningPeer, firewall.BlockedPeerIds[0], "The first peer should be attempted first.");
            AssertEqual(OtherQuarantinedPeer, firewall.BlockedPeerIds[1], "A failed peer must not stop the next peer.");
            AssertEqual(1, firstPeer.CloseSessionCalls, "The first peer should still receive a CloseSession attempt after its block.");
            AssertEqual(1, secondPeer.CloseSessionCalls, "The second peer should receive a CloseSession attempt despite the first failure.");
        }

        private sealed class FakePeer : SteamPeerBase
        {
            private readonly PeerNetworkEndpoint endpoint = new PeerNetworkEndpoint(IPAddress.Parse("203.0.113.10"), 27015);
            private readonly bool throwOnClose;

            public int CloseSessionCalls { get; private set; }

            public FakePeer(ulong steamId, bool throwOnClose = false)
                : base(new CSteamID(steamId))
            {
                this.throwOnClose = throwOnClose;
            }

            public override bool IsOldAPI => false;
            public override string ConnectionTypeName => "Fake";
            public override double Ping => 0d;
            public override double ConnectionQuality => 1d;

            public override bool UpdatePeerInfo()
            {
                return true;
            }

            public override bool TryGetRemoteEndpoint(out PeerNetworkEndpoint peerEndpoint)
            {
                peerEndpoint = endpoint;
                return true;
            }

            public override bool CloseSession()
            {
                CloseSessionCalls++;
                if (throwOnClose)
                    throw new InvalidOperationException("Synthetic CloseSession failure.");
                return true;
            }
        }

        private sealed class FakeFirewallBlockService : IFirewallBlockService
        {
            public List<ulong> BlockedPeerIds { get; } = new List<ulong>();

            public FirewallBlockResult Block(ulong steamId, PeerNetworkEndpoint endpoint)
            {
                BlockedPeerIds.Add(steamId);
                return FirewallBlockResult.Ok("fake exact UDP flow");
            }

            public FirewallBlockResult BlockAllGameUdp(ulong steamId)
            {
                return FirewallBlockResult.Failed("Not used by the exact-flow workflow.");
            }

            public FirewallBlockResult BlockGameOwnedUdpPorts(ulong steamId)
            {
                return FirewallBlockResult.Failed("Not used by the exact-flow workflow.");
            }

            public void Remove(ulong steamId)
            {
            }

            public void RemoveAll()
            {
            }

            public void Dispose()
            {
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private static void AssertFalse(bool condition, string message)
        {
            AssertTrue(!condition, message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }
}
