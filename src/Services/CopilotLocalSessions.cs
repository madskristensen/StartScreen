using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace StartScreen.Services
{
    internal sealed class CopilotSessionDiscovery
    {
        internal IReadOnlyList<CopilotActivitySession> Sessions { get; set; } = Array.Empty<CopilotActivitySession>();
        internal string Status { get; set; }
    }

    internal static class CopilotLocalSessions
    {
        internal const int MaximumSessions = 100;

        internal sealed class DiscoveryCache
        {
            private readonly object _sync = new();
            private readonly Dictionary<string, CachedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
            private readonly Func<string, string> _readText;

            internal DiscoveryCache(Func<string, string> readText = null)
            {
                _readText = readText ?? ReadBoundedText;
            }

            internal CopilotActivitySession ReadSession(string path, string id, string home, Func<IDeserializer> getDeserializer, bool forceRefresh,
                CopilotSessionOrigin origin)
            {
                path = Path.GetFullPath(path);
                lock (_sync)
                {
                    var stamp = GetStamp(path);
                    CopilotActivitySession session;
                    if (!forceRefresh && _sessions.TryGetValue(path, out var cached) && cached.Stamp == stamp && cached.Origin == origin)
                        session = cached.Value;
                    else
                    {
                        _sessions.Remove(path);
                        session = ParseMetadata(_readText(path), id, home, getDeserializer(), origin);
                        if (GetStamp(path) == stamp)
                        {
                            _sessions[path] = new CachedSession { Stamp = stamp, Value = session, Origin = origin };
                            if (_sessions.Count > MaximumSessions)
                                _sessions.Remove(_sessions.Keys.First());
                        }
                    }
                    var copy = session?.Clone();
                    if (copy != null)
                        copy.SessionHome = home;
                    return copy;
                }
            }

            internal void PruneSessions(HashSet<string> paths)
            {
                lock (_sync)
                {
                    foreach (var path in _sessions.Keys.Where(path => !paths.Contains(path)).ToArray())
                        _sessions.Remove(path);
                }
            }

            private static (long Length, DateTime WrittenUtc, DateTime CreatedUtc) GetStamp(string path)
            {
                var file = new FileInfo(path);
                return (file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
            }

            private sealed class CachedSession
            {
                internal (long Length, DateTime WrittenUtc, DateTime CreatedUtc) Stamp;
                internal CopilotActivitySession Value;
                internal CopilotSessionOrigin Origin;
            }
        }

        internal static string VisualStudioHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "VisualStudio", "CopilotCli");
        internal static string DefaultHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
        internal static async Task<CopilotSessionDiscovery> ReadAsync(string home, CancellationToken token,
            DiscoveryCache cache = null, bool forceRefresh = false, CopilotSessionOrigin origin = CopilotSessionOrigin.Cli)
        {
            if (origin != CopilotSessionOrigin.Cli && origin != CopilotSessionOrigin.VisualStudio)
                throw new ArgumentOutOfRangeException(nameof(origin));
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            token.ThrowIfCancellationRequested();
            var sessions = new List<CopilotActivitySession>();
            var failures = 0;
            IDeserializer deserializer = null;
            IDeserializer GetDeserializer() => deserializer ??= new DeserializerBuilder().IgnoreUnmatchedProperties().WithDuplicateKeyChecking().Build();
            var metadataPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var root = Path.Combine(home, "session-state");
            if (Directory.Exists(root))
            {
                foreach (var directory in new DirectoryInfo(root).EnumerateDirectories()
                    .Where(directory => Guid.TryParseExact(directory.Name, "D", out _))
                    .OrderByDescending(directory => directory.LastWriteTimeUtc).Take(MaximumSessions))
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.Combine(directory.FullName, "workspace.yaml");
                    if (!File.Exists(path))
                        continue;
                    metadataPaths.Add(Path.GetFullPath(path));
                    try
                    {
                        var session = cache == null ? ParseMetadata(ReadBoundedText(path), directory.Name, home, GetDeserializer(), origin)
                            : cache.ReadSession(path, directory.Name, home, GetDeserializer, forceRefresh, origin);
                        if (session == null)
                            continue;
                        sessions.Add(session);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is YamlException
                        || ex is InvalidDataException || ex is FormatException)
                    {
                        await ex.LogAsync();
                        failures++;
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            cache?.PruneSessions(metadataPaths);
            return new CopilotSessionDiscovery
            {
                Sessions = sessions.OrderByDescending(session => session.LastActivity).ToArray(),
                Status = failures > 0 ? "Some local Copilot session metadata could not be read. Refresh to retry." : string.Empty,
            };
        }

        internal static CopilotActivitySession ParseMetadata(string yaml, string directoryId, string home,
            CopilotSessionOrigin origin = CopilotSessionOrigin.Cli)
        {
            return ParseMetadata(yaml, directoryId, home,
                new DeserializerBuilder().IgnoreUnmatchedProperties().WithDuplicateKeyChecking().Build(), origin);
        }

        private static CopilotActivitySession ParseMetadata(string yaml, string directoryId, string home, IDeserializer deserializer,
            CopilotSessionOrigin origin)
        {
            if (origin != CopilotSessionOrigin.Cli && origin != CopilotSessionOrigin.VisualStudio)
                throw new ArgumentOutOfRangeException(nameof(origin));
            if (yaml == null || yaml.Length > 65536)
                throw new InvalidDataException("Copilot session metadata is missing or too large.");
            var parser = new Parser(new StringReader(yaml));
            var depth = 0;
            while (parser.MoveNext())
            {
                if (parser.Current is YamlDotNet.Core.Events.MappingStart || parser.Current is YamlDotNet.Core.Events.SequenceStart)
                    depth++;
                else if (parser.Current is YamlDotNet.Core.Events.MappingEnd || parser.Current is YamlDotNet.Core.Events.SequenceEnd)
                    depth--;
                if (depth > 16 || parser.Current is YamlDotNet.Core.Events.AnchorAlias)
                    throw new InvalidDataException("Copilot session metadata contains unsupported nesting or aliases.");
            }
            var metadata = deserializer.Deserialize<SessionMetadata>(yaml)
                ?? throw new InvalidDataException("The Copilot session metadata is empty.");
            if (!Guid.TryParseExact(metadata.Id, "D", out var id) || !string.Equals(metadata.Id, directoryId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Copilot session ID does not match its saved directory.");
            // client_name records the creator. last_client_name changes when another app resumes it.
            var supported = origin == CopilotSessionOrigin.Cli ? metadata.ClientName == "github/cli"
                : metadata.ClientName == "visualstudio-chat" || metadata.ClientName == "microsoft/visualstudio-chat";
            if (!supported)
                return null;
            if (!DateTimeOffset.TryParse(metadata.UpdatedAt ?? metadata.CreatedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var updated))
                throw new InvalidDataException("The Copilot session has no valid activity timestamp.");
            return new CopilotActivitySession
            {
                Id = id.ToString("D"),
                Origin = origin,
                IsCached = origin == CopilotSessionOrigin.VisualStudio,
                SessionHome = home,
                Title = string.IsNullOrWhiteSpace(metadata.Name) ? "Untitled conversation" : metadata.Name,
                WorkingDirectory = metadata.WorkingDirectory,
                LastActivity = updated,
            };
        }

        internal static string ReadBoundedText(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 65536)
                throw new InvalidDataException("Copilot session metadata is too large.");
            using var reader = new StreamReader(stream);
            var buffer = new char[65537];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count > 65536)
                throw new InvalidDataException("Copilot session metadata is too large.");
            return new string(buffer, 0, count);
        }

        private sealed class SessionMetadata
        {
            [YamlMember(Alias = "id")]
            public string Id { get; set; }
            [YamlMember(Alias = "client_name")]
            public string ClientName { get; set; }
            [YamlMember(Alias = "name")]
            public string Name { get; set; }
            [YamlMember(Alias = "cwd")]
            public string WorkingDirectory { get; set; }
            [YamlMember(Alias = "updated_at")]
            public string UpdatedAt { get; set; }
            [YamlMember(Alias = "created_at")]
            public string CreatedAt { get; set; }
        }
    }
}