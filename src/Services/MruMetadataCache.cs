using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;

namespace StartScreen.Services
{
    internal sealed class MruMetadataCache
    {
        internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(3);
        private readonly string _path;
        private readonly Func<DateTime> _utcNow;
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        internal MruMetadataCache(string path = null, Func<DateTime> utcNow = null)
        {
            _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StartScreen", "mru-metadata.json");
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal async Task LoadAsync(CancellationToken cancellationToken)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            try
            {
                var entries = await AtomicJsonCache.ReadAsync<Entry[]>(_path, cancellationToken);
                _entries.Clear();
                foreach (var entry in entries ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(entry?.Path) && entry.Metadata != null)
                        _entries[entry.Path] = entry;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                OutputLog.Info("Workspace", "Cached recent-project details are unavailable. Rebuilding them in the background.");
            }
        }

        internal void Apply(IEnumerable<MruItem> items, string activityContext = null, bool includeLocal = true)
        {
            foreach (var item in items)
            {
                if (_entries.TryGetValue(item.Path, out var entry) && entry.Type == item.Type)
                    item.ApplyMetadata(entry.Metadata, activityContext != null && entry.ActivityContext == activityContext, includeLocal);
            }
        }

        internal bool IsStale(MruItem item)
        {
            return !_entries.TryGetValue(item.Path, out var entry) || entry.Type != item.Type
                || entry.UpdatedUtc == default || _utcNow() - entry.UpdatedUtc >= Lifetime;
        }

        internal bool IsSummaryStale(MruItem item, string activityContext)
        {
            return activityContext == null || !_entries.TryGetValue(item.Path, out var entry)
                || entry.Type != item.Type || entry.ActivityContext != activityContext
                || entry.Metadata?.Git?.RemoteUrl != item.RemoteUrl
                || entry.SummaryUpdatedUtc == default || _utcNow() - entry.SummaryUpdatedUtc >= Lifetime;
        }

        internal void InvalidateSummaries()
        {
            foreach (var entry in _entries.Values)
                entry.SummaryUpdatedUtc = default;
        }

        internal void Update(IEnumerable<MruItem> items, bool local, string activityContext = null)
        {
            foreach (var item in items)
            {
                if (!_entries.TryGetValue(item.Path, out var entry) || entry.Type != item.Type)
                    _entries[item.Path] = entry = new Entry { Path = item.Path, Type = item.Type };
                if (entry.Metadata?.Git?.RemoteUrl != item.RemoteUrl)
                    entry.SummaryUpdatedUtc = default;
                entry.Metadata = item.CaptureMetadata();
                if (local)
                    entry.UpdatedUtc = _utcNow();
                else
                {
                    entry.ActivityContext = activityContext;
                    entry.SummaryUpdatedUtc = _utcNow();
                }
            }
        }

        internal async Task SaveAsync(IEnumerable<MruItem> items, CancellationToken cancellationToken)
        {
            var paths = new HashSet<string>(items.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
            var snapshot = _entries.Values.Where(entry => paths.Contains(entry.Path)).Select(entry => new Entry
            {
                Path = entry.Path,
                Type = entry.Type,
                UpdatedUtc = entry.UpdatedUtc,
                SummaryUpdatedUtc = entry.SummaryUpdatedUtc,
                ActivityContext = entry.ActivityContext,
                Metadata = entry.Metadata,
            }).ToArray();
            try
            {
                await AtomicJsonCache.WriteAsync(_path, snapshot, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                OutputLog.Info("Workspace", "Could not save recent-project details. The previous cache is retained.");
            }
        }

        internal sealed class Entry
        {
            public string Path { get; set; }
            public MruItemType Type { get; set; }
            public DateTime UpdatedUtc { get; set; }
            public DateTime SummaryUpdatedUtc { get; set; }
            public string ActivityContext { get; set; }
            public MruItem.Metadata Metadata { get; set; }
        }
    }
}
