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
    }
}
