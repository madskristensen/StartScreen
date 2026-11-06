using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models;

namespace StartScreen.Services
{
    internal sealed class CopilotActivityCache
    {
        internal static readonly string DefaultFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartScreen", "CopilotCache", "sessions.json");

        internal IReadOnlyList<CopilotActivitySession> Sessions { get; private set; } = Array.Empty<CopilotActivitySession>();

        internal static async Task<IReadOnlyList<CopilotActivitySession>> ReadAsync(string cacheFile, CancellationToken cancellationToken)
        {
            var entries = await AtomicJsonCache.ReadAsync<List<CacheEntry>>(cacheFile, cancellationToken).ConfigureAwait(false);
            if (entries == null && !File.Exists(cacheFile))
                return Array.Empty<CopilotActivitySession>();
            if (entries == null)
                throw new InvalidDataException("The Copilot history cache contains no session list.");
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
            cancellationToken.ThrowIfCancellationRequested();
            await AtomicJsonCache.WriteAsync(cacheFile, sessions.Take(100).Select(session => new CacheEntry
            {
                Id = session.Id,
                Title = session.Title,
                WorkingDirectory = session.WorkingDirectory,
                LastActivity = session.LastActivity,
                IsTranscript = session.IsTranscript,
            }), cancellationToken).ConfigureAwait(false);
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