using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Xml.Linq;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test
{
    [TestClass]
    public class NewsKeyboardNavigationTests
    {
        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void AdjacentCards_FollowRenderedRowsWithoutWrapping(int columns)
        {
            var positions = Enumerable.Range(0, columns * 2 + 1)
                .Select(index => new Point(index % columns * 380, index / columns * 200)).ToArray();

            Assert.AreEqual(columns, NewsKeyboardNavigation.GetAdjacentIndex(positions, 0, Key.Down));
            Assert.AreEqual(0, NewsKeyboardNavigation.GetAdjacentIndex(positions, columns, Key.Up));
            Assert.AreEqual(-1, NewsKeyboardNavigation.GetAdjacentIndex(positions, 0, Key.Left));
            Assert.AreEqual(-1, NewsKeyboardNavigation.GetAdjacentIndex(positions, 0, Key.Up));
            Assert.AreEqual(-1, NewsKeyboardNavigation.GetAdjacentIndex(positions, columns - 1, Key.Right));
            Assert.AreEqual(-1, NewsKeyboardNavigation.GetAdjacentIndex(positions, positions.Length - 1, Key.Down));
            Assert.AreEqual(columns * 2, NewsKeyboardNavigation.GetAdjacentIndex(positions, columns * 2 - 1, Key.Down),
                "A partial last row remains reachable from any column.");
            if (columns > 1)
            {
                Assert.AreEqual(1, NewsKeyboardNavigation.GetAdjacentIndex(positions, 0, Key.Right));
                Assert.AreEqual(0, NewsKeyboardNavigation.GetAdjacentIndex(positions, 1, Key.Left));
            }
        }

        [TestMethod]
        public void AdjacentCards_HandleSeparatePinnedGridAndChangingColumnCounts()
        {
            Point[] positions = [new(0, 0), new(0, 250), new(380, 250), new(0, 450), new(380, 450)];
            Assert.AreEqual(1, NewsKeyboardNavigation.GetAdjacentIndex(positions, 0, Key.Down));
            Assert.AreEqual(0, NewsKeyboardNavigation.GetAdjacentIndex(positions, 2, Key.Up));
            Assert.AreEqual(4, NewsKeyboardNavigation.GetAdjacentIndex(positions, 2, Key.Down));
            Assert.AreEqual(2, NewsKeyboardNavigation.GetAdjacentIndex(positions, 4, Key.Up));
            Assert.AreEqual(-1, NewsKeyboardNavigation.GetAdjacentIndex(positions, 0, Key.Right));

            Point[] narrowPositions = positions.Select((_, index) => new Point(0, index * 200)).ToArray();
            Assert.AreEqual(3, NewsKeyboardNavigation.GetAdjacentIndex(narrowPositions, 2, Key.Down));
            Assert.AreEqual(-1, NewsKeyboardNavigation.GetAdjacentIndex(narrowPositions, 2, Key.Right));
        }

        [STATestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void BladeEntry_FocusesSelectedTabsContentAndReturnsThroughItsHeader(int selected)
        {
            var (tabs, targets) = CreateTabs();
            tabs.SelectedIndex = selected;
            var host = Show(tabs);
            try
            {
                Assert.IsTrue(NewsKeyboardNavigation.FocusFirstItem(tabs));
                Assert.AreSame(targets[selected][0], Keyboard.FocusedElement);

                Press(tabs, Key.Up);
                Assert.AreSame(tabs.SelectedItem, Keyboard.FocusedElement);
                Press(tabs, Key.Down);
                Assert.AreSame(targets[selected][0], Keyboard.FocusedElement);

                var exits = 0;
                Assert.IsTrue(NewsKeyboardNavigation.HandleKey(tabs, (DependencyObject)targets[selected][0], Key.Left, () => exits++));
                Assert.AreEqual(1, exits, "Every tab's first column must be able to return to the adjacent activity blade.");
            }
            finally
            {
                host.Close();
            }
        }

        [STATestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public void CardsAndLinks_NavigateBothColumnsAndRowsWithRoutedArrowKeys(int selected)
        {
            var (tabs, targets) = CreateTabs();
            tabs.SelectedIndex = selected;
            var host = Show(tabs);
            try
            {
                Assert.IsTrue(NewsKeyboardNavigation.FocusFirstItem(tabs));
                Press(tabs, Key.Right);
                Assert.AreSame(targets[selected][1], Keyboard.FocusedElement);
                Press(tabs, Key.Down);
                Assert.AreSame(targets[selected][3], Keyboard.FocusedElement);
                Press(tabs, Key.Left);
                Assert.AreSame(targets[selected][2], Keyboard.FocusedElement);
                Press(tabs, Key.Up);
                Assert.AreSame(targets[selected][0], Keyboard.FocusedElement);

                Assert.IsFalse(NewsKeyboardNavigation.HandleKey(tabs, (DependencyObject)targets[selected][0], Key.Enter, () => Assert.Fail()));
                Assert.IsFalse(NewsKeyboardNavigation.HandleKey(tabs, (DependencyObject)targets[selected][0], Key.C, () => Assert.Fail()));
            }
            finally
            {
                host.Close();
            }
        }

        [STATestMethod]
        public void Headers_SwitchTabsWithoutWrappingAndEmptyTabsKeepAKeyboardDestination()
        {
            var (tabs, _) = CreateTabs();
            var host = Show(tabs);
            try
            {
                var exits = 0;
                ((TabItem)tabs.Items[0]).Focus();
                Assert.IsTrue(NewsKeyboardNavigation.HandleKey(tabs, (DependencyObject)Keyboard.FocusedElement, Key.Left, () => exits++));
                Assert.AreEqual(0, tabs.SelectedIndex);
                Assert.AreEqual(1, exits);

                for (var index = 1; index < tabs.Items.Count; index++)
                {
                    Press(tabs, Key.Right);
                    Assert.AreEqual(index, tabs.SelectedIndex);
                    Assert.AreSame(tabs.SelectedItem, Keyboard.FocusedElement);
                }
                Press(tabs, Key.Right);
                Assert.AreEqual(3, tabs.SelectedIndex);
                Press(tabs, Key.Left);
                Assert.AreEqual(2, tabs.SelectedIndex);

                foreach (TabItem tab in tabs.Items)
                {
                    tab.Content = new StackPanel();
                    tab.IsSelected = true;
                    Assert.IsTrue(NewsKeyboardNavigation.FocusFirstItem(tabs));
                    Assert.AreSame(tab, Keyboard.FocusedElement);
                    Press(tabs, Key.Down);
                    Assert.AreSame(tab, Keyboard.FocusedElement);
                }
            }
            finally
            {
                host.Close();
            }
        }

        [TestMethod]
        public void Layout_WiresNewsNavigationAtTheTabControlAndRemovesObsoleteCrossTabEvents()
        {
            using var stream = typeof(NewsKeyboardNavigationTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var tabs = document.Descendants(p + "TabControl").Single(element => element.Attribute(x + "Name")?.Value == "NewsTabs");
            Assert.AreEqual("NewsTabs_PreviewKeyDown", tabs.Attribute("PreviewKeyDown")?.Value);
            Assert.IsFalse(document.Descendants().Attributes().Any(attribute => attribute.Name.LocalName == "FocusYouTubeRequested"));
            Assert.IsFalse(document.Descendants().Attributes().Any(attribute => attribute.Value == "YouTubeVideoControl_FocusNewsRequested"));
        }

        private static (TabControl Tabs, List<IInputElement[]> Targets) CreateTabs()
        {
            using var stream = typeof(NewsKeyboardNavigationTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var template = new XElement(document.Descendants(p + "ControlTemplate")
                .Single(element => element.Attribute("TargetType")?.Value == "TabControl"));
            template.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
            var tabs = new TabControl { Template = (ControlTemplate)XamlReader.Parse(template.ToString()) };
            var targets = new List<IInputElement[]>();
            foreach (var name in new[] { "Blogs", "Videos", "Feedback", "Release notes" })
            {
                var grid = new UniformGrid { Columns = 2 };
                var items = new List<IInputElement>();
                for (var index = 0; index < (name == "Release notes" ? 1 : 4); index++)
                {
                    if (name == "Feedback" || name == "Release notes")
                    {
                        var link = new Hyperlink(new Run(name + index));
                        var text = new TextBlock { Height = 80, Margin = new Thickness(8) };
                        text.Inlines.Add(link);
                        grid.Children.Add(text);
                        items.Add(link);
                    }
                    else
                    {
                        var border = new Border { Focusable = true, Height = 80, Margin = new Thickness(8),
                            Child = new Button { Content = "Pin" } };
                        grid.Children.Add(border);
                        items.Add(border);
                    }
                }
                grid.Children.Insert(0, new Border { Focusable = true, Visibility = Visibility.Collapsed });
                tabs.Items.Add(new TabItem { Header = name, Content = grid });
                targets.Add(items.ToArray());
            }
            tabs.PreviewKeyDown += (_, e) => e.Handled = NewsKeyboardNavigation.HandleKey(tabs,
                (DependencyObject)Keyboard.FocusedElement, e.Key, () => { });
            return (tabs, targets);
        }

        private static Window Show(TabControl tabs)
        {
            var host = new Window { Content = tabs, Width = 900, Height = 600, Left = -32000, Top = -32000,
                ShowActivated = false, ShowInTaskbar = false };
            host.Show();
            host.UpdateLayout();
            return host;
        }

        private static void Press(TabControl tabs, Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tabs), 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            if (Keyboard.FocusedElement is UIElement element)
                element.RaiseEvent(args);
            else
                ((ContentElement)Keyboard.FocusedElement).RaiseEvent(args);
            Assert.IsTrue(args.Handled);
            tabs.UpdateLayout();
        }
    }
}