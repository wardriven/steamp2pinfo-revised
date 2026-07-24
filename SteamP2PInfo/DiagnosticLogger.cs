using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

using Newtonsoft.Json;

using SteamP2PInfo.Config;

namespace SteamP2PInfo
{
    /// <summary>
    /// Opt-in, session-scoped diagnostics intended to accompany GitHub bug reports.
    /// Logging must never be allowed to interrupt normal application behaviour.
    /// </summary>
    internal static class DiagnosticLogger
    {
        private static readonly object SyncRoot = new object();
        private static StreamWriter writer;
        private static string currentLogPath;
        private static string lastSettingsSnapshot;

        public static bool IsEnabled
        {
            get
            {
                lock (SyncRoot)
                    return writer != null;
            }
        }

        public static string CurrentLogPath
        {
            get
            {
                lock (SyncRoot)
                    return currentLogPath;
            }
        }

        public static bool TryStartNewSession(GameConfig config, out string logPath, out string error)
        {
            lock (SyncRoot)
            {
                CloseWriter();
                logPath = null;
                error = null;

                try
                {
                    string logDirectory = Path.GetFullPath(Path.Combine("logs", "debug"));
                    Directory.CreateDirectory(logDirectory);

                    string processName = MakeFileNameSafe(config == null ? null : config.ProcessName);
                    string fileName = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}-debug-{1:yyyyMMdd-HHmmss-fff}-{2}.log",
                        processName,
                        DateTime.Now,
                        Guid.NewGuid().ToString("N").Substring(0, 8));

                    currentLogPath = Path.Combine(logDirectory, fileName);
                    writer = new StreamWriter(
                        new FileStream(currentLogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false));
                    writer.AutoFlush = true;
                    lastSettingsSnapshot = null;

                    WriteLineUnsafe("SESSION", "Debug logging enabled. This file is intended for a GitHub bug report.");
                    WriteLineUnsafe("SESSION", "Log file: " + currentLogPath);
                    WriteEnvironmentUnsafe();
                    WriteSettingsIfChangedUnsafe(config, "Initial settings");

                    logPath = currentLogPath;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    CloseWriter();
                    return false;
                }
            }
        }

        public static void Stop(string reason = null)
        {
            lock (SyncRoot)
            {
                if (writer == null)
                    return;

                try
                {
                    WriteLineUnsafe("SESSION", string.IsNullOrWhiteSpace(reason) ? "Debug logging disabled." : reason);
                }
                catch
                {
                    // Best-effort logging must not interfere with shutdown or config changes.
                }
                finally
                {
                    CloseWriter();
                }
            }
        }

        public static void Write(string category, string message)
        {
            lock (SyncRoot)
            {
                if (writer == null)
                    return;

                try
                {
                    WriteLineUnsafe(category, message);
                }
                catch
                {
                    // A diagnostic write failure must not alter application behaviour.
                }
            }
        }

        public static void WriteException(string category, Exception exception, string context = null)
        {
            if (exception == null)
                return;

            string message = string.IsNullOrWhiteSpace(context)
                ? exception.ToString()
                : context + Environment.NewLine + exception;
            Write(category, message);
        }

        public static void WriteSettingsIfChanged(GameConfig config, string reason)
        {
            lock (SyncRoot)
            {
                if (writer == null)
                    return;

                try
                {
                    WriteSettingsIfChangedUnsafe(config, reason);
                }
                catch (Exception ex)
                {
                    try
                    {
                        WriteLineUnsafe("ERROR", "Could not record settings: " + ex);
                    }
                    catch
                    {
                        // A failed fallback write must not affect application behaviour.
                    }
                }
            }
        }

        public static void WriteApplicationSetting(string name, object value)
        {
            string displayValue = string.Equals(name, "SteamWebApiKey", StringComparison.OrdinalIgnoreCase)
                ? "<redacted>"
                : Convert.ToString(value, CultureInfo.InvariantCulture);
            Write("SETTINGS", string.Format("Application setting changed: {0}={1}", name, displayValue));
        }

        private static void WriteEnvironmentUnsafe()
        {
            WriteLineUnsafe("ENVIRONMENT", "Application version: " + VersionCheck.CurrentVersionDisplay);
            WriteLineUnsafe("ENVIRONMENT", "OS: " + Environment.OSVersion);
            WriteLineUnsafe("ENVIRONMENT", ".NET runtime: " + Environment.Version);
            WriteLineUnsafe("ENVIRONMENT", "64-bit OS: " + Environment.Is64BitOperatingSystem + "; 64-bit process: " + Environment.Is64BitProcess);
            WriteLineUnsafe("ENVIRONMENT", "Culture: " + CultureInfo.CurrentCulture.Name + "; UI culture: " + CultureInfo.CurrentUICulture.Name);
            WriteLineUnsafe("ENVIRONMENT", "Process ID: " + Process.GetCurrentProcess().Id);
            WriteLineUnsafe("SETTINGS", "Steam IPC log path: " + Settings.Default.SteamLogPath);
            WriteLineUnsafe("SETTINGS", "Steam bootstrap log path: " + Settings.Default.SteamBootstrapLogPath);
            WriteLineUnsafe("SETTINGS", "Steam Web API key: <redacted>");
        }

        private static void WriteSettingsIfChangedUnsafe(GameConfig config, string reason)
        {
            if (config == null)
                return;

            string snapshot = JsonConvert.SerializeObject(config, Formatting.Indented);
            if (string.Equals(lastSettingsSnapshot, snapshot, StringComparison.Ordinal))
                return;

            lastSettingsSnapshot = snapshot;
            WriteLineUnsafe("SETTINGS", (string.IsNullOrWhiteSpace(reason) ? "Settings" : reason) + ":" + Environment.NewLine + snapshot);
        }

        private static void WriteLineUnsafe(string category, string message)
        {
            string normalizedCategory = string.IsNullOrWhiteSpace(category) ? "DEBUG" : category.Trim().ToUpperInvariant();
            string normalizedMessage = (message ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
            string prefix = string.Format(
                CultureInfo.InvariantCulture,
                "[{0:yyyy-MM-dd HH:mm:ss.fff zzz}] [thread {1}] [{2}] ",
                DateTimeOffset.Now,
                Environment.CurrentManagedThreadId,
                normalizedCategory);

            string[] lines = normalizedMessage.Split(new[] { '\n' });
            foreach (string line in lines)
                writer.WriteLine(prefix + line);
        }

        private static string MakeFileNameSafe(string value)
        {
            string result = string.IsNullOrWhiteSpace(value) ? "SteamP2PInfo" : value.Trim();
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
                result = result.Replace(invalidCharacter, '_');
            return result;
        }

        private static void CloseWriter()
        {
            if (writer != null)
            {
                try
                {
                    writer.Flush();
                    writer.Dispose();
                }
                catch
                {
                    // There is no safe recovery path for a failed diagnostic writer close.
                }
            }

            writer = null;
            currentLogPath = null;
            lastSettingsSnapshot = null;
        }
    }
}
