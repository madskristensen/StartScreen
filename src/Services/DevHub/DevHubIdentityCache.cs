using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Services.DevHub
{
    internal sealed class DevHubIdentityCache
    {
        private readonly object _gate = new object();
        private readonly Dictionary<(string Url, string Username, string Token, bool Bearer), Entry> _entries = new();
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Func<int> _credentialGeneration;
        private int _generation;

        internal DevHubIdentityCache(Func<DateTimeOffset> utcNow = null, Func<int> credentialGeneration = null)
        {
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _credentialGeneration = credentialGeneration ?? (() => DevHubCredentialHelper.CredentialGeneration);
        }

        internal async Task<string> GetAsync(DevHubCredential credential, string url,
            Func<DevHubCredential, string, CancellationToken, Task<HttpResponseMessage>> send,
            bool gitHub, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entry entry = GetEntry(credential, url);
            await entry.Gate.WaitAsync(cancellationToken);
            try
            {
                lock (_gate)
                {
                    if (entry.Identity != null && _utcNow() < entry.ExpiresAt)
                        return entry.Identity;
                }

                var identity = await DevHubRepoSummaryFetcher.GetIdentityAsync(credential, url, send, gitHub, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    entry.Identity = identity;
                    entry.ExpiresAt = GetExpiration(credential);
                }
                return identity;
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        internal void Remember(DevHubCredential credential, string url, string identity)
        {
            Entry entry = GetEntry(credential, url);
            lock (_gate)
            {
                entry.Identity = identity;
                entry.ExpiresAt = GetExpiration(credential);
            }
        }

        private DateTimeOffset GetExpiration(DevHubCredential credential)
        {
            var expiration = _utcNow().AddMinutes(5);
            return credential.ExpiresOn < expiration ? credential.ExpiresOn : expiration;
        }

        private Entry GetEntry(DevHubCredential credential, string url)
        {
            lock (_gate)
            {
                var generation = _credentialGeneration();
                if (generation != _generation)
                {
                    _entries.Clear();
                    _generation = generation;
                }
                foreach (var key in _entries.Where(pair => pair.Value.Gate.CurrentCount > 0
                    && pair.Value.ExpiresAt <= _utcNow()).Select(pair => pair.Key).ToArray())
                    _entries.Remove(key);

                var cacheKey = (url, credential.Username, credential.Token, credential.IsBearer);
                if (!_entries.TryGetValue(cacheKey, out Entry entry))
                {
                    entry = new Entry();
                    _entries.Add(cacheKey, entry);
                }
                return entry;
            }
        }

        private sealed class Entry
        {
            internal SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);
            internal string Identity { get; set; }
            internal DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.MaxValue;
        }
    }
}