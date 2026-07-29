using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class MruServiceTests
    {
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
    }
}
