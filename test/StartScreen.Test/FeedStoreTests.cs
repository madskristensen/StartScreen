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
            };

            await FeedStore.SaveFeedsAsync(feeds, filePath);
            List<FeedInfo> restored = FeedStore.ReadFeeds(filePath);

            Assert.HasCount(1, restored);
            Assert.AreEqual("Feed", restored[0].Name);
            Assert.AreEqual("https://example.com/feed", restored[0].Url);
            Assert.IsTrue(restored[0].Enabled);
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
    }
}
