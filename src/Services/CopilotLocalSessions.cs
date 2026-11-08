using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
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
        internal static string VisualStudioHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "VisualStudio", "CopilotCli");
        internal static string DefaultHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
        internal static string CodeCatalog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "agentSessionData");

        internal static async Task<CopilotSessionDiscovery> ReadAsync(string home, string codeCatalog, CancellationToken token)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            var sessions = new List<CopilotActivitySession>();
            var failures = 0;
            var unsupported = 0;
            var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().WithDuplicateKeyChecking().Build();
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
                    try
                    {
                        var session = ParseMetadata(ReadBoundedText(path), directory.Name, home, deserializer);
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
            var codeSessions = sessions.Where(session => session.Origin == CopilotSessionOrigin.VisualStudioCode).ToArray();
            if (codeSessions.Length > 0)
            {
                var mappings = ReadCodeMappings(codeCatalog, token, ex =>
                {
                    ex.LogAsync().FileAndForget(nameof(CopilotLocalSessions));
                    failures++;
                }, new HashSet<string>(codeSessions.Select(session => session.Id), StringComparer.OrdinalIgnoreCase));
                foreach (var session in codeSessions)
                {
                    token.ThrowIfCancellationRequested();
                    if (!mappings.TryGetValue(session.Id, out var native))
                    {
                        unsupported++;
                        sessions.Remove(session);
                        continue;
                    }
                    session.NativeSessionId = native.NativeSessionId;
                    session.IsWorkspaceless = native.IsWorkspaceless;
                    session.WorkingDirectory = native.IsWorkspaceless ? null : native.WorkingDirectory ?? session.WorkingDirectory;
                }
            }
            return new CopilotSessionDiscovery
            {
                Sessions = sessions.OrderByDescending(session => session.LastActivity).ToArray(),
                Status = string.Join(" ", new[]
                {
                    failures > 0 ? "Some local Copilot session metadata could not be read. Refresh to retry." : null,
                    unsupported > 0 ? "Some VS Code sessions have no compatible native-session mapping and are not shown." : null,
                }.Where(message => message != null)),
            };
        }

        internal static CopilotActivitySession ParseMetadata(string yaml, string directoryId, string home)
        {
            return ParseMetadata(yaml, directoryId, home,
                new DeserializerBuilder().IgnoreUnmatchedProperties().WithDuplicateKeyChecking().Build());
        }

        private static CopilotActivitySession ParseMetadata(string yaml, string directoryId, string home, IDeserializer deserializer)
        {
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
            var origin = metadata.ClientName switch
            {
                "github/cli" => CopilotSessionOrigin.Cli,
                "vscode-agent-host" => CopilotSessionOrigin.VisualStudioCode,
                _ => (CopilotSessionOrigin?)null,
            };
            if (!origin.HasValue)
                return null;
            if (!DateTimeOffset.TryParse(metadata.UpdatedAt ?? metadata.CreatedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var updated))
                throw new InvalidDataException("The Copilot session has no valid activity timestamp.");
            return new CopilotActivitySession
            {
                Id = id.ToString("D"),
                Origin = origin.Value,
                SessionHome = home,
                Title = string.IsNullOrWhiteSpace(metadata.Name) ? "Untitled conversation" : metadata.Name,
                WorkingDirectory = metadata.WorkingDirectory,
                LastActivity = updated,
            };
        }

        internal static Dictionary<string, CodeSessionMetadata> ReadCodeMappings(string catalog, CancellationToken token,
            Action<Exception> onError, HashSet<string> requiredSessionIds = null)
        {
            var mappings = new Dictionary<string, CodeSessionMetadata>(StringComparer.OrdinalIgnoreCase);
            var remaining = requiredSessionIds == null ? null : new HashSet<string>(requiredSessionIds, StringComparer.OrdinalIgnoreCase);
            token.ThrowIfCancellationRequested();
            if (remaining?.Count == 0)
                return mappings;
            if (!Directory.Exists(catalog))
                return mappings;
            foreach (var directory in new DirectoryInfo(catalog).EnumerateDirectories()
                .Where(directory => Guid.TryParseExact(directory.Name, "D", out _))
                .OrderByDescending(directory => directory.LastWriteTimeUtc).Take(200))
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(directory.FullName, "session.db");
                if (!File.Exists(path))
                    continue;
                try
                {
                    var metadata = ReadCodeMetadata(path);
                    if (metadata != null && (remaining == null || remaining.Contains(metadata.RuntimeId))
                        && !mappings.ContainsKey(metadata.RuntimeId))
                    {
                        metadata.NativeSessionId = directory.Name;
                        mappings.Add(metadata.RuntimeId, metadata);
                        remaining?.Remove(metadata.RuntimeId);
                        if (remaining?.Count == 0)
                            break;
                    }
                }
                catch (Exception ex) when (ex is SqliteException || ex is IOException || ex is UnauthorizedAccessException
                    || ex is JsonException || ex is InvalidDataException)
                {
                    onError(ex);
                }
            }
            return mappings;
        }

        internal static string ReadCodeMapping(string path) => ReadCodeMetadata(path)?.RuntimeId;

        private static CodeSessionMetadata ReadCodeMetadata(string path)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM session_metadata WHERE key IN ('defaultChatProviderData', 'agentHost.workspaceless', 'copilot.workingDirectory')";
            using var reader = command.ExecuteReader();
            var metadata = new CodeSessionMetadata();
            while (reader.Read())
            {
                var value = reader.GetString(1);
                if (value.Length > 16384)
                    throw new InvalidDataException("VS Code session metadata is too large.");
                switch (reader.GetString(0))
                {
                    case "defaultChatProviderData":
                        using (var json = JsonDocument.Parse(value))
                        {
                            if (json.RootElement.ValueKind != JsonValueKind.Object)
                                throw new InvalidDataException("VS Code session metadata is not an object.");
                            if (json.RootElement.TryGetProperty("sdkSessionId", out var id) && id.ValueKind == JsonValueKind.String
                                && Guid.TryParseExact(id.GetString(), "D", out var parsed))
                                metadata.RuntimeId = parsed.ToString("D");
                        }
                        break;
                    case "agentHost.workspaceless":
                        if (!bool.TryParse(value, out var workspaceless))
                            throw new InvalidDataException("VS Code session workspace metadata is not a boolean.");
                        metadata.IsWorkspaceless = workspaceless;
                        break;
                    case "copilot.workingDirectory":
                        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile)
                            throw new InvalidDataException("VS Code session workspace metadata is not a file URI.");
                        var directory = uri.LocalPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                        // VS Code encodes the drive colon, which .NET Framework leaves as a rooted URI path.
                        if (directory.Length >= 4 && directory[0] == Path.DirectorySeparatorChar && char.IsLetter(directory[1])
                            && directory[2] == Path.VolumeSeparatorChar && directory[3] == Path.DirectorySeparatorChar)
                            directory = directory.Substring(1);
                        metadata.WorkingDirectory = directory;
                        break;
                }
            }
            return metadata.RuntimeId == null ? null : metadata;
        }

        internal sealed class CodeSessionMetadata
        {
            internal string RuntimeId { get; set; }
            internal string NativeSessionId { get; set; }
            internal string WorkingDirectory { get; set; }
            internal bool IsWorkspaceless { get; set; }
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