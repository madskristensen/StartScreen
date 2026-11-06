using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Xml;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
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
        private const int _maxThumbnailBytes = 2 * 1024 * 1024;
        private const int _maxCachedThumbnails = 100;

        private static readonly string _cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartScreen", "FeedCache");

        private static readonly string _cacheFile = Path.Combine(_cacheFolder, "_youtube.json");
        private static readonly string _thumbsFolder = Path.Combine(_cacheFolder, "thumbs");

        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly SemaphoreSlim _thumbnailPublishGate = new SemaphoreSlim(1, 1);
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
        public static Task<List<YouTubeVideo>> GetCachedVideosAsync(CancellationToken cancellationToken = default)
        {
            return GetCachedVideosAsync(_cacheFile, cancellationToken);
        }

        internal static async Task<List<YouTubeVideo>> GetCachedVideosAsync(string cacheFile, CancellationToken cancellationToken = default)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var entries = await AtomicJsonCache.ReadAsync<List<YouTubeCacheEntry>>(cacheFile, cancellationToken).ConfigureAwait(false);

                return entries
                    ?.Select(YouTubeVideo.FromCacheEntry)
                    .Where(v => v != null)
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
        public static async Task<List<YouTubeVideo>> DownloadVideosAsync(
            CancellationToken cancellationToken = default)
        {
            const int baseDelayMs = 2000;
            return await DownloadVideosAsync(
                _httpClient,
                attempt => Task.Delay(baseDelayMs * attempt, cancellationToken),
                _cacheFile,
                cancellationToken);
        }

        internal static async Task<List<YouTubeVideo>> DownloadVideosAsync(
            HttpClient httpClient,
            Func<int, Task> delayAsync,
            string cacheFile,
            CancellationToken cancellationToken = default)
        {
            const int maxAttempts = 3;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using (var response = await httpClient.GetAsync(_feedUrl,
                        HttpCompletionOption.ResponseHeadersRead, cancellationToken))
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

                        var bytes = await HttpResponseReader.ReadBytesAsync(response, cancellationToken, FeedService.MaxFeedBytes);
                        using (var stream = new MemoryStream(bytes, writable: false))
                        using (var reader = XmlReader.Create(stream, FeedService.CreateReaderSettings()))
                        {
                            var feed = SyndicationFeed.Load(reader);
                            cancellationToken.ThrowIfCancellationRequested();

                            if (feed?.Items == null)
                                return null;

                            var videos = feed.Items
                                .Take(_maxVideos)
                                .Select(YouTubeVideo.FromSyndicationItem)
                                .Where(v => v != null)
                                .ToList();

                            await WriteCacheToDiskAsync(videos, cacheFile, cancellationToken).ConfigureAwait(false);

                            Trace.TraceInformation("YouTube feed cache refreshed successfully.");

                            return videos;
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
        public static Task LoadThumbnailsAsync(
            List<YouTubeVideo> videos,
            CancellationToken cancellationToken = default)
        {
            return LoadThumbnailsAsync(videos, _httpClient, _thumbsFolder, cancellationToken);
        }

        internal static async Task LoadThumbnailsAsync(List<YouTubeVideo> videos, HttpClient httpClient,
            string thumbnailsFolder, CancellationToken cancellationToken = default)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            cancellationToken.ThrowIfCancellationRequested();

            if (videos == null || videos.Count == 0)
                return;

            Directory.CreateDirectory(thumbnailsFolder);

            using (var semaphore = new SemaphoreSlim(2))
            {
                await Task.WhenAll(videos
                    .Where(v => !string.IsNullOrEmpty(v.ThumbnailUrl))
                    .Select(async video =>
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        try
                        {
                            await LoadThumbnailForVideoAsync(video, httpClient, thumbnailsFolder, cancellationToken);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
            }
            foreach (var file in new DirectoryInfo(thumbnailsFolder).EnumerateFiles("*.jpg")
                .OrderByDescending(file => file.LastWriteTimeUtc).Skip(_maxCachedThumbnails))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                }
            }
        }

        private static async Task LoadThumbnailForVideoAsync(
            YouTubeVideo video, HttpClient httpClient, string thumbnailsFolder,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var videoId = YouTubeVideo.ExtractVideoIdFromUrl(video.Url);
                if (string.IsNullOrEmpty(videoId))
                    return;

                if (videoId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("The video identifier is not a valid thumbnail file name.");
                var localPath = Path.Combine(thumbnailsFolder, videoId + ".jpg");

                if (File.Exists(localPath))
                {
                    try
                    {
                        using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                            FileShare.Read | FileShare.Delete, 4096, useAsync: true);
                        if (fs.Length > _maxThumbnailBytes)
                            throw new InvalidDataException("The cached thumbnail is too large.");
                        var cachedBytes = new byte[fs.Length];
                        var offset = 0;
                        while (offset < cachedBytes.Length)
                        {
                            var read = await fs.ReadAsync(cachedBytes, offset, cachedBytes.Length - offset, cancellationToken);
                            if (read == 0)
                                throw new EndOfStreamException("The cached thumbnail was truncated.");
                            offset += read;
                        }
                        var cachedBitmap = DecodeThumbnail(cachedBytes);
                        cancellationToken.ThrowIfCancellationRequested();
                        video.Thumbnail = cachedBitmap;
                        return;
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is FileFormatException
                        || ex is NotSupportedException || ex is ArgumentException)
                    {
                        await ex.LogAsync();
                        File.Delete(localPath);
                    }
                }

                byte[] imageBytes;
                using (var response = await httpClient.GetAsync(video.ThumbnailUrl,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    imageBytes = await HttpResponseReader.ReadBytesAsync(response, cancellationToken, _maxThumbnailBytes);
                }

                var bitmap = DecodeThumbnail(imageBytes);
                try
                {
                    await WriteThumbnailAsync(localPath, imageBytes, cancellationToken);
                }
                catch (IOException ex)
                {
                    await ex.LogAsync();
                }
                catch (UnauthorizedAccessException ex)
                {
                    await ex.LogAsync();
                }
                cancellationToken.ThrowIfCancellationRequested();
                video.Thumbnail = bitmap;
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

        private static BitmapImage DecodeThumbnail(byte[] imageBytes)
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(imageBytes, writable: false))
            {
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 320;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }

        private static async Task WriteThumbnailAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        {
            var temporaryFile = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, useAsync: true))
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await _thumbnailPublishGate.WaitAsync(cancellationToken);
                try
                {
                    if (File.Exists(path))
                        File.Replace(temporaryFile, path, null);
                    else
                        File.Move(temporaryFile, path);
                }
                finally
                {
                    _thumbnailPublishGate.Release();
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryFile))
                        File.Delete(temporaryFile);
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                }
            }
        }

        /// <summary>
        /// Writes the YouTube videos to the JSON cache file.
        /// </summary>
        internal static void WriteCacheToDisk(List<YouTubeVideo> videos, string cacheFile)
        {
            try
            {
                AtomicJsonCache.Write(cacheFile, videos.Select(v => v.ToCacheEntry()).ToList());
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
            }
        }

        internal static async Task WriteCacheToDiskAsync(List<YouTubeVideo> videos, string cacheFile, CancellationToken cancellationToken = default)
        {
            try
            {
                await AtomicJsonCache.WriteAsync(cacheFile, videos.Select(v => v.ToCacheEntry()).ToList(), cancellationToken).ConfigureAwait(false);
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
    }
}
