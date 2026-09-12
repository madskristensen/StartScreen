using System.Net;
using System.Net.Http;
using System.Text;
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
        public void IsCacheStale_UsesFourHourBoundary()
        {
            using var directory = new TemporaryDirectory();
            var cacheFile = directory.GetPath("youtube.json");
            File.WriteAllText(cacheFile, "[]");
            var now = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

            File.SetLastWriteTimeUtc(cacheFile, now.AddHours(-3));
            Assert.IsFalse(YouTubeService.IsCacheStale(cacheFile, now));

            File.SetLastWriteTimeUtc(cacheFile, now.AddHours(-4));
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
    }
}
