using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using StartScreen.Models.DevHub;

namespace StartScreen.Services
{
    internal static class CopilotWorkItemSessions
    {
        private static readonly SemaphoreSlim _gate = new(1, 1);
        internal static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartScreen", "CopilotCache", "work-items.json");

        internal static string GetKey(DevHubWorkItem item)
        {
            if (!CopilotWorkItemPrompt.CanStartChat(item))
                throw new ArgumentException("Select an issue or failed build.", nameof(item));
            // Build IDs can be query parameters, so retain the query and discard only the fragment.
            var uri = new UriBuilder(PullRequestNavigationService.GetValidatedUri(item.WebUrl)) { Fragment = string.Empty };
            return item.Kind + ":" + uri.Uri.AbsoluteUri;
        }

        internal static async System.Threading.Tasks.Task<string> FindAsync(string path, DevHubWorkItem item, CancellationToken cancellationToken)
        {
            var key = GetKey(item);
            var entries = await AtomicJsonCache.ReadAsync<List<Entry>>(path, cancellationToken);
            var entry = entries?.FirstOrDefault(candidate => candidate.Key == key);
            return Guid.TryParseExact(entry?.SessionId, "D", out _) ? entry.SessionId : null;
        }

        internal static async Task SaveAsync(string path, DevHubWorkItem item, string sessionId, CancellationToken cancellationToken)
        {
            var key = GetKey(item);
            if (!Guid.TryParseExact(sessionId, "D", out _))
                throw new ArgumentException("A valid Copilot conversation ID is required.", nameof(sessionId));
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var entries = await AtomicJsonCache.ReadAsync<List<Entry>>(path, cancellationToken) ?? new List<Entry>();
                var updated = new[] { new Entry { Key = key, SessionId = sessionId } }
                    .Concat(entries.Where(entry => entry.Key != key)).Take(500).ToArray();
                await AtomicJsonCache.WriteAsync(path, updated, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        internal sealed class Entry
        {
            public string Key { get; set; }
            public string SessionId { get; set; }
        }
    }
}
