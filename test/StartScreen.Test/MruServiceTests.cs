using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class MruServiceTests
    {
        [TestMethod]
        public async Task GetExtendedOnlyItems_CancellationPreservesHistoryAsync()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var options = new Options { ExtendedMruItems = "history", PinnedItems = "pinned" };

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                MruService.GetExtendedOnlyItemsAsync([], options, cancellation.Token));

            Assert.AreEqual("history", options.ExtendedMruItems);
            Assert.AreEqual("pinned", options.PinnedItems);
        }

        [TestMethod]
        public void TryReserveBackgroundFetch_ThrottlesReopensAndExpiresAtBoundary()
        {
            var repoPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsTrue(MruService.TryReserveBackgroundFetch(repoPath, now));
            Assert.IsFalse(MruService.TryReserveBackgroundFetch(repoPath.ToUpperInvariant(), now.AddMinutes(14)));
            Assert.IsTrue(MruService.TryReserveBackgroundFetch(repoPath, now.AddMinutes(15)));
            Assert.IsFalse(MruService.TryReserveBackgroundFetch(repoPath, now.AddMinutes(16)));
        }

        [TestMethod]
        public void TryReserveBackgroundFetch_ConcurrentRefreshesReserveOnlyOnce()
        {
            var repoPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var now = DateTime.UtcNow;
            var reservations = 0;

            Parallel.For(0, 20, _ =>
            {
                if (MruService.TryReserveBackgroundFetch(repoPath, now))
                    Interlocked.Increment(ref reservations);
            });

            Assert.AreEqual(1, reservations);
            Assert.IsTrue(MruService.TryReserveBackgroundFetch(repoPath + ".other", now));
        }

        [TestMethod]
        public void FilterToCurrentRecentProjects_WhenSettingsUnavailable_PreservesLegacyItems()
        {
            var item = new MruItem { Path = @"C:\Projects\App\App.sln", Type = MruItemType.Solution };

            List<MruItem> result = MruService.FilterToCurrentRecentProjects([item], null);

            CollectionAssert.AreEqual(new[] { item }, result);
        }

        [TestMethod]
        public void FilterToCurrentRecentProjects_WhenRecentListCleared_RemovesLegacyItems()
        {
            var item = new MruItem { Path = @"C:\Projects\App\App.sln", Type = MruItemType.Solution };

            List<MruItem> result = MruService.FilterToCurrentRecentProjects([item], []);

            Assert.IsEmpty(result);
        }

        [TestMethod]
        public void FilterToCurrentRecentProjects_UsesSolutionDirectoryDeduplication()
        {
            var item = new MruItem { Path = @"C:\Projects\App\App.sln", Type = MruItemType.Solution };
            string[] recentPaths = [@"C:\Projects\App\App.slnx"];

            List<MruItem> result = MruService.FilterToCurrentRecentProjects([item], recentPaths);

            CollectionAssert.AreEqual(new[] { item }, result);
        }

        [TestMethod]
        public void FilterToCurrentRecentProjects_RemovesEntriesMissingFromCurrentList()
        {
            var current = new MruItem { Path = @"C:\Projects\Current\Current.sln", Type = MruItemType.Solution };
            var stale = new MruItem { Path = @"C:\Projects\Stale\Stale.sln", Type = MruItemType.Solution };
            string[] recentPaths = [current.Path];

            List<MruItem> result = MruService.FilterToCurrentRecentProjects([current, stale], recentPaths);

            CollectionAssert.AreEqual(new[] { current }, result);
        }

        [TestMethod]
        public void ClearPersistedHistoryIfRecentProjectsEmpty_ClearsHistoryAndPins()
        {
            var options = new Options
            {
                ExtendedMruItems = "history",
                PinnedItems = @"C:\Projects\App\App.sln"
            };

            bool changed = MruService.ClearPersistedHistoryIfRecentProjectsEmpty([], options);

            Assert.IsTrue(changed);
            Assert.AreEqual(string.Empty, options.ExtendedMruItems);
            Assert.AreEqual(string.Empty, options.PinnedItems);
        }

        [TestMethod]
        public void ClearPersistedHistoryIfRecentProjectsEmpty_WhenSettingsUnavailable_PreservesHistory()
        {
            var options = new Options { ExtendedMruItems = "history" };

            bool changed = MruService.ClearPersistedHistoryIfRecentProjectsEmpty(null, options);

            Assert.IsFalse(changed);
            Assert.AreEqual("history", options.ExtendedMruItems);
        }

        [TestMethod]
        [DataRow(@"C:\Code\App.sln", MruItemType.Solution)]
        [DataRow(@"C:\Code\App.slnx", MruItemType.Solution)]
        [DataRow(@"C:\Code\App.csproj", MruItemType.Project)]
        [DataRow(@"C:\Code\App.vbproj", MruItemType.Project)]
        [DataRow(@"C:\Code\App.fsproj", MruItemType.Project)]
        [DataRow(@"C:\Code\App.vcxproj", MruItemType.Project)]
        [DataRow(@"C:\Code\Folder", MruItemType.Folder)]
        public void DetermineType_ReturnsExpectedType(string path, MruItemType expected)
        {
            Assert.AreEqual(expected, MruService.DetermineType(path));
        }

        [TestMethod]
        public void ParseMruEntry_WithValidEntry_MapsFieldsAndPreservesRawValue()
        {
            const string raw = @"C:\Code\App.sln|{guid}|False|Display App|ignored";

            MruItem item = MruService.ParseMruEntry(raw);

            Assert.IsNotNull(item);
            Assert.AreEqual(@"C:\Code\App.sln", item.Path);
            Assert.AreEqual("Display App", item.Name);
            Assert.AreEqual(MruItemType.Solution, item.Type);
            CollectionAssert.AreEqual(new[] { raw }, item.RawMruEntries);
        }

        [TestMethod]
        public void ParseMruEntry_WithMissingDisplayName_UsesFileName()
        {
            MruItem item = MruService.ParseMruEntry(@"C:\Code\App.csproj|{guid}|False||ignored");

            Assert.AreEqual("App", item.Name);
        }

        [TestMethod]
        public void ParseMruEntry_WithMalformedEntry_ReturnsNull()
        {
            Assert.IsNull(MruService.ParseMruEntry("not|enough|parts"));
        }

        [TestMethod]
        public void FindSuoLastWriteTime_ReturnsNewestSuo()
        {
            using var directory = new TemporaryDirectory();
            var solutionPath = directory.GetPath("App.sln");
            File.WriteAllText(solutionPath, string.Empty);
            var first = directory.GetPath(@".vs\App\v17\.suo");
            var second = directory.GetPath(@".vs\App\v18\.suo");
            Directory.CreateDirectory(Path.GetDirectoryName(first));
            Directory.CreateDirectory(Path.GetDirectoryName(second));
            File.WriteAllText(first, string.Empty);
            File.WriteAllText(second, string.Empty);
            var older = new DateTime(2026, 1, 1, 0, 0, 0);
            var newer = older.AddDays(1);
            File.SetLastWriteTime(first, older);
            File.SetLastWriteTime(second, newer);

            DateTime? result = MruService.FindSuoLastWriteTime(solutionPath);

            Assert.AreEqual(newer, result);
        }

        [TestMethod]
        public async Task GetExtendedOnlyItemsAsync_ExcludesCurrentItemsAndRestoresPinsAsync()
        {
            var current = new MruItem
            {
                Path = @"C:\Code\Current\Current.sln",
                Name = "Current",
                Type = MruItemType.Solution,
            };
            var options = new Options
            {
                ExtendedMruItems =
                    "[{\"Path\":\"C:\\\\Code\\\\Current\\\\Current.sln\",\"Name\":\"Old current\",\"Type\":0,\"LastAccessed\":\"2026-01-01T00:00:00\"}," +
                    "{\"Path\":\"C:\\\\Code\\\\Older\\\\Older.sln\",\"Name\":\"Older\",\"Type\":0,\"LastAccessed\":\"2025-01-01T00:00:00\"}]",
                PinnedItems = @"C:\Code\Older\Older.sln",
            };

            List<MruItem> result = await MruService.GetExtendedOnlyItemsAsync([current], options);

            Assert.HasCount(1, result);
            Assert.AreEqual("Older", result[0].Name);
            Assert.IsTrue(result[0].IsFromExtendedList);
            Assert.IsTrue(result[0].IsPinned);
        }
    }
}
