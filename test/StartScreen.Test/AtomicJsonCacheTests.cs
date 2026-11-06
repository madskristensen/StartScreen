using System.Text.Json;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;
using StartScreen.Services.DevHub;

namespace StartScreen.Test
{
    [TestClass]
    public class AtomicJsonCacheTests
    {
        [TestMethod]
        public async Task FailedPublication_PreservesSnapshotAndCleansTemporaryFileAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("cache.json");
            await AtomicJsonCache.WriteAsync(path, new[] { "original" });

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsAsync<IOException>(() => AtomicJsonCache.WriteAsync(path, new[] { "replacement" }));
            }

            CollectionAssert.AreEqual(new[] { "original" }, await AtomicJsonCache.ReadAsync<string[]>(path));
            Assert.HasCount(1, Directory.GetFiles(directory.Path));
            await AtomicJsonCache.WriteAsync(path, new[] { "recovered" });
            CollectionAssert.AreEqual(new[] { "recovered" }, await AtomicJsonCache.ReadAsync<string[]>(path));
        }

        [TestMethod]
        public async Task CanceledBeforePublication_PreservesSnapshotAndCleansTemporaryFileAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("cache.json");
            await AtomicJsonCache.WriteAsync(path, new { Value = "original" });
            using var cancellation = new CancellationTokenSource();

            await Assert.ThrowsAsync<OperationCanceledException>(() => AtomicJsonCache.WriteAsync(path,
                new CancelingPayload(cancellation), cancellation.Token));

            using var restored = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual("original", restored.RootElement.GetProperty("Value").GetString());
            Assert.HasCount(1, Directory.GetFiles(directory.Path));
            await AtomicJsonCache.WriteAsync(path, new { Value = "recovered" });
        }

        [TestMethod]
        public async Task OverlappingSyncAndAsyncWriters_PublishWholeSnapshotsAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("cache.json");
            await AtomicJsonCache.WriteAsync(path, new[] { new string('a', 100000) });

            await Task.WhenAll(Enumerable.Range(0, 20).Select(async index =>
            {
                var text = new string((char)('a' + index), 100000);
                if (index % 2 == 0)
                    await Task.Run(() => AtomicJsonCache.Write(path, new[] { text }));
                else
                    await AtomicJsonCache.WriteAsync(path, new[] { text });

                var restored = await AtomicJsonCache.ReadAsync<string[]>(path);
                Assert.HasCount(1, restored);
                Assert.AreEqual(100000, restored[0].Length);
                Assert.IsTrue(restored[0].All(character => character == restored[0][0]));
            }));

            Assert.HasCount(1, Directory.GetFiles(directory.Path));
        }

        [TestMethod]
        public async Task OversizedWrite_PreservesExistingSnapshotAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("cache.json");
            await AtomicJsonCache.WriteAsync(path, "original");

            await Assert.ThrowsAsync<InvalidDataException>(() => AtomicJsonCache.WriteAsync(path, new string('x', AtomicJsonCache.MaxBytes)));

            Assert.AreEqual("original", await AtomicJsonCache.ReadAsync<string>(path));
            Assert.HasCount(1, Directory.GetFiles(directory.Path));
        }

        [TestMethod]
        public async Task OversizedSnapshots_AreRejectedByEveryCacheAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("dashboard.json");
            using (var stream = File.Create(path))
                stream.SetLength(AtomicJsonCache.MaxBytes + 1);

            await Assert.ThrowsAsync<InvalidDataException>(() => AtomicJsonCache.ReadAsync<object>(path));
            Assert.IsNull(await FeedService.GetCachedPostsAsync(path));
            Assert.IsNull(await YouTubeService.GetCachedVideosAsync(path));
            Assert.IsNull(await new DevHubCacheService(directory.Path).ReadDashboardAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => CopilotActivityCache.ReadAsync(path, CancellationToken.None));
        }

        [TestMethod]
        public async Task AbandonedTemporarySnapshot_IsNeverReadAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("cache.json");
            await AtomicJsonCache.WriteAsync(path, new[] { "complete" });
            File.WriteAllText(path + ".interrupted.tmp", "[\"incomplete");

            CollectionAssert.AreEqual(new[] { "complete" }, await AtomicJsonCache.ReadAsync<string[]>(path));
            await AtomicJsonCache.WriteAsync(path, new[] { "next" });
            CollectionAssert.AreEqual(new[] { "next" }, await AtomicJsonCache.ReadAsync<string[]>(path));
        }

        [TestMethod]
        public async Task DashboardAsyncWrite_PropagatesCancellationWithoutReplacingSnapshotAsync()
        {
            using var directory = new TemporaryDirectory();
            var cache = new DevHubCacheService(directory.Path);
            await cache.WriteDashboardAsync(new DevHubDashboard
            {
                Users = [new DevHubUser { Username = "original", ProviderName = "GitHub" }],
            });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => cache.WriteDashboardAsync(new DevHubDashboard(), cancellation.Token));

            var restored = await cache.ReadDashboardAsync();
            Assert.IsNotNull(restored);
            Assert.AreEqual("original", restored.Users.Single().Username);
            Assert.HasCount(1, Directory.GetFiles(directory.Path));
        }

        private sealed class CancelingPayload(CancellationTokenSource cancellation)
        {
            public string Value
            {
                get
                {
                    cancellation.Cancel();
                    return "replacement";
                }
            }
        }
    }
}