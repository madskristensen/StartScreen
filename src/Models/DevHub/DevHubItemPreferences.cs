using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StartScreen.Models.DevHub
{
    internal sealed class DevHubItemPreferences
    {
        private const int _maxEntries = 2000;
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        internal static string GetKey(DevHubWorkItem item)
        {
            if (item == null || item.IsCopilot || string.IsNullOrWhiteSpace(item.WebUrl))
                throw new ArgumentException("A repository work item with a URL is required.", nameof(item));
            return $"{item.Kind}:{item.WebUrl}";
        }

        internal bool IsPinned(DevHubWorkItem item) => !item.IsCopilot && !string.IsNullOrWhiteSpace(item.WebUrl)
            && _entries.TryGetValue(GetKey(item), out var entry) && entry.IsPinned;

        internal DateTime? GetSnoozedUntil(DevHubWorkItem item) => !item.IsCopilot && !string.IsNullOrWhiteSpace(item.WebUrl)
            && _entries.TryGetValue(GetKey(item), out var entry) ? entry.SnoozedUntilUtc : null;

        internal void SetPinned(DevHubWorkItem item, bool pinned, DateTime nowUtc)
        {
            Prune(nowUtc);
            var key = GetKey(item);
            var entry = GetOrCreate(key);
            entry.IsPinned = pinned;
            Prune(nowUtc);
        }

        internal void Snooze(DevHubWorkItem item, DateTime? untilUtc, DateTime nowUtc)
        {
            if (untilUtc.HasValue && (untilUtc.Value.Kind != DateTimeKind.Utc || untilUtc <= nowUtc))
                throw new ArgumentException("Snooze expiry must be a future UTC time.", nameof(untilUtc));
            Prune(nowUtc);
            GetOrCreate(GetKey(item)).SnoozedUntilUtc = untilUtc;
            Prune(nowUtc);
        }

        internal DateTime? NextExpiry(DateTime nowUtc) => _entries.Values
            .Where(entry => entry.SnoozedUntilUtc > nowUtc).Select(entry => entry.SnoozedUntilUtc).Min();

        private Entry GetOrCreate(string key)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                if (_entries.Count >= _maxEntries)
                    throw new InvalidOperationException("The pin and snooze limit has been reached. Unpin or unsnooze an item first.");
                _entries.Add(key, entry = new Entry());
            }
            return entry;
        }

        private void Prune(DateTime nowUtc)
        {
            foreach (var key in _entries.Where(pair => !pair.Value.IsPinned && !(pair.Value.SnoozedUntilUtc > nowUtc))
                .Select(pair => pair.Key).ToArray())
                _entries.Remove(key);
        }

        internal string ToJson(DateTime nowUtc)
        {
            Prune(nowUtc);
            return JsonSerializer.Serialize(_entries);
        }

        internal static DevHubItemPreferences FromJson(string json)
        {
            var result = new DevHubItemPreferences();
            if (string.IsNullOrWhiteSpace(json))
                return result;
            if (json.Length > 1024 * 1024)
                throw new InvalidDataException("Pin and snooze settings are too large.");
            var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json)
                ?? throw new InvalidDataException("Pin and snooze settings are invalid.");
            if (entries.Count > _maxEntries)
                throw new InvalidDataException("There are too many pin and snooze settings.");
            foreach (var pair in entries)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null
                    || (pair.Value.SnoozedUntilUtc.HasValue && pair.Value.SnoozedUntilUtc.Value.Kind != DateTimeKind.Utc))
                    throw new InvalidDataException("Pin and snooze settings contain an invalid entry.");
                result._entries.Add(pair.Key, pair.Value);
            }
            return result;
        }

        internal sealed class Entry
        {
            public bool IsPinned { get; set; }
            public DateTime? SnoozedUntilUtc { get; set; }
        }
    }
}
