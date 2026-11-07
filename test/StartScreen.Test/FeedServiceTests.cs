using System.Net;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Text;
using System.Xml;
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
            var now = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

            AtomicJsonCache.Write(cacheFile, new { Entries = Array.Empty<FeedCacheEntry>(), ValidatedUtc = new[] { now.AddHours(-3) } });
            Assert.IsFalse(FeedService.IsCacheStale(cacheFile, now));

            AtomicJsonCache.Write(cacheFile, new { Entries = Array.Empty<FeedCacheEntry>(), ValidatedUtc = new[] { now.AddHours(-6) } });
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

        [TestMethod]
        public async Task DownloadFromUrlAsync_RejectsOversizedResponsesAsync()
        {
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[FeedService.MaxFeedBytes + 1]),
            });
            using var client = new HttpClient(handler);

            var result = await FeedService.DownloadFromUrlAsync(client, "https://example.com/feed", DateTime.UtcNow);

            Assert.IsNull(result);
            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        public async Task DownloadFromUrlAsync_RejectsDocumentTypeDeclarationsAsync()
        {
            const string xml = "<!DOCTYPE rss [<!ENTITY text 'expanded'>]><rss version='2.0'><channel>" +
                "<title>&text;</title><description>Feed</description><link>https://example.com</link></channel></rss>";
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml),
            });
            using var client = new HttpClient(handler);

            var result = await FeedService.DownloadFromUrlAsync(client, "https://example.com/feed", DateTime.UtcNow);

            Assert.IsNull(result);
            Assert.AreEqual(System.Xml.DtdProcessing.Prohibit, FeedService.CreateReaderSettings().DtdProcessing);
            Assert.AreEqual(FeedService.MaxFeedBytes, FeedService.CreateReaderSettings().MaxCharactersInDocument);
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_ForcedRefreshBypassesFreshSourceCacheAsync()
        {
            using var directory = new TemporaryDirectory();
            WriteSourceCache(directory, "Example", "Cached post", DateTime.UtcNow.AddHours(-1));
            var handler = new TestHttpMessageHandler((_, _) => CreateFeedResponse("Fresh post"));
            using var client = new HttpClient(handler);

            FeedRefreshResult result = await FeedService.DownloadFeedsAsync(client,
                [CreateFeed("Example")], directory.Path, _ => Task.CompletedTask, forceRefresh: true);

            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsTrue(result.Success);
            Assert.AreEqual("Fresh post", result.Posts.Single().Title);
            Assert.IsEmpty(result.CachedFeeds);
            Assert.IsEmpty(result.FailedFeeds);
            var cached = await FeedService.GetCachedPostsAsync(directory.GetPath("_feed.json"));
            Assert.IsNotNull(cached);
            Assert.AreEqual("Fresh post", cached.Single().Title);
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_AutomaticRefreshReusesFreshSourceCacheAsync()
        {
            using var directory = new TemporaryDirectory();
            WriteSourceCache(directory, "Example", "Cached post", DateTime.UtcNow.AddHours(-1));
            var handler = new TestHttpMessageHandler((_, _) => CreateFeedResponse("Fresh post"));
            using var client = new HttpClient(handler);

            FeedRefreshResult result = await FeedService.DownloadFeedsAsync(client,
                [CreateFeed("Example")], directory.Path, _ => Task.CompletedTask);

            Assert.AreEqual(0, handler.RequestCount);
            Assert.IsTrue(result.Success);
            Assert.AreEqual("Cached post", result.Posts.Single().Title);
            CollectionAssert.AreEqual(new[] { "Example" }, result.CachedFeeds.ToArray());
            Assert.IsEmpty(result.FailedFeeds);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task DownloadFeedsAsync_NotModifiedValidatesCacheAndResetsCheckTimeAsync(bool forceRefresh)
        {
            using var directory = new TemporaryDirectory();
            var oldCheck = DateTime.UtcNow.AddHours(-7);
            WriteSourceCache(directory, "Example", "Cached post", oldCheck);
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotModified));
            using var client = new HttpClient(handler);

            FeedRefreshResult result = await FeedService.DownloadFeedsAsync(client,
                [CreateFeed("Example")], directory.Path, _ => Task.CompletedTask, forceRefresh: forceRefresh);

            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsTrue(result.Success);
            Assert.AreEqual("Cached post", result.Posts.Single().Title);
            Assert.IsEmpty(result.CachedFeeds);
            Assert.IsEmpty(result.FailedFeeds);
            Assert.AreEqual(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                handler.LastRequest?.Headers.IfModifiedSince);
            Assert.IsFalse(FeedService.IsCacheStale(directory.GetPath("_feed.json"), DateTime.UtcNow));
        }

        [TestMethod]
        public async Task ConditionalRefresh_PreservesWeakETagAcrossNotModifiedAsync()
        {
            using var directory = new TemporaryDirectory();
            var handler = new TestHttpMessageHandler((request, attempt) =>
            {
                if (attempt == 1)
                {
                    var response = CreateFeedResponse("Cached post");
                    response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"version\"", true);
                    return response;
                }
                Assert.AreEqual("W/\"version\"", request.Headers.IfNoneMatch.Single().ToString());
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });
            using var client = new HttpClient(handler);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await FeedService.DownloadFeedsAsync(client, [CreateFeed("Example")], directory.Path,
                    _ => Task.CompletedTask, forceRefresh: true);
                Assert.IsTrue(result.Success);
                Assert.AreEqual("Cached post", result.Posts.Single().Title);
            }
            Assert.AreEqual(3, handler.RequestCount);
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_RetriesFailedSourceBeforeUsingCacheAsync()
        {
            using var directory = new TemporaryDirectory();
            WriteSourceCache(directory, "Example", "Cached post", DateTime.UtcNow.AddHours(-1));
            var retries = 0;
            var handler = new TestHttpMessageHandler((_, attempt) => attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : CreateFeedResponse("Recovered post"));
            using var client = new HttpClient(handler);

            FeedRefreshResult result = await FeedService.DownloadFeedsAsync(client,
                [CreateFeed("Example")], directory.Path, _ =>
                {
                    retries++;
                    return Task.CompletedTask;
                }, forceRefresh: true);

            Assert.AreEqual(2, handler.RequestCount);
            Assert.AreEqual(1, retries);
            Assert.IsTrue(result.Success);
            Assert.AreEqual("Recovered post", result.Posts.Single().Title);
            Assert.IsEmpty(result.CachedFeeds);
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_FailureRetainsCacheWithoutAdvancingFreshnessAsync()
        {
            using var directory = new TemporaryDirectory();
            var oldCheck = DateTime.UtcNow.AddHours(-7);
            WriteSourceCache(directory, "Example", "Cached post", oldCheck);
            var combinedFile = directory.GetPath("_feed.json");
            const string originalCache = "[{\"Title\":\"Previous combined content\"}]";
            File.WriteAllText(combinedFile, originalCache);
            File.SetLastWriteTimeUtc(combinedFile, oldCheck);
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            using var client = new HttpClient(handler);

            FeedRefreshResult result = await FeedService.DownloadFeedsAsync(client,
                [CreateFeed("Example")], directory.Path, _ => Task.CompletedTask);

            Assert.AreEqual(2, handler.RequestCount);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("Cached post", result.Posts.Single().Title);
            CollectionAssert.AreEqual(new[] { "Example" }, result.FailedFeeds.ToArray());
            CollectionAssert.AreEqual(new[] { "Example" }, result.CachedFeeds.ToArray());
            Assert.AreEqual(originalCache, File.ReadAllText(combinedFile));
            Assert.AreEqual(oldCheck, File.GetLastWriteTimeUtc(combinedFile));
            Assert.AreEqual(oldCheck, File.GetLastWriteTimeUtc(FeedService.GetSourceCachePath(directory.Path, CreateFeed("Example").Url)));
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_PartialFailureKeepsFreshAndCachedPostsAsync()
        {
            using var directory = new TemporaryDirectory();
            WriteSourceCache(directory, "Failing", "Cached post", DateTime.UtcNow.AddHours(-5));
            var handler = new TestHttpMessageHandler((request, _) => request.RequestUri?.Host == "failing.example.com"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : CreateFeedResponse("Fresh post"));
            using var client = new HttpClient(handler);

            FeedRefreshResult result = await FeedService.DownloadFeedsAsync(client,
                [CreateFeed("Working"), CreateFeed("Failing")], directory.Path, _ => Task.CompletedTask, forceRefresh: true);

            Assert.AreEqual(3, handler.RequestCount);
            Assert.AreEqual(2, result.FeedCount);
            Assert.IsFalse(result.Success);
            CollectionAssert.AreEquivalent(new[] { "Fresh post", "Cached post" }, result.Posts.Select(post => post.Title).ToArray());
            CollectionAssert.AreEqual(new[] { "Failing" }, result.FailedFeeds.ToArray());
            CollectionAssert.AreEqual(new[] { "Failing" }, result.CachedFeeds.ToArray());
            Assert.IsFalse(File.Exists(directory.GetPath("_feed.json")));
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_FailureWithoutCacheIsExplicitAsync()
        {
            using var directory = new TemporaryDirectory();
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            using var client = new HttpClient(handler);

            FeedRefreshResult result = await FeedService.DownloadFeedsAsync(client,
                [CreateFeed("Example")], directory.Path, _ => Task.CompletedTask);

            Assert.AreEqual(2, handler.RequestCount);
            Assert.IsFalse(result.Success);
            Assert.IsEmpty(result.Posts);
            Assert.IsEmpty(result.CachedFeeds);
            CollectionAssert.AreEqual(new[] { "Example" }, result.FailedFeeds.ToArray());
            Assert.IsFalse(File.Exists(directory.GetPath("_feed.json")));
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_DisabledFeedsAreNotFailuresOrRequestsAsync()
        {
            using var directory = new TemporaryDirectory();
            var feed = CreateFeed("Disabled");
            feed.Enabled = false;
            var handler = new TestHttpMessageHandler((_, _) => CreateFeedResponse("Unexpected"));
            using var client = new HttpClient(handler);

            FeedRefreshResult result = await FeedService.DownloadFeedsAsync(client,
                [feed], directory.Path, _ => Task.CompletedTask, forceRefresh: true);

            Assert.AreEqual(0, handler.RequestCount);
            Assert.IsTrue(result.Success);
            Assert.AreEqual(0, result.FeedCount);
            Assert.IsEmpty(result.Posts);
            Assert.IsEmpty(result.FailedFeeds);
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_EmptySuccessfulFeedDoesNotRetryAsync()
        {
            using var directory = new TemporaryDirectory();
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss version=\"2.0\"><channel><title>Empty</title>" +
                    "<description>Empty feed</description><link>https://example.com</link></channel></rss>"),
            });
            using var client = new HttpClient(handler);

            FeedRefreshResult result = await FeedService.DownloadFeedsAsync(client,
                [CreateFeed("Example")], directory.Path, _ => Task.CompletedTask);

            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsTrue(result.Success);
            Assert.IsEmpty(result.Posts);
            Assert.IsEmpty(result.FailedFeeds);
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_MalformedCacheIsDownloadedEvenWhenRecentAsync()
        {
            using var directory = new TemporaryDirectory();
            File.WriteAllText(FeedService.GetSourceCachePath(directory.Path, CreateFeed("Example").Url), "not json");
            var handler = new TestHttpMessageHandler((_, _) => CreateFeedResponse("Recovered post"));
            using var client = new HttpClient(handler);

            FeedRefreshResult result = await FeedService.DownloadFeedsAsync(client,
                [CreateFeed("Example")], directory.Path, _ => Task.CompletedTask);

            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsTrue(result.Success);
            Assert.AreEqual("Recovered post", result.Posts.Single().Title);
            Assert.IsNull(handler.LastRequest?.Headers.IfModifiedSince);
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_CancellationDuringRetryDoesNotPublishCacheAsync()
        {
            using var directory = new TemporaryDirectory();
            using var cancellation = new CancellationTokenSource();
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            using var client = new HttpClient(handler);

            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => FeedService.DownloadFeedsAsync(client,
                [CreateFeed("Example")], directory.Path, _ =>
                {
                    cancellation.Cancel();
                    return Task.Delay(Timeout.Infinite, cancellation.Token);
                }, cancellation.Token, forceRefresh: true));

            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsFalse(File.Exists(directory.GetPath("_feed.json")));
        }

        [TestMethod]
        public void SourceCacheKey_NormalizesHostPortAndFragmentButPreservesPathAndQuery()
        {
            using var directory = new TemporaryDirectory();
            var original = FeedService.GetSourceCachePath(directory.Path, "http://example.com/feed?a=1");

            Assert.AreEqual(original, FeedService.GetSourceCachePath(directory.Path, " HTTP://EXAMPLE.COM:80/feed?a=1#section "));
            Assert.AreNotEqual(original, FeedService.GetSourceCachePath(directory.Path, "http://example.com/Feed?a=1"));
            Assert.AreNotEqual(original, FeedService.GetSourceCachePath(directory.Path, "http://example.com/feed?a=2"));
            Assert.IsTrue(Path.GetFileName(original).StartsWith("source-", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_RenameReusesUrlCacheAndDisplaysCurrentNameAsync()
        {
            using var directory = new TemporaryDirectory();
            WriteSourceCache(directory, "Example", "Cached post", DateTime.UtcNow.AddHours(-1));
            var feed = CreateFeed("Example");
            feed.Name = @"..\renamed: source";
            var handler = new TestHttpMessageHandler((_, _) => CreateFeedResponse("Unexpected"));
            using var client = new HttpClient(handler);

            var result = await FeedService.DownloadFeedsAsync(client, [feed], directory.Path, _ => Task.CompletedTask);

            Assert.AreEqual(0, handler.RequestCount);
            Assert.IsTrue(result.Success);
            Assert.AreEqual("Cached post", result.Posts.Single().Title);
            Assert.IsTrue(result.Posts.Single().Source.EndsWith(" in " + feed.Name, StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_SameDisplayNameWithDifferentUrlsDoesNotShareCacheAsync()
        {
            using var directory = new TemporaryDirectory();
            WriteSourceCache(directory, "Example", "Cached post", DateTime.UtcNow.AddHours(-1));
            var original = CreateFeed("Example");
            var other = CreateFeed("Other");
            other.Name = original.Name;
            var handler = new TestHttpMessageHandler((_, _) => CreateFeedResponse("Other post"));
            using var client = new HttpClient(handler);

            var result = await FeedService.DownloadFeedsAsync(client, [original, other], directory.Path, _ => Task.CompletedTask);

            Assert.AreEqual(1, handler.RequestCount);
            Assert.AreEqual("other.example.com", handler.LastRequest?.RequestUri?.Host);
            CollectionAssert.AreEquivalent(new[] { "Cached post", "Other post" }, result.Posts.Select(post => post.Title).ToArray());
            Assert.AreNotEqual(FeedService.GetSourceCachePath(directory.Path, original.Url), FeedService.GetSourceCachePath(directory.Path, other.Url));
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_AggregateRewriteDoesNotExtendOldestSourceDeadlineAsync()
        {
            using var directory = new TemporaryDirectory();
            var oldestCheck = DateTime.UtcNow.AddHours(-3).AddMinutes(-59);
            WriteSourceCache(directory, "Example", "Cached post", oldestCheck);
            var handler = new TestHttpMessageHandler((_, _) => CreateFeedResponse("New source post"));
            using var client = new HttpClient(handler);
            var combinedPath = directory.GetPath("_feed.json");

            var result = await FeedService.DownloadFeedsAsync(client, [CreateFeed("Example"), CreateFeed("Other")], directory.Path, _ => Task.CompletedTask);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsFalse(FeedService.IsCacheStale(combinedPath, oldestCheck.AddHours(6).AddTicks(-1)));
            Assert.IsTrue(FeedService.IsCacheStale(combinedPath, oldestCheck.AddHours(6)));

            await FeedService.DownloadFeedsAsync(client, [CreateFeed("Example"), CreateFeed("Other")], directory.Path, _ => Task.CompletedTask);

            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsTrue(FeedService.IsCacheStale(combinedPath, oldestCheck.AddHours(6)));
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_LegacyNameCachesAreSafelyInvalidatedAsync()
        {
            using var directory = new TemporaryDirectory();
            File.WriteAllText(directory.GetPath("Example.xml"), "legacy data from an unknown URL");
            File.WriteAllText(directory.GetPath("_feed.json"), "[]");
            var handler = new TestHttpMessageHandler((_, _) => CreateFeedResponse("New content"));
            using var client = new HttpClient(handler);

            Assert.IsTrue(FeedService.IsCacheStale(directory.GetPath("_feed.json"), DateTime.UtcNow));
            Assert.IsNull(await FeedService.GetCachedPostsAsync(directory.GetPath("_feed.json")));
            var result = await FeedService.DownloadFeedsAsync(client, [CreateFeed("Example")], directory.Path, _ => Task.CompletedTask);

            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsTrue(result.Success);
            Assert.AreEqual("New content", result.Posts.Single().Title);
            Assert.IsNull(handler.LastRequest?.Headers.IfModifiedSince);
        }

        [TestMethod]
        public async Task DownloadFeedsAsync_PersistenceFailureDoesNotBecomeNetworkFailureAsync()
        {
            using var directory = new TemporaryDirectory();
            var blockedFolder = directory.GetPath("not-a-folder");
            File.WriteAllText(blockedFolder, "blocked");
            var handler = new TestHttpMessageHandler((_, _) => CreateFeedResponse("Fresh post"));
            using var client = new HttpClient(handler);

            var result = await FeedService.DownloadFeedsAsync(client, [CreateFeed("Example")], blockedFolder, _ => Task.CompletedTask);

            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsTrue(result.Success);
            Assert.IsEmpty(result.FailedFeeds);
            Assert.AreEqual("Fresh post", result.Posts.Single().Title);
        }

        private static FeedInfo CreateFeed(string name)
        {
            return new FeedInfo { Name = name, Url = $"https://{name.ToLowerInvariant()}.example.com/feed", Enabled = true };
        }

        private static HttpResponseMessage CreateFeedResponse(string title)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss version=\"2.0\"><channel><title>Feed</title>" +
                    "<description>Description</description><link>https://example.com</link>" +
                    $"<item><title>{title}</title><link>https://example.com/post</link></item></channel></rss>"),
            };
        }

        private static void WriteSourceCache(TemporaryDirectory directory, string name, string title, DateTime checkedAt)
        {
            var path = FeedService.GetSourceCachePath(directory.Path, CreateFeed(name).Url);
            var feed = new SyndicationFeed("Feed", "Description", new Uri("https://example.com"))
            {
                LastUpdatedTime = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                Items = [CreateItem(title, DateTimeOffset.UtcNow)],
            };
            using (var text = new StringWriter())
            {
                using (var writer = XmlWriter.Create(text))
                    feed.SaveAsRss20(writer);
                AtomicJsonCache.Write(path, new { Xml = text.ToString(), ValidatedUtc = checkedAt });
            }
            File.SetLastWriteTimeUtc(path, checkedAt);
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
