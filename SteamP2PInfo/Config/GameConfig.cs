using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Windows.Controls;
using MahApps.Metro.Controls;

namespace SteamP2PInfo.Config
{
    public class GameConfig : INotifyPropertyChanged
    {
        public const double DefaultDisconnectPingThresholdMs = 100d;
        public const double MaximumDisconnectPingThresholdMs = 60000d;

        /// <summary>
        /// Name of the game process. Used to identify which config to load when attaching to a game, and to find the window.
        /// </summary>
        [JsonProperty("process_name")]
        public string ProcessName { get; set; } = "";

        /// <summary>
        /// The Steam App ID of the game.
        /// </summary>
        [JsonProperty("steam_appid")]
        public int SteamAppId { get; set; } = 0;

        /// <summary>
        /// If true, will call SteamFriends.SetPlayedWith on each detected peer. Mainly intended to add this
        /// feature to games that don't support it, like Elden Ring. 
        /// </summary>
        [JsonProperty("set_played_with")]
        [ConfigBindingElement("Set Played With", typeof(ToggleSwitch), "IsOnProperty",
            Tooltip: "If enabled, will make peers show up in the Steam \"Recent Players\" list.",
            UIElementProperties: new object[] {
                new object[] { "OnContent", "Yes" },
                new object[] { "OffContent", "No" }
            })]
        public bool SetPlayedWith { get; set; } = false;

        [JsonProperty("open_profile_in_overlay")]
        [ConfigBindingElement("Show profiles in Steam overlay", typeof(ToggleSwitch), "IsOnProperty",
            Tooltip: "If enabled, double clicking on a peer's name in the Session Info tab will open their profile\ninside the Steam overlay. Otherwise, the default browser is used.",
            UIElementProperties: new object[] {
                new object[] { "OnContent", "Yes" },
                new object[] { "OffContent", "No" }
            })]
        public bool OpenProfileInOverlay { get; set; } = true;

        /// <summary>
        /// If true, will dump peer information into a game-specific log file.
        /// </summary>
        [JsonProperty("log_activity")]
        [ConfigBindingElement("Log Activity", typeof(ToggleSwitch), "IsOnProperty",
            Tooltip: "If enabled, will log each peer connection / disconnection in a game-specific log file.",
            UIElementProperties: new object[] {
                new object[] { "OnContent", "Yes" },
                new object[] { "OffContent", "No" }
            })]
        public bool LogActivity { get; set; } = false;

        /// <summary>
        /// If true, the hotkey system will be enabled while attached to this game.
        /// </summary>
        [JsonProperty("hotkeys_enabled")]
        [ConfigBindingElement("Enable Hotkeys", typeof(ToggleSwitch), "IsOnProperty",
            Tooltip: "If enabled, the hotkey system will be active while this game is running.",
            UIElementProperties: new object[] {
                new object[] { "OnContent", "Yes" },
                new object[] { "OffContent", "No" }
            })]
        public bool HotkeysEnabled { get; set; } = true;

        /// <summary>
        /// If true, a sound will be played when a new multiplayer session is detected.
        /// </summary>
        [JsonProperty("play_sound_on_new_session")]
        [ConfigBindingElement("Play sound on new session", typeof(ToggleSwitch), "IsOnProperty",
            Tooltip: "If enabled, a sound will be played when a new multiplayer session is detected.",
            UIElementProperties: new object[] {
                new object[] { "OnContent", "Yes" },
                new object[] { "OffContent", "No" }
            })]
        public bool PlaySoundOnNewSession { get; set; } = false;

        /// <summary>
        /// If true, peers with a valid ping strictly greater than the configured threshold are blocked and disconnected.
        /// </summary>
        [JsonProperty("disconnect_high_ping_enabled")]
        [ConfigBindingElement("Disconnect high-ping peers", typeof(ToggleSwitch), "IsOnProperty",
            Tooltip: "If enabled, the first valid ping above the configured limit creates an exact UDP firewall block before closing the Steam P2P session.",
            UIElementProperties: new object[] {
                new object[] { "OnContent", "Yes" },
                new object[] { "OffContent", "No" }
            })]
        public bool DisconnectHighPingEnabled { get; set; } = false;

        /// <summary>
        /// Permit a narrowly scoped fallback when ETW proves that the exact P2P
        /// tuple is owned by steam.exe instead of the selected game process.
        /// </summary>
        [JsonProperty("allow_steam_owned_exact_flow_fallback")]
        [ConfigBindingElement("Allow Steam-owned exact-flow fallback", typeof(ToggleSwitch), "IsOnProperty",
            Tooltip: "If a high-ping peer's exact UDP flow is owned by steam.exe, block only that observed local-port/remote-IP/remote-port tuple. This can affect Steam traffic sharing the same tuple.",
            UIElementProperties: new object[] {
                new object[] { "OnContent", "Yes" },
                new object[] { "OffContent", "No" }
            })]
        public bool AllowSteamOwnedExactFlowFallback { get; set; } = false;

        /// <summary>
        /// Keep high-ping enforcement failures in the log without interrupting
        /// play with a modal notification.
        /// </summary>
        [JsonProperty("mute_high_ping_enforcement_error_notifications")]
        [ConfigBindingElement("Mute high-ping enforcement error notifications", typeof(ToggleSwitch), "IsOnProperty",
            Tooltip: "If enabled, high-ping enforcement failures remain in the game log but do not show a pop-up notification.",
            UIElementProperties: new object[] {
                new object[] { "OnContent", "Muted" },
                new object[] { "OffContent", "Show" }
            })]
        public bool MuteHighPingEnforcementErrorNotifications { get; set; } = false;

        private double disconnectPingThresholdMs = DefaultDisconnectPingThresholdMs;

        /// <summary>
        /// Ping threshold in milliseconds. Enforcement triggers only when ping is strictly greater than this value.
        /// </summary>
        [JsonProperty("disconnect_ping_threshold_ms")]
        [ConfigBindingElement("High-ping limit (ms)", typeof(NumericUpDown), "ValueProperty",
            Tooltip: "Disconnect peers when the first valid ping is strictly greater than this value. The default is 100 ms.",
            UIElementProperties: new object[] {
                new object[] { "Minimum", 1d },
                new object[] { "Maximum", MaximumDisconnectPingThresholdMs },
                new object[] { "Interval", 1d },
                new object[] { "ChangeValueOnTextChanged", true },
                new object[] { "SnapToMultipleOfInterval", true }
            })]
        public double DisconnectPingThresholdMs
        {
            get { return disconnectPingThresholdMs; }
            set
            {
                disconnectPingThresholdMs = !double.IsNaN(value) && !double.IsInfinity(value) && value > 0d && value <= MaximumDisconnectPingThresholdMs
                    ? value
                    : DefaultDisconnectPingThresholdMs;
            }
        }

        /// <summary>
        /// Overlay configuration for this game. Includes things like placement, enabled/disabled, etc.
        /// </summary>
        [JsonProperty("overlay")]
        [ConfigCategory("Overlay Config")]
        public OverlayConfig OverlayConfig { get; private set; } = new OverlayConfig();

        /// <summary>
        /// Configuration of the currently selected game.
        /// </summary>
        public static GameConfig Current { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Load a settings file as the current game settings, or create a new file if the game does not have associated settings yet.
        /// Returns true if this process name was not found and a new config file was created.
        /// </summary>
        /// <param name="processName"></param>
        public static bool LoadOrCreate(string processName)
        {
            if (!Directory.Exists("config"))
                Directory.CreateDirectory("config");

            if (!File.Exists($"config\\{processName}.json"))
            {
                Current = new GameConfig() { ProcessName = processName };
                Current.Save();
                return true;
            }
            else
            {
                string json = File.ReadAllText($"config\\{processName}.json");
                Current = JsonConvert.DeserializeObject<GameConfig>(json);
                return false;
            }
        }

        public void Save()
        {
            string json = JsonConvert.SerializeObject(Current, Formatting.Indented);
            File.WriteAllText($"config\\{Current.ProcessName}.json", json);
        }
    }
}
