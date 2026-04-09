using StartScreen.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ServiceModel.Syndication;
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

        private static readonly string CombinedFeedFile = Path.Combine(CacheFolder, "_feed.xml");

        private static readonly HttpClient HttpClient = new HttpClient();
        private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(4);
        private const int MaxConcurrentDownloads = 6;

        static FeedService()
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "Start Screen for Visual Studio");
            HttpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Gets the cached combined feed synchronously (instant, no network).
        /// Returns null if no cache exists.
        /// </summary>
        public static SyndicationFeed GetCachedFeed()
        {
            try
            {
                if (!File.Exists(CombinedFeedFile))
                    return null;

                using (var reader = XmlReader.Create(CombinedFeedFile))
                {
                    var feed = SyndicationFeed.Load(reader);
                    feed.LastUpdatedTime = File.GetLastWriteTimeUtc(CombinedFeedFile);
                    return feed;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to load cached feed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Refreshes feeds in the background if stale (>4 hours).
        /// Returns the updated feed, or null if cache was fresh.
        /// </summary>
        public static async Task<SyndicationFeed> RefreshFeedInBackgroundAsync(IEnumerable<FeedInfo> feeds)
        {
            if (feeds == null || !feeds.Any())
                return null;

            // Check staleness
            if (File.Exists(CombinedFeedFile))
            {
                var lastModified = File.GetLastWriteTimeUtc(CombinedFeedFile);
                if (DateTime.UtcNow - lastModified < StaleThreshold)
                {
                    Trace.TraceInformation("Feed cache is fresh, skipping refresh.");
                    return null; // Cache is fresh
                }
            }

            // Download and combine feeds
            try
            {
                var combinedFeed = await DownloadAndCombineFeedsAsync(feeds);

                if (combinedFeed != null)
                {
                    // Write to cache
                    Directory.CreateDirectory(CacheFolder);
                    using (var writer = XmlWriter.Create(CombinedFeedFile))
                    {
                        combinedFeed.SaveAsRss20(writer);
                    }

                    Trace.TraceInformation("Feed cache refreshed successfully.");
                }

                return combinedFeed;
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to refresh feeds: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Downloads and combines multiple feeds with concurrency limiting.
        /// </summary>
        private static async Task<SyndicationFeed> DownloadAndCombineFeedsAsync(IEnumerable<FeedInfo> feeds)
        {
            var selectedFeeds = feeds.Where(f => f.IsSelected).ToList();
            if (!selectedFeeds.Any())
                return new SyndicationFeed("Start Screen", "Developer News", null);

            var downloadedFeeds = new List<SyndicationFeed>();

            using (var semaphore = new SemaphoreSlim(MaxConcurrentDownloads, MaxConcurrentDownloads))
            {
                var downloadTasks = selectedFeeds.Select(async feedInfo =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var feed = await DownloadFeedAsync(feedInfo);
                        if (feed != null)
                        {
                            feed.Title = new TextSyndicationContent(feedInfo.DisplayName);

                            // Set source feed for all items
                            if (feed.Items != null)
                            {
                                foreach (var item in feed.Items)
                                {
                                    item.SourceFeed = feed;
                                }
                            }

                            return feed;
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError($"Failed to download feed {feedInfo.DisplayName}: {ex.Message}");
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
            var fileName = $"{feedInfo.DisplayName}.xml";
            var cacheFile = Path.Combine(CacheFolder, fileName);

            DateTime lastModified = File.Exists(cacheFile) 
                ? File.GetLastWriteTimeUtc(cacheFile) 
                : DateTime.UtcNow.AddMonths(-2);

            // Try download if cache is stale
            if (DateTime.UtcNow - lastModified >= StaleThreshold)
            {
                var feed = await DownloadFromUrlAsync(feedInfo.Url, lastModified);
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
                    Trace.TraceError($"Failed to read cached feed {fileName}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Downloads feed from URL with If-Modified-Since header.
        /// </summary>
        private static async Task<SyndicationFeed> DownloadFromUrlAsync(string url, DateTime lastModified)
        {
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                return null;

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.IfModifiedSince = lastModified;

                    var response = await HttpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        using (var stream = await response.Content.ReadAsStreamAsync())
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
                Trace.TraceError($"Failed to download feed from {url}: {ex.Message}");
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
                    // Limit items for performance
                    feed.Items = feed.Items.Take(20);
                    feed.SaveAsRss20(writer);
                }

                if (feed.LastUpdatedTime.DateTime != DateTime.MinValue)
                {
                    File.SetLastWriteTimeUtc(filePath, feed.LastUpdatedTime.DateTime);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to write feed to disk: {ex.Message}");
            }
        }

        /// <summary>
        /// Combines multiple feeds into a single feed, deduping by title.
        /// </summary>
        private static SyndicationFeed CombineFeeds(List<SyndicationFeed> feeds)
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
                .Take(100)
                .ToList();

            combined.LastUpdatedTime = DateTime.UtcNow;
            return combined;
        }

        /// <summary>
        /// Converts a SyndicationFeed to a list of NewsPost models.
        /// </summary>
        public static List<NewsPost> ConvertToNewsPosts(SyndicationFeed feed)
        {
            if (feed?.Items == null)
                return new List<NewsPost>();

            return feed.Items
                .Select(item => NewsPost.FromSyndicationItem(item))
                .Where(post => post != null)
                .ToList();
        }
    }
}
