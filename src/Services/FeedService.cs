using StartScreen.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace StartScreen.Services
{
    /// <summary>
    /// Manages RSS/Atom feed downloading, caching, and combination.
    /// Cache-first design: loads from disk instantly, refreshes in background if stale.
    /// </summary>
    public static class FeedService
    {
        private static readonly string CacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartScreen", "FeedCache");

        private static readonly string CombinedFeedFile = Path.Combine(CacheFolder, "_feed.json");

        private static readonly HttpClient HttpClient = new HttpClient();
        private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(4);
        private const int MaxConcurrentDownloads = 6;
        private const int MaxNewsItems = 40;

        static FeedService()
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "Start Screen for Visual Studio");
            HttpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Gets the cached news posts asynchronously (no network).
        /// Returns null if no cache exists.
        /// </summary>
        public static async Task<List<NewsPost>> GetCachedPostsAsync()
        {
            return await GetCachedPostsAsync(CombinedFeedFile);
        }

        internal static async Task<List<NewsPost>> GetCachedPostsAsync(string cacheFile)
        {
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
                        var bytesRead = await stream.ReadAsync(fileBytes, offset, fileBytes.Length - offset);
                        if (bytesRead == 0)
                            break;

                        offset += bytesRead;
                    }
                }

                List<FeedCacheEntry> entries = JsonSerializer.Deserialize<List<FeedCacheEntry>>(fileBytes);

                return entries
                    ?.Select(NewsPost.FromCacheEntry)
                    .Where(post => post != null)
                    .ToList();
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
            return IsCacheStale(CombinedFeedFile, DateTime.UtcNow);
        }

        internal static bool IsCacheStale(string cacheFile, DateTime utcNow)
        {
            if (!File.Exists(cacheFile))
                return true;

            DateTime lastModified = File.GetLastWriteTimeUtc(cacheFile);
            return utcNow - lastModified >= StaleThreshold;
        }

        /// <summary>
        /// Downloads and refreshes feeds from the network.
        /// Retries the full download up to 2 times for transient failures.
        /// Returns the updated news posts, or null if all attempts failed.
        /// </summary>
        public static async Task<List<NewsPost>> DownloadFeedsAsync(IEnumerable<FeedInfo> feeds)
        {
            if (feeds == null || !feeds.Any())
                return null;

            const int maxAttempts = 2;
            const int baseDelayMs = 3000;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    SyndicationFeed combinedFeed = await DownloadAndCombineFeedsAsync(feeds);

                    if (combinedFeed?.Items != null && combinedFeed.Items.Any())
                    {
                        List<NewsPost> posts = ConvertToNewsPosts(combinedFeed);
                        WriteCacheToDisk(posts);
                        Trace.TraceInformation("Feed cache refreshed successfully.");
                        return posts;
                    }

                    Trace.TraceWarning($"News feed download returned no items on attempt {attempt}/{maxAttempts}.");

                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(baseDelayMs * attempt);
                    }
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();

                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(baseDelayMs * attempt);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Downloads and combines multiple feeds with concurrency limiting.
        /// </summary>
        private static async Task<SyndicationFeed> DownloadAndCombineFeedsAsync(IEnumerable<FeedInfo> feeds)
        {
            var selectedFeeds = feeds.Where(f => f.Enabled).ToList();
            if (!selectedFeeds.Any())
                return new SyndicationFeed("Start Screen", "Developer News", null);

            var downloadedFeeds = new List<SyndicationFeed>();

            using (var semaphore = new SemaphoreSlim(MaxConcurrentDownloads, MaxConcurrentDownloads))
            {
                IEnumerable<Task<SyndicationFeed>> downloadTasks = selectedFeeds.Select(async feedInfo =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        SyndicationFeed feed = await DownloadFeedAsync(feedInfo);
                        if (feed != null)
                        {
                            feed.Title = new TextSyndicationContent(feedInfo.Name);

                            // Set source feed for all items
                            if (feed.Items != null)
                            {
                                foreach (SyndicationItem item in feed.Items)
                                {
                                    item.SourceFeed = feed;
                                }
                            }

                            return feed;
                        }
                    }
                    catch (Exception ex)
                    {
                        ex.Log();
                    }
                    finally
                    {
                        semaphore.Release();
                    }

                    return null;
                });

                downloadedFeeds.AddRange((await Task.WhenAll(downloadTasks)).Where(f => f != null));
            }

            // Combine feeds
            return CombineFeeds(downloadedFeeds);
        }

        /// <summary>
        /// Downloads a single feed with disk caching.
        /// </summary>
        private static async Task<SyndicationFeed> DownloadFeedAsync(FeedInfo feedInfo)
        {
            var fileName = $"{feedInfo.Name}.xml";
            var cacheFile = Path.Combine(CacheFolder, fileName);

            DateTime lastModified = File.Exists(cacheFile) 
                ? File.GetLastWriteTimeUtc(cacheFile) 
                : DateTime.UtcNow.AddMonths(-2);

            // Try download if cache is stale
            if (DateTime.UtcNow - lastModified >= StaleThreshold)
            {
                SyndicationFeed feed = await DownloadFromUrlAsync(feedInfo.Url, lastModified);
                if (feed != null)
                {
                    WriteFeedToDisk(cacheFile, feed);
                    return feed;
                }
            }

            // Fall back to cache
            if (File.Exists(cacheFile))
            {
                try
                {
                    using (var reader = XmlReader.Create(cacheFile))
                    {
                        return SyndicationFeed.Load(reader);
                    }
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            }

            return null;
        }

        /// <summary>
        /// Downloads feed from URL with If-Modified-Since header.
        /// </summary>
        private static async Task<SyndicationFeed> DownloadFromUrlAsync(string url, DateTime lastModified)
        {
            return await DownloadFromUrlAsync(HttpClient, url, lastModified);
        }

        internal static async Task<SyndicationFeed> DownloadFromUrlAsync(
            HttpClient httpClient,
            string url,
            DateTime lastModified)
        {
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                return null;

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.IfModifiedSince = lastModified;

                    HttpResponseMessage response = await httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        using (Stream stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = XmlReader.Create(stream))
                        {
                            var feed = SyndicationFeed.Load(reader);

                            if (response.Content.Headers.LastModified.HasValue)
                            {
                                feed.LastUpdatedTime = response.Content.Headers.LastModified.Value;
                            }

                            return feed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            return null;
        }

        /// <summary>
        /// Writes feed to disk cache.
        /// </summary>
        private static void WriteFeedToDisk(string filePath, SyndicationFeed feed)
        {
            try
            {
                Directory.CreateDirectory(CacheFolder);

                using (var writer = XmlWriter.Create(filePath))
                {
                    feed.Items = feed.Items.Take(MaxNewsItems);
                    feed.SaveAsRss20(writer);
                }

                if (feed.LastUpdatedTime.DateTime != DateTime.MinValue)
                {
                    File.SetLastWriteTimeUtc(filePath, feed.LastUpdatedTime.DateTime);
                }
            }
            catch (Exception ex)
            {
                ex.Log();
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
                .Take(MaxNewsItems)
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
        private static void WriteCacheToDisk(List<NewsPost> posts)
        {
            WriteCacheToDisk(posts, CombinedFeedFile);
        }

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
