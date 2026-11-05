using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.VisualStudio.Threading;
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
        private static readonly TimeSpan _staleThreshold = TimeSpan.FromHours(4);
        private const int _maxConcurrentDownloads = 6;
        private const int _maxNewsItems = 40;

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
                if (!File.Exists(cacheFile))
                    return null;

                byte[] fileBytes = new byte[new FileInfo(cacheFile).Length];
                using (var stream = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                {
                    var offset = 0;
                    while (offset < fileBytes.Length)
                    {
                        var bytesRead = await stream.ReadAsync(fileBytes, offset, fileBytes.Length - offset, cancellationToken);
                        if (bytesRead == 0)
                            break;

                        offset += bytesRead;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                List<FeedCacheEntry> entries = JsonSerializer.Deserialize<List<FeedCacheEntry>>(fileBytes);

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
        /// Returns true if the feed cache is stale (>4 hours old) or doesn't exist.
        /// </summary>
        public static bool IsCacheStale()
        {
            return IsCacheStale(_combinedFeedFile, DateTime.UtcNow);
        }

        internal static bool IsCacheStale(string cacheFile, DateTime utcNow)
        {
            if (!File.Exists(cacheFile))
                return true;

            DateTime lastModified = File.GetLastWriteTimeUtc(cacheFile);
            return utcNow - lastModified >= _staleThreshold;
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
            (SyndicationFeed Feed, bool Refreshed, bool Failed)[] results;

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
                        return (Feed: (SyndicationFeed)null, Refreshed: false, Failed: true);
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
                WriteCacheToDisk(refreshResult.Posts, Path.Combine(cacheFolder, "_feed.json"));
            }

            return refreshResult;
        }

        /// <summary>
        /// Downloads a single feed with disk caching.
        /// </summary>
        private static async Task<(SyndicationFeed Feed, bool Refreshed, bool Failed)> DownloadFeedAsync(
            HttpClient httpClient,
            FeedInfo feedInfo,
            string cacheFolder,
            Func<int, Task> delayAsync,
            CancellationToken cancellationToken,
            bool forceRefresh)
        {
            var fileName = $"{feedInfo.Name}.xml";
            var cacheFile = Path.Combine(cacheFolder, fileName);
            SyndicationFeed cachedFeed = null;

            if (File.Exists(cacheFile))
            {
                try
                {
                    using (var reader = XmlReader.Create(cacheFile))
                    {
                        cachedFeed = SyndicationFeed.Load(reader);
                    }
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                }
            }

            if (cachedFeed != null && !forceRefresh && !IsCacheStale(cacheFile, DateTime.UtcNow))
                return (cachedFeed, false, false);

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
                    await WriteFeedToDiskAsync(cacheFile, download.Feed);
                    return (download.Feed, true, false);
                }

                if (download.NotModified && cachedFeed != null)
                {
                    await WriteFeedToDiskAsync(cacheFile, cachedFeed);
                    return (cachedFeed, true, false);
                }

                if (attempt < maxAttempts)
                    await delayAsync(attempt);
            }

            return (cachedFeed, false, true);
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

                    using (HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken))
                    {
                        if (response.StatusCode == HttpStatusCode.NotModified && lastModified.HasValue)
                            return (null, true);

                        response.EnsureSuccessStatusCode();
                        using (Stream stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = XmlReader.Create(stream))
                        {
                            var feed = SyndicationFeed.Load(reader);

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

        /// <summary>
        /// Writes feed to disk cache.
        /// </summary>
        private static async Task WriteFeedToDiskAsync(string filePath, SyndicationFeed feed)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));

                using (var writer = XmlWriter.Create(filePath))
                {
                    feed.Items = feed.Items.Take(_maxNewsItems);
                    feed.SaveAsRss20(writer);
                }
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
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));

                List<FeedCacheEntry> entries = posts
                    .Select(p => p.ToCacheEntry())
                    .ToList();

                var json = JsonSerializer.SerializeToUtf8Bytes(entries);
                File.WriteAllBytes(cacheFile, json);
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }
    }
}
