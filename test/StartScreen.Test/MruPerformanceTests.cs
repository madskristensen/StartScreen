using System.Collections.Concurrent;
using System.Collections.Specialized;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Services;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class MruPerformanceTests
    {
        [TestMethod]
        public async Task Metadata_PublishesReadyItemsBeforeSlowItemAndBoundsBatchesAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var release = new ManualResetEventSlim();
            var firstPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = new ConcurrentBag<int>();
            var active = 0;
            var processing = BackgroundBatchProcessor.ProcessAsync(Enumerable.Range(0, 40).ToArray(), index =>
            {
                Assert.IsLessThanOrEqualTo(2, Interlocked.Increment(ref active));
                try
                {
                    if (index == 0)
                        release.Wait(timeout.Token);
                    return index;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }, (batch, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.IsLessThanOrEqualTo(8, batch.Count);
                foreach (var index in batch)
                    published.Add(index);
                if (batch.Contains(1))
                    firstPublished.TrySetResult(true);
                return Task.CompletedTask;
            }, timeout.Token);
            try
            {
                Assert.AreSame(firstPublished.Task, await Task.WhenAny(firstPublished.Task, Task.Delay(TimeSpan.FromSeconds(5))));
                Assert.IsFalse(processing.IsCompleted);
                Assert.IsFalse(published.Contains(0));
            }
            finally
            {
                release.Set();
                await processing;
            }
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 40).ToArray(), published.ToArray());
            Assert.AreEqual(0, active);
        }

        [TestMethod]
        public async Task Metadata_CancellationPreventsStalePublicationAsync()
        {
            using var cancellation = new CancellationTokenSource();
            var published = 0;

            await Assert.ThrowsAsync<OperationCanceledException>(() => BackgroundBatchProcessor.ProcessAsync(new[] { 1, 2 }, value =>
            {
                cancellation.Cancel();
                return value;
            }, (batch, token) =>
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref published);
                return Task.CompletedTask;
            }, cancellation.Token));

            Assert.AreEqual(0, published);
        }

        [TestMethod]
        public void Timestamps_MemoizeSharedDirectoryIncludingMissingSuoForOnePass()
        {
            var scans = 0;
            var cache = new MruService.MetadataCache(_ => { Interlocked.Increment(ref scans); return null; });
            Parallel.For(0, 20, index => Assert.IsNull(cache.GetSuoTime($@"C:\Code\App\Project{index}.csproj")));
            Assert.AreEqual(1, scans);
            Assert.IsNull(cache.GetSuoTime(@"c:\code\app\Other.sln"));
            Assert.AreEqual(1, scans);

            var nextPass = new MruService.MetadataCache(_ => { scans++; return DateTime.Today; });
            Assert.AreEqual(DateTime.Today, nextPass.GetSuoTime(@"C:\Code\App\App.sln"));
            Assert.AreEqual(2, scans);
        }

        [TestMethod]
        public void TimestampCache_IsSharedByParsingAndCurrentProjectReconciliation()
        {
            var scans = 0;
            var timestamp = DateTime.Today.AddDays(-1);
            var cache = new MruService.MetadataCache(_ => { scans++; return timestamp; });
            var parsed = MruService.ParseMruEntry(@"C:\Code\App\App.sln|guid|false|App", metadataCache: cache);

            var merged = MruService.MergeCurrentRecentProjects([parsed], [@"C:\Code\App\App.slnx"], metadataCache: cache);

            Assert.AreEqual(1, scans);
            Assert.AreEqual(timestamp, parsed.LastAccessed);
            Assert.AreEqual(timestamp, merged.Single().LastAccessed);
        }

        [TestMethod]
        public void Sessions_MemoizeSharedRootAndPreserveNestedItemPlusRepositoryCounts()
        {
            var scans = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var cache = new MruService.MetadataCache(countSessions: root =>
            {
                scans.AddOrUpdate(root, 1, (_, count) => count + 1);
                return root.TrimEnd('\\').EndsWith("Nested", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
            });

            Parallel.For(0, 20, index => Assert.AreEqual(5, CopilotChatHelper.CountSessions(
                $@"C:\Code\Repo\Nested\Project{index}.csproj", MruItemType.Project, @"C:\Code\Repo", cache.CountSessions)));

            Assert.HasCount(2, scans);
            Assert.IsTrue(scans.Values.All(count => count == 1));
            Assert.AreEqual(3, CopilotChatHelper.CountSessions(@"C:\Code\Repo\App.sln", MruItemType.Solution,
                @"c:\code\repo\", cache.CountSessions));
            Assert.HasCount(2, scans);
        }

        [TestMethod]
        public void Sessions_DriveRootRemainsAbsolute()
        {
            var cache = new MruService.MetadataCache(countSessions: root =>
            {
                Assert.AreEqual(@"C:\", root);
                return 1;
            });

            Assert.AreEqual(1, CopilotChatHelper.CountSessions(@"C:\App.sln", MruItemType.Solution, @"C:\", cache.CountSessions));
        }

        [TestMethod]
        public void Filtering_UnchangedMatchesPreserveCollectionsAndAvoidReset()
        {
            var item = Item("Alpha", 0);
            var model = new StartScreenViewModel([item, Item("Other", 0)]);
            model.SearchFilter = "Al";
            var pinned = model.PinnedItems;
            var groups = model.GroupedMruItems;
            var resets = 0;
            model.VisibleMruItems.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Reset)
                    resets++;
            };

            model.SearchFilter = "ALP";

            Assert.AreEqual(0, resets);
            Assert.AreSame(pinned, model.PinnedItems);
            Assert.AreSame(groups, model.GroupedMruItems);
            CollectionAssert.AreEqual(new[] { item }, model.VisibleMruItems.ToArray());
            model.SearchFilter = "Other";
            Assert.AreEqual(1, resets);
        }

        [TestMethod]
        public void Filtering_RegroupsExistingItemsWhenPinOrTimeGroupChanges()
        {
            var item = Item("Alpha", 0);
            var model = new StartScreenViewModel([item]);
            var changes = 0;
            model.VisibleMruItems.CollectionChanged += (_, _) => changes++;
            item.LastAccessed = DateTime.Today.AddDays(-90);

            model.FilterMru(null!);

            Assert.AreEqual(1, changes);
            Assert.AreEqual("Older", model.GroupedMruItems.Single().GroupName);
            item.IsPinned = true;
            model.FilterMru(null!);
            Assert.AreEqual(2, changes);
            CollectionAssert.AreEqual(new[] { item }, model.PinnedItems.ToArray());
            Assert.IsEmpty(model.GroupedMruItems);
        }

        private static MruItem Item(string name, int daysAgo) => new()
        {
            Name = name, Path = $@"C:\Code\{name}.sln", Type = MruItemType.Solution,
            LastAccessed = DateTime.Today.AddDays(-daysAgo),
        };
    }
}