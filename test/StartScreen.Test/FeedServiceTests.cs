using System.Net;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Text;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class FeedServiceTests
    {
        [TestMethod]
        public async Task CacheRoundTrip_PreservesPostsAsync()
        {
            using var directory = new TemporaryDirectory();
            var cacheFile = directory.GetPath("feed.json");
            var posts = new List<NewsPost>
            {
                new NewsPost
                {
                    Title = "Title",
                    Summary = "Summary",
                    Url = "https://example.com/post",
                    PublishDate = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                    Source = "Example",
                    HasDescription = true,
                }
            };

            FeedService.WriteCacheToDisk(posts, cacheFile);
            List<NewsPost> restored = await FeedService.GetCachedPostsAsync(cacheFile);

            Assert.HasCount(1, restored);
            Assert.AreEqual(posts[0].Title, restored[0].Title);
            Assert.AreEqual(posts[0].Summary, restored[0].Summary);
            Assert.AreEqual(posts[0].Url, restored[0].Url);
            Assert.AreEqual(posts[0].PublishDate, restored[0].PublishDate);
            Assert.AreEqual(posts[0].Source, restored[0].Source);
            Assert.AreEqual(posts[0].HasDescription, restored[0].HasDescription);
        }

        [TestMethod]
        public async Task GetCachedPostsAsync_WhenCacheMissing_ReturnsNullAsync()
        {
            using var directory = new TemporaryDirectory();

            List<NewsPost> result = await FeedService.GetCachedPostsAsync(directory.GetPath("missing.json"));

            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task GetCachedPostsAsync_WhenCacheIsMalformed_ReturnsNullAsync()
        {
            using var directory = new TemporaryDirectory();
            var cacheFile = directory.GetPath("feed.json");
            File.WriteAllText(cacheFile, "not json");

            List<NewsPost> result = await FeedService.GetCachedPostsAsync(cacheFile);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void IsCacheStale_UsesFourHourBoundary()
        {
            using var directory = new TemporaryDirectory();
            var cacheFile = directory.GetPath("feed.json");
            File.WriteAllText(cacheFile, "[]");
            var now = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

            File.SetLastWriteTimeUtc(cacheFile, now.AddHours(-3));
            Assert.IsFalse(FeedService.IsCacheStale(cacheFile, now));

            File.SetLastWriteTimeUtc(cacheFile, now.AddHours(-4));
            Assert.IsTrue(FeedService.IsCacheStale(cacheFile, now));
        }

        [TestMethod]
        public void IsCacheStale_WhenCacheMissing_ReturnsTrue()
        {
            using var directory = new TemporaryDirectory();

            Assert.IsTrue(FeedService.IsCacheStale(
                directory.GetPath("missing.json"),
                new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc)));
        }

        [TestMethod]
        public void CombineFeeds_WhenNoFeeds_ReturnsEmptyFeed()
        {
            SyndicationFeed result = FeedService.CombineFeeds([]);

            Assert.IsEmpty(result.Items);
        }

        [TestMethod]
        public void CombineFeeds_DeduplicatesByTitleAndKeepsNewestItem()
        {
            var older = CreateItem("Same title", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
            var newer = CreateItem("same TITLE", new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
            var other = CreateItem("Other", new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));

            SyndicationFeed result = FeedService.CombineFeeds(
                [
                    new SyndicationFeed { Items = [older, other] },
                    new SyndicationFeed { Items = [newer] },
                ]);

            var items = result.Items.ToList();
            Assert.HasCount(2, items);
            Assert.AreSame(other, items[0]);
            Assert.AreSame(newer, items[1]);
        }

        [TestMethod]
        public void CombineFeeds_OmitsUntitledItemsAndCapsResults()
        {
            var items = Enumerable.Range(0, 45)
                .Select(index => CreateItem($"Post {index}", DateTimeOffset.UtcNow.AddMinutes(-index)))
                .ToList();
            items.Add(new SyndicationItem());

            SyndicationFeed result = FeedService.CombineFeeds([new SyndicationFeed { Items = items }]);

            Assert.HasCount(40, result.Items);
            Assert.IsTrue(result.Items.All(item => !string.IsNullOrWhiteSpace(item.Title?.Text)));
        }

        [TestMethod]
        public void ConvertToNewsPosts_WhenFeedIsNull_ReturnsEmptyList()
        {
            List<NewsPost> result = FeedService.ConvertToNewsPosts(null);

            Assert.IsEmpty(result);
        }

        [TestMethod]
        public async Task DownloadFromUrlAsync_SendsConditionalRequestAndParsesFeedAsync()
        {
            const string rss =
                "<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>Feed</title>" +
                "<description>Description</description><link>https://example.com</link>" +
                "<item><title>Post</title><link>https://example.com/post</link></item>" +
                "</channel></rss>";
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(rss, Encoding.UTF8, "application/rss+xml"),
            });
            using var client = new HttpClient(handler);
            var lastModified = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

            SyndicationFeed result =
                await FeedService.DownloadFromUrlAsync(client, "https://example.com/feed", lastModified);

            Assert.IsNotNull(result);
            Assert.AreEqual("Post", result.Items.Single().Title.Text);
            Assert.IsNotNull(handler.LastRequest);
            Assert.AreEqual(lastModified, handler.LastRequest.Headers.IfModifiedSince?.UtcDateTime);
        }

        [TestMethod]
        public async Task DownloadFromUrlAsync_UsesResponseLastModifiedAsync()
        {
            const string rss =
                "<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>Feed</title>" +
                "<description>Description</description><link>https://example.com</link></channel></rss>";
            var responseLastModified = new DateTimeOffset(2026, 9, 2, 3, 4, 5, TimeSpan.Zero);
            var handler = new TestHttpMessageHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(rss, Encoding.UTF8, "application/rss+xml"),
                };
                response.Content.Headers.LastModified = responseLastModified;
                return response;
            });
            using var client = new HttpClient(handler);

            SyndicationFeed result = await FeedService.DownloadFromUrlAsync(
                client,
                "https://example.com/feed",
                new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.IsNotNull(result);
            Assert.AreEqual(responseLastModified, result.LastUpdatedTime);
        }

        [TestMethod]
        public async Task DownloadFromUrlAsync_WithInvalidUrl_DoesNotSendRequestAsync()
        {
            var handler = new TestHttpMessageHandler(
                (_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);

            SyndicationFeed result =
                await FeedService.DownloadFromUrlAsync(client, "not a url", DateTime.UtcNow);

            Assert.IsNull(result);
            Assert.AreEqual(0, handler.RequestCount);
        }

        [TestMethod]
        public async Task DownloadFromUrlAsync_WithMalformedFeed_ReturnsNullAsync()
        {
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not xml", Encoding.UTF8, "application/rss+xml"),
            });
            using var client = new HttpClient(handler);

            SyndicationFeed result =
                await FeedService.DownloadFromUrlAsync(client, "https://example.com/feed", DateTime.UtcNow);

            Assert.IsNull(result);
            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        public async Task DownloadFromUrlAsync_WithFailureStatus_ReturnsNullAsync()
        {
            var handler = new TestHttpMessageHandler(
                (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            using var client = new HttpClient(handler);

            SyndicationFeed result =
                await FeedService.DownloadFromUrlAsync(client, "https://example.com/feed", DateTime.UtcNow);

            Assert.IsNull(result);
            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        public async Task DownloadFromUrlAsync_WhenCanceled_ThrowsAsync()
        {
            var handler = new TestHttpMessageHandler(
                (_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                FeedService.DownloadFromUrlAsync(
                    client,
                    "https://example.com/feed",
                    DateTime.UtcNow,
                    cancellation.Token));
        }

        private static SyndicationItem CreateItem(string title, DateTimeOffset publishDate)
        {
            return new SyndicationItem(title, "Content", null)
            {
                PublishDate = publishDate,
            };
        }
    }
}
