using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
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
                AutomaticHighPingDisconnectIsRetired,
                HotkeyPressLatchIgnoresAutoRepeatUntilRelease,
                DebugLoggingDefaultsOffAndNotifies,
                DiagnosticLoggerCreatesFreshSupportLog,
                ConnectionHistoryAveragesAndDeduplicatesSamples,
                ConnectionHistoryRejectsInvalidSamplesAndUnavailableEndpoints,
                ConnectionHistoryCompletionIsOnceOnly,
                ConnectionHistoryPersistsPerGameAndPreservesSteamId,
                ConnectionHistoryRetainsNewestFiveHundred,
                ConnectionHistoryQuarantinesMalformedFilesAndClears,
                ConnectionHistoryRecoveryCleanupCanBeRetried,
                ConnectionHistoryFailedLoadCannotOverwriteExistingFile,
                ConnectionHistoryWriteFailureIsIsolated,
                ConnectionHistoryReplaceFailurePreservesExistingFile,
                SteamPeerManagerFinalizesHistoryForEveryRemovalReason,
                SteamPeerManagerHistoryFailureDoesNotInterruptRemoval,
                SteamPeerManagerLoggingFailureDoesNotInterruptRemoval,
                ManualQuarantineSurvivesAutomaticCleanup,
                ManualBlockWorkflowContinuesAfterPeerFailure,
                ManualBlockSingleActionRetriesUntilEvidenceAppears,
                ManualBlockFailedCloseIsRetriedUntilConfirmed,
                ManualBlockSinglePressTracksEndpointMigrations,
                ManualBlockPressWaitsForInitiallyMissingPeer,
                ManualBlockPressCoversDeadlineCrossingTick,
                ManualBlockScanSurvivesPeerReplacement,
                ManualBlockScanSurvivesPostMinimumReplacementGap,
                ManualBlockScanRunsForTwentySecondsWithoutLobbyExit,
                ManualBlockScanDoesNotCrossLobbyWhenLeaveEventIsMissed,
                ManualBlockScanStopsAtSafetyLimit,
                ManualBlockDelayedTickCannotCrossSafetyLimit,
                ManualBlockLobbyExitEndsScanImmediately,
                ApplicationVersionIs150,
                ValidVersionFileIsParsed,
                BlankAndMalformedVersionFilesAreRejected,
                EqualAndOlderRemoteVersionsDoNotRequireAnUpdate,
                NewerRemoteVersionIsComparedNumerically,
                LatestVersionRequestRevalidatesAndCacheBusts
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

        private static void AutomaticHighPingDisconnectIsRetired()
        {
            var config = new GameConfig();
            config.DisconnectHighPingEnabled = true;

            AssertFalse(config.DisconnectHighPingEnabled, "Legacy configs must not be able to reactivate automatic high-ping disconnection.");
            AssertEqual(
                0,
                typeof(GameConfig).GetProperty(nameof(GameConfig.DisconnectHighPingEnabled))
                    .GetCustomAttributes(typeof(ConfigBindingElementAttribute), true)
                    .Length,
                "The removed automatic high-ping option must not appear in the generated config UI.");
        }

        private static void HotkeyPressLatchIgnoresAutoRepeatUntilRelease()
        {
            const int VirtualKeyF8 = 0x77;
            int activeVirtualKey = 0;

            AssertTrue(
                HotkeyManager.TryBeginPress(ref activeVirtualKey, VirtualKeyF8),
                "The first keydown should start one hotkey action.");
            AssertFalse(
                HotkeyManager.TryBeginPress(ref activeVirtualKey, VirtualKeyF8),
                "An auto-repeat keydown must not start another action while the key remains held.");

            HotkeyManager.EndPress(ref activeVirtualKey, VirtualKeyF8);

            AssertTrue(
                HotkeyManager.TryBeginPress(ref activeVirtualKey, VirtualKeyF8),
                "A new physical press should be accepted after keyup releases the latch.");
        }

        private static void DebugLoggingDefaultsOffAndNotifies()
        {
            var config = new GameConfig();
            int notifications = 0;
            string changedProperty = null;
            config.PropertyChanged += (sender, args) =>
            {
                notifications++;
                changedProperty = args.PropertyName;
            };

            AssertFalse(config.DebugLoggingEnabled, "Debug logging must be opt-in.");
            config.DebugLoggingEnabled = true;
            AssertTrue(config.DebugLoggingEnabled, "The debug logging setting should be enabled after toggling it on.");
            AssertEqual(1, notifications, "Enabling debug logging should raise exactly one change notification.");
            AssertEqual(nameof(GameConfig.DebugLoggingEnabled), changedProperty, "The change notification should identify the debug logging setting.");

            config.DebugLoggingEnabled = true;
            AssertEqual(1, notifications, "Assigning the current debug logging value should not raise another notification.");

            bool ignoredDuringSerialization = false;
            foreach (object attribute in typeof(GameConfig).GetProperty(nameof(GameConfig.DebugLoggingEnabled)).GetCustomAttributes(false))
            {
                if (attribute.GetType().FullName == "Newtonsoft.Json.JsonIgnoreAttribute")
                    ignoredDuringSerialization = true;
            }
            AssertTrue(ignoredDuringSerialization, "Session-scoped debug logging must be ignored by configuration serialization.");
        }

        private static void DiagnosticLoggerCreatesFreshSupportLog()
        {
            var config = new GameConfig { ProcessName = "DiagnosticLoggerLifecycleTest" };
            string logPath = null;

            try
            {
                AssertTrue(
                    DiagnosticLogger.TryStartNewSession(config, out logPath, out string error),
                    "The diagnostic logger should create a fresh support log. " + error);
                AssertTrue(File.Exists(logPath), "The diagnostic log should exist while the session is active.");

                DiagnosticLogger.Write("ACTION", "Synthetic lifecycle-test action.");
                DiagnosticLogger.WriteException("ERROR", new InvalidOperationException("Synthetic lifecycle-test failure."));
                config.HotkeysEnabled = false;
                DiagnosticLogger.WriteSettingsIfChanged(config, "Lifecycle-test settings changed");
                DiagnosticLogger.Stop("Synthetic lifecycle-test session completed.");

                string contents = File.ReadAllText(logPath);
                AssertTrue(contents.Contains("[SESSION]"), "The diagnostic log should identify session events.");
                AssertTrue(contents.Contains("[ACTION] Synthetic lifecycle-test action."), "The diagnostic log should record application actions.");
                AssertTrue(contents.Contains("[ERROR] System.InvalidOperationException: Synthetic lifecycle-test failure."), "The diagnostic log should include exception details.");
                AssertTrue(contents.Contains("\"hotkeys_enabled\": false"), "The diagnostic log should record changed settings.");
                AssertTrue(contents.Contains("Steam Web API key: <redacted>"), "The diagnostic log must redact the Steam Web API key.");
            }
            finally
            {
                DiagnosticLogger.Stop();
                if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
                    File.Delete(logPath);
            }
        }

        private static void ConnectionHistoryAveragesAndDeduplicatesSamples()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                DateTime now = new DateTime(2026, 7, 25, 18, 0, 0, DateTimeKind.Utc);
                var store = ConnectionHistoryStore.ForGame("history-average", tempDirectory);
                AssertTrue(store.TryLoad(out string loadError), "A missing history file should load as empty. " + loadError);

                var tracker = new PeerConnectionHistoryTracker(store, () => now);
                var peer = new FakePeer(ReturningPeer)
                {
                    PeerName = "First Name",
                    CurrentPing = 40d,
                    Endpoint = new PeerNetworkEndpoint(IPAddress.Parse("203.0.113.10"), 27015)
                };
                int completionEvents = 0;
                tracker.ConnectionCompleted += entry => completionEvents++;

                tracker.Observe(peer, 1);
                peer.CurrentPing = 400d;
                peer.PeerName = "Latest Name";
                peer.Endpoint = new PeerNetworkEndpoint(IPAddress.Parse("2001:db8::10"), 27016);
                tracker.Observe(peer, 1);

                now = now.AddSeconds(6);
                peer.CurrentPing = 60d;
                tracker.Observe(peer, 2);
                now = now.AddSeconds(1);

                PeerHistoryEntry completed = tracker.Complete(ReturningPeer, out string completionError);
                AssertTrue(completed != null, "A tracked connection should complete successfully. " + completionError);
                AssertEqual(50d, completed.AveragePingMs.Value, "Same-cycle observations must not double-count the first ping sample.");
                AssertEqual("Latest Name", completed.SteamName, "The latest nonblank Steam name should be retained.");
                AssertEqual("2001:db8::10", completed.IpAddressDisplay, "The latest valid IP address should be retained without its port.");
                AssertEqual(1, completionEvents, "A completed connection should raise one UI update event.");
                AssertEqual(1, store.Entries.Count, "The completed connection should be persisted immediately.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void ConnectionHistoryRejectsInvalidSamplesAndUnavailableEndpoints()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                DateTime now = new DateTime(2026, 7, 25, 18, 10, 0, DateTimeKind.Utc);
                var store = ConnectionHistoryStore.ForGame("history-invalid", tempDirectory);
                var tracker = new PeerConnectionHistoryTracker(store, () => now);
                var peer = new FakePeer(ReturningPeer)
                {
                    PeerName = "No Measurements",
                    EndpointAvailable = false
                };

                double[] invalidPings = { -1d, 0d, double.NaN, double.PositiveInfinity, double.NegativeInfinity };
                for (int i = 0; i < invalidPings.Length; i++)
                {
                    peer.CurrentPing = invalidPings[i];
                    tracker.Observe(peer, i + 1);
                    now = now.AddSeconds(6);
                }

                PeerHistoryEntry completed = tracker.Complete(ReturningPeer, out string completionError);
                AssertTrue(completed != null, "An established connection should be stored even when measurements are unavailable. " + completionError);
                AssertFalse(completed.AveragePingMs.HasValue, "Invalid and unknown ping readings must not affect the average.");
                AssertEqual("Unavailable", completed.AveragePingDisplay, "A missing average should have an explicit display value.");
                AssertEqual("Unavailable", completed.IpAddressDisplay, "A relay or unavailable endpoint should have an explicit display value.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void ConnectionHistoryCompletionIsOnceOnly()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                DateTime now = new DateTime(2026, 7, 25, 18, 20, 0, DateTimeKind.Utc);
                var store = ConnectionHistoryStore.ForGame("history-once", tempDirectory);
                var tracker = new PeerConnectionHistoryTracker(store, () => now);
                var peer = new FakePeer(ReturningPeer) { CurrentPing = 45d };

                AssertTrue(tracker.Complete(ReturningPeer) == null, "A raw auth callback without an established transport must not create history.");
                tracker.Observe(peer, 1);
                now = now.AddSeconds(10);
                AssertTrue(tracker.Complete(ReturningPeer) != null, "The established connection should complete once.");
                AssertTrue(tracker.Complete(ReturningPeer) == null, "A late duplicate removal callback must not create another row.");
                AssertEqual(1, store.Entries.Count, "The first connection should have exactly one record.");

                now = now.AddSeconds(1);
                tracker.Observe(peer, 2);
                now = now.AddSeconds(10);
                AssertTrue(tracker.Complete(ReturningPeer) != null, "A later reconnect should create a separate completed connection.");
                AssertEqual(2, store.Entries.Count, "Reconnects after removal should remain separate rows.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void ConnectionHistoryPersistsPerGameAndPreservesSteamId()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                DateTime connectedAt = new DateTime(2026, 7, 25, 18, 30, 0, DateTimeKind.Utc);
                var gameA = ConnectionHistoryStore.ForGame("game-a", tempDirectory);
                var gameB = ConnectionHistoryStore.ForGame("game-b", tempDirectory);
                var entry = new PeerHistoryEntry(
                    ReturningPeer,
                    "Persistent Player",
                    55.5d,
                    "198.51.100.25",
                    connectedAt,
                    connectedAt.AddMinutes(2));

                AssertTrue(gameA.TryAppend(entry, out string appendError), "The per-game history should save successfully. " + appendError);
                AssertEqual(0, gameB.Entries.Count, "Another game's in-memory history must remain isolated.");

                var reloadedA = ConnectionHistoryStore.ForGame("game-a", tempDirectory);
                var reloadedB = ConnectionHistoryStore.ForGame("game-b", tempDirectory);
                AssertTrue(reloadedA.TryLoad(out string loadAError), "Game A should reload its history. " + loadAError);
                AssertTrue(reloadedB.TryLoad(out string loadBError), "A missing Game B history should load empty. " + loadBError);
                AssertEqual(1, reloadedA.Entries.Count, "Game A should reload exactly its own record.");
                AssertEqual(0, reloadedB.Entries.Count, "Game B must not see Game A's record.");
                AssertEqual(ReturningPeer.ToString(), reloadedA.Entries[0].SteamId, "SteamID64 must round-trip without numeric precision loss.");
                AssertEqual(DateTimeKind.Utc, reloadedA.Entries[0].ConnectedAtUtc.Kind, "Persisted lifecycle timestamps should reload as UTC.");

                string json = File.ReadAllText(reloadedA.FilePath);
                AssertTrue(json.Contains("\"steamId\": \"" + ReturningPeer + "\""), "SteamID64 must be represented as a quoted decimal string in JSON.");

                AssertTrue(reloadedA.TryClear(out string clearError), "Clearing Game A should persist successfully. " + clearError);
                AssertEqual(0, reloadedA.Entries.Count, "Clearing should remove Game A's visible records.");
                AssertEqual(0, reloadedB.Entries.Count, "Clearing Game A must not affect another game.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void ConnectionHistoryRetainsNewestFiveHundred()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                DateTime startedAt = new DateTime(2026, 7, 25, 19, 0, 0, DateTimeKind.Utc);
                var store = ConnectionHistoryStore.ForGame("history-retention", tempDirectory);

                for (int i = 0; i < 502; i++)
                {
                    DateTime connectedAt = startedAt.AddMinutes(i);
                    var entry = new PeerHistoryEntry(
                        ReturningPeer + (ulong)i,
                        "Player " + i,
                        40d + i,
                        "203.0.113.10",
                        connectedAt,
                        connectedAt.AddSeconds(30));
                    AssertTrue(store.TryAppend(entry, out string appendError), "Retention fixture entry " + i + " should save. " + appendError);
                }

                AssertEqual(ConnectionHistoryStore.MaximumEntryCount, store.Entries.Count, "History should retain exactly the newest 500 records.");
                AssertEqual((ReturningPeer + 501UL).ToString(), store.Entries[0].SteamId, "The newest completed connection should be first.");
                AssertEqual((ReturningPeer + 2UL).ToString(), store.Entries[store.Entries.Count - 1].SteamId, "The two oldest records should be trimmed.");

                var reloaded = ConnectionHistoryStore.ForGame("history-retention", tempDirectory);
                AssertTrue(reloaded.TryLoad(out string loadError), "The retained history should reload. " + loadError);
                AssertEqual(ConnectionHistoryStore.MaximumEntryCount, reloaded.Entries.Count, "The 500-record cap should survive reload.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void ConnectionHistoryQuarantinesMalformedFilesAndClears()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                string historyPath = Path.Combine(tempDirectory, "malformed.json");
                File.WriteAllText(historyPath, "{ this is not valid JSON");
                var store = new ConnectionHistoryStore(historyPath);

                AssertTrue(store.TryLoad(out string loadWarning), "Malformed history should be quarantined and recovered as empty. " + loadWarning);
                AssertTrue(!string.IsNullOrWhiteSpace(loadWarning), "Malformed recovery should report where the original file was preserved.");
                AssertEqual(0, store.Entries.Count, "Malformed history should not expose partial records.");
                AssertEqual(1, Directory.GetFiles(tempDirectory, "malformed.json.corrupt-*").Length, "The malformed source should be preserved exactly once.");
                AssertTrue(store.HasRecoveryCopies, "The recovered store should expose that a private recovery copy remains.");

                DateTime now = new DateTime(2026, 7, 25, 20, 0, 0, DateTimeKind.Utc);
                var recoveredEntry = new PeerHistoryEntry(ReturningPeer, "Recovered", 70d, null, now, now.AddMinutes(1));
                AssertTrue(store.TryAppend(recoveredEntry, out string appendError), "The recovered store should accept new records. " + appendError);
                AssertTrue(store.TryClear(out string clearError), "The recovered store should clear atomically. " + clearError);
                AssertEqual(0, Directory.GetFiles(tempDirectory, "malformed.json.corrupt-*").Length, "Clearing history should remove preserved recovery copies for that game.");
                AssertFalse(store.HasRecoveryCopies, "A successful clear should report that no recovery copies remain.");

                var reloaded = new ConnectionHistoryStore(historyPath);
                AssertTrue(reloaded.TryLoad(out string reloadError), "Cleared history should reload. " + reloadError);
                AssertEqual(0, reloaded.Entries.Count, "A successful clear should remain empty across reload.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void ConnectionHistoryRecoveryCleanupCanBeRetried()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                string historyPath = Path.Combine(tempDirectory, "cleanup-retry.json");
                File.WriteAllText(historyPath, "{ malformed recovery data");
                var store = new ConnectionHistoryStore(historyPath);
                AssertTrue(store.TryLoad(out string loadWarning), "Malformed history should recover before the cleanup retry test. " + loadWarning);

                string recoveryPath = Directory.GetFiles(tempDirectory, "cleanup-retry.json.corrupt-*")[0];
                using (var lockedRecovery = new FileStream(recoveryPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    AssertTrue(store.TryClear(out string cleanupWarning), "The canonical clear should succeed even while recovery-copy cleanup is blocked.");
                    AssertTrue(!string.IsNullOrWhiteSpace(cleanupWarning), "Blocked recovery-copy cleanup should return a warning.");
                    AssertTrue(store.HasRecoveryCopies, "The locked recovery copy should remain discoverable for a later retry.");
                }

                AssertTrue(store.TryClear(out string retryError), "Recovery-copy cleanup should be retryable after the file is unlocked. " + retryError);
                AssertTrue(string.IsNullOrWhiteSpace(retryError), "A successful cleanup retry should not return a warning.");
                AssertFalse(store.HasRecoveryCopies, "The cleanup retry should remove the private recovery copy.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void ConnectionHistoryFailedLoadCannotOverwriteExistingFile()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                string historyPath = Path.Combine(tempDirectory, "locked.json");
                byte[] originalBytes = System.Text.Encoding.UTF8.GetBytes("existing history that must not be overwritten");
                File.WriteAllBytes(historyPath, originalBytes);
                var store = new ConnectionHistoryStore(historyPath);

                using (var lockedFile = new FileStream(historyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    AssertTrue(!store.TryLoad(out string loadError), "An exclusively locked history file should fail to load.");
                    AssertTrue(!string.IsNullOrWhiteSpace(loadError), "A failed load should report a diagnostic error.");
                }

                DateTime now = new DateTime(2026, 7, 25, 20, 5, 0, DateTimeKind.Utc);
                var entry = new PeerHistoryEntry(ReturningPeer, "Must Not Save", 75d, "203.0.113.10", now, now.AddMinutes(1));
                AssertTrue(!store.TryAppend(entry, out string appendError), "A store that failed to load must reject writes.");
                AssertTrue(!string.IsNullOrWhiteSpace(appendError), "A rejected write should explain that history is read-only.");
                AssertEqual(
                    Convert.ToBase64String(originalBytes),
                    Convert.ToBase64String(File.ReadAllBytes(historyPath)),
                    "A failed load must leave the existing history file byte-for-byte unchanged.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void ConnectionHistoryWriteFailureIsIsolated()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                string blockedParent = Path.Combine(tempDirectory, "not-a-directory");
                File.WriteAllText(blockedParent, "This file intentionally prevents creation of a history directory.");
                var store = new ConnectionHistoryStore(Path.Combine(blockedParent, "history.json"));
                DateTime now = new DateTime(2026, 7, 25, 20, 10, 0, DateTimeKind.Utc);
                var tracker = new PeerConnectionHistoryTracker(store, () => now);
                var peer = new FakePeer(ReturningPeer) { CurrentPing = 80d };
                int completionEvents = 0;
                tracker.ConnectionCompleted += entry => completionEvents++;

                tracker.Observe(peer, 1);
                now = now.AddMinutes(1);
                PeerHistoryEntry completed = tracker.Complete(ReturningPeer, out string completionError);

                AssertTrue(completed == null, "A failed atomic write must not report a persisted completion.");
                AssertTrue(!string.IsNullOrWhiteSpace(completionError), "A failed write should provide a diagnostic error.");
                AssertEqual(0, completionEvents, "The UI completion event must not fire when persistence fails.");
                AssertEqual(0, store.Entries.Count, "A failed write must leave the store's previous in-memory state unchanged.");
                AssertEqual(0, tracker.ActiveConnectionCount, "A failed history write must not leave peer cleanup blocked on an active tracker record.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void ConnectionHistoryReplaceFailurePreservesExistingFile()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                string historyPath = Path.Combine(tempDirectory, "replace-failure.json");
                var store = new ConnectionHistoryStore(historyPath);
                DateTime now = new DateTime(2026, 7, 25, 20, 15, 0, DateTimeKind.Utc);
                var originalEntry = new PeerHistoryEntry(ReturningPeer, "Original", 50d, "198.51.100.20", now, now.AddMinutes(1));
                AssertTrue(store.TryAppend(originalEntry, out string initialError), "The original history fixture should save. " + initialError);
                byte[] originalBytes = File.ReadAllBytes(historyPath);

                var replacementEntry = new PeerHistoryEntry(OtherQuarantinedPeer, "Replacement", 70d, "203.0.113.20", now, now.AddMinutes(2));
                using (var lockedFile = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    AssertTrue(!store.TryAppend(replacementEntry, out string replaceError), "Replacing an exclusively locked history file should fail.");
                    AssertTrue(!string.IsNullOrWhiteSpace(replaceError), "A failed atomic replacement should report a diagnostic error.");
                }

                AssertEqual(
                    Convert.ToBase64String(originalBytes),
                    Convert.ToBase64String(File.ReadAllBytes(historyPath)),
                    "A failed atomic replacement must preserve the existing file byte-for-byte.");
                AssertEqual(1, store.Entries.Count, "A failed replacement must preserve the existing in-memory history.");
                AssertEqual(ReturningPeer.ToString(), store.Entries[0].SteamId, "The failed replacement must not expose the unsaved entry.");
                AssertEqual(0, Directory.GetFiles(tempDirectory, ".replace-failure.json.*.tmp").Length, "A failed replacement should clean up its temporary file.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void SteamPeerManagerFinalizesHistoryForEveryRemovalReason()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                int fixtureIndex = 0;
                foreach (PeerRemovalReason removalReason in Enum.GetValues(typeof(PeerRemovalReason)))
                {
                    ulong steamId = ReturningPeer + (ulong)fixtureIndex++;
                    DateTime now = new DateTime(2026, 7, 25, 20, 20, 0, DateTimeKind.Utc);
                    var store = ConnectionHistoryStore.ForGame("manager-" + removalReason, tempDirectory);
                    var tracker = new PeerConnectionHistoryTracker(store, () => now);
                    var peer = new FakePeer(steamId) { CurrentPing = 65d };
                    tracker.Observe(peer, 1);
                    now = now.AddMinutes(1);

                    int callbackCount = 0;
                    ulong callbackSteamId = 0;
                    PeerRemovalReason callbackReason = default(PeerRemovalReason);
                    SteamPeerManager.FinalizePeerRemoval(
                        new SteamPeerInfo(peer),
                        peer.SteamID,
                        removalReason,
                        tracker,
                        (removedSteamId, reason) =>
                        {
                            callbackCount++;
                            callbackSteamId = removedSteamId;
                            callbackReason = reason;
                        });

                    AssertEqual(1, store.Entries.Count, removalReason + " should persist exactly one completed connection.");
                    AssertEqual(0, tracker.ActiveConnectionCount, removalReason + " should finalize the active tracker record.");
                    AssertEqual(1, peer.DisposeCalls, removalReason + " should dispose the peer after history finalization.");
                    AssertEqual(1, callbackCount, removalReason + " should preserve the existing removal callback.");
                    AssertEqual(steamId, callbackSteamId, removalReason + " should preserve the callback SteamID.");
                    AssertEqual(removalReason, callbackReason, removalReason + " should preserve the callback reason.");
                }
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void SteamPeerManagerHistoryFailureDoesNotInterruptRemoval()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                string blockedParent = Path.Combine(tempDirectory, "not-a-directory");
                File.WriteAllText(blockedParent, "This file intentionally prevents creation of a history directory.");
                var store = new ConnectionHistoryStore(Path.Combine(blockedParent, "history.json"));
                DateTime now = new DateTime(2026, 7, 25, 20, 30, 0, DateTimeKind.Utc);
                var tracker = new PeerConnectionHistoryTracker(store, () => now);
                var peer = new FakePeer(ReturningPeer) { CurrentPing = 90d };
                tracker.Observe(peer, 1);
                now = now.AddMinutes(1);
                int callbackCount = 0;

                SteamPeerManager.FinalizePeerRemoval(
                    new SteamPeerInfo(peer),
                    peer.SteamID,
                    PeerRemovalReason.TransportTimeout,
                    tracker,
                    (removedSteamId, reason) => callbackCount++);

                AssertEqual(0, store.Entries.Count, "The synthetic history write failure should not create a record.");
                AssertEqual(0, tracker.ActiveConnectionCount, "The failed history write should still finalize the tracker state.");
                AssertEqual(1, peer.DisposeCalls, "A history failure must not prevent peer disposal.");
                AssertEqual(1, callbackCount, "A history failure must not prevent the existing removal callback.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
        }

        private static void SteamPeerManagerLoggingFailureDoesNotInterruptRemoval()
        {
            string tempDirectory = CreateHistoryTestDirectory();
            try
            {
                DateTime now = new DateTime(2026, 7, 25, 20, 40, 0, DateTimeKind.Utc);
                var store = ConnectionHistoryStore.ForGame("manager-log-failure", tempDirectory);
                var tracker = new PeerConnectionHistoryTracker(store, () => now);
                var peer = new FakePeer(ReturningPeer) { CurrentPing = 55d };
                tracker.Observe(peer, 1);
                now = now.AddMinutes(1);
                int callbackCount = 0;

                SteamPeerManager.FinalizePeerRemoval(
                    new SteamPeerInfo(peer),
                    peer.SteamID,
                    PeerRemovalReason.Shutdown,
                    tracker,
                    (removedSteamId, reason) => callbackCount++,
                    () => throw new IOException("Synthetic disconnect-log failure."));

                AssertEqual(1, store.Entries.Count, "History should be persisted before the disconnect log is attempted.");
                AssertEqual(0, tracker.ActiveConnectionCount, "A disconnect-log failure must not strand tracker state.");
                AssertEqual(1, peer.DisposeCalls, "A disconnect-log failure must not prevent peer disposal.");
                AssertEqual(1, callbackCount, "A disconnect-log failure must not prevent the existing removal callback.");
            }
            finally
            {
                DeleteHistoryTestDirectory(tempDirectory);
            }
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

                AssertEqual(2, firewall.BlockedPeerIds.Count, "The exact-flow block should be attempted for every current peer.");
                AssertEqual(ReturningPeer, firewall.BlockedPeerIds[0], "The first peer should be attempted first.");
                AssertEqual(OtherQuarantinedPeer, firewall.BlockedPeerIds[1], "A failed peer must not stop the next peer.");
                AssertEqual(1, firstPeer.CloseSessionCalls, "The first peer should still receive a CloseSession attempt after its block.");
                AssertEqual(1, secondPeer.CloseSessionCalls, "The second peer should receive a CloseSession attempt despite the first failure.");

                coordinator.RetryPendingManualBlocks();
                AssertEqual(2, firstPeer.CloseSessionCalls, "A thrown close must remain pending and be retried by the sustained scan.");
                AssertEqual(1, secondPeer.CloseSessionCalls, "A successfully closed unchanged endpoint must not be closed repeatedly.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockSingleActionRetriesUntilEvidenceAppears()
        {
            var peer = new FakePeer(ReturningPeer);
            var firewall = new FakeFirewallBlockService(failuresBeforeSuccess: 2);
            DateTime now = new DateTime(2026, 7, 23, 22, 48, 14, DateTimeKind.Utc);
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => new SteamPeerBase[] { peer },
                () => firewall,
                () => now);

            try
            {
                coordinator.BlockAllConnectedPeers();
                AssertEqual(1, firewall.BlockedPeerIds.Count, "A single hotkey action should make its first exact-flow attempt immediately.");
                AssertEqual(0, peer.CloseSessionCalls, "The Steam session must remain open while exact-flow evidence is unavailable.");

                coordinator.RetryPendingManualBlocks();
                AssertEqual(2, firewall.BlockedPeerIds.Count, "The pending action should retry while exact-flow evidence is unavailable.");
                AssertEqual(0, peer.CloseSessionCalls, "A failed retry must not close the Steam session.");

                coordinator.RetryPendingManualBlocks();
                AssertEqual(3, firewall.BlockedPeerIds.Count, "The same single action should retry until the exact-flow block succeeds.");
                AssertEqual(1, peer.CloseSessionCalls, "The successful retry should close the Steam session exactly once.");

                coordinator.RetryPendingManualBlocks();
                AssertEqual(4, firewall.BlockedPeerIds.Count, "A successful tuple must continue to be checked while sustained scanning is active.");
                AssertEqual(1, peer.CloseSessionCalls, "An unchanged successfully blocked endpoint must not be closed repeatedly.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockSinglePressTracksEndpointMigrations()
        {
            var peer = new FakePeer(ReturningPeer);
            var firewall = new FakeFirewallBlockService();
            DateTime now = new DateTime(2026, 7, 24, 10, 42, 18, DateTimeKind.Utc);
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => new SteamPeerBase[] { peer },
                () => firewall,
                () => now);

            try
            {
                coordinator.BlockAllConnectedPeers();
                AssertEqual("203.0.113.10:27015", firewall.BlockedEndpoints[0].ToString(), "The initial endpoint should be blocked immediately.");
                AssertEqual(1, peer.CloseSessionCalls, "The initial endpoint should receive one close request.");

                peer.Endpoint = new PeerNetworkEndpoint(IPAddress.Parse("203.0.113.10"), 27016);
                now = now.AddSeconds(8);
                coordinator.RetryPendingManualBlocks();
                AssertEqual("203.0.113.10:27016", firewall.BlockedEndpoints[1].ToString(), "The same press should follow the first endpoint migration.");
                AssertEqual(2, peer.CloseSessionCalls, "A newly blocked endpoint should receive a new close request.");

                coordinator.RetryPendingManualBlocks();
                AssertEqual(3, firewall.BlockedEndpoints.Count, "The active scanner should continue checking an unchanged tuple.");
                AssertEqual(2, peer.CloseSessionCalls, "Repeated checks of the same endpoint must not repeatedly close the session.");

                peer.Endpoint = new PeerNetworkEndpoint(IPAddress.Parse("203.0.113.10"), 27017);
                now = now.AddSeconds(13);
                coordinator.RetryPendingManualBlocks();
                AssertEqual("203.0.113.10:27017", firewall.BlockedEndpoints[3].ToString(), "Scanning must continue past twenty seconds when the game has not left the lobby.");
                AssertEqual(3, peer.CloseSessionCalls, "A second migrated endpoint should receive one close request.");

                coordinator.SteamPeerManager_LobbyLeft();
                coordinator.RetryPendingManualBlocks();
                AssertFalse(coordinator.IsManualBlockScanActive, "The scan should finish after both the minimum window and game lobby exit are satisfied.");

                peer.Endpoint = new PeerNetworkEndpoint(IPAddress.Parse("203.0.113.10"), 27018);
                coordinator.RetryPendingManualBlocks();
                AssertEqual(4, firewall.BlockedEndpoints.Count, "No further endpoint should be scanned after lobby-confirmed completion.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockFailedCloseIsRetriedUntilConfirmed()
        {
            var peer = new FakePeer(ReturningPeer, closeFailuresBeforeSuccess: 2);
            var firewall = new FakeFirewallBlockService();
            DateTime now = new DateTime(2026, 7, 24, 10, 42, 0, DateTimeKind.Utc);
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => new SteamPeerBase[] { peer },
                () => firewall,
                () => now);

            try
            {
                coordinator.BlockAllConnectedPeers();
                AssertEqual(1, peer.CloseSessionCalls, "The first close attempt should run immediately.");

                coordinator.RetryPendingManualBlocks();
                AssertEqual(2, peer.CloseSessionCalls, "A false close result must be retried on the next scan.");

                coordinator.RetryPendingManualBlocks();
                AssertEqual(3, peer.CloseSessionCalls, "Scanning should retry until Steam confirms the close.");

                coordinator.RetryPendingManualBlocks();
                AssertEqual(3, peer.CloseSessionCalls, "An unchanged session must not be closed again after Steam confirms success.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockPressWaitsForInitiallyMissingPeer()
        {
            SteamPeerBase[] currentPeers = Array.Empty<SteamPeerBase>();
            var firewall = new FakeFirewallBlockService();
            DateTime now = new DateTime(2026, 7, 24, 10, 43, 15, DateTimeKind.Utc);
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => currentPeers,
                () => firewall,
                () => now);

            try
            {
                coordinator.BlockAllConnectedPeers();
                AssertTrue(coordinator.IsManualBlockScanActive, "A press should start its scan even when no peer is visible on the immediate pass.");
                AssertEqual(0, firewall.BlockedEndpoints.Count, "No flow can be blocked before a peer appears.");

                var delayedPeer = new FakePeer(ReturningPeer);
                currentPeers = new SteamPeerBase[] { delayedPeer };
                now = now.AddSeconds(4);
                coordinator.RetryPendingManualBlocks();

                AssertEqual(1, firewall.BlockedEndpoints.Count, "The same press should discover and block a peer that appears during its scan window.");
                AssertEqual(1, delayedPeer.CloseSessionCalls, "The delayed peer should receive a close request without another hotkey press.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockScanSurvivesPeerReplacement()
        {
            var firstPeer = new FakePeer(ReturningPeer);
            SteamPeerBase[] currentPeers = { firstPeer };
            var firewall = new FakeFirewallBlockService();
            DateTime now = new DateTime(2026, 7, 24, 10, 43, 30, DateTimeKind.Utc);
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => currentPeers,
                () => firewall,
                () => now);

            try
            {
                coordinator.BlockAllConnectedPeers();
                currentPeers = Array.Empty<SteamPeerBase>();
                coordinator.SteamPeerManager_PeerRemoved(ReturningPeer, PeerRemovalReason.AuthSessionEnded);
                coordinator.RetryPendingManualBlocks();

                var replacementPeer = new FakePeer(ReturningPeer);
                coordinator.SteamPeerManager_PeerBeginAuthSession(ReturningPeer);
                currentPeers = new SteamPeerBase[] { replacementPeer };
                now = now.AddSeconds(5);
                coordinator.RetryPendingManualBlocks();

                AssertEqual(2, firewall.BlockedEndpoints.Count, "A replacement peer object with the same Steam ID should be reacquired and scanned.");
                AssertEqual("203.0.113.10:27015", firewall.BlockedEndpoints[1].ToString(), "A replacement session should be scanned even when it reuses the same endpoint.");
                AssertEqual(1, replacementPeer.CloseSessionCalls, "The replacement session should receive its own close request even when the endpoint is unchanged.");
                AssertTrue(coordinator.IsManualBlockScanActive, "AuthSessionEnded and BeginAuthSession must not complete sustained scanning.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockPressCoversDeadlineCrossingTick()
        {
            SteamPeerBase[] currentPeers = Array.Empty<SteamPeerBase>();
            var firewall = new FakeFirewallBlockService();
            DateTime startedUtc = new DateTime(2026, 7, 24, 10, 43, 25, DateTimeKind.Utc);
            DateTime now = startedUtc;
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => currentPeers,
                () => firewall,
                () => now);

            try
            {
                coordinator.BlockAllConnectedPeers();
                var boundaryPeer = new FakePeer(ReturningPeer);
                currentPeers = new SteamPeerBase[] { boundaryPeer };
                now = startedUtc.Add(P2PEnforcementCoordinator.ManualBlockMinimumScanWindow).AddMilliseconds(100);
                coordinator.RetryPendingManualBlocks();

                AssertEqual(1, boundaryPeer.CloseSessionCalls, "The first timer tick crossing the deadline must still process a peer that appeared near twenty seconds.");
                AssertTrue(coordinator.IsManualBlockScanActive, "A peer found on the deadline-crossing scan should remain tracked after the minimum window.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockScanRunsForTwentySecondsWithoutLobbyExit()
        {
            var peer = new FakePeer(ReturningPeer);
            var firewall = new FakeFirewallBlockService();
            DateTime startedUtc = new DateTime(2026, 7, 24, 10, 44, 41, DateTimeKind.Utc);
            DateTime now = startedUtc;
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => new SteamPeerBase[] { peer },
                () => firewall,
                () => now);

            try
            {
                AssertTrue(
                    P2PEnforcementCoordinator.ManualBlockMinimumScanWindow >= TimeSpan.FromSeconds(20),
                    "A single hotkey press must scan for at least twenty seconds.");

                coordinator.BlockAllConnectedPeers();
                now = startedUtc.Add(P2PEnforcementCoordinator.ManualBlockMinimumScanWindow).AddMilliseconds(-1);
                coordinator.RetryPendingManualBlocks();
                AssertTrue(coordinator.IsManualBlockScanActive, "The scan must remain active through the last millisecond of its minimum window.");

                now = startedUtc.Add(P2PEnforcementCoordinator.ManualBlockMinimumScanWindow);
                coordinator.RetryPendingManualBlocks();
                AssertTrue(coordinator.IsManualBlockScanActive, "The scan must continue beyond twenty seconds while the game remains in the lobby.");

                coordinator.SteamPeerManager_LobbyLeft();
                AssertFalse(coordinator.IsManualBlockScanActive, "The scan should complete when the game leaves the lobby after the minimum window.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockScanSurvivesPostMinimumReplacementGap()
        {
            var firstPeer = new FakePeer(ReturningPeer);
            SteamPeerBase[] currentPeers = { firstPeer };
            var firewall = new FakeFirewallBlockService();
            DateTime startedUtc = new DateTime(2026, 7, 24, 10, 44, 30, DateTimeKind.Utc);
            DateTime now = startedUtc;
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => currentPeers,
                () => firewall,
                () => now);

            try
            {
                coordinator.BlockAllConnectedPeers();
                currentPeers = Array.Empty<SteamPeerBase>();
                now = startedUtc.Add(P2PEnforcementCoordinator.ManualBlockMinimumScanWindow).AddSeconds(1);
                coordinator.SteamPeerManager_PeerRemoved(ReturningPeer, PeerRemovalReason.AuthSessionEnded);
                coordinator.RetryPendingManualBlocks();
                AssertTrue(coordinator.IsManualBlockScanActive, "A single absent-peer tick after twenty seconds must not end scanning during a replacement handoff.");

                var replacementPeer = new FakePeer(ReturningPeer);
                now = now.AddSeconds(1);
                coordinator.SteamPeerManager_PeerBeginAuthSession(ReturningPeer);
                currentPeers = new SteamPeerBase[] { replacementPeer };
                coordinator.RetryPendingManualBlocks();

                AssertEqual(1, replacementPeer.CloseSessionCalls, "A replacement session arriving during the post-minimum absence grace must still be closed.");
                AssertTrue(coordinator.IsManualBlockScanActive, "The reacquired tracked peer should keep scanning active.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockLobbyExitEndsScanImmediately()
        {
            var peer = new FakePeer(ReturningPeer);
            var firewall = new FakeFirewallBlockService();
            DateTime now = new DateTime(2026, 7, 24, 10, 45, 0, DateTimeKind.Utc);
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => new SteamPeerBase[] { peer },
                () => firewall,
                () => now);

            try
            {
                coordinator.BlockAllConnectedPeers();
                now = now.AddSeconds(5);
                coordinator.SteamPeerManager_LobbyLeft();
                AssertFalse(coordinator.IsManualBlockScanActive, "LeaveLobby should end the old lobby's scan immediately, even before twenty seconds.");

                peer.Endpoint = new PeerNetworkEndpoint(IPAddress.Parse("203.0.113.10"), 27018);
                coordinator.SteamPeerManager_PeerBeginAuthSession(ReturningPeer);
                coordinator.RetryPendingManualBlocks();
                AssertEqual(1, firewall.BlockedEndpoints.Count, "A new lobby must not inherit scanning from the completed action.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockDelayedTickCannotCrossSafetyLimit()
        {
            SteamPeerBase[] currentPeers = Array.Empty<SteamPeerBase>();
            var laterLobbyPeer = new FakePeer(OtherQuarantinedPeer);
            var firewall = new FakeFirewallBlockService();
            DateTime startedUtc = new DateTime(2026, 7, 24, 10, 45, 10, DateTimeKind.Utc);
            DateTime now = startedUtc;
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => currentPeers,
                () => firewall,
                () => now);

            try
            {
                coordinator.BlockAllConnectedPeers();
                currentPeers = new SteamPeerBase[] { laterLobbyPeer };
                now = startedUtc.Add(P2PEnforcementCoordinator.ManualBlockMaximumScanWindow).AddSeconds(1);
                coordinator.RetryPendingManualBlocks();

                AssertFalse(coordinator.IsManualBlockScanActive, "A delayed timer callback must complete immediately after the safety deadline.");
                AssertEqual(0, laterLobbyPeer.CloseSessionCalls, "A peer visible only after the safety deadline must never be closed by the stale action.");
                AssertEqual(0, firewall.BlockedEndpoints.Count, "The safety deadline must be checked before enrolling or blocking peers.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockScanDoesNotCrossLobbyWhenLeaveEventIsMissed()
        {
            var originalPeer = new FakePeer(ReturningPeer);
            var laterLobbyPeer = new FakePeer(OtherQuarantinedPeer);
            SteamPeerBase[] currentPeers = { originalPeer };
            var firewall = new FakeFirewallBlockService();
            DateTime startedUtc = new DateTime(2026, 7, 24, 10, 44, 50, DateTimeKind.Utc);
            DateTime now = startedUtc;
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => currentPeers,
                () => firewall,
                () => now);

            try
            {
                coordinator.BlockAllConnectedPeers();
                currentPeers = Array.Empty<SteamPeerBase>();
                now = startedUtc.Add(P2PEnforcementCoordinator.ManualBlockMinimumScanWindow);
                coordinator.RetryPendingManualBlocks();

                AssertTrue(coordinator.IsManualBlockScanActive, "The action should allow a brief replacement-session gap after its minimum window.");

                currentPeers = new SteamPeerBase[] { laterLobbyPeer };
                now = now.Add(P2PEnforcementCoordinator.EnforcementInterval);
                coordinator.RetryPendingManualBlocks();
                AssertEqual(0, laterLobbyPeer.CloseSessionCalls, "A peer first seen after the minimum window must not be enrolled into the old lobby's action.");

                now = startedUtc
                    .Add(P2PEnforcementCoordinator.ManualBlockMinimumScanWindow)
                    .Add(P2PEnforcementCoordinator.ManualBlockPeerAbsenceGracePeriod);
                coordinator.RetryPendingManualBlocks();
                AssertFalse(coordinator.IsManualBlockScanActive, "The action must end when the originally tracked peer remains absent through the grace period.");
                AssertEqual(1, firewall.BlockedEndpoints.Count, "A missed LeaveLobby event must not allow the old action to block a later-lobby peer.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ManualBlockScanStopsAtSafetyLimit()
        {
            var peer = new FakePeer(ReturningPeer);
            var firewall = new FakeFirewallBlockService();
            DateTime startedUtc = new DateTime(2026, 7, 24, 10, 44, 55, DateTimeKind.Utc);
            DateTime now = startedUtc;
            var coordinator = new P2PEnforcementCoordinator(
                123,
                () => new SteamPeerBase[] { peer },
                () => firewall,
                () => now);

            try
            {
                AssertTrue(
                    P2PEnforcementCoordinator.ManualBlockMaximumScanWindow > P2PEnforcementCoordinator.ManualBlockMinimumScanWindow,
                    "The safety limit must preserve the full minimum scan window.");

                coordinator.BlockAllConnectedPeers();
                now = startedUtc.Add(P2PEnforcementCoordinator.ManualBlockMaximumScanWindow).AddMilliseconds(-1);
                coordinator.RetryPendingManualBlocks();
                AssertTrue(coordinator.IsManualBlockScanActive, "A still-visible tracked peer should remain covered until the safety limit.");

                now = startedUtc.Add(P2PEnforcementCoordinator.ManualBlockMaximumScanWindow);
                coordinator.RetryPendingManualBlocks();
                AssertFalse(coordinator.IsManualBlockScanActive, "A missed LeaveLobby event must not leave scanning armed beyond the safety limit.");
            }
            finally
            {
                coordinator.Dispose();
            }
        }

        private static void ApplicationVersionIs150()
        {
            AssertEqual(new System.Version("1.5.0.0"), VersionCheck.CurrentVersion, "Application assembly metadata must identify version 1.5.0.");
            AssertEqual("v1.5.0", VersionCheck.CurrentVersionDisplay, "The displayed application version must identify v1.5.0.");
        }

        private static void ValidVersionFileIsParsed()
        {
            AssertTrue(VersionCheck.TryParseVersion(" 1.2.0 \r\n", out System.Version version), "A trimmed semantic version should be accepted.");
            AssertEqual(new System.Version("1.2.0"), version, "The parsed version should preserve all version components.");
        }

        private static void BlankAndMalformedVersionFilesAreRejected()
        {
            AssertFalse(VersionCheck.TryParseVersion("   ", out _), "A blank version file must be rejected.");
            AssertFalse(VersionCheck.TryParseVersion("version 1.2.0", out _), "A malformed version file must be rejected.");
        }

        private static void EqualAndOlderRemoteVersionsDoNotRequireAnUpdate()
        {
            System.Version localVersion = new System.Version("1.2.0");

            AssertFalse(VersionCheck.IsRemoteVersionNewer(localVersion, new System.Version("1.2.0")), "An equal remote version must not prompt for an update.");
            AssertFalse(VersionCheck.IsRemoteVersionNewer(localVersion, new System.Version("1.1.9")), "An older remote version must not prompt for an update.");
        }

        private static void NewerRemoteVersionIsComparedNumerically()
        {
            AssertTrue(VersionCheck.IsRemoteVersionNewer(new System.Version("1.2.9"), new System.Version("1.2.10")), "Version comparison must be numeric rather than lexical.");
        }

        private static void LatestVersionRequestRevalidatesAndCacheBusts()
        {
            using (HttpRequestMessage request = VersionCheck.CreateLatestVersionRequest("lifecycle-test"))
            {
                AssertEqual("/wardriven/steamp2pinfo-revised/master/version.md", request.RequestUri.AbsolutePath, "The version request must target the raw GitHub version file.");
                AssertEqual("?cacheBust=lifecycle-test", request.RequestUri.Query, "The version request must add a cache-busting query value.");
                AssertTrue(request.Headers.CacheControl != null && request.Headers.CacheControl.NoCache, "The version request must revalidate cached responses.");
            }
        }

        private sealed class FakePeer : SteamPeerBase
        {
            private readonly bool throwOnClose;
            private readonly int closeFailuresBeforeSuccess;

            public int CloseSessionCalls { get; private set; }
            public int DisposeCalls { get; private set; }
            public string PeerName { get; set; } = "Fake Player";
            public double CurrentPing { get; set; }
            public bool EndpointAvailable { get; set; } = true;
            public PeerNetworkEndpoint Endpoint { get; set; } = new PeerNetworkEndpoint(IPAddress.Parse("203.0.113.10"), 27015);

            public FakePeer(ulong steamId, bool throwOnClose = false, int closeFailuresBeforeSuccess = 0)
                : base(new CSteamID(steamId))
            {
                this.throwOnClose = throwOnClose;
                this.closeFailuresBeforeSuccess = closeFailuresBeforeSuccess;
            }

            public override bool IsOldAPI => false;
            public override string Name => PeerName;
            public override string ConnectionTypeName => "Fake";
            public override double Ping => CurrentPing;
            public override double ConnectionQuality => 1d;

            public override bool UpdatePeerInfo()
            {
                return true;
            }

            public override bool TryGetRemoteEndpoint(out PeerNetworkEndpoint peerEndpoint)
            {
                if (!EndpointAvailable || Endpoint == null)
                {
                    peerEndpoint = null;
                    return false;
                }

                peerEndpoint = Endpoint;
                return true;
            }

            public override bool CloseSession()
            {
                CloseSessionCalls++;
                if (throwOnClose)
                    throw new InvalidOperationException("Synthetic CloseSession failure.");
                return CloseSessionCalls > closeFailuresBeforeSuccess;
            }

            public override void Dispose()
            {
                DisposeCalls++;
            }
        }

        private sealed class FakeFirewallBlockService : IFirewallBlockService
        {
            private readonly int failuresBeforeSuccess;

            public List<ulong> BlockedPeerIds { get; } = new List<ulong>();
            public List<PeerNetworkEndpoint> BlockedEndpoints { get; } = new List<PeerNetworkEndpoint>();

            public FakeFirewallBlockService(int failuresBeforeSuccess = 0)
            {
                this.failuresBeforeSuccess = failuresBeforeSuccess;
            }

            public FirewallBlockResult Block(ulong steamId, PeerNetworkEndpoint endpoint)
            {
                BlockedPeerIds.Add(steamId);
                BlockedEndpoints.Add(endpoint);
                if (BlockedPeerIds.Count <= failuresBeforeSuccess)
                    return FirewallBlockResult.Failed("Synthetic exact-flow evidence is not available yet.");
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

        private static string CreateHistoryTestDirectory()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "SteamP2PInfo-HistoryTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void DeleteHistoryTestDirectory(string directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                Directory.Delete(directory, true);
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
