using System.IO;
using StartScreen.Models;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class StartScreenViewModelTests
    {
        [TestMethod]
        public void Shutdown_CancelsBackgroundWorkWithoutUnloadingControl()
        {
            using var shutdown = new CancellationTokenSource();
            var viewModel = new StartScreenViewModel(shutdown.Token);
            Task pendingWork = Task.Delay(Timeout.Infinite, viewModel.GetBackgroundCancellationToken());
            Assert.IsFalse(pendingWork.IsCompleted);

            shutdown.Cancel();

            Assert.IsTrue(pendingWork.IsCanceled);
            viewModel.ForceRefreshNews();
            viewModel.ForceRefreshYouTube();
            Assert.IsFalse(viewModel.IsRefreshingAny);
        }

        [TestMethod]
        public void ActivateBackgroundWork_CannotRestartAfterShutdown()
        {
            using var shutdown = new CancellationTokenSource();
            var viewModel = new StartScreenViewModel(shutdown.Token);
            viewModel.CancelBackgroundWork();
            Assert.IsTrue(viewModel.GetBackgroundCancellationToken().IsCancellationRequested);
            viewModel.ActivateBackgroundWork();
            Assert.IsFalse(viewModel.GetBackgroundCancellationToken().IsCancellationRequested);

            shutdown.Cancel();
            viewModel.ActivateBackgroundWork();

            Assert.IsTrue(viewModel.GetBackgroundCancellationToken().IsCancellationRequested);
            viewModel.ForceRefreshNews();
            viewModel.ForceRefreshYouTube();
            Assert.IsFalse(viewModel.IsRefreshingAny);
        }

        [TestMethod]
        public void ActivateBackgroundWork_PreCanceledShutdownRemainsCanceled()
        {
            using var shutdown = new CancellationTokenSource();
            shutdown.Cancel();
            var viewModel = new StartScreenViewModel(shutdown.Token);

            viewModel.ActivateBackgroundWork();

            Assert.IsTrue(viewModel.GetBackgroundCancellationToken().IsCancellationRequested);
        }

        [TestMethod]
        public void CancelBackgroundWork_SuppressesRefreshRequestsWhileHidden()
        {
            var item = CreateItem("Existing", @"C:\Code\Existing.sln");
            var viewModel = new StartScreenViewModel([item]);
            var changedProperties = new List<string?>();
            viewModel.PropertyChanged += (sender, args) => changedProperties.Add(args.PropertyName);
            viewModel.CancelBackgroundWork();

            viewModel.ForceRefreshNews();
            viewModel.ForceRefreshYouTube();

            Assert.IsFalse(viewModel.IsRefreshingNews);
            Assert.IsFalse(viewModel.IsRefreshingYouTube);
            Assert.IsEmpty(changedProperties);
            CollectionAssert.AreEqual(new[] { item }, viewModel.VisibleMruItems.ToArray());
        }

        [TestMethod]
        public void ShouldClearMruItem_All_ReturnsTrueForPinnedItem()
        {
            var item = new MruItem { IsPinned = true };

            Assert.IsTrue(StartScreenViewModel.ShouldClearMruItem(item, MruClearMode.All));
        }

        [TestMethod]
        public void ShouldClearMruItem_AllButPinned_PreservesPinnedItem()
        {
            var pinnedItem = new MruItem { IsPinned = true };
            var unpinnedItem = new MruItem();

            Assert.IsFalse(StartScreenViewModel.ShouldClearMruItem(pinnedItem, MruClearMode.AllButPinned));
            Assert.IsTrue(StartScreenViewModel.ShouldClearMruItem(unpinnedItem, MruClearMode.AllButPinned));
        }

        [TestMethod]
        public void ShouldClearMruItem_Missing_OnlyClearsMissingItems()
        {
            var existingPath = Path.GetTempFileName();

            try
            {
                var existingItem = new MruItem { Path = existingPath };
                var missingItem = new MruItem { Path = existingPath + ".missing" };
                existingItem.ApplyExists(existingItem.CalculateExists());
                missingItem.ApplyExists(missingItem.CalculateExists());

                Assert.IsFalse(StartScreenViewModel.ShouldClearMruItem(existingItem, MruClearMode.Missing));
                Assert.IsTrue(StartScreenViewModel.ShouldClearMruItem(missingItem, MruClearMode.Missing));
            }
            finally
            {
                File.Delete(existingPath);
            }
        }

        [TestMethod]
        public void FilterMru_MatchesNameAndPathCaseInsensitively()
        {
            var byName = CreateItem("Alpha", @"C:\Code\One\One.sln");
            var byPath = CreateItem("Beta", @"C:\Code\Special\Two.sln");
            var other = CreateItem("Gamma", @"C:\Code\Three\Three.sln");
            var viewModel = new StartScreenViewModel([byName, byPath, other]);

            viewModel.FilterMru("SPECIAL");

            Assert.HasCount(1, viewModel.GroupedMruItems);
            Assert.HasCount(1, viewModel.GroupedMruItems[0].Items);
            Assert.AreSame(byPath, viewModel.GroupedMruItems[0].Items[0]);
        }

        [TestMethod]
        public void FilterMru_SeparatesPinnedMatches()
        {
            var pinned = CreateItem("Pinned", @"C:\Code\Pinned.sln");
            pinned.IsPinned = true;
            var unpinned = CreateItem("Unpinned", @"C:\Code\Unpinned.sln");
            var viewModel = new StartScreenViewModel([pinned, unpinned]);

            viewModel.FilterMru("pinned");

            Assert.HasCount(1, viewModel.PinnedItems);
            Assert.AreSame(pinned, viewModel.PinnedItems[0]);
            Assert.HasCount(1, viewModel.GroupedMruItems);
            Assert.AreSame(unpinned, viewModel.GroupedMruItems[0].Items[0]);
        }

        [TestMethod]
        public void FilterMru_ExtendedMatchesAppearInEarlierGroup()
        {
            var extended = CreateItem("Old project", @"C:\Code\Old\Old.sln");
            extended.IsFromExtendedList = true;
            var viewModel = new StartScreenViewModel([extended]);

            viewModel.FilterMru("old");

            Assert.HasCount(1, viewModel.GroupedMruItems);
            Assert.AreEqual("Earlier", viewModel.GroupedMruItems[0].GroupName);
            Assert.AreSame(extended, viewModel.GroupedMruItems[0].Items[0]);
        }

        [TestMethod]
        public void FilterMru_BlankQueryRestoresAllItems()
        {
            var first = CreateItem("First", @"C:\Code\First.sln");
            var second = CreateItem("Second", @"C:\Code\Second.sln");
            var viewModel = new StartScreenViewModel([first, second]);
            viewModel.FilterMru("First");

            viewModel.FilterMru(string.Empty);

            Assert.HasCount(2, viewModel.MruItems);
        }

        [TestMethod]
        public void SearchFilter_FiltersMruImmediately()
        {
            var matching = CreateItem("Matching", @"C:\Code\Matching.sln");
            var other = CreateItem("Other", @"C:\Code\Other.sln");
            var viewModel = new StartScreenViewModel([matching, other]);

            viewModel.SearchFilter = "Matching";

            Assert.HasCount(1, viewModel.GroupedMruItems);
            Assert.HasCount(1, viewModel.GroupedMruItems[0].Items);
            Assert.AreSame(matching, viewModel.GroupedMruItems[0].Items[0]);
        }

        [TestMethod]
        public void SearchFilter_RapidChangesKeepLatestQuery()
        {
            var alpha = CreateItem("Alpha", @"C:\Code\Alpha.sln");
            var beta = CreateItem("Beta", @"C:\Code\Beta.sln");
            var viewModel = new StartScreenViewModel([alpha, beta]);

            viewModel.SearchFilter = "Alpha";
            viewModel.SearchFilter = "Beta";

            Assert.HasCount(1, viewModel.VisibleMruItems);
            Assert.AreSame(beta, viewModel.VisibleMruItems[0]);
        }

        [TestMethod]
        public void SearchFilter_PreservesBoundCollectionInstance()
        {
            var viewModel = new StartScreenViewModel(
                [
                    CreateItem("Alpha", @"C:\Code\Alpha.sln"),
                    CreateItem("Beta", @"C:\Code\Beta.sln"),
                ]);
            var boundCollection = viewModel.VisibleMruItems;

            viewModel.SearchFilter = "Alpha";

            Assert.AreSame(boundCollection, viewModel.VisibleMruItems);
        }

        [TestMethod]
        public void SetMruItems_PreservesActiveSearchFilter()
        {
            var original = CreateItem("Original match", @"C:\Code\Original.sln");
            var refreshedMatch = CreateItem("Refreshed match", @"C:\Code\Refreshed.sln");
            var refreshedOther = CreateItem("Other", @"C:\Code\Other.sln");
            var viewModel = new StartScreenViewModel([original])
            {
                SearchFilter = "match",
            };

            viewModel.SetMruItems([refreshedMatch, refreshedOther]);

            Assert.HasCount(1, viewModel.VisibleMruItems);
            Assert.AreSame(refreshedMatch, viewModel.VisibleMruItems[0]);
        }

        [TestMethod]
        public void SearchFilter_LargeMruReturnsOnlyMatchingItem()
        {
            var items = Enumerable.Range(0, 2000)
                .Select(index => CreateItem($"Project {index}", $@"C:\Code\Project{index}.sln"))
                .ToList();
            var viewModel = new StartScreenViewModel(items);

            viewModel.SearchFilter = "Project 1999";

            Assert.HasCount(1, viewModel.VisibleMruItems);
            Assert.AreSame(items[1999], viewModel.VisibleMruItems[0]);
        }

        [TestMethod]
        public void VisibleMruItems_OrdersPinnedBeforeTimeGroupsAndExtendedItems()
        {
            var older = CreateItem("Older", @"C:\Code\Older.sln");
            older.LastAccessed = DateTime.Today.AddDays(-60);
            var today = CreateItem("Today", @"C:\Code\Today.sln");
            var pinned = CreateItem("Pinned", @"C:\Code\Pinned.sln");
            pinned.IsPinned = true;
            var extended = CreateItem("Extended", @"C:\Code\Extended.sln");
            extended.IsFromExtendedList = true;

            var viewModel = new StartScreenViewModel([older, extended, today, pinned]);

            CollectionAssert.AreEqual(
                new[] { pinned, today, older, extended },
                viewModel.VisibleMruItems.ToArray());
        }

        [TestMethod]
        public void SelectedMruItem_UpdatesSelectionState()
        {
            var first = new MruItem();
            var second = new MruItem();
            var viewModel = new StartScreenViewModel();

            viewModel.SelectedMruItem = first;
            viewModel.SelectedMruItem = second;

            Assert.IsFalse(first.IsSelected);
            Assert.IsTrue(second.IsSelected);
            Assert.IsTrue(viewModel.HasSelectedMruItem);
        }

        [TestMethod]
        public void IsRefreshingAny_TracksAllRefreshOperations()
        {
            var viewModel = new StartScreenViewModel();

            Assert.IsFalse(viewModel.IsRefreshingAny);

            viewModel.IsRefreshingNews = true;
            Assert.IsTrue(viewModel.IsRefreshingAny);

            viewModel.IsRefreshingNews = false;
            viewModel.IsRefreshingYouTube = true;
            Assert.IsTrue(viewModel.IsRefreshingAny);

            viewModel.IsRefreshingYouTube = false;
            viewModel.IsRefreshingDevHub = true;
            Assert.IsTrue(viewModel.IsRefreshingAny);
        }

        [TestMethod]
        public void UnifiedRefreshStatus_ListsRefreshingSectionsAndLabelsIdleStatus()
        {
            var viewModel = new StartScreenViewModel
            {
                LastDevHubRefreshText = "Updated 5 minutes ago",
            };

            Assert.AreEqual("Dev Hub: Updated 5 minutes ago", viewModel.UnifiedRefreshStatus);

            viewModel.IsRefreshingNews = true;
            viewModel.IsRefreshingYouTube = true;

            Assert.AreEqual("Refreshing news, videos...", viewModel.UnifiedRefreshStatus);
        }

        [TestMethod]
        public void UnifiedRefreshStatus_IncludesNewsAndVideoFailuresAlongsideDevHubSuccess()
        {
            var viewModel = new StartScreenViewModel
            {
                LastDevHubRefreshText = "Updated just now",
                LastNewsRefreshText = "Refresh failed - Example. Use Refresh to retry.",
                LastYouTubeRefreshText = "Showing cached content - refresh failed. Use Refresh to retry.",
            };

            Assert.AreEqual("Dev Hub: Updated just now; News: Refresh failed - Example. Use Refresh to retry.; " +
                "Videos: Showing cached content - refresh failed. Use Refresh to retry.", viewModel.UnifiedRefreshStatus);
        }

        [TestMethod]
        public void UnifiedRefreshStatus_ReportsNewsWithoutDevHubAndNotifiesAfterRefresh()
        {
            var viewModel = new StartScreenViewModel { IsRefreshingNews = true };
            var changed = new List<string?>();
            viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            viewModel.LastNewsRefreshText = "Showing cached content";
            viewModel.IsRefreshingNews = false;

            Assert.AreEqual("News: Showing cached content", viewModel.UnifiedRefreshStatus);
            Assert.HasCount(2, changed.Where(name => name == nameof(StartScreenViewModel.UnifiedRefreshStatus)));
        }

        [TestMethod]
        public void NewsRefreshStatus_OnlyReportsFreshAfterEverySourceIsValidated()
        {
            var viewModel = new StartScreenViewModel { IsNewsUnavailable = true };

            viewModel.UpdateNewsRefreshStatus(new FeedRefreshResult([new NewsPost()], 1, [], []));

            Assert.IsFalse(viewModel.IsNewsUnavailable);
            Assert.AreEqual("Updated just now", viewModel.LastNewsRefreshText);
        }

        [TestMethod]
        public void NewsRefreshStatus_FreshCacheIsNotReportedAsRemoteRefresh()
        {
            var viewModel = new StartScreenViewModel();

            viewModel.UpdateNewsRefreshStatus(new FeedRefreshResult([new NewsPost()], 1, [], ["Example"]));

            Assert.IsFalse(viewModel.IsNewsUnavailable);
            Assert.AreEqual("Showing cached content from Example", viewModel.LastNewsRefreshText);
        }

        [TestMethod]
        public void NewsRefreshStatus_PartialFailureNamesFailedAndCachedSources()
        {
            var viewModel = new StartScreenViewModel();

            viewModel.UpdateNewsRefreshStatus(new FeedRefreshResult([new NewsPost()], 2, ["Failing"], ["Failing"]));

            Assert.IsFalse(viewModel.IsNewsUnavailable);
            Assert.AreEqual("Partially refreshed - unavailable: Failing. Showing cached content from Failing. Use Refresh to retry.",
                viewModel.LastNewsRefreshText);
            StringAssert.Contains(viewModel.UnifiedRefreshStatus, viewModel.LastNewsRefreshText);
        }

        [TestMethod]
        public void NewsRefreshStatus_AllFailedWithoutCacheShowsUnavailableAndRetry()
        {
            var viewModel = new StartScreenViewModel();

            viewModel.UpdateNewsRefreshStatus(new FeedRefreshResult([], 1, ["Example"], []));

            Assert.IsTrue(viewModel.IsNewsUnavailable);
            Assert.AreEqual("Refresh failed - unavailable: Example. Use Refresh to retry.", viewModel.LastNewsRefreshText);
        }

        [TestMethod]
        public void NewsRefreshStatus_AllFailedWithCacheIsNotFreshOrUnavailable()
        {
            var viewModel = new StartScreenViewModel();

            viewModel.UpdateNewsRefreshStatus(new FeedRefreshResult([new NewsPost()], 1, ["Example"], ["Example"]));

            Assert.IsFalse(viewModel.IsNewsUnavailable);
            Assert.AreEqual("Refresh failed - unavailable: Example. Showing cached content from Example. Use Refresh to retry.",
                viewModel.LastNewsRefreshText);
        }

        [TestMethod]
        public void NewsRefreshStatus_DisabledFeedsAreNotReportedAsUnavailable()
        {
            var viewModel = new StartScreenViewModel { IsNewsUnavailable = true };

            viewModel.UpdateNewsRefreshStatus(new FeedRefreshResult([], 0, [], []));

            Assert.IsFalse(viewModel.IsNewsUnavailable);
            Assert.AreEqual("No news feeds enabled", viewModel.LastNewsRefreshText);
        }

        private static MruItem CreateItem(string name, string path)
        {
            return new MruItem
            {
                Name = name,
                Path = path,
                LastAccessed = DateTime.Now,
                Type = MruItemType.Solution,
            };
        }
    }
}
