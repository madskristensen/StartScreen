using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Xml;
using StartScreen.Models;

namespace StartScreen.Services
{
    /// <summary>
    /// Manages downloading and caching the Visual Studio YouTube channel feed.
    /// Cache-first design: loads from disk instantly, refreshes in background if stale.
    /// </summary>
    public static class YouTubeService
    {
        private const string _feedUrl = "https://www.youtube.com/feeds/videos.xml?channel_id=UChqrDOwARrxdJF-ykAptc7w";
        private const int _maxVideos = 10;

        private static readonly string _cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartScreen", "FeedCache");

        private static readonly string _cacheFile = Path.Combine(_cacheFolder, "_youtube.json");
        private static readonly string _thumbsFolder = Path.Combine(_cacheFolder, "thumbs");

        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly TimeSpan _staleThreshold = TimeSpan.FromHours(4);

        static YouTubeService()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Start Screen for Visual Studio");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Gets the cached YouTube videos asynchronously (no network).
        /// Returns null if no cache exists.
        /// </summary>
        public static async Task<List<YouTubeVideo>> GetCachedVideosAsync()
        {
            return await GetCachedVideosAsync(_cacheFile);
        }

        internal static async Task<List<YouTubeVideo>> GetCachedVideosAsync(string cacheFile)
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
            return IsCacheStale(_cacheFile, DateTime.UtcNow);
        }

        internal static bool IsCacheStale(string cacheFile, DateTime utcNow)
        {
            if (!File.Exists(cacheFile))
                return true;

            var lastModified = File.GetLastWriteTimeUtc(cacheFile);
            return utcNow - lastModified >= _staleThreshold;
        }

        /// <summary>
        /// Downloads and refreshes the YouTube feed from the network.
        /// Retries up to 3 times with exponential backoff for transient failures.
        /// Returns the updated videos, or null if all attempts failed.
        /// </summary>
        public static async Task<List<YouTubeVideo>> DownloadVideosAsync()
        {
            const int baseDelayMs = 2000;
            return await DownloadVideosAsync(
                _httpClient,
                attempt => Task.Delay(baseDelayMs * attempt),
                _cacheFile);
        }

        internal static async Task<List<YouTubeVideo>> DownloadVideosAsync(
            HttpClient httpClient,
            Func<int, Task> delayAsync,
            string cacheFile)
        {
            const int maxAttempts = 3;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using (var response = await httpClient.GetAsync(_feedUrl))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            Trace.TraceWarning($"YouTube feed returned {(int)response.StatusCode} on attempt {attempt}/{maxAttempts}.");

                            if (attempt < maxAttempts)
                            {
                                await delayAsync(attempt);
                                continue;
                            }

                            return null;
                        }

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = XmlReader.Create(stream))
                        {
                            var feed = SyndicationFeed.Load(reader);

                            if (feed?.Items == null)
                                return null;

                            var videos = feed.Items
                                .Take(_maxVideos)
                                .Select(YouTubeVideo.FromSyndicationItem)
                                .Where(v => v != null)
                                .ToList();

                            WriteCacheToDisk(videos, cacheFile);

                            Trace.TraceInformation("YouTube feed cache refreshed successfully.");

                            return videos;
                        }
                    }
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();

                    if (attempt < maxAttempts)
                    {
                        await delayAsync(attempt);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Downloads thumbnails for each video (if not already cached on disk) and sets
        /// <see cref="YouTubeVideo.Thumbnail"/> so the UI can bind to it directly.
        /// Safe to call on a background thread.
        /// </summary>
        public static async Task LoadThumbnailsAsync(List<YouTubeVideo> videos)
        {
            if (videos == null || videos.Count == 0)
                return;

            PackageUtilities.EnsureOutputPath(_thumbsFolder);

            await Task.WhenAll(videos
                .Where(v => !string.IsNullOrEmpty(v.ThumbnailUrl))
                .Select(v => LoadThumbnailForVideoAsync(v)));
        }

        private static async Task LoadThumbnailForVideoAsync(YouTubeVideo video)
        {
            try
            {
                var videoId = YouTubeVideo.ExtractVideoIdFromUrl(video.Url);
                if (string.IsNullOrEmpty(videoId))
                    return;

                var localPath = Path.Combine(_thumbsFolder, videoId + ".jpg");

                byte[] imageBytes;
                if (File.Exists(localPath))
                {
                    using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                    {
                        imageBytes = new byte[fs.Length];
                        await fs.ReadAsync(imageBytes, 0, imageBytes.Length);
                    }
                }
                else
                {
                    imageBytes = await _httpClient.GetByteArrayAsync(video.ThumbnailUrl);
                    var tmp = localPath + ".tmp";
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                    {
                        await fs.WriteAsync(imageBytes, 0, imageBytes.Length);
                    }
                    File.Move(tmp, localPath);
                }

                var bitmap = new BitmapImage();
                using (var ms = new System.IO.MemoryStream(imageBytes))
                {
                    bitmap.BeginInit();
                    bitmap.StreamSource = ms;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                }
                bitmap.Freeze();

                video.Thumbnail = bitmap;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        /// <summary>
        /// Writes the YouTube videos to the JSON cache file.
        /// </summary>
        private static void WriteCacheToDisk(List<YouTubeVideo> videos)
        {
            WriteCacheToDisk(videos, _cacheFile);
        }

        internal static void WriteCacheToDisk(List<YouTubeVideo> videos, string cacheFile)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));

                var entries = videos
                    .Select(v => v.ToCacheEntry())
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
