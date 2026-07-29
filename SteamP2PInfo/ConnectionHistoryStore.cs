using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Newtonsoft.Json;

namespace SteamP2PInfo
{
    internal sealed class ConnectionHistoryStore
    {
        internal const int CurrentFormatVersion = 1;
        internal const int MaximumEntryCount = 500;

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            Formatting = Formatting.Indented
        };

        private readonly object syncRoot = new object();
        private readonly List<PeerHistoryEntry> entries = new List<PeerHistoryEntry>();
        private readonly ReadOnlyCollection<PeerHistoryEntry> readOnlyEntries;
        private bool writesBlockedAfterLoadFailure;

        internal string FilePath { get; }

        internal IReadOnlyList<PeerHistoryEntry> Entries
        {
            get { return readOnlyEntries; }
        }

        internal bool HasRecoveryCopies
        {
            get
            {
                lock (syncRoot)
                {
                    try
                    {
                        string directory = Path.GetDirectoryName(FilePath);
                        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                            return false;

                        string searchPattern = Path.GetFileName(FilePath) + ".corrupt-*";
                        return Directory.EnumerateFiles(directory, searchPattern).Any();
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        internal ConnectionHistoryStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A history file path is required.", nameof(filePath));

            FilePath = Path.GetFullPath(filePath);
            readOnlyEntries = entries.AsReadOnly();
        }

        internal static ConnectionHistoryStore ForGame(string processName)
        {
            return ForGame(processName, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history"));
        }

        internal static ConnectionHistoryStore ForGame(string processName, string historyDirectory)
        {
            if (string.IsNullOrWhiteSpace(historyDirectory))
                throw new ArgumentException("A history directory is required.", nameof(historyDirectory));

            string safeProcessName = MakeFileNameSafe(processName);
            return new ConnectionHistoryStore(Path.Combine(historyDirectory, safeProcessName + ".json"));
        }

        internal bool TryLoad(out string error)
        {
            lock (syncRoot)
            {
                error = null;

                if (!File.Exists(FilePath))
                {
                    entries.Clear();
                    writesBlockedAfterLoadFailure = false;
                    return true;
                }

                try
                {
                    string json = File.ReadAllText(FilePath, Encoding.UTF8);
                    HistoryDocument document =
                        JsonConvert.DeserializeObject<HistoryDocument>(json, SerializerSettings);

                    if (document == null)
                        throw new InvalidDataException("The history document was empty.");

                    if (document.Version != CurrentFormatVersion)
                    {
                        throw new InvalidDataException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Unsupported history format version {0}.",
                                document.Version));
                    }

                    if (document.Entries == null)
                        throw new InvalidDataException("The history document did not contain an entries list.");

                    foreach (PeerHistoryEntry entry in document.Entries)
                    {
                        if (entry == null)
                            throw new InvalidDataException("The history document contained an empty entry.");

                        entry.NormalizeAfterLoad();
                        if (!entry.IsValid())
                            throw new InvalidDataException("The history document contained an invalid entry.");
                    }

                    List<PeerHistoryEntry> loadedEntries = document.Entries
                        .OrderByDescending(entry => entry.DisconnectedAtUtc)
                        .Take(MaximumEntryCount)
                        .ToList();

                    entries.Clear();
                    entries.AddRange(loadedEntries);
                    writesBlockedAfterLoadFailure = false;
                    return true;
                }
                catch (Exception ex) when (IsMalformedDocumentException(ex))
                {
                    string quarantinePath;
                    string quarantineError;
                    if (!TryQuarantineMalformedFile(out quarantinePath, out quarantineError))
                    {
                        writesBlockedAfterLoadFailure = true;
                        error = string.Format(
                            CultureInfo.CurrentCulture,
                            "Connection history could not be read ({0}) and the malformed file could not be preserved ({1}).",
                            ex.Message,
                            quarantineError);
                        return false;
                    }

                    entries.Clear();
                    writesBlockedAfterLoadFailure = false;
                    error = string.Format(
                        CultureInfo.CurrentCulture,
                        "Connection history was malformed and has been preserved at \"{0}\". An empty history was loaded. {1}",
                        quarantinePath,
                        ex.Message);
                    return true;
                }
                catch (Exception ex)
                {
                    writesBlockedAfterLoadFailure = true;
                    error = "Connection history could not be loaded: " + ex.Message;
                    return false;
                }
            }
        }

        internal bool TryAppend(PeerHistoryEntry entry, out string error)
        {
            if (entry == null)
            {
                error = "A history entry is required.";
                return false;
            }

            if (!entry.IsValid())
            {
                error = "The history entry was invalid.";
                return false;
            }

            lock (syncRoot)
            {
                if (writesBlockedAfterLoadFailure)
                {
                    error = "Connection history is read-only because its existing file could not be loaded safely.";
                    return false;
                }

                List<PeerHistoryEntry> updatedEntries = new List<PeerHistoryEntry>(entries.Count + 1)
                {
                    entry
                };
                updatedEntries.AddRange(entries);

                if (updatedEntries.Count > MaximumEntryCount)
                    updatedEntries.RemoveRange(MaximumEntryCount, updatedEntries.Count - MaximumEntryCount);

                if (!TryWrite(updatedEntries, out error))
                    return false;

                entries.Clear();
                entries.AddRange(updatedEntries);
                return true;
            }
        }

        internal bool TryClear(out string error)
        {
            lock (syncRoot)
            {
                if (writesBlockedAfterLoadFailure)
                {
                    error = "Connection history cannot be cleared because its existing file could not be loaded safely.";
                    return false;
                }

                if (!TryWrite(Array.Empty<PeerHistoryEntry>(), out error))
                    return false;

                entries.Clear();
                if (!TryDeleteQuarantinedFiles(out string quarantineCleanupError))
                    error = "Connection history was cleared, but preserved recovery copies could not be removed: " + quarantineCleanupError;
                return true;
            }
        }

        private bool TryWrite(IEnumerable<PeerHistoryEntry> historyEntries, out string error)
        {
            string directory = Path.GetDirectoryName(FilePath);
            string tempPath = null;
            error = null;

            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                    throw new InvalidOperationException("The history file does not have a parent directory.");

                Directory.CreateDirectory(directory);

                string fileName = Path.GetFileName(FilePath);
                tempPath = Path.Combine(
                    directory,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        ".{0}.{1}.tmp",
                        fileName,
                        Guid.NewGuid().ToString("N")));

                HistoryDocument document = new HistoryDocument
                {
                    Version = CurrentFormatVersion,
                    Entries = historyEntries.ToList()
                };
                string json = JsonConvert.SerializeObject(document, SerializerSettings);

                using (FileStream stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(FilePath))
                    File.Replace(tempPath, FilePath, null);
                else
                    File.Move(tempPath, FilePath);

                tempPath = null;
                return true;
            }
            catch (Exception ex)
            {
                error = "Connection history could not be saved: " + ex.Message;
                return false;
            }
            finally
            {
                if (tempPath != null)
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private bool TryQuarantineMalformedFile(out string quarantinePath, out string error)
        {
            quarantinePath = null;
            error = null;

            try
            {
                string directory = Path.GetDirectoryName(FilePath);
                string fileName = Path.GetFileName(FilePath);
                string suffix = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyyMMdd-HHmmss-fff}-{1}",
                    DateTime.UtcNow,
                    Guid.NewGuid().ToString("N").Substring(0, 8));
                quarantinePath = Path.Combine(directory, fileName + ".corrupt-" + suffix);
                File.Move(FilePath, quarantinePath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                quarantinePath = null;
                return false;
            }
        }

        private bool TryDeleteQuarantinedFiles(out string error)
        {
            error = null;

            try
            {
                string directory = Path.GetDirectoryName(FilePath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                    return true;

                string searchPattern = Path.GetFileName(FilePath) + ".corrupt-*";
                foreach (string quarantinePath in Directory.GetFiles(directory, searchPattern))
                    File.Delete(quarantinePath);

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool IsMalformedDocumentException(Exception ex)
        {
            return ex is JsonException ||
                   ex is InvalidDataException ||
                   ex is FormatException ||
                   ex is OverflowException;
        }

        private static string MakeFileNameSafe(string value)
        {
            string candidate = string.IsNullOrWhiteSpace(value) ? "unknown-game" : value.Trim();
            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(candidate.Length);

            foreach (char character in candidate)
                builder.Append(invalidCharacters.Contains(character) ? '_' : character);

            string result = builder.ToString().TrimEnd('.', ' ');
            return string.IsNullOrWhiteSpace(result) ? "unknown-game" : result;
        }

        [JsonObject(MemberSerialization.OptIn)]
        private sealed class HistoryDocument
        {
            [JsonProperty("version")]
            public int Version { get; set; }

            [JsonProperty("entries")]
            public List<PeerHistoryEntry> Entries { get; set; }
        }
    }
}
