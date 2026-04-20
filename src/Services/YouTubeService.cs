using StartScreen.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;

namespace StartScreen.Services
{
    /// <summary>
    /// Manages downloading and caching the Visual Studio YouTube channel feed.
    /// Cache-first design: loads from disk instantly, refreshes in background if stale.
    /// </summary>
    public static class YouTubeService
    {
        private const string FeedUrl = "https://www.youtube.com/feeds/videos.xml?channel_id=UChqrDOwARrxdJF-ykAptc7w";
        private const int MaxVideos = 5;

        private static readonly string CacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartScreen", "FeedCache");

        private static readonly string CacheFile = Path.Combine(CacheFolder, "_youtube.json");

        private static readonly HttpClient HttpClient = new HttpClient();
        private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(4);

        static YouTubeService()
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "Start Screen for Visual Studio");
            HttpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Gets the cached YouTube videos asynchronously (no network).
        /// Returns null if no cache exists.
        /// </summary>
        public static async Task<List<YouTubeVideo>> GetCachedVideosAsync()
        {
            try
            {
                if (!File.Exists(CacheFile))
                    return null;

                byte[] fileBytes;
                using (var stream = new FileStream(CacheFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                {
                    fileBytes = new byte[stream.Length];
                    await stream.ReadAsync(fileBytes, 0, fileBytes.Length);
                }

                var entries = JsonSerializer.Deserialize<List<YouTubeCacheEntry>>(fileBytes);

                return entries
                    ?.Select(YouTubeVideo.FromCacheEntry)
                    .Where(v => v != null)
                    .ToList();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return null;
            }
        }

        /// <summary>
        /// Returns true if the YouTube cache is stale (>4 hours old) or doesn't exist.
        /// </summary>
        public static bool IsCacheStale()
        {
            if (!File.Exists(CacheFile))
                return true;

            var lastModified = File.GetLastWriteTimeUtc(CacheFile);
            return DateTime.UtcNow - lastModified >= StaleThreshold;
        }

        /// <summary>
        /// Downloads and refreshes the YouTube feed from the network.
        /// Returns the updated videos, or null if download failed.
        /// </summary>
        public static async Task<List<YouTubeVideo>> DownloadVideosAsync()
        {
            try
            {
                using (var response = await HttpClient.GetAsync(FeedUrl))
                {
                    if (!response.IsSuccessStatusCode)
                        return null;

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var reader = XmlReader.Create(stream))
                    {
                        var feed = SyndicationFeed.Load(reader);

                        if (feed?.Items == null)
                            return null;

                        var videos = feed.Items
                            .Take(MaxVideos)
                            .Select(YouTubeVideo.FromSyndicationItem)
                            .Where(v => v != null)
                            .ToList();

                        WriteCacheToDisk(videos);

                        Trace.TraceInformation("YouTube feed cache refreshed successfully.");

                        return videos;
                    }
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return null;
            }
        }

        /// <summary>
        /// Writes the YouTube videos to the JSON cache file.
        /// </summary>
        private static void WriteCacheToDisk(List<YouTubeVideo> videos)
        {
            try
            {
                Directory.CreateDirectory(CacheFolder);

                var entries = videos
                    .Select(v => v.ToCacheEntry())
                    .ToList();

                var json = JsonSerializer.SerializeToUtf8Bytes(entries);
                File.WriteAllBytes(CacheFile, json);
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }
    }
}
