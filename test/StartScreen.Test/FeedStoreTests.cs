using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class FeedStoreTests
    {
        [TestMethod]
        public async Task SaveAndReadFeeds_PreservesConfigurationAsync()
        {
            using var directory = new TemporaryDirectory();
            var filePath = directory.GetPath("newsfeeds.json");
            var feeds = new[]
            {
                new FeedInfo { Name = "Feed", Url = "https://example.com/feed", Enabled = true },
                new FeedInfo { Name = "Disabled custom feed", Url = "https://example.com/custom", Enabled = false },
            };

            await FeedStore.SaveFeedsAsync(feeds, filePath);
            List<FeedInfo> restored = FeedStore.ReadFeeds(filePath);

            Assert.HasCount(2, restored);
            Assert.AreEqual("Feed", restored[0].Name);
            Assert.AreEqual("https://example.com/feed", restored[0].Url);
            Assert.IsTrue(restored[0].Enabled);
            Assert.AreEqual("Disabled custom feed", restored[1].Name);
            Assert.AreEqual("https://example.com/custom", restored[1].Url);
            Assert.IsFalse(restored[1].Enabled);
            StringAssert.Contains(File.ReadAllText(filePath), "\"$schema\"");
        }

        [TestMethod]
        public void ReadFeeds_WhenFeedsPropertyMissing_ReturnsDefaults()
        {
            using var directory = new TemporaryDirectory();
            var filePath = directory.GetPath("newsfeeds.json");
            File.WriteAllText(filePath, "{}");

            List<FeedInfo> result = FeedStore.ReadFeeds(filePath);

            Assert.HasCount(6, result);
            Assert.AreEqual("Visual Studio", result[0].Name);
            Assert.IsTrue(result[0].Enabled);
        }

        [TestMethod]
        public async Task AddCustomFeedAsync_DoesNotAddDuplicateNameOrUrlAsync()
        {
            using var directory = new TemporaryDirectory();
            var filePath = directory.GetPath("newsfeeds.json");
            await FeedStore.SaveFeedsAsync(
                [new FeedInfo { Name = "Existing", Url = "https://example.com/feed", Enabled = true }],
                filePath);

            await FeedStore.AddCustomFeedAsync("existing", "https://example.com/other", filePath);
            await FeedStore.AddCustomFeedAsync("Other", "https://example.com/FEED", filePath);

            Assert.HasCount(1, FeedStore.ReadFeeds(filePath));
        }

        [TestMethod]
        public async Task AddCustomFeedAsync_WhenFileMissing_AppendsToDefaultsAsync()
        {
            using var directory = new TemporaryDirectory();
            var filePath = directory.GetPath("newsfeeds.json");

            await FeedStore.AddCustomFeedAsync("Custom", "https://example.com/custom", filePath);

            List<FeedInfo> result = FeedStore.ReadFeeds(filePath);
            Assert.HasCount(7, result);
            Assert.IsTrue(result.Any(feed => feed.Name == "Custom" && feed.Enabled));
        }

        [TestMethod]
        public async Task AddAndRemoveCustomFeedAsync_UpdatesPersistedFeedsAsync()
        {
            using var directory = new TemporaryDirectory();
            var filePath = directory.GetPath("newsfeeds.json");
            await FeedStore.SaveFeedsAsync(Array.Empty<FeedInfo>(), filePath);

            await FeedStore.AddCustomFeedAsync("Custom", "https://example.com/custom", filePath);
            Assert.HasCount(1, FeedStore.ReadFeeds(filePath));

            await FeedStore.RemoveFeedAsync("CUSTOM", filePath);
            Assert.IsEmpty(FeedStore.ReadFeeds(filePath));
        }

        [TestMethod]
        public async Task ConcurrentEdits_PreserveEverySuccessfulChangeAsync()
        {
            using var directory = new TemporaryDirectory();
            var filePath = directory.GetPath("newsfeeds.json");
            await FeedStore.SaveFeedsAsync(
                [new FeedInfo { Name = "Remove", Url = "https://example.com/remove", Enabled = true }], filePath);

            await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
                FeedStore.AddCustomFeedAsync($"Feed {index}", $"https://example.com/{index}", filePath))
                .Append(FeedStore.RemoveFeedAsync("Remove", filePath)));

            var feeds = FeedStore.ReadFeeds(filePath);
            Assert.HasCount(20, feeds);
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 20).Select(index => $"Feed {index}").ToArray(),
                feeds.Select(feed => feed.Name).ToArray());
            Assert.IsEmpty(Directory.GetFiles(directory.Path, "*.tmp"));
        }

        [TestMethod]
        public async Task SaveFailure_PreservesPreviousConfigurationAndThrowsAsync()
        {
            using var directory = new TemporaryDirectory();
            var filePath = directory.GetPath("newsfeeds.json");
            await FeedStore.SaveFeedsAsync(
                [new FeedInfo { Name = "Original", Url = "https://example.com/original", Enabled = true }], filePath);
            var before = File.ReadAllText(filePath);
            var oversized = new FeedInfo { Name = new string('X', AtomicJsonCache.MaxBytes + 1), Url = "https://example.com" };

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => FeedStore.SaveFeedsAsync([oversized], filePath));

            Assert.AreEqual(before, File.ReadAllText(filePath));
            Assert.IsEmpty(Directory.GetFiles(directory.Path, "*.tmp"));
            await FeedStore.AddCustomFeedAsync("After failure", "https://example.com/after", filePath);
            Assert.HasCount(2, FeedStore.ReadFeeds(filePath));
        }

        [TestMethod]
        public async Task FailedAtomicReplacement_LeavesOriginalFileAndCleansStagingFileAsync()
        {
            using var directory = new TemporaryDirectory();
            var filePath = directory.GetPath("newsfeeds.json");
            await FeedStore.SaveFeedsAsync(
                [new FeedInfo { Name = "Original", Url = "https://example.com/original", Enabled = true }], filePath);
            var before = File.ReadAllText(filePath);
            using (var locked = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsAsync<IOException>(() => FeedStore.SaveFeedsAsync(
                    [new FeedInfo { Name = "Replacement", Url = "https://example.com/replacement" }], filePath));
                Assert.AreEqual(before, File.ReadAllText(filePath));
                Assert.IsEmpty(Directory.GetFiles(directory.Path, "*.tmp"));
            }

            await FeedStore.SaveFeedsAsync(
                [new FeedInfo { Name = "Replacement", Url = "https://example.com/replacement" }], filePath);
            Assert.AreEqual("Replacement", FeedStore.ReadFeeds(filePath).Single().Name);
        }
    }
}
