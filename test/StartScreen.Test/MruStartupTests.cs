using System.Text.Json;
using StartScreen.Models;
using StartScreen.Services;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public sealed class MruStartupTests
    {
        [TestMethod]
        public void InitialParse_SkipsDiskTimestampAndPreservesOpenPath()
        {
            using var directory = new TemporaryDirectory();
            string path = directory.GetPath("App.sln");
            File.WriteAllText(path, string.Empty);
            string raw = path + "|{guid}|False|App";

            MruItem initial = MruService.ParseMruEntry(raw, includeMetadata: false);

            Assert.AreEqual(path, initial.Path);
            Assert.AreEqual("App", initial.Name);
            Assert.AreEqual(MruItemType.Solution, initial.Type);
            Assert.AreEqual(DateTime.MinValue, initial.LastAccessed);
            CollectionAssert.AreEqual(new[] { raw }, initial.RawMruEntries);
            Assert.AreEqual(File.GetLastWriteTime(path), MruService.ParseMruEntry(raw).LastAccessed);
        }

        [TestMethod]
        public void InitialReconciliation_UsesAuthoritativePathWithoutTimestampProbe()
        {
            using var directory = new TemporaryDirectory();
            string path = directory.GetPath("App.slnx");
            File.WriteAllText(path, string.Empty);
            MruItem legacy = MruService.ParseMruEntry(directory.GetPath("App.sln") + "|{guid}|False|App", includeMetadata: false);
            var stale = new MruItem { Path = directory.GetPath(@"Stale\Stale.sln"), Type = MruItemType.Solution };

            List<MruItem> result = MruService.MergeCurrentRecentProjects([legacy, stale], [path, path], includeMetadata: false);

            Assert.HasCount(1, result);
            Assert.AreEqual(path, result[0].Path);
            Assert.AreEqual(DateTime.MinValue, result[0].LastAccessed);
            CollectionAssert.AreEqual(legacy.RawMruEntries, result[0].RawMruEntries);
            Assert.IsEmpty(MruService.MergeCurrentRecentProjects([legacy], [], includeMetadata: false));
        }

        [TestMethod]
        public void InitialTimestamps_RestoreNewestStoredTimeAcrossSolutionFormats()
        {
            var oldTime = new DateTime(2026, 1, 1);
            DateTime newTime = oldTime.AddDays(1);
            var options = new Options
            {
                ExtendedMruItems = JsonSerializer.Serialize(new[]
                {
                    new { Path = @"C:\Code\App\App.sln", Name = "App", Type = 0, LastAccessed = oldTime },
                    new { Path = @"C:\Code\App\App.slnx", Name = "App", Type = 0, LastAccessed = newTime }
                })
            };
            var current = new MruItem { Path = @"C:\Code\App\App.slnx", Type = MruItemType.Solution };
            var unknown = new MruItem { Path = @"C:\Code\Other\Other.sln", Type = MruItemType.Solution };

            MruService.RestoreStoredAccessTimes([current, unknown], options);

            Assert.AreEqual(newTime, current.LastAccessed);
            Assert.AreEqual(DateTime.MinValue, unknown.LastAccessed);
        }

        [TestMethod]
        public async Task InitialExtendedItems_DoNotPersistButRetainPinsAndHistoryAsync()
        {
            var options = new Options
            {
                PinnedItems = @"C:\Code\Older\Older.sln",
                ExtendedMruItems = "[{\"Path\":\"C:\\\\Code\\\\Older\\\\Older.sln\",\"Name\":\"Older\",\"Type\":0,\"LastAccessed\":\"2025-01-01T00:00:00\"}]"
            };
            string originalHistory = options.ExtendedMruItems;
            var current = new MruItem { Path = @"C:\Code\New\New.sln", Name = "New", Type = MruItemType.Solution };

            List<MruItem> extended = await MruService.GetExtendedOnlyItemsAsync([current], options, persistChanges: false);

            Assert.AreEqual(originalHistory, options.ExtendedMruItems);
            Assert.HasCount(1, extended);
            Assert.IsTrue(extended[0].IsPinned);
            Assert.IsTrue(extended[0].IsFromExtendedList);
            var viewModel = new StartScreenViewModel(new[] { current }.Concat(extended));
            CollectionAssert.AreEqual(new[] { extended[0], current }, viewModel.VisibleMruItems.ToArray());
            Assert.AreEqual(current.Path, viewModel.VisibleMruItems[1].Path);
        }

        [TestMethod]
        public async Task InitialExtendedItems_ExcludeCurrentDeduplicatedSolutionAsync()
        {
            var options = new Options
            {
                ExtendedMruItems = JsonSerializer.Serialize(new[]
                {
                    new { Path = @"C:\Code\App\App.sln", Name = "App", Type = 0, LastAccessed = new DateTime(2026, 1, 1) }
                })
            };
            var current = new MruItem { Path = @"C:\Code\App\App.slnx", Type = MruItemType.Solution };

            List<MruItem> extended = await MruService.GetExtendedOnlyItemsAsync([current], options, persistChanges: false);

            Assert.IsEmpty(extended);
        }

        [TestMethod]
        public void InitialExists_IsOptimisticUntilBackgroundResultIsApplied()
        {
            using var directory = new TemporaryDirectory();
            var item = new MruItem { Path = directory.GetPath("missing.sln"), Type = MruItemType.Solution };
            var changes = new List<string?>();
            item.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

            Assert.IsTrue(item.Exists);
            Assert.IsFalse(item.CalculateExists());
            Assert.IsTrue(item.Exists);
            Assert.IsEmpty(changes);

            item.ApplyExists(false);

            Assert.IsFalse(item.Exists);
            CollectionAssert.AreEqual(new[] { nameof(MruItem.Exists) }, changes);
            item.Path = directory.GetPath("other.sln");
            Assert.IsTrue(item.Exists);
            item.ApplyExists(true);
            Assert.IsTrue(item.Exists);
        }

        [TestMethod]
        public async Task InitialExtendedItems_CancellationLeavesHistoryUnchangedAsync()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var options = new Options { ExtendedMruItems = "history", PinnedItems = "pinned" };

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                MruService.GetExtendedOnlyItemsAsync([], options, cancellation.Token, persistChanges: false));

            Assert.AreEqual("history", options.ExtendedMruItems);
            Assert.AreEqual("pinned", options.PinnedItems);
        }
    }
}