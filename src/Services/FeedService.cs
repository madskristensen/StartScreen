using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.ServiceModel.Syndication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;

namespace StartScreen.Services
{
    /// <summary>
    /// Manages RSS/Atom feed downloading, caching, and combination.
    /// Cache-first design: loads from disk instantly, refreshes in background if stale.
    /// </summary>
    public static class FeedService
    {
        private static readonly string _cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartScreen", "FeedCache");

        private static readonly string _combinedFeedFile = Path.Combine(_cacheFolder, "_feed.json");

        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly TimeSpan _staleThreshold = TimeSpan.FromHours(6);
        private const int _maxConcurrentDownloads = 6;
        private const int _maxNewsItems = 40;
        internal const int MaxFeedBytes = 4 * 1024 * 1024;

        static FeedService()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Start Screen for Visual Studio");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Gets the cached news posts asynchronously (no network).
        /// Returns null if no cache exists.
        /// </summary>
        public static Task<List<NewsPost>> GetCachedPostsAsync(CancellationToken cancellationToken = default)
        {
            return GetCachedPostsAsync(_combinedFeedFile, cancellationToken);
        }

        internal static async Task<List<NewsPost>> GetCachedPostsAsync(string cacheFile, CancellationToken cancellationToken = default)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var snapshot = await AtomicJsonCache.ReadAsync<CombinedCache>(cacheFile, cancellationToken).ConfigureAwait(false);
                var entries = snapshot?.Entries;

                return entries
                    ?.Select(NewsPost.FromCacheEntry)
                    .Where(post => post != null)
                    .ToList();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return null;
            }
        }

        /// <summary>
        /// Returns true if any source validation is at least four hours old or the snapshot is missing.
        /// </summary>
        public static bool IsCacheStale()
        {
            return IsCacheStale(_combinedFeedFile, DateTime.UtcNow);
        }

        internal static bool IsCacheStale(string cacheFile, DateTime utcNow)
        {
            try
            {
                var snapshot = AtomicJsonCache.Read<CombinedCache>(cacheFile);
                return snapshot?.Entries == null || snapshot.ValidatedUtc == null || snapshot.ValidatedUtc.Length == 0 ||
                    snapshot.ValidatedUtc.Any(time => time > utcNow || utcNow - time >= _staleThreshold);
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
                return true;
            }
        }

        /// <summary>
        /// Refreshes enabled feeds, retaining cached content and reporting failed sources.
        /// Forced refreshes contact each source even when its cache is fresh.
        /// </summary>
        public static Task<FeedRefreshResult> DownloadFeedsAsync(
            IEnumerable<FeedInfo> feeds,
            CancellationToken cancellationToken = default,
            bool forceRefresh = false)
        {
            const int baseDelayMs = 3000;
            return DownloadFeedsAsync(_httpClient, feeds, _cacheFolder,
                attempt => Task.Delay(baseDelayMs * attempt, cancellationToken), cancellationToken, forceRefresh);
        }

        internal static async Task<FeedRefreshResult> DownloadFeedsAsync(
            HttpClient httpClient,
            IEnumerable<FeedInfo> feeds,
            string cacheFolder,
            Func<int, Task> delayAsync,
            CancellationToken cancellationToken = default,
            bool forceRefresh = false)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            cancellationToken.ThrowIfCancellationRequested();
            var selectedFeeds = feeds.Where(f => f.Enabled).ToList();
            (SyndicationFeed Feed, bool Refreshed, bool Failed, DateTime ValidatedUtc)[] results;

            using (var semaphore = new SemaphoreSlim(_maxConcurrentDownloads, _maxConcurrentDownloads))
            {
                var downloadTasks = selectedFeeds.Select(async feedInfo =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var result = await DownloadFeedAsync(httpClient, feedInfo, cacheFolder,
                            delayAsync, cancellationToken, forceRefresh);
                        if (result.Feed != null)
                        {
                            result.Feed.Title = new TextSyndicationContent(feedInfo.Name);

                            if (result.Feed.Items != null)
                            {
                                foreach (SyndicationItem item in result.Feed.Items)
                                {
                                    item.SourceFeed = result.Feed;
                                }
                            }
                        }

                        return result;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await ex.LogAsync();
                        return (Feed: (SyndicationFeed)null, Refreshed: false, Failed: true, ValidatedUtc: DateTime.MinValue);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                results = await Task.WhenAll(downloadTasks);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var combinedFeed = CombineFeeds(results.Where(result => result.Feed != null)
                .Select(result => result.Feed).ToList());
            var failedFeeds = selectedFeeds.Where((_, index) => results[index].Failed)
                .Select(feed => feed.Name).ToList();
            var cachedFeeds = selectedFeeds.Where((_, index) =>
                results[index].Feed != null && !results[index].Refreshed)
                .Select(feed => feed.Name).ToList();
            var refreshResult = new FeedRefreshResult(ConvertToNewsPosts(combinedFeed),
                selectedFeeds.Count, failedFeeds, cachedFeeds);

            // A fallback must not reset the combined cache's freshness clock.
            if (refreshResult.Success)
            {
                await WriteCacheToDiskAsync(refreshResult.Posts, Path.Combine(cacheFolder, "_feed.json"),
                    results.Length == 0 ? new[] { DateTime.UtcNow } : results.Select(result => result.ValidatedUtc).ToArray(),
                    cancellationToken).ConfigureAwait(false);
            }

            return refreshResult;
        }

        /// <summary>
        /// Downloads a single feed with disk caching.
        /// </summary>
        private static async Task<(SyndicationFeed Feed, bool Refreshed, bool Failed, DateTime ValidatedUtc)> DownloadFeedAsync(
            HttpClient httpClient,
            FeedInfo feedInfo,
            string cacheFolder,
            Func<int, Task> delayAsync,
            CancellationToken cancellationToken,
            bool forceRefresh)
        {
            var cacheFile = GetSourceCachePath(cacheFolder, feedInfo.Url);
            SyndicationFeed cachedFeed = null;
            var validatedUtc = DateTime.MinValue;

            try
            {
                var snapshot = await AtomicJsonCache.ReadAsync<SourceCache>(cacheFile, cancellationToken).ConfigureAwait(false);
                if (snapshot != null)
                {
                    using (var text = new StringReader(snapshot.Xml))
                    using (var reader = XmlReader.Create(text, CreateReaderSettings()))
                    {
                        cachedFeed = SyndicationFeed.Load(reader);
                    }
                    validatedUtc = snapshot.ValidatedUtc;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }

            if (cachedFeed != null && !forceRefresh && validatedUtc <= DateTime.UtcNow && DateTime.UtcNow - validatedUtc < _staleThreshold)
                return (cachedFeed, false, false, validatedUtc);

            DateTime? lastModified = cachedFeed?.LastUpdatedTime > DateTimeOffset.MinValue
                ? cachedFeed.LastUpdatedTime.UtcDateTime : (DateTime?)null;

            const int maxAttempts = 2;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var download = await DownloadFromUrlWithStatusAsync(httpClient,
                    feedInfo.Url, lastModified, cancellationToken);
                if (download.Feed != null)
                {
                    var checkedUtc = DateTime.UtcNow;
                    await WriteFeedToDiskAsync(cacheFile, download.Feed, checkedUtc, cancellationToken);
                    return (download.Feed, true, false, checkedUtc);
                }

                if (download.NotModified && cachedFeed != null)
                {
                    var checkedUtc = DateTime.UtcNow;
                    await WriteFeedToDiskAsync(cacheFile, cachedFeed, checkedUtc, cancellationToken);
                    return (cachedFeed, true, false, checkedUtc);
                }

                if (attempt < maxAttempts)
                    await delayAsync(attempt);
            }

            return (cachedFeed, false, true, validatedUtc);
        }

        internal static async Task<SyndicationFeed> DownloadFromUrlAsync(
            HttpClient httpClient,
            string url,
            DateTime lastModified,
            CancellationToken cancellationToken = default)
        {
            var result = await DownloadFromUrlWithStatusAsync(httpClient, url, lastModified, cancellationToken);
            return result.Feed;
        }

        private static async Task<(SyndicationFeed Feed, bool NotModified)> DownloadFromUrlWithStatusAsync(
            HttpClient httpClient,
            string url,
            DateTime? lastModified,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.IfModifiedSince = lastModified;

                    using (HttpResponseMessage response = await httpClient.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        if (response.StatusCode == HttpStatusCode.NotModified && lastModified.HasValue)
                            return (null, true);

                        response.EnsureSuccessStatusCode();
                        var bytes = await HttpResponseReader.ReadBytesAsync(response, cancellationToken, MaxFeedBytes);
                        using (var stream = new MemoryStream(bytes, writable: false))
                        using (var reader = XmlReader.Create(stream, CreateReaderSettings()))
                        {
                            var feed = SyndicationFeed.Load(reader);
                            cancellationToken.ThrowIfCancellationRequested();

                            if (response.Content.Headers.LastModified.HasValue)
                            {
                                feed.LastUpdatedTime = response.Content.Headers.LastModified.Value;
                            }

                            return (feed, false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }

            return (null, false);
        }

        internal static XmlReaderSettings CreateReaderSettings()
        {
            return new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxFeedBytes
            };
        }

        /// <summary>
        /// Writes feed to disk cache.
        /// </summary>
        internal static async Task WriteFeedToDiskAsync(string filePath, SyndicationFeed feed, DateTime validatedUtc, CancellationToken cancellationToken = default)
        {
            try
            {
                using (var text = new StringWriter())
                {
                    using (var writer = XmlWriter.Create(text))
                    {
                        feed.Items = feed.Items.Take(_maxNewsItems).ToArray();
                        feed.SaveAsRss20(writer);
                    }
                    await AtomicJsonCache.WriteAsync(filePath, new SourceCache { Xml = text.ToString(), ValidatedUtc = validatedUtc },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        /// <summary>
        /// Combines multiple feeds into a single feed, deduping by title.
        /// </summary>
        internal static SyndicationFeed CombineFeeds(List<SyndicationFeed> feeds)
        {
            var combined = new SyndicationFeed("Start Screen", "Developer News", null);

            if (!feeds.Any())
                return combined;

            // Combine and dedupe items
            combined.Items = feeds
                .Where(f => f?.Items != null)
                .SelectMany(f => f.Items)
                .Where(i => !string.IsNullOrWhiteSpace(i.Title?.Text))
                .GroupBy(i => i.Title.Text, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(i => i.PublishDate).First())
                .OrderByDescending(i => i.PublishDate.Date)
                .Take(_maxNewsItems)
                .ToList();

            combined.LastUpdatedTime = DateTime.UtcNow;
            return combined;
        }

        /// <summary>
        /// Converts a SyndicationFeed to a list of NewsPost models.
        /// </summary>
        internal static List<NewsPost> ConvertToNewsPosts(SyndicationFeed feed)
        {
            if (feed?.Items == null)
                return new List<NewsPost>();

            return feed.Items
                .Select(item => NewsPost.FromSyndicationItem(item))
                .Where(post => post != null)
                .ToList();
        }

        /// <summary>
        /// Writes the combined news posts to the JSON cache file.
        /// </summary>
        internal static void WriteCacheToDisk(List<NewsPost> posts, string cacheFile)
        {
            try
            {
                AtomicJsonCache.Write(cacheFile, new CombinedCache
                {
                    Entries = posts.Select(p => p.ToCacheEntry()).ToList(),
                    ValidatedUtc = new[] { DateTime.UtcNow },
                });
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
            }
        }

        private static async Task WriteCacheToDiskAsync(List<NewsPost> posts, string cacheFile, DateTime[] validatedUtc, CancellationToken cancellationToken)
        {
            try
            {
                await AtomicJsonCache.WriteAsync(cacheFile, new CombinedCache
                {
                    Entries = posts.Select(p => p.ToCacheEntry()).ToList(),
                    ValidatedUtc = validatedUtc,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        internal static string GetSourceCachePath(string cacheFolder, string url)
        {
            var uri = new UriBuilder(url.Trim()) { Fragment = string.Empty }.Uri;
            using var hash = SHA256.Create();
            var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
            return Path.Combine(cacheFolder, "source-" + BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant() + ".json");
        }

        private sealed class SourceCache
        {
            public SourceCache() { }
            public string Xml { get; set; }
            public DateTime ValidatedUtc { get; set; }
        }

        private sealed class CombinedCache
        {
            public CombinedCache() { }
            public List<FeedCacheEntry> Entries { get; set; }
            public DateTime[] ValidatedUtc { get; set; }
        }
    }
}
