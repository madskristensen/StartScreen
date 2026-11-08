using System.Collections.Generic;
using System.Linq;

namespace StartScreen.Services.DevHub
{
    internal sealed class DevHubCredentialCache<T> where T : class
    {
        internal const int Capacity = 256;
        private readonly object _gate = new object();
        private readonly Dictionary<(string Resource, string Username, string Token, bool Bearer), Entry> _entries = new();
        private readonly TimeSpan _lifetime;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Func<int> _getGeneration;
        private int _generation;

        internal DevHubCredentialCache(TimeSpan lifetime, Func<DateTimeOffset> utcNow = null, Func<int> getGeneration = null)
        {
            _lifetime = lifetime;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _getGeneration = getGeneration ?? (() => DevHubCredentialHelper.CredentialGeneration);
        }

        internal int Generation => _getGeneration();

        internal bool IsCurrent(DevHubCredential credential, int generation) => credential != null
            && generation == Generation && credential.ExpiresOn > _utcNow();

        internal bool TryGetValue(DevHubCredential credential, string resource, out T value)
        {
            lock (_gate)
            {
                Prune();
                value = null;
                if (!IsCurrent(credential, _generation))
                    return false;
                if (!_entries.TryGetValue((resource, credential.Username, credential.Token, credential.IsBearer), out var entry))
                    return false;
                value = entry.Value;
                return true;
            }
        }

        internal bool Store(DevHubCredential credential, string resource, T value, int generation)
        {
            lock (_gate)
            {
                Prune();
                if (value == null || !IsCurrent(credential, generation))
                    return false;
                var now = _utcNow();
                var expiration = now.Add(_lifetime);
                _entries[(resource, credential.Username, credential.Token, credential.IsBearer)] = new Entry
                {
                    Value = value,
                    StoredAt = now,
                    ExpiresAt = credential.ExpiresOn < expiration ? credential.ExpiresOn : expiration,
                };
                while (_entries.Count > Capacity)
                    _entries.Remove(_entries.OrderBy(pair => pair.Value.StoredAt).First().Key);
                return true;
            }
        }

        internal void Remove(DevHubCredential credential, string resource)
        {
            lock (_gate)
                _entries.Remove((resource, credential.Username, credential.Token, credential.IsBearer));
        }

        private void Prune()
        {
            var generation = Generation;
            if (generation != _generation)
            {
                _entries.Clear();
                _generation = generation;
            }
            var now = _utcNow();
            foreach (var key in _entries.Where(pair => now < pair.Value.StoredAt || now >= pair.Value.ExpiresAt)
                .Select(pair => pair.Key).ToArray())
                _entries.Remove(key);
        }

        private sealed class Entry
        {
            internal T Value { get; set; }
            internal DateTimeOffset StoredAt { get; set; }
            internal DateTimeOffset ExpiresAt { get; set; }
        }
    }
}
