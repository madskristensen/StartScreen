using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;

namespace StartScreen.Services
{
    internal sealed class CopilotActivityCache
    {
        private const int _maxCacheBytes = 4 * 1024 * 1024;
        private static readonly SemaphoreSlim _writeGate = new(1, 1);
        internal static readonly string DefaultFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartScreen", "CopilotCache", "sessions.json");

        internal IReadOnlyList<CopilotActivitySession> Sessions { get; private set; } = Array.Empty<CopilotActivitySession>();

        internal static async Task<IReadOnlyList<CopilotActivitySession>> ReadAsync(string cacheFile, CancellationToken cancellationToken)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(cacheFile))
                return Array.Empty<CopilotActivitySession>();

            using var stream = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            if (stream.Length > _maxCacheBytes)
                throw new InvalidDataException("The Copilot history cache is too large.");
            var entries = await JsonSerializer.DeserializeAsync<List<CacheEntry>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The Copilot history cache contains no session list.");
            return entries.Take(100).Select(entry => entry == null || string.IsNullOrWhiteSpace(entry.Id)
                ? throw new InvalidDataException("The Copilot history cache contains an invalid session.")
                : new CopilotActivitySession
                {
                    Id = entry.Id,
                    Title = entry.Title,
                    WorkingDirectory = entry.WorkingDirectory,
                    LastActivity = entry.LastActivity,
                    IsTranscript = entry.IsTranscript,
                    IsCached = true,
                }).ToArray();
        }

        internal static async Task WriteAsync(string cacheFile, IReadOnlyList<CopilotActivitySession> sessions, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string temporaryFile = null;
            try
            {
                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(sessions.Take(100).Select(session => new CacheEntry
                {
                    Id = session.Id,
                    Title = session.Title,
                    WorkingDirectory = session.WorkingDirectory,
                    LastActivity = session.LastActivity,
                    IsTranscript = session.IsTranscript,
                }).ToArray());
                if (bytes.Length > _maxCacheBytes)
                    throw new InvalidDataException("The Copilot history cache is too large.");

                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                temporaryFile = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(cacheFile))
                    File.Replace(temporaryFile, cacheFile, null);
                else
                    File.Move(temporaryFile, cacheFile);
            }
            finally
            {
                try
                {
                    if (temporaryFile != null && File.Exists(temporaryFile))
                        File.Delete(temporaryFile);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
        }

        internal IReadOnlyList<CopilotActivitySession> Update(IReadOnlyList<CopilotActivitySession> current, bool complete)
        {
            var ids = new HashSet<string>(current.Select(session => session.Id), StringComparer.OrdinalIgnoreCase);
            var visible = complete ? current : current.Concat(Sessions.Where(session => !ids.Contains(session.Id))).ToArray();
            Sessions = visible.OrderByDescending(session => session.LastActivity).Take(100)
                .Select(session => session.AsCached()).ToArray();
            return visible;
        }

        private sealed class CacheEntry
        {
            public CacheEntry() { }
            public string Id { get; set; }
            public string Title { get; set; }
            public string WorkingDirectory { get; set; }
            public DateTimeOffset LastActivity { get; set; }
            public bool IsTranscript { get; set; }
        }
    }
}