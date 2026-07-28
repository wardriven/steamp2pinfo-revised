using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using System.IO;
using Steamworks;
using System.Security.Permissions;
using System.Media;
using SteamP2PInfo.Config;

namespace SteamP2PInfo
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        private ObservableCollection<SteamPeerBase> peers;
        private ObservableCollection<PeerHistoryEntry> historyEntries;
        private OverlayWindow overlay;
        private Timer timer;
        private int timerTicks = 0;
        private int overlayHotkey = 0;
        private int manualBlockHotkey = 0;
        private int previousPeersAmount = 0;
        private P2PEnforcementCoordinator enforcementCoordinator;
        private GameConfig subscribedConfig;
        private ConnectionHistoryStore historyStore;
        private PeerConnectionHistoryTracker historyTracker;
        private string historyGameDisplayName;
        private bool historyStorageAvailable;
        private bool historyCleanupNeedsRetry;
        private bool historyClearInProgress;

        private const string STEAM_COMMAND = "log_ipc \"BeginAuthSession,EndAuthSession,LeaveLobby,SendClanChatMessage\"";
        private const int MAX_HISTORY_ENTRIES = 500;

        private WindowSelectDialog.WindowInfo wInfo;

        public MainWindow()
        {
            if (Process.GetProcessesByName("SteamP2PInfo").Length > 1)
            {
                MessageBox.Show("Cannot run 2 instances of Steam P2P Info at once.", "Program Already Running", MessageBoxButton.OK, MessageBoxImage.Stop);
                Close();
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Exception exception = e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject));
                DiagnosticLogger.WriteException("UNHANDLED ERROR", exception, "Unhandled CurrentDomain exception. Terminating: " + e.IsTerminating);
                ShowUnhandledException(exception, "CurrentDomain", e.IsTerminating);
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                DiagnosticLogger.WriteException("UNHANDLED ERROR", e.Exception, "Unobserved TaskScheduler exception.");
                ShowUnhandledException(e.Exception, "TaskScheduler", false);
            };
            Dispatcher.UnhandledException += (s, e) =>
            {
                DiagnosticLogger.WriteException("UNHANDLED ERROR", e.Exception, "Unhandled Dispatcher exception.");
                if (!Debugger.IsAttached)
                    ShowUnhandledException(e.Exception, "Dispatcher", true);
            };

            InitializeComponent();
            Closing += MainWindow_Closed;

            string staleRuleCleanupError = WindowsFirewallBlockService.RemoveStaleRules();
            if (staleRuleCleanupError != null)
            {
                MessageBox.Show($"Could not remove stale SteamP2PInfo firewall rules from an earlier run:\n\n{staleRuleCleanupError}",
                    "Firewall Cleanup Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            peers = new ObservableCollection<SteamPeerBase>();
            dataGridSession.DataContext = peers;
            historyEntries = new ObservableCollection<PeerHistoryEntry>();
            dataGridHistory.DataContext = historyEntries;
            UpdateHistoryTabState();
            Title = "Steam P2P INFO /W PingGuard [" + VersionCheck.CurrentVersionDisplay + "]";

            timer = new Timer(Timer_Tick, null, Timeout.Infinite, Timeout.Infinite);
            Settings.Default.PropertyChanged += Settings_PropertyChanged;

            StartUpdateCheck();
        }

        private void StartUpdateCheck()
        {
            DiagnosticLogger.Write("ACTION", "Checking for application updates.");
            _ = CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            System.Version latestVersion = await Task.Run(() => VersionCheck.FetchLatestVersionAsync());
            if (!VersionCheck.IsRemoteVersionNewer(VersionCheck.CurrentVersion, latestVersion))
            {
                DiagnosticLogger.Write("ACTION", "Update check completed; no newer release was found.");
                return;
            }

            string latestVersionDisplay = VersionCheck.FormatDisplayVersion(latestVersion);
            DiagnosticLogger.Write("ACTION", "Update check found version " + latestVersionDisplay + ".");
            linkUpdate.NavigateUri = new Uri(VersionCheck.ReleasesPageUrl);
            textUpdate.Text = string.Format("NEW VERSION ({0}), DOWNLOAD HERE", latestVersionDisplay);

            MetroDialogSettings dialogSettings = new MetroDialogSettings
            {
                ColorScheme = MetroDialogColorScheme.Accented,
                AffirmativeButtonText = "Open Releases",
                NegativeButtonText = "Later"
            };

            MessageDialogResult result = await this.ShowMessageAsync(
                "New Version Available",
                string.Format("{0} is available. You are currently using {1}.", latestVersionDisplay, VersionCheck.CurrentVersionDisplay),
                MessageDialogStyle.AffirmativeAndNegative,
                dialogSettings);

            if (result == MessageDialogResult.Affirmative)
                Process.Start(new ProcessStartInfo(VersionCheck.ReleasesPageUrl));
        }

        private void Timer_Tick(object o)
        {
            this.Invoke(() =>
            {
                // Necessary to close the program after the game exits, as SteamAPI_Shutdown isn't
                // sufficient to have steam recognize the game is no longer running
                if (!WinAPI.User32.IsWindow(wInfo.Handle))
                    Close();

                if (HotkeyManager.Enabled && !GameConfig.Current.HotkeysEnabled)
                    HotkeyManager.Disable();

                if (!HotkeyManager.Enabled && GameConfig.Current.HotkeysEnabled)
                    HotkeyManager.Enable();

                timerTicks = (timerTicks + 1) % 6;
                if (timerTicks == 0)
                {
                    // Rather not have the settings update on a loop, but 
                    // Fody generated OnChange seems to break PropertyChanged 
                    // for GameConfig. So do this for now.
                    GameConfig.Current?.Save();
                }

                // Parse Steam's IPC log every second so a replacement auth
                // session becomes visible promptly. Only the sixth poll emits
                // the legacy dummy IPC call used to encourage a Steam log flush.
                SteamPeerManager.UpdatePeerList(timerTicks == 0);

                peers.Clear();
                foreach (SteamPeerBase p in SteamPeerManager.GetPeers())
                    peers.Add(p);

                if (GameConfig.Current.PlaySoundOnNewSession)
                {
                    if (peers.Count > 0 && previousPeersAmount == 0)
                    {
                        SystemSounds.Beep.Play();
                    }
                    previousPeersAmount = peers.Count;
                }

                // Update session info column sizes
                foreach (var col in dataGridSession.Columns)
                {
                    col.Width = new DataGridLength(1, DataGridLengthUnitType.Pixel);
                    col.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
                }
                dataGridSession.UpdateLayout();

                // Update overlay column sizes
                foreach (var col in overlay.dataGrid.Columns)
                {
                    col.Width = new DataGridLength(1, DataGridLengthUnitType.Pixel);
                    col.Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells);
                }
                overlay.dataGrid.UpdateLayout();

                // Queue position update after the overlay has re-rendered
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(overlay.UpdatePosition));
                overlay.UpdateVisibility();
            });
        }

        private void ShowUnhandledException(Exception err, string type, bool fatal)
        {
            DiagnosticLogger.WriteException("UNHANDLED ERROR", err, string.Format("Unhandled exception source: {0}; fatal: {1}", type, fatal));
            MetroDialogSettings diagSettings = new MetroDialogSettings()
            {
                ColorScheme = MetroDialogColorScheme.Accented,
                AffirmativeButtonText = "Copy",
                NegativeButtonText = "Close"
            };

            SystemSounds.Exclamation.Play();
            var result = this.ShowModalMessageExternal($"Unhandled Exception: {err.GetType().Name}", $"{err.Message}\n{err.StackTrace}", MessageDialogStyle.AffirmativeAndNegative, diagSettings);
            if (result == MessageDialogResult.Affirmative)
                Clipboard.SetText($"{err.GetType().Name}: {err.Message}\n{err.StackTrace}");

            Close();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            DiagnosticLogger.Write("ACTION", "Application shutdown started.");
            timer?.Change(Timeout.Infinite, Timeout.Infinite);
            enforcementCoordinator?.Dispose();
            enforcementCoordinator = null;
            SteamPeerManager.Shutdown();
            if (historyTracker != null)
                historyTracker.ConnectionCompleted -= HistoryTracker_ConnectionCompleted;
            if (GameConfig.Current != null) GameConfig.Current.Save();
            Settings.Default.Save();
            if (overlay != null) overlay.Close();
            HotkeyManager.RemoveHotkey(overlayHotkey);
            HotkeyManager.RemoveHotkey(manualBlockHotkey);
            HotkeyManager.Disable();
            ETWPingMonitor.Stop();
            if (subscribedConfig != null)
                subscribedConfig.PropertyChanged -= GameConfig_PropertyChanged;
            Settings.Default.PropertyChanged -= Settings_PropertyChanged;
            DiagnosticLogger.Stop("Application shutdown completed.");
        }

        private void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Settings.Default.Save();
            object value = string.IsNullOrWhiteSpace(e.PropertyName) ? null : Settings.Default[e.PropertyName];
            DiagnosticLogger.WriteApplicationSetting(e.PropertyName, value);
        }

        private void SubscribeToDebugSetting()
        {
            if (subscribedConfig != null)
                subscribedConfig.PropertyChanged -= GameConfig_PropertyChanged;

            subscribedConfig = GameConfig.Current;
            if (subscribedConfig == null)
                return;

            subscribedConfig.PropertyChanged += GameConfig_PropertyChanged;
            if (subscribedConfig.DebugLoggingEnabled)
                StartDebugLogging(false);
        }

        private void GameConfig_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(GameConfig.DebugLoggingEnabled), StringComparison.Ordinal))
                return;

            if (GameConfig.Current.DebugLoggingEnabled)
                StartDebugLogging(true);
            else
                DiagnosticLogger.Stop("Debug logging disabled in the configuration.");
        }

        private void StartDebugLogging(bool showWarning)
        {
            if (showWarning)
            {
                MessageBox.Show(
                    "Debug logging should only be enabled while reproducing an issue for a GitHub bug report. " +
                    "The log includes application settings, Steam IDs, peer network endpoints, firewall activity, and hotkey presses. " +
                    "A new file will be created in the logs\\debug folder. Review it before sharing it publicly and turn debug logging off when you are finished.",
                    "Debug Logging for Bug Reports",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            if (DiagnosticLogger.TryStartNewSession(GameConfig.Current, out string logPath, out string error))
            {
                DiagnosticLogger.Write("ACTION", "A new debug session was started for the selected game. Log file: " + logPath);
                return;
            }

            MessageBox.Show(
                "Debug logging could not be started:\n\n" + error,
                "Debug Logging Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            GameConfig.Current.DebugLoggingEnabled = false;
        }


        private void headerFmt_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Process.Start("https://docs.microsoft.com/en-us/dotnet/standard/base-types/custom-date-and-time-format-strings");
        }

        private void webLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri));
            e.Handled = true;
        }

        private void labelGameState_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            if (wInfo == null)
            {
                WindowSelectDialog dialog = new WindowSelectDialog() { Owner = this };
                if (dialog.ShowDialog() == true)
                {
                    bool newConfigCreated = GameConfig.LoadOrCreate(dialog.SelectedWindow.ProcessName);
                    SubscribeToDebugSetting();
                    DiagnosticLogger.Write(
                        "ACTION",
                        string.Format(
                            "Selected game window '{0}' (process {1}, PID {2}); configuration {3}.",
                            dialog.SelectedWindow.Title,
                            dialog.SelectedWindow.ProcessName,
                            dialog.SelectedWindow.ProcessId,
                            newConfigCreated ? "created" : "loaded"));

                    if (!Directory.Exists(System.IO.Path.GetDirectoryName(Settings.Default.SteamLogPath)))
                    {
                        DiagnosticLogger.Write("ERROR", "Steam IPC log file directory does not exist: " + Settings.Default.SteamLogPath);
                        MessageBox.Show("Steam IPC log file directory does not exist. Please modify the config accordingly.", "Directory Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    if (GameConfig.Current.SteamAppId == 0)
                    {
                        string input = Microsoft.VisualBasic.Interaction.InputBox("Please enter the Steam App ID to use with this game:", "Steam App ID Required");
                        if (!uint.TryParse(input, out uint result))
                        {
                            DiagnosticLogger.Write("ERROR", "A valid numeric Steam App ID was not supplied.");
                            MessageBox.Show("Please input a valid number", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                        GameConfig.Current.SteamAppId = (int)result;
                    }

                    Environment.SetEnvironmentVariable("SteamAppId", GameConfig.Current.SteamAppId.ToString());
                    if (!SteamAPI.Init())
                    {
                        DiagnosticLogger.Write("ERROR", "Steam API initialization failed for App ID " + GameConfig.Current.SteamAppId + ".");
                        MessageBox.Show("Could not initialize Steam API. Make sure the provided AppId is valid!", "Steam API Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        GameConfig.Current.SteamAppId = 0;
                        GameConfig.Current.Save();
                        return;
                    }

                    wInfo = dialog.SelectedWindow;
                    DiagnosticLogger.Write("ACTION", "Steam API initialized successfully.");
                    InitializeHistoryForAttachedGame(wInfo.ProcessName, wInfo.Title);
                    SteamPeerManager.Init(historyTracker);

                    if(MustEnterSteamCommand())
                        SteamConsoleHelper();

                    HotkeyManager.RemoveHotkey(overlayHotkey);
                    overlayHotkey = HotkeyManager.AddHotkey(wInfo.Handle, () => GameConfig.Current.OverlayConfig.Hotkey, () => GameConfig.Current.OverlayConfig.Enabled ^= true);

                    overlay = new OverlayWindow(wInfo.Handle, wInfo.ProcessId, wInfo.ThreadId);
                    overlay.dataGrid.DataContext = peers;
                    if (!overlay.InstallMsgHook())
                    {
                        DiagnosticLogger.Write("ERROR", "Failed to install the overlay window message hook.");
                        overlay.Close();
                        MessageBox.Show("Failed to setup overlay message hook", "WINAPI Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        Close();
                        return;
                    }

                    textGameState.Text = wInfo.Title;
                    textGameState.Foreground = Brushes.LawnGreen;

                    Grid configEditor = ConfigUIBuilder.CreateConfigEditor(GameConfig.Current);
                    int creditsIndex = ConfigTab.Children.IndexOf(ConfigCredits);
                    if (creditsIndex >= 0)
                    {
                        ConfigTab.Children.Insert(creditsIndex, configEditor);
                    }
                    else
                    {
                        ConfigTab.Children.Add(configEditor);
                    }

                    ETWPingMonitor.Start();
                    ETWPingMonitor.TrackProcessUdpFlows((int)wInfo.ProcessId);
                    DiagnosticLogger.Write("ACTION", "ETW UDP monitoring started for PID " + wInfo.ProcessId + ".");
                    enforcementCoordinator = new P2PEnforcementCoordinator((int)wInfo.ProcessId);
                    enforcementCoordinator.Start();
                    DiagnosticLogger.Write("ACTION", "P2P enforcement monitoring started.");
                    HotkeyManager.RemoveHotkey(manualBlockHotkey);
                    manualBlockHotkey = HotkeyManager.AddHotkey(
                        wInfo.Handle,
                        () => GameConfig.Current != null && GameConfig.Current.HotkeysEnabled
                            ? GameConfig.Current.ManualBlockHotkey
                            : 0,
                        () => enforcementCoordinator?.BlockAllConnectedPeers());
                    timer.Change(0, 1000);
                }
            }
        }

        private bool MustEnterSteamCommand()
        {
            DateTime ipcLogDate;
            String startupDateString = null;

            // Check if the program was recently updated -- We'll want to enter the command again if so
            if (Settings.Default.LastRunVersion != VersionCheck.CurrentVersionDisplay)
            {
                Settings.Default.LastRunVersion = VersionCheck.CurrentVersionDisplay;
                Settings.Default.Save();
                return true;
            }

            if (!File.Exists(Settings.Default.SteamLogPath))
                return true;

            try
            {
                ipcLogDate = File.GetLastWriteTime(Settings.Default.SteamLogPath);

            } catch (Exception ex)
            {
                DiagnosticLogger.WriteException("ERROR", ex, "Could not read the Steam IPC log timestamp.");
                return true;
            }

            try
            {
                using (FileStream stream = new FileStream(Settings.Default.SteamBootstrapLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var reader = new ReverseTextReader(stream, Encoding.UTF8);
                    var today = DateTime.Today;
                    int dateCheckCountdown = 20;
                    while (!reader.EndOfStream)
                    {
                        String line = reader.ReadLine();
                        if (line.Trim().Length == 0)
                            continue;
                        else if (line.Contains("Startup - updater built"))
                        {
                            int substringStartIndex = line.IndexOf("[") + 1;
                            startupDateString = line.Substring(substringStartIndex, line.IndexOf("]") - substringStartIndex);
                            break;
                        }
                        else
                        {
                            if (--dateCheckCountdown == 0)
                            {
                                int substringStartIndex = line.IndexOf("[") + 1;
                                DateTime lineDate = DateTime.Parse(line.Substring(substringStartIndex, line.IndexOf("]") - substringStartIndex));
                                if (today.Subtract(lineDate).TotalHours > 24)
                                    return true; // let's assume Steam hasn't been running for 24+ hours
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.WriteException("ERROR", ex, "Could not inspect the Steam bootstrap log.");
                return true;
            }

            return startupDateString == null || DateTime.Parse(startupDateString) > ipcLogDate;
        }

        private void SteamConsoleHelper()
        {
            Process.Start(new ProcessStartInfo("steam://open/console"));

            MetroDialogSettings diagSettings = new MetroDialogSettings()
            {
                ColorScheme = MetroDialogColorScheme.Accented,
                AffirmativeButtonText = "Copy Command",
                NegativeButtonText = "Close"
            };

            var result = this.ShowModalMessageExternal(
                "Necessary Step", $"The Steam console has just been opened. Please enter the following to enable matchmaking call logging: '{STEAM_COMMAND}'",
                MessageDialogStyle.AffirmativeAndNegative, diagSettings
            );
            if (result == MessageDialogResult.Affirmative)
            {
                try
                {
                    Clipboard.SetText(STEAM_COMMAND);
                }
                catch (Exception e)
                {
                    DiagnosticLogger.WriteException("ERROR", e, "Failed to copy the Steam console command to the clipboard.");
                    MessageBox.Show($"Failed to copy command to clipboard. Please enter '{STEAM_COMMAND}' manually.\n\n {e}", "Write to Clipboard Failed!", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void InitializeHistoryForAttachedGame(string processName, string gameTitle)
        {
            if (historyTracker != null)
                historyTracker.ConnectionCompleted -= HistoryTracker_ConnectionCompleted;

            historyStore = ConnectionHistoryStore.ForGame(processName);
            historyEntries.Clear();

            bool historyLoaded = historyStore.TryLoad(out string historyError);
            historyStorageAvailable = historyLoaded;
            historyCleanupNeedsRetry = historyLoaded && historyStore.HasRecoveryCopies;
            if (!string.IsNullOrWhiteSpace(historyError))
                DiagnosticLogger.Write(historyLoaded ? "HISTORY" : "ERROR", historyError);

            if (!historyLoaded)
            {
                MessageBox.Show(
                    "Connection history for this game could not be loaded. The existing file was left unchanged, and history recording is disabled for this attachment.\n\n" + historyError,
                    "Connection History Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            if (historyLoaded)
            {
                foreach (PeerHistoryEntry entry in historyStore.Entries)
                    historyEntries.Add(entry);

                historyTracker = new PeerConnectionHistoryTracker(historyStore);
                historyTracker.ConnectionCompleted += HistoryTracker_ConnectionCompleted;
            }
            else
            {
                historyTracker = null;
            }

            historyGameDisplayName = string.IsNullOrWhiteSpace(gameTitle) ? processName : gameTitle;
            UpdateHistoryTabState();
        }

        private void HistoryTracker_ConnectionCompleted(PeerHistoryEntry entry)
        {
            if (entry == null)
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => HistoryTracker_ConnectionCompleted(entry)));
                return;
            }

            historyEntries.Insert(0, entry);
            while (historyEntries.Count > MAX_HISTORY_ENTRIES)
                historyEntries.RemoveAt(historyEntries.Count - 1);
            UpdateHistoryTabState();
        }

        private void UpdateHistoryTabState()
        {
            bool attached = historyStore != null;
            bool hasEntries = attached && historyStorageAvailable && historyEntries.Count > 0;
            bool hasRecoveryCopies =
                attached &&
                historyStorageAvailable &&
                (historyCleanupNeedsRetry || historyStore.HasRecoveryCopies);

            if (!attached)
            {
                textHistoryScope.Text = "No game attached";
                textHistoryEmpty.Text = "Attach a game to view that game's connection history.";
            }
            else if (!historyStorageAvailable)
            {
                textHistoryScope.Text = historyGameDisplayName + " — history unavailable";
                textHistoryEmpty.Text = "Existing history could not be loaded and was left unchanged.";
            }
            else
            {
                textHistoryScope.Text = string.Format(
                    "{0} — {1} of {2} saved connection{3}",
                    historyGameDisplayName,
                    historyEntries.Count,
                    MAX_HISTORY_ENTRIES,
                    historyEntries.Count == 1 ? "" : "s");
                textHistoryEmpty.Text = hasRecoveryCopies
                    ? "No readable records remain. Use Clear History to remove preserved recovery copies for this game."
                    : "No previous connections have been recorded for this game yet.";
            }

            dataGridHistory.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
            textHistoryEmpty.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;
            buttonClearHistory.IsEnabled =
                historyStorageAvailable &&
                (hasEntries || hasRecoveryCopies) &&
                !historyClearInProgress;
        }

        private async void buttonClearHistory_Click(object sender, RoutedEventArgs e)
        {
            bool hasRecoveryCopies =
                historyStore != null &&
                historyStorageAvailable &&
                (historyCleanupNeedsRetry || historyStore.HasRecoveryCopies);
            if (historyStore == null ||
                !historyStorageAvailable ||
                (historyEntries.Count == 0 && !hasRecoveryCopies) ||
                historyClearInProgress)
            {
                return;
            }

            historyClearInProgress = true;
            UpdateHistoryTabState();
            try
            {
                MetroDialogSettings dialogSettings = new MetroDialogSettings
                {
                    ColorScheme = MetroDialogColorScheme.Accented,
                    AffirmativeButtonText = "Clear History",
                    NegativeButtonText = "Cancel"
                };

                string confirmationMessage;
                if (historyEntries.Count == 0)
                {
                    confirmationMessage = string.Format(
                        "Delete preserved connection-history recovery copies for {0}? This cannot be undone.",
                        historyGameDisplayName);
                }
                else
                {
                    confirmationMessage = string.Format(
                        "Delete all {0} saved connection record{1} for {2}{3}? This cannot be undone.",
                        historyEntries.Count,
                        historyEntries.Count == 1 ? "" : "s",
                        historyGameDisplayName,
                        hasRecoveryCopies ? " and its preserved recovery copies" : "");
                }

                MessageDialogResult result = await this.ShowMessageAsync(
                    "Clear connection history?",
                    confirmationMessage,
                    MessageDialogStyle.AffirmativeAndNegative,
                    dialogSettings);

                if (result != MessageDialogResult.Affirmative)
                    return;

                if (!historyStore.TryClear(out string historyError))
                {
                    DiagnosticLogger.Write("ERROR", "Could not clear connection history: " + historyError);
                    MessageBox.Show(
                        "Connection history could not be cleared. No records were removed.\n\n" + historyError,
                        "Connection History Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                historyEntries.Clear();
                historyCleanupNeedsRetry =
                    !string.IsNullOrWhiteSpace(historyError) ||
                    historyStore.HasRecoveryCopies;
                if (!string.IsNullOrWhiteSpace(historyError))
                {
                    DiagnosticLogger.Write("ERROR", historyError);
                    MessageBox.Show(
                        historyError,
                        "Connection History Warning",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                historyClearInProgress = false;
                UpdateHistoryTabState();
            }
        }

        private void dataGridSession_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject dep = (DependencyObject)e.OriginalSource;
            while ((dep != null) && !(dep is DataGridRow))
            {
                dep = VisualTreeHelper.GetParent(dep);
            }
            if (dep == null) return;

            if (dep is DataGridRow)
            {
                DataGridRow row = dep as DataGridRow;
                if (GameConfig.Current.OpenProfileInOverlay)
                    SteamFriends.ActivateGameOverlayToUser("steamid", peers[row.GetIndex()].SteamID);
                else
                    Process.Start($"https://steamcommunity.com/profiles/{peers[row.GetIndex()].SteamID}");
            }
        }
    }
}
