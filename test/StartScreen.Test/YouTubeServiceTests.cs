using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class YouTubeServiceTests
    {
        [TestMethod]
        public async Task CacheRoundTrip_PreservesVideosAsync()
        {
            using var directory = new TemporaryDirectory();
            var cacheFile = directory.GetPath("youtube.json");
            var videos = new List<YouTubeVideo>
            {
                new YouTubeVideo
                {
                    Title = "Video",
                    Url = "https://www.youtube.com/watch?v=abc123",
                    ThumbnailUrl = "https://i.ytimg.com/vi/abc123/mqdefault.jpg",
                    PublishDate = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                }
            };

            YouTubeService.WriteCacheToDisk(videos, cacheFile);
            List<YouTubeVideo> restored = await YouTubeService.GetCachedVideosAsync(cacheFile);

            Assert.HasCount(1, restored);
            Assert.AreEqual(videos[0].Title, restored[0].Title);
            Assert.AreEqual(videos[0].Url, restored[0].Url);
            Assert.AreEqual(videos[0].ThumbnailUrl, restored[0].ThumbnailUrl);
            Assert.AreEqual(videos[0].PublishDate, restored[0].PublishDate);
        }

        [TestMethod]
        public async Task GetCachedVideosAsync_WhenCacheMissing_ReturnsNullAsync()
        {
            using var directory = new TemporaryDirectory();

            List<YouTubeVideo> result = await YouTubeService.GetCachedVideosAsync(directory.GetPath("missing.json"));

            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task GetCachedVideosAsync_WhenCacheIsMalformed_ReturnsNullAsync()
        {
            using var directory = new TemporaryDirectory();
            var cacheFile = directory.GetPath("youtube.json");
            File.WriteAllText(cacheFile, "not json");

            List<YouTubeVideo> result = await YouTubeService.GetCachedVideosAsync(cacheFile);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void IsCacheStale_UsesFourHourBoundary()
        {
            using var directory = new TemporaryDirectory();
            var cacheFile = directory.GetPath("youtube.json");
            File.WriteAllText(cacheFile, "[]");
            var now = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

            File.SetLastWriteTimeUtc(cacheFile, now.AddHours(-5));
            Assert.IsFalse(YouTubeService.IsCacheStale(cacheFile, now));

            File.SetLastWriteTimeUtc(cacheFile, now.AddHours(-6));
            Assert.IsTrue(YouTubeService.IsCacheStale(cacheFile, now));
        }

        [TestMethod]
        public async Task DownloadVideosAsync_WithSuccessfulFeed_MapsAndCachesVideosAsync()
        {
            const string atom =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<feed xmlns=\"http://www.w3.org/2005/Atom\">" +
                "<title>Videos</title><id>channel</id><updated>2026-09-12T10:00:00Z</updated>" +
                "<entry><title>Video</title><id>video</id><updated>2026-09-12T10:00:00Z</updated>" +
                "<published>2026-09-12T09:00:00Z</published>" +
                "<link href=\"https://www.youtube.com/watch?v=abc123\" /></entry></feed>";
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(atom, Encoding.UTF8, "application/atom+xml"),
            });
            using var client = new HttpClient(handler);
            using var directory = new TemporaryDirectory();
            var cacheFile = directory.GetPath("youtube.json");

            List<YouTubeVideo> result = await YouTubeService.DownloadVideosAsync(
                client,
                _ => Task.CompletedTask,
                cacheFile);

            Assert.HasCount(1, result);
            Assert.AreEqual("Video", result[0].Title);
            Assert.AreEqual("https://www.youtube.com/watch?v=abc123", result[0].Url);
            Assert.IsTrue(File.Exists(cacheFile));
        }

        [TestMethod]
        public async Task DownloadVideosAsync_CacheFailureStillReturnsNetworkResultWithoutRetryAsync()
        {
            using var directory = new TemporaryDirectory();
            var blockedFolder = directory.GetPath("not-a-folder");
            File.WriteAllText(blockedFolder, "blocked");
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>Videos</title>" +
                    "<entry><title>Video</title><link href=\"https://www.youtube.com/watch?v=abc123\" /></entry></feed>"),
            });
            using var client = new HttpClient(handler);

            var result = await YouTubeService.DownloadVideosAsync(client, _ => Task.CompletedTask,
                Path.Combine(blockedFolder, "videos.json"));

            Assert.AreEqual(1, handler.RequestCount);
            Assert.HasCount(1, result);
            Assert.AreEqual("Video", result[0].Title);
        }

        [TestMethod]
        public async Task DownloadVideosAsync_WithTransientFailures_RetriesThreeTimesAsync()
        {
            var handler = new TestHttpMessageHandler(
                (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            using var client = new HttpClient(handler);
            using var directory = new TemporaryDirectory();
            var delays = new List<int>();

            List<YouTubeVideo> result = await YouTubeService.DownloadVideosAsync(
                client,
                attempt =>
                {
                    delays.Add(attempt);
                    return Task.CompletedTask;
                },
                directory.GetPath("youtube.json"));

            Assert.IsNull(result);
            Assert.AreEqual(3, handler.RequestCount);
            CollectionAssert.AreEqual(new[] { 1, 2 }, delays);
        }

        [TestMethod]
        public async Task DownloadVideosAsync_WithMalformedFeed_RetriesThreeTimesAsync()
        {
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not xml", Encoding.UTF8, "application/atom+xml"),
            });
            using var client = new HttpClient(handler);
            using var directory = new TemporaryDirectory();
            var delays = new List<int>();

            List<YouTubeVideo> result = await YouTubeService.DownloadVideosAsync(
                client,
                attempt =>
                {
                    delays.Add(attempt);
                    return Task.CompletedTask;
                },
                directory.GetPath("youtube.json"));

            Assert.IsNull(result);
            Assert.AreEqual(3, handler.RequestCount);
            CollectionAssert.AreEqual(new[] { 1, 2 }, delays);
        }

        [TestMethod]
        public async Task DownloadVideosAsync_WhenCanceled_DoesNotStartRequestAsync()
        {
            var handler = new TestHttpMessageHandler(
                (_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            using var directory = new TemporaryDirectory();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                YouTubeService.DownloadVideosAsync(
                    client,
                    _ => Task.CompletedTask,
                    directory.GetPath("youtube.json"),
                    cancellation.Token));

            Assert.AreEqual(0, handler.RequestCount);
        }

        [STATestMethod]
        public async Task Thumbnails_CorruptCacheIsReplacedAndThenReusedAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("abc123.jpg");
            File.WriteAllText(path, "not an image");
            var image = CreateImage();
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(image),
            });
            using var client = new HttpClient(handler);
            var first = CreateVideo();

            await YouTubeService.LoadThumbnailsAsync([first], client, directory.Path);
            var second = CreateVideo();
            await YouTubeService.LoadThumbnailsAsync([second], client, directory.Path);

            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsNotNull(first.Thumbnail);
            Assert.IsTrue(first.Thumbnail.IsFrozen);
            Assert.IsNotNull(second.Thumbnail);
            CollectionAssert.AreEqual(image, File.ReadAllBytes(path));
            Assert.IsEmpty(Directory.GetFiles(directory.Path, "*.tmp"));
        }

        [STATestMethod]
        public async Task Thumbnails_InvalidDownloadIsNotCachedAndNextAttemptRecoversAsync()
        {
            using var directory = new TemporaryDirectory();
            var image = CreateImage();
            var handler = new TestHttpMessageHandler((_, attempt) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(attempt == 1 ? new byte[] { 1, 2, 3 } : image),
            });
            using var client = new HttpClient(handler);
            var video = CreateVideo();

            await YouTubeService.LoadThumbnailsAsync([video], client, directory.Path);

            Assert.IsNull(video.Thumbnail);
            Assert.IsFalse(File.Exists(directory.GetPath("abc123.jpg")));
            await YouTubeService.LoadThumbnailsAsync([video], client, directory.Path);
            Assert.IsNotNull(video.Thumbnail);
            Assert.AreEqual(2, handler.RequestCount);
            Assert.IsEmpty(Directory.GetFiles(directory.Path, "*.tmp"));
        }

        [STATestMethod]
        public async Task Thumbnails_OversizedCacheIsEvictedAndHistoryIsBoundedAsync()
        {
            using var directory = new TemporaryDirectory();
            for (var index = 0; index < 105; index++)
            {
                var path = directory.GetPath($"old{index}.jpg");
                File.WriteAllBytes(path, [0]);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1).AddMinutes(-index));
            }
            File.WriteAllBytes(directory.GetPath("abc123.jpg"), new byte[2 * 1024 * 1024 + 1]);
            var image = CreateImage();
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(image),
            });
            using var client = new HttpClient(handler);
            var video = CreateVideo();

            await YouTubeService.LoadThumbnailsAsync([video], client, directory.Path);

            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsNotNull(video.Thumbnail);
            Assert.HasCount(100, Directory.GetFiles(directory.Path, "*.jpg"));
            Assert.IsTrue(File.Exists(directory.GetPath("abc123.jpg")));
            Assert.IsFalse(File.Exists(directory.GetPath("old104.jpg")));
        }

        [TestMethod]
        public async Task Thumbnails_CancellationDoesNotStartRequestOrCreateFilesAsync()
        {
            using var directory = new TemporaryDirectory();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);

            await Assert.ThrowsAsync<OperationCanceledException>(() => YouTubeService.LoadThumbnailsAsync(
                [CreateVideo()], client, directory.Path, cancellation.Token));

            Assert.AreEqual(0, handler.RequestCount);
            Assert.IsEmpty(Directory.GetFiles(directory.Path));
        }

        [STATestMethod]
        public async Task Thumbnails_OversizedDownloadIsNotPublishedAsync()
        {
            using var directory = new TemporaryDirectory();
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]),
            });
            using var client = new HttpClient(handler);
            var video = CreateVideo();

            await YouTubeService.LoadThumbnailsAsync([video], client, directory.Path);

            Assert.IsNull(video.Thumbnail);
            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsEmpty(Directory.GetFiles(directory.Path));
        }

        [STATestMethod]
        public async Task Thumbnails_ConcurrentPublicationKeepsBothImagesAndOneCompleteFileAsync()
        {
            using var directory = new TemporaryDirectory();
            var image = CreateImage();
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(image),
            });
            using var client = new HttpClient(handler);
            var first = CreateVideo();
            var second = CreateVideo();

            await Task.WhenAll(YouTubeService.LoadThumbnailsAsync([first], client, directory.Path),
                YouTubeService.LoadThumbnailsAsync([second], client, directory.Path));

            Assert.IsNotNull(first.Thumbnail);
            Assert.IsNotNull(second.Thumbnail);
            CollectionAssert.AreEqual(image, File.ReadAllBytes(directory.GetPath("abc123.jpg")));
            Assert.IsEmpty(Directory.GetFiles(directory.Path, "*.tmp"));
        }

        private static YouTubeVideo CreateVideo() => new()
        {
            Url = "https://www.youtube.com/watch?v=abc123",
            ThumbnailUrl = "https://i.ytimg.com/vi/abc123/mqdefault.jpg",
        };

        private static byte[] CreateImage()
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }
}
