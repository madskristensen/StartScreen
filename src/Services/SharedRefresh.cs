using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

namespace StartScreen.Services
{
    internal sealed class SharedRefresh<T>
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _successLifetime;

        internal SharedRefresh(Func<DateTime> utcNow = null, TimeSpan successLifetime = default)
        {
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _successLifetime = successLifetime;
        }

        internal async Task<T> RunAsync(string key, bool force, Func<CancellationToken, Task<T>> refresh,
            Func<T, bool> successful, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<T> task;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out Entry entry) ||
                    (entry.Task.IsCompleted && (force || _utcNow() >= entry.RetryUtc)))
                {
                    entry = new Entry();
                    _entries[key] = entry;
                    Entry current = entry;
                    entry.Task = Task.Run(async () =>
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                        try
                        {
                            T result = await refresh(deadline.Token);
                            lock (_gate)
                                current.RetryUtc = successful(result) ? _utcNow().Add(_successLifetime) : _utcNow().AddMinutes(1);
                            return result;
                        }
                        catch (Exception ex)
                        {
                            await ex.LogAsync();
                            lock (_gate)
                                current.RetryUtc = _utcNow().AddMinutes(1);
                            throw;
                        }
                    });
                    if (_entries.Count > 256)
                    {
                        foreach (string old in new List<string>(_entries.Keys))
                        {
                            if (old != key && _entries[old].Task.IsCompleted)
                                _entries.Remove(old);
                            if (_entries.Count <= 256)
                                break;
                        }
                    }
                }
                task = entry.Task;
            }
            T result = await task.WithCancellation(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        private sealed class Entry
        {
            internal Task<T> Task { get; set; }
            internal DateTime RetryUtc { get; set; }
        }
    }

    internal static class HttpRetryBackoff
    {
        private static readonly object _gate = new object();
        private static readonly Dictionary<string, DateTimeOffset> _retryUtc = new Dictionary<string, DateTimeOffset>();

        internal static bool IsDeferred(string url)
        {
            lock (_gate)
            {
                if (!_retryUtc.TryGetValue(url, out DateTimeOffset until))
                    return false;
                if (until > DateTimeOffset.UtcNow)
                    return true;
                _retryUtc.Remove(url);
                return false;
            }
        }

        internal static void Observe(string url, HttpResponseMessage response)
        {
            if (((int)response.StatusCode != 429 && (int)response.StatusCode != 503) || response.Headers.RetryAfter == null)
                return;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset until = response.Headers.RetryAfter?.Date
                ?? now.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            lock (_gate)
                _retryUtc[url] = until > now ? until : now;
        }
    }
}
