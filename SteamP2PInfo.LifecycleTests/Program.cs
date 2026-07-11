using System;
using System.Collections.Generic;
using SteamP2PInfo;

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
                ReturningPeerBelowThresholdIsNotBlocked
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
            lifecycle.Retain(ReturningPeer);

            AssertTrue(lifecycle.Contains(ReturningPeer), "The high-ping peer should be quarantined after its WFP block is applied.");
        }

        private static void ImmediateCloseCallbacksRetainQuarantine()
        {
            var lifecycle = new PeerQuarantineLifecycle();
            lifecycle.Retain(ReturningPeer);

            AssertTrue(lifecycle.ShouldRetainAfterPeerRemoval(ReturningPeer), "The companion CloseSession callback must retain the quarantine.");
            AssertTrue(lifecycle.Contains(ReturningPeer), "The immediate peer-removal callback must not clear the quarantined Steam ID.");
        }

        private static void ConfirmedReturningBeginAuthClearsOnlyThatPeersFilters()
        {
            var lifecycle = new PeerQuarantineLifecycle();
            var removedPeerFilters = new List<ulong>();
            lifecycle.Retain(ReturningPeer);
            lifecycle.Retain(OtherQuarantinedPeer);

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
            lifecycle.Retain(ReturningPeer);
            lifecycle.ClearForConfirmedBeginAuthSession(ReturningPeer, _ => { });

            AssertFalse(lifecycle.Contains(ReturningPeer), "The returning peer should be fresh after its confirmed BeginAuthSession.");
            AssertFalse(P2PEnforcementCoordinator.ShouldDisconnect(45d, 100d), "A returning peer below the ping threshold must not be blocked.");
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
