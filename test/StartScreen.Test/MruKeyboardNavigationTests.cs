using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StartScreen.Models;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test
{
    [TestClass]
    public class MruKeyboardNavigationTests
    {
        [STATestMethod]
        public void AdjacentNavigation_UsesFilteredGroupedLogicalOrderAndStopsAtBoundaries()
        {
            var items = new[]
            {
                new MruItem { Name = "Recent", Path = @"C:\Recent" },
                new MruItem { Name = "Hidden", Path = @"C:\Hidden" },
                new MruItem { Name = "Pinned", Path = @"C:\Pinned", IsPinned = true },
            };
            var view = new ListCollectionView(items);
            view.Filter = value => ((MruItem)value).Name != "Hidden";
            view.SortDescriptions.Add(new SortDescription(nameof(MruItem.IsPinned), ListSortDirection.Descending));
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MruItem.DisplayGroup)));
            var list = new ListBox { ItemsSource = view };

            Assert.AreSame(items[2], list.Items[0]);
            Assert.AreEqual(1, MruItemControl.GetAdjacentItemIndex(list, items[2], true));
            Assert.AreEqual(0, MruItemControl.GetAdjacentItemIndex(list, items[0], false));
            Assert.AreEqual(-1, MruItemControl.GetAdjacentItemIndex(list, items[2], false), "Up at the first item returns to search.");
            Assert.AreEqual(-1, MruItemControl.GetAdjacentItemIndex(list, items[0], true));
            Assert.AreEqual(-1, MruItemControl.GetAdjacentItemIndex(list, items[1], true));
        }

        [STATestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Navigation_RealizesOffscreenTargetsAndCanReturnToLogicalFirstItem(bool grouped)
        {
            var items = Enumerable.Range(0, 200).Select(index => new MruItem
            {
                Name = "Item " + index, Path = @"C:\Item" + index, IsPinned = index < 3,
            }).ToArray();
            var view = new ListCollectionView(items);
            if (grouped)
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MruItem.DisplayGroup)));
            var list = new ListBox
            {
                Width = 300,
                Height = 100,
                ItemsSource = view,
                ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) },
                ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
            };
            if (grouped)
                list.GroupStyle.Add(new GroupStyle
                {
                    Panel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
                });
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(list, true);
            var host = new Window
            {
                Content = list, Width = 320, Height = 140, Left = -32000, Top = -32000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            try
            {
                host.Show();
                host.UpdateLayout();
                Assert.IsNull(list.ItemContainerGenerator.ContainerFromIndex(items.Length - 1));

                var last = MruItemControl.RealizeItemContainer(list, items.Length - 1);
                Assert.IsNotNull(last);
                Assert.AreSame(items[items.Length - 1], last.Content);
                var previousIndex = MruItemControl.GetAdjacentItemIndex(list, last.Content, false);
                Assert.AreEqual(items.Length - 2, previousIndex);
                Assert.AreSame(items[previousIndex], MruItemControl.RealizeItemContainer(list, previousIndex)!.Content);

                var first = MruItemControl.RealizeItemContainer(list, 0);
                Assert.IsNotNull(first);
                Assert.AreSame(items[0], first.Content);
                list.InvalidateMeasure();
                Assert.AreSame(first, MruItemControl.RealizeItemContainer(list, 0));
                Assert.IsFalse(list.IsMeasureValid, "An already-realized target must not force a synchronous layout pass.");
                Assert.IsNull(MruItemControl.RealizeItemContainer(list, -1));
                Assert.IsNull(MruItemControl.RealizeItemContainer(list, items.Length));
            }
            finally
            {
                host.Close();
            }
        }
    }
}