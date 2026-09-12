using System.IO;
using StartScreen.Models;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class StartScreenViewModelTests
    {
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
