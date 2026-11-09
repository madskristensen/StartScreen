using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;

namespace StartScreen.Services
{
    internal sealed partial class EventService
    {
        internal const string FeedUrl = "https://raw.githubusercontent.com/madskristensen/StartScreen/master/events.json";
        internal static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        private static readonly HttpClient _defaultClient = CreateClient();
        internal static EventService Instance { get; } = new EventService();
        private readonly HttpClient _client;
        private readonly string _cacheFolder;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        internal EventService(HttpClient client = null, string cacheFolder = null, Func<DateTimeOffset> utcNow = null)
        {
            _client = client ?? _defaultClient;
            _cacheFolder = cacheFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StartScreen", "FeedCache", "Events");
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("User-Agent", "Start Screen for Visual Studio");
            return client;
        }

        internal async Task<EventFeedResult> GetAsync(bool force, CancellationToken token)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            await _gate.WaitAsync(token);
            try
            {
                var path = Path.Combine(_cacheFolder, "events.json");
                Snapshot cached = null;
                try
                {
                    cached = await AtomicJsonCache.ReadAsync<Snapshot>(path, token, _jsonOptions);
                    if (cached != null)
                        Validate(cached.Catalog);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is JsonException || ex is UnauthorizedAccessException)
                {
                    await ex.LogAsync();
                    cached = null;
                }
                token.ThrowIfCancellationRequested();
                if (!force && cached != null && IsFresh(cached.FetchedAtUtc, _utcNow()))
                    return Present(cached, cached: true);

                Snapshot downloaded;
                try
                {
                    using var response = await _client.GetAsync(FeedUrl, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    var json = await HttpResponseReader.ReadStringAsync(response, token, 1024 * 1024);
                    var catalog = Parse(json);
                    downloaded = new Snapshot { Catalog = catalog, FetchedAtUtc = _utcNow() };
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    token.ThrowIfCancellationRequested();
                    if (cached == null)
                        return new EventFeedResult { IsUnavailable = true, IsStale = true,
                            Message = "Events are unavailable. Select Events again or use Refresh to retry." };
                    var stale = Present(cached, cached: true);
                    stale.IsStale = true;
                    stale.Message = "Could not update events. Showing the cached schedule; select Events again or use Refresh to retry.";
                    return stale;
                }

                var result = Present(downloaded, cached: false);
                try
                {
                    await AtomicJsonCache.WriteAsync(path, downloaded, token, _jsonOptions);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    await ex.LogAsync();
                    result.Message = "Events updated, but the seven-day disk cache could not be saved.";
                }
                token.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        internal static bool IsFresh(DateTimeOffset fetchedAtUtc, DateTimeOffset utcNow)
            => fetchedAtUtc <= utcNow && utcNow - fetchedAtUtc < CacheLifetime;

        internal static EventCatalog Parse(string json)
        {
            var catalog = JsonSerializer.Deserialize<EventCatalog>(json, _jsonOptions);
            Validate(catalog);
            return catalog;
        }

        private static void Validate(EventCatalog catalog)
        {
            if (catalog?.Version != 1 || catalog.Events == null || catalog.Events.Count > 250)
                throw new InvalidDataException("The events feed has an unsupported version or invalid event list.");
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in catalog.Events)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name))
                    throw new InvalidDataException("Every event needs a name.");
                if (!item.StartDate.HasValue)
                    continue;
                if (item.StartDate.Value.TimeOfDay != TimeSpan.Zero
                    || item.EndDate.HasValue && (item.EndDate.Value.TimeOfDay != TimeSpan.Zero || item.EndDate < item.StartDate)
                    || string.IsNullOrWhiteSpace(item.Description) || string.IsNullOrWhiteSpace(item.Location))
                    throw new InvalidDataException("A dated event needs a description, location, and a valid calendar date range: " + item.Name);
                RequireHttps(item.Url);
                RequireHttps(item.ThumbnailUrl);
                if (item.HasSubscriberDiscount)
                    RequireHttps(item.SubscriberDiscountUrl);
                if (!urls.Add(item.Url))
                    throw new InvalidDataException("The events feed contains a duplicate event URL: " + item.Url);
            }
        }

        private static void RequireHttps(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("Event links and thumbnails must use absolute HTTPS URLs.");
        }

        private EventFeedResult Present(Snapshot snapshot, bool cached)
        {
            var today = _utcNow().LocalDateTime.Date;
            var events = snapshot.Catalog.Events.Where(item => item.StartDate.HasValue
                    && (item.EndDate ?? item.StartDate).Value.Date >= today)
                .OrderBy(item => item.StartDate).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return new EventFeedResult
            {
                Events = events,
                IsCached = cached,
                LastSuccessfulRefreshUtc = snapshot.FetchedAtUtc,
                Message = events.Length == 0 ? "No upcoming events with confirmed dates are listed yet."
                    : cached ? "Showing cached events (updated at most once every seven days)." : "Events updated just now.",
            };
        }

        internal sealed class Snapshot
        {
            public DateTimeOffset FetchedAtUtc { get; set; }
            public EventCatalog Catalog { get; set; }
        }
    }
}
