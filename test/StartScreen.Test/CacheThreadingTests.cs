using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;
using StartScreen.Services.DevHub;

namespace StartScreen.Test
{
    [TestClass]
    public class CacheThreadingTests
    {
        [TestMethod]
        public async Task FeedCache_DoesNotMarshalIoBackToCallerAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("feed.json");
            FeedService.WriteCacheToDisk(
                [new NewsPost { Title = "Cached post", Url = "https://example.com/post", Summary = new string('x', 100000) }], path);

            var posts = await ReadWithoutCallerContextAsync(() => FeedService.GetCachedPostsAsync(path));

            Assert.HasCount(1, posts);
            Assert.AreEqual("Cached post", posts[0].Title);
        }

        [TestMethod]
        public async Task VideoCache_DoesNotMarshalIoBackToCallerAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("videos.json");
            YouTubeService.WriteCacheToDisk(
                [new YouTubeVideo { Title = new string('x', 100000), Url = "https://www.youtube.com/watch?v=abc123" }], path);

            var videos = await ReadWithoutCallerContextAsync(() => YouTubeService.GetCachedVideosAsync(path));

            Assert.HasCount(1, videos);
            Assert.AreEqual(100000, videos[0].Title.Length);
        }

        [TestMethod]
        public async Task DevHubCache_DoesNotMarshalIoBackToCallerAsync()
        {
            using var directory = new TemporaryDirectory();
            var cache = new DevHubCacheService(directory.Path);
            cache.WriteDashboard(new DevHubDashboard
            {
                Users = [new DevHubUser { Username = new string('x', 100000), ProviderName = "GitHub" }]
            });

            var dashboard = await ReadWithoutCallerContextAsync(() => cache.ReadDashboardAsync());

            Assert.IsNotNull(dashboard);
            Assert.HasCount(1, dashboard.Users);
            Assert.AreEqual(100000, dashboard.Users[0].Username.Length);
        }

        [TestMethod]
        public async Task CacheReads_PropagateCancellationEvenWhenCacheIsMissingAsync()
        {
            using var directory = new TemporaryDirectory();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var path = directory.GetPath("missing.json");
            var cache = new DevHubCacheService(directory.Path);

            await Assert.ThrowsAsync<OperationCanceledException>(() => FeedService.GetCachedPostsAsync(path, cancellation.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() => YouTubeService.GetCachedVideosAsync(path, cancellation.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() => cache.ReadDashboardAsync(cancellation.Token));
        }

        private static async Task<T> ReadWithoutCallerContextAsync<T>(Func<Task<T>> readAsync)
        {
            var context = new RecordingSynchronizationContext();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            Task<T> task;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                task = readAsync();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }

            T result = await task;
            Assert.AreEqual(0, context.PostCount, "Cache I/O must not post continuations to the UI context.");
            return result;
        }

        private sealed class RecordingSynchronizationContext : SynchronizationContext
        {
            private int _postCount;

            public int PostCount => _postCount;

            public override void Post(SendOrPostCallback callback, object? state)
            {
                Interlocked.Increment(ref _postCount);
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
        }
    }
}