using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public sealed class MruMetadataCacheTests
    {
        [TestMethod]
        public async Task RoundTrip_RestoresCompleteLocalMetadataWithoutProbingMissingWorkspaceAsync()
        {
            using var directory = new TemporaryDirectory();
            var now = new DateTime(2026, 11, 7, 22, 0, 0, DateTimeKind.Utc);
            var cache = new MruMetadataCache(directory.GetPath("metadata.json"), () => now);
            var item = Item(directory.GetPath("Missing.sln"));
            item.LastAccessed = now.AddDays(-1);
            item.ApplyExists(false);
            item.ApplyGitStatus(new GitStatus
            {
                BranchName = "feature",
                HeadCommitId = "abc123",
                IsDetachedHead = true,
                CommitsAhead = 2,
                CommitsBehind = 3,
                HasUncommittedChanges = true,
                LastCommitTime = now.AddHours(-1),
                StashCount = 4,
                CurrentOperation = "Merge",
                RemoteUrl = "https://github.com/owner/repo",
            });
            item.CopilotChatSessionCount = 5;
            item.IsPinned = true;
            item.IsSelected = true;
            item.RawMruEntries.Add("old raw entry");
            cache.Update([item], local: true);
            await cache.SaveAsync([item], CancellationToken.None);

            var reopened = new MruMetadataCache(directory.GetPath("metadata.json"), () => now);
            await reopened.LoadAsync(CancellationToken.None);
            var restored = Item(item.Path);
            restored.RawMruEntries.Add("current raw entry");
            reopened.Apply([restored]);

            Assert.IsFalse(restored.Exists);
            Assert.AreEqual(item.LastAccessed, restored.LastAccessed);
            Assert.AreEqual("feature", restored.GitBranch);
            Assert.AreEqual("abc123", restored.HeadCommitId);
            Assert.IsTrue(restored.IsDetachedHead);
            Assert.AreEqual(2, restored.CommitsAhead);
            Assert.AreEqual(3, restored.CommitsBehind);
            Assert.IsTrue(restored.HasUncommittedChanges);
            Assert.AreEqual(item.LastCommitTime, restored.LastCommitTime);
            Assert.AreEqual(4, restored.StashCount);
            Assert.AreEqual("Merge", restored.CurrentOperation);
            Assert.AreEqual(item.RemoteUrl, restored.RemoteUrl);
            Assert.AreEqual(5, restored.CopilotChatSessionCount);
            Assert.IsFalse(restored.IsPinned);
            Assert.IsFalse(restored.IsSelected);
            CollectionAssert.AreEqual(new[] { "current raw entry" }, restored.RawMruEntries);
            Assert.IsFalse(reopened.IsStale(restored));
        }

        [TestMethod]
        [DataRow(-1, false)]
        [DataRow(0, true)]
        [DataRow(1, true)]
        public void Freshness_UsesExactThreeHourBoundaryWithoutExtendingOnRead(int ticks, bool stale)
        {
            var now = new DateTime(2026, 11, 7, 22, 0, 0, DateTimeKind.Utc);
            var cache = new MruMetadataCache(utcNow: () => now);
            var item = Item(@"C:\Code\App.sln");
            cache.Update([item], local: true);
            now = now.Add(MruMetadataCache.Lifetime).AddTicks(ticks);

            cache.Apply([item]);

            Assert.AreEqual(stale, cache.IsStale(item));
        }

        [TestMethod]
        public async Task StaleCache_StillRendersWhileUnknownItemsRequireRefreshAsync()
        {
            using var directory = new TemporaryDirectory();
            var now = DateTime.UtcNow;
            var cache = new MruMetadataCache(directory.GetPath("metadata.json"), () => now);
            var item = Item(@"C:\Code\App.sln");
            item.GitBranch = "cached";
            cache.Update([item], local: true);
            await cache.SaveAsync([item], CancellationToken.None);
            now = now.AddHours(4);
            var reopened = new MruMetadataCache(directory.GetPath("metadata.json"), () => now);
            await reopened.LoadAsync(CancellationToken.None);
            var restored = Item(item.Path);
            reopened.Apply([restored]);

            Assert.AreEqual("cached", restored.GitBranch);
            Assert.IsTrue(reopened.IsStale(restored));
            Assert.IsTrue(reopened.IsStale(Item(@"C:\Code\New.sln")));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("different identity")]
        [DataRow("verified identity")]
        public async Task ActivityMetadata_RestoresOnlyAfterMatchingIdentityValidationAsync(string? context)
        {
            using var directory = new TemporaryDirectory();
            var cache = new MruMetadataCache(directory.GetPath("metadata.json"));
            var item = Item(@"C:\Code\App.sln");
            item.GitBranch = "main";
            item.RemoteUrl = "https://github.com/owner/repo";
            item.ApplyRepositorySummary("2 open PRs", "Cached repository details", 2, 1);
            item.ApplyBranchActivity("Matching branch PR");
            item.ApplyVisitChanges("Changes since last visit");
            cache.Update([item], local: true);
            cache.Update([item], local: false, activityContext: "verified identity");
            await cache.SaveAsync([item], CancellationToken.None);
            var reopened = new MruMetadataCache(directory.GetPath("metadata.json"));
            await reopened.LoadAsync(CancellationToken.None);
            var restored = Item(item.Path);
            reopened.Apply([restored], context);

            Assert.AreEqual("main", restored.GitBranch);
            Assert.AreEqual(context == "verified identity", restored.HasRepositoryCounts);
            Assert.AreEqual(context == "verified identity" ? "Matching branch PR" : null, restored.BranchActivityTooltip);
            Assert.AreEqual(context == "verified identity" ? "Changes since last visit" : null, restored.VisitChangesTooltip);
            Assert.AreEqual(context != "verified identity", reopened.IsSummaryStale(restored, context!));
            if (context == "verified identity")
            {
                Assert.AreEqual("2", restored.RepositoryPullRequestCountText);
                Assert.AreEqual("1 review", restored.RepositoryReviewsText);
                Assert.AreEqual("Cached repository details", restored.RepositorySummaryTooltip);
            }
        }

        [TestMethod]
        public void SummaryRestore_DoesNotOverwriteNewerLocalStatusOrDifferentRemote()
        {
            var cache = new MruMetadataCache();
            var item = Item(@"C:\Code\App.sln");
            item.GitBranch = "cached branch";
            item.RemoteUrl = "https://github.com/owner/repo";
            item.ApplyRepositorySummary("2 open PRs", "details", 2, 1);
            cache.Update([item], local: false, activityContext: "identity");
            item.GitBranch = "new branch";
            item.CommitsAhead = 8;
            cache.Apply([item], "identity", includeLocal: false);

            Assert.AreEqual("new branch", item.GitBranch);
            Assert.AreEqual(8, item.CommitsAhead);
            Assert.IsTrue(item.HasRepositoryCounts);
            item.RemoteUrl = "https://github.com/owner/different";
            cache.Apply([item], "identity", includeLocal: false);
            Assert.IsFalse(item.HasRepositoryCounts);
        }

        [TestMethod]
        public void SummaryUpdate_DoesNotExtendLocalFreshnessAndRemoteChangesInvalidateSummary()
        {
            var now = DateTime.UtcNow;
            var cache = new MruMetadataCache(utcNow: () => now);
            var item = Item(@"C:\Code\App.sln");
            item.RemoteUrl = "https://github.com/owner/repo";
            cache.Update([item], local: true);
            now = now.AddHours(3);
            cache.Update([item], local: false, activityContext: "identity");

            Assert.IsTrue(cache.IsStale(item));
            Assert.IsFalse(cache.IsSummaryStale(item, "identity"));
            item.RemoteUrl = "https://github.com/owner/new";
            Assert.IsTrue(cache.IsSummaryStale(item, "identity"));
            cache.Update([item], local: true);
            Assert.IsTrue(cache.IsSummaryStale(item, "identity"));
        }

        [TestMethod]
        public void ManualInvalidation_ForcesFreshSummaryButRetainsCachedDisplay()
        {
            var cache = new MruMetadataCache();
            var item = Item(@"C:\Code\App.sln");
            item.ApplyRepositorySummary("No open PRs", "details", 0, 0);
            cache.Update([item], local: true);
            cache.Update([item], local: false, activityContext: "identity");
            cache.InvalidateSummaries();
            var restored = Item(item.Path);
            cache.Apply([restored], "identity");

            Assert.IsTrue(cache.IsSummaryStale(restored, "identity"));
            Assert.IsFalse(cache.IsStale(restored));
            Assert.IsTrue(restored.HasRepositoryCounts);
            Assert.AreEqual("0", restored.RepositoryPullRequestCountText);
        }

        [TestMethod]
        public async Task Save_ExcludesRemovedItemsAndTypeChangesDoNotReuseMetadataAsync()
        {
            using var directory = new TemporaryDirectory();
            var cache = new MruMetadataCache(directory.GetPath("metadata.json"));
            var retained = Item(@"C:\Code\Retained.sln");
            var removed = Item(@"C:\Code\Removed.sln");
            cache.Update([retained, removed], local: true);
            await cache.SaveAsync([retained], CancellationToken.None);
            var reopened = new MruMetadataCache(directory.GetPath("metadata.json"));
            await reopened.LoadAsync(CancellationToken.None);
            Assert.IsTrue(reopened.IsStale(removed));
            retained.Type = MruItemType.Folder;
            Assert.IsTrue(reopened.IsStale(retained));
        }

        [TestMethod]
        public async Task InvalidCache_IsLoggedAndRebuiltWithoutPretendingToBeFreshAsync()
        {
            using var directory = new TemporaryDirectory();
            File.WriteAllText(directory.GetPath("metadata.json"), "invalid json");
            var cache = new MruMetadataCache(directory.GetPath("metadata.json"));

            await cache.LoadAsync(CancellationToken.None);

            Assert.IsTrue(cache.IsStale(Item(@"C:\Code\App.sln")));
        }

        [TestMethod]
        public async Task Cancellation_DoesNotReplaceExistingSnapshotAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("metadata.json");
            var cache = new MruMetadataCache(path);
            var item = Item(@"C:\Code\App.sln");
            cache.Update([item], local: true);
            await cache.SaveAsync([item], CancellationToken.None);
            var original = File.ReadAllText(path);
            var token = new CancellationToken(true);

            await Assert.ThrowsAsync<OperationCanceledException>(() => cache.LoadAsync(token));
            await Assert.ThrowsAsync<OperationCanceledException>(() => cache.SaveAsync([], token));
            Assert.AreEqual(original, File.ReadAllText(path));
        }

        private static MruItem Item(string path) => new() { Path = path, Name = "App", Type = MruItemType.Solution };
    }
}
