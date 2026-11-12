using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using StartScreen.Models.DevHub;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test
{
    [TestClass]
    public class StartScreenDesignLayoutTests
    {
        private static readonly XNamespace _presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace _xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        [TestMethod]
        [DataRow(0, 5, false, Key.Down, 1)]
        [DataRow(4, 5, false, Key.Down, -1)]
        [DataRow(1, 5, false, Key.Up, 0)]
        [DataRow(0, 5, false, Key.Up, -1)]
        [DataRow(0, 5, false, Key.Right, -1)]
        [DataRow(1, 5, false, Key.Left, -1)]
        [DataRow(0, 5, true, Key.Down, 2)]
        [DataRow(1, 5, true, Key.Down, 3)]
        [DataRow(2, 5, true, Key.Up, 0)]
        [DataRow(1, 5, true, Key.Up, -1)]
        [DataRow(3, 5, true, Key.Down, -1)]
        [DataRow(0, 5, true, Key.Right, 1)]
        [DataRow(1, 5, true, Key.Left, 0)]
        [DataRow(1, 5, true, Key.Right, -1)]
        [DataRow(2, 5, true, Key.Left, -1)]
        [DataRow(4, 5, true, Key.Right, -1)]
        public void ActivityNavigation_FollowsPersonalRowsAndTeamColumns(int index, int count, bool team, Key key, int expected)
        {
            Assert.AreEqual(expected, DevHubPanel.GetActivityNavigationTarget(index, count, team, key));
        }

        [STATestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ActivityFilterChanges_PreserveLayoutAndTemplateContext(bool team)
        {
            var document = Read("DevHubPanel.xaml");
            var definition = new XElement(document.Descendants(_presentation + "ItemsControl")
                .Single(element => element.Attribute(_xaml + "Name")?.Value == (team ? "IncomingList" : "MyWorkList")));
            definition.Attribute("ItemTemplate")!.Remove();
            var original = (ItemsControl)SharedXamlReader.Parse(definition.ToString());
            var host = new StackPanel();
            host.Children.Add(original);
            var presentation = new DevHubActivityList(original);
            presentation.Show(DevHubWorkItemKind.PullRequest, false, []);
            var issues = presentation.Show(DevHubWorkItemKind.Issue, false, []);
            var builds = presentation.Show(DevHubWorkItemKind.Build, false, []);

            foreach (var list in new[] { original, issues, builds })
            {
                Assert.AreEqual(team ? "Team" : null, list.Tag);
                Assert.AreEqual(team ? 0 : int.MaxValue, list.AlternationCount);
                Assert.AreSame(original.ItemsPanel, list.ItemsPanel);
                Assert.AreSame(original.ItemContainerStyle, list.ItemContainerStyle);
                Assert.AreEqual(original.Margin, list.Margin);
                if (team)
                {
                    var panel = (System.Windows.Controls.Primitives.UniformGrid)list.ItemsPanel.LoadContent();
                    Assert.AreEqual(2, panel.Columns);
                }
            }
            Assert.AreSame(issues, presentation.Show(DevHubWorkItemKind.Issue, false, []));
        }

        [STATestMethod]
        [DataRow(640d, 480d, 12d)]
        [DataRow(1000d, 700d, 20d)]
        public void StackedActivity_ConstrainsAndIndependentlyScrollsBothLists(double width, double height, double fontSize)
        {
            var document = Read("DevHubPanel.xaml");
            var definition = new XElement(document.Descendants(_presentation + "Grid")
                .Single(element => element.Attribute(_xaml + "Name")?.Value == "WorkBlades"));
            foreach (var attribute in definition.DescendantsAndSelf().Attributes().Where(attribute =>
                new[] { "Click", "ButtonBase.Click", "Checked", "Unchecked" }.Contains(attribute.Name.LocalName)).ToArray())
                attribute.Remove();
            foreach (var list in definition.Descendants(_presentation + "ItemsControl"))
            {
                list.Attribute("ItemTemplate")!.Remove();
                list.Add(new XElement(_presentation + "ItemsControl.ItemTemplate",
                    new XElement(_presentation + "DataTemplate", new XElement(_presentation + "TextBlock",
                        new XAttribute("Text", "{Binding}"), new XAttribute("Height", "60")))));
            }
            foreach (var empty in definition.Descendants(_presentation + "TextBlock").Where(element =>
                element.Attribute(_xaml + "Name")?.Value is "MyWorkEmpty" or "IncomingEmpty"))
                empty.SetAttributeValue("Visibility", "Collapsed");
            var filterStyle = new XElement(document.Descendants(_presentation + "Style")
                .Single(element => element.Attribute(_xaml + "Key")?.Value == "ActivityFilterStyle"));
            var root = (Grid)SharedXamlReader.Parse(new XElement(_presentation + "Grid",
                new XAttribute(XNamespace.Xmlns + "x", _xaml.NamespaceName),
                new XElement(_presentation + "Grid.Resources", filterStyle,
                    new XElement(_presentation + "BooleanToVisibilityConverter", new XAttribute(_xaml + "Key", "BoolToVisibilityConverter"))),
                definition).ToString());
            root.DataContext = new { HasSelectedMruItem = false };
            root.Resources["TestEnvironmentFontSize"] = fontSize;
            StartScreen.ToolWindows.Typography.SetEnvironmentSize(root, fontSize);
            var blades = (Grid)root.Children[0];
            foreach (var blade in blades.Children.OfType<Grid>())
            {
                var scroll = blade.Children.OfType<ScrollViewer>().Single();
                ((StackPanel)scroll.Content).Children.OfType<ItemsControl>().Single().ItemsSource = Enumerable.Range(1, 50).ToArray();
            }
            Layout(root, width, height);
            var sections = blades.Children.OfType<Grid>().ToArray();
            var personal = sections[0].Children.OfType<ScrollViewer>().Single();
            var team = sections[1].Children.OfType<ScrollViewer>().Single();
            Assert.AreEqual(height, blades.ActualHeight, 0.01);
            Assert.IsGreaterThan(sections[0].ActualHeight, sections[1].TranslatePoint(new Point(), root).Y);
            foreach (var scroll in new[] { personal, team })
            {
                Assert.IsGreaterThan(0d, scroll.ViewportHeight);
                Assert.IsGreaterThan(scroll.ViewportHeight, scroll.ExtentHeight);
                Assert.IsLessThan(height / 2, scroll.ActualHeight);
            }
            personal.ScrollToVerticalOffset(120);
            Layout(root, width, height);
            Assert.AreEqual(120d, personal.VerticalOffset, 0.01);
            Assert.AreEqual(0d, team.VerticalOffset);
            team.ScrollToVerticalOffset(60);
            Layout(root, width, height);
            Assert.AreEqual(120d, personal.VerticalOffset, 0.01);
            Assert.AreEqual(60d, team.VerticalOffset, 0.01);
            foreach (var filter in new[]
            {
                ("MyWorkCopilotFilter", "Recent conversations", "Last active"),
                ("MyWorkPrFilter", "Your pull requests", "Updated"),
                ("MyWorkIssueFilter", "Your issues", "Updated"),
                ("MyWorkBuildFilter", "Your builds", "Updated"),
                ("MyWorkAttentionFilter", "Needs your attention", "Updated"),
            })
            {
                Descendants<RadioButton>(root).Single(element => element.Name == filter.Item1).IsChecked = true;
                Layout(root, width, height);
                var heading = Descendants<TextBlock>(root).Single(element => element.Name == "MyWorkColumnHeading");
                Assert.AreEqual(filter.Item2, heading.Text);
                var columns = (Grid)heading.Parent;
                Assert.AreEqual(filter.Item3, columns.Children.OfType<TextBlock>().Last().Text);
                if (filter.Item1 == "MyWorkIssueFilter")
                    Assert.AreEqual("State / assignee", columns.Children.OfType<TextBlock>().ElementAt(1).Text);
            }
            foreach (var sectionHeading in Descendants<TextBlock>(root).Where(element =>
                element.Name == "IncomingHeading" || element.Name == "WorkHeadingLine" || element.Text == "My work"))
            {
                Assert.AreEqual(fontSize * 20 / 12, sectionHeading.FontSize, 0.01);
                Assert.AreEqual(FontWeights.SemiBold, sectionHeading.FontWeight);
                Assert.AreEqual(1d, sectionHeading.Opacity);
            }
        }

        [STATestMethod]
        [DataRow("#252526", "#F1F1F1")]
        [DataRow("#F5F5F5", "#1E1E1E")]
        public void NewsSidebar_ActuallyRendersDistinctPixelsWhenHostBrushesAreIdentical(string background, string foreground)
        {
            var definition = new XElement(Read("StartScreenControl.xaml").Descendants(_presentation + "Grid")
                .Single(element => element.Attribute(_xaml + "Name")?.Value == "NewsColumnPanel"));
            definition.Elements(_presentation + "Grid").Remove();
            var panel = (Grid)SharedXamlReader.Parse(definition.ToString());
            var baseColor = (Color)ColorConverter.ConvertFromString(background);
            var foregroundColor = (Color)ColorConverter.ConvertFromString(foreground);
            panel.Resources["TestThemeBrush"] = new SolidColorBrush(baseColor);
            panel.Resources[SystemParameters.HighContrastKey] = false;
            var tint = panel.Children.OfType<Border>().Single(element => element.Style == panel.FindResource("StartScreen.SidebarTint"));
            Assert.AreEqual("{DynamicResource {x:Static platformui:EnvironmentColors.ToolWindowTextBrushKey}}",
                SharedXamlReader.Setter("StartScreen.SidebarTint", "Background"));
            tint.SetResourceReference(Border.BackgroundProperty, "TestSidebarForeground");
            panel.Resources["TestSidebarForeground"] = new SolidColorBrush(foregroundColor);
            Layout(panel, 80, 80);
            var pixel = RenderPixel(panel);
            Assert.IsGreaterThanOrEqualTo(6, Math.Abs(pixel.R - baseColor.R));
            Assert.IsLessThanOrEqualTo(16, Math.Abs(pixel.R - baseColor.R));
            panel.Resources[SystemParameters.HighContrastKey] = true;
            Layout(panel, 80, 80);
            Assert.AreEqual(Visibility.Collapsed, tint.Visibility);
            Assert.AreEqual(SystemColors.WindowColor, RenderPixel(panel));
        }

        [STATestMethod]
        public void ExplicitSlimScrollbar_OverridesHostStylesAndStillScrolls()
        {
            var scroll = (ScrollViewer)SharedXamlReader.Parse(new XElement(_presentation + "ScrollViewer",
                new XAttribute("Style", "{StaticResource StartScreen.ScrollViewer}"), new XAttribute("VerticalScrollBarVisibility", "Auto"),
                new XElement(_presentation + "Border", new XAttribute("Height", "1400"))).ToString());
            scroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar))
            {
                Setters = { new Setter(FrameworkElement.WidthProperty, 40d) },
            };
            scroll.Resources[SystemParameters.HighContrastKey] = false;
            Layout(scroll, 440, 200);
            var bar = Descendants<System.Windows.Controls.Primitives.ScrollBar>(scroll).Single();
            Assert.AreEqual(10d, bar.ActualWidth);
            var track = (System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track", bar);
            var thumb = (Border)track.Thumb.Template.FindName("ThumbBorder", track.Thumb);
            Assert.AreEqual(4d, thumb.ActualWidth, 0.01);
            Assert.AreEqual(0.38, thumb.Opacity);
            Assert.AreEqual(24d, track.Thumb.MinHeight);
            track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0)
            {
                RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            });
            track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, 120 / track.ValueFromDistance(0, 1))
            {
                RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent,
            });
            track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false)
            {
                RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            });
            Layout(scroll, 440, 200);
            Assert.AreEqual(120d, scroll.VerticalOffset, 0.01);
            scroll.Resources[SystemParameters.HighContrastKey] = true;
            Layout(scroll, 440, 200);
            Assert.AreEqual(1d, thumb.Opacity);
            Assert.AreSame(SystemColors.WindowTextBrush, thumb.Background);
        }

        [STATestMethod]
        public void TeamGutter_Remains32PixelsAfterSwitchingFilters()
        {
            var definition = new XElement(Read("DevHubPanel.xaml").Descendants(_presentation + "ItemsControl")
                .Single(element => element.Attribute(_xaml + "Name")?.Value == "IncomingList"));
            definition.Attribute("ItemTemplate")!.Remove();
            var original = (ItemsControl)SharedXamlReader.Parse(definition.ToString());
            var host = new StackPanel();
            host.Children.Add(original);
            var presentation = new DevHubActivityList(original);
            var items = DevHubWorkItem.GetIncoming([], Enumerable.Range(1, 2).Select(index => new DevHubIssue
            {
                Title = "Issue " + index, WebUrl = "https://example.com/issues/" + index,
            }), []);
            foreach (var kind in new[] { DevHubWorkItemKind.PullRequest, DevHubWorkItemKind.Issue, DevHubWorkItemKind.Build })
            {
                var list = presentation.Show(kind, false, items);
                Layout(host, 800, 200);
                var left = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0);
                var right = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.AreEqual(32d, right.TranslatePoint(new Point(), host).X - left.TranslatePoint(new Point(left.ActualWidth, 0), host).X, 0.01);
                Assert.AreEqual(800d, right.TranslatePoint(new Point(right.ActualWidth, 0), host).X, 0.01);
            }
        }

        [STATestMethod]
        public void ShortRepositoryTitles_DoNotReserveAnEmptySecondLine()
        {
            var title = new TextBlock { Text = "Short issue title", TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
            StartScreen.ToolWindows.Typography.SetMaxLineCount(title, 2);
            title.Measure(new Size(400, double.PositiveInfinity));
            title.Arrange(new Rect(0, 0, 400, title.DesiredSize.Height));
            Assert.AreEqual(title.LineHeight, title.ActualHeight, 0.01);
            Assert.AreEqual(2 * title.LineHeight, title.MaxHeight, 0.01);
        }

        [TestMethod]
        [DataRow("active", "Ada Lovelace", "active", "Ada Lovelace")]
        [DataRow("open", null, "Open", null)]
        [DataRow("open", "  Grace Hopper  ", "Open", "Grace Hopper")]
        public void Issues_MapActualStateAndAssigneeWithoutInventingPriority(string state, string? assignee, string expectedState, string? expectedAssignee)
        {
            var item = DevHubWorkItem.GetMyWork([], [new DevHubIssue
            {
                State = state, AssignedTo = assignee, IsAuthoredByCurrentUser = true, WebUrl = "https://example.com/issues/1",
            }], []).Single();
            Assert.AreEqual(expectedState, item.StatusText);
            Assert.AreEqual(expectedAssignee, item.AssigneeText);
        }

        [TestMethod]
        [DataRow("success")]
        [DataRow("failure")]
        [DataRow("pending")]
        public void Builds_UseNativeStatusMonikersAndDurationBesideStatus(string status)
        {
            var item = DevHubWorkItem.GetMyWork([], [], [], builds: [new DevHubCiRun
            {
                Status = status, IsTriggeredByCurrentUser = true, WebUrl = "https://example.com/build/1",
            }]).Single();
            var expected = status == "success" ? Microsoft.VisualStudio.Imaging.KnownMonikers.StatusOK
                : status == "failure" ? Microsoft.VisualStudio.Imaging.KnownMonikers.StatusError
                : Microsoft.VisualStudio.Imaging.KnownMonikers.StatusRunning;
            Assert.AreEqual(expected.Id, item.StatusMoniker.Id);
            Assert.AreEqual(expected.Guid, item.StatusMoniker.Guid);
            var template = Read("DevHubPanel.xaml").Descendants(_presentation + "DataTemplate")
                .Single(element => element.Attribute(_xaml + "Key")?.Value == "RepositoryItemMetadataTemplate");
            var duration = template.Descendants(_presentation + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding DurationText}");
            Assert.AreEqual(_presentation + "Grid", duration.Parent!.Name);
            Assert.AreEqual("1", duration.Attribute("Grid.Column")?.Value);
            Assert.HasCount(1, duration.Parent.Elements(_presentation + "Border"));
            Assert.HasCount(1, template.Descendants().Where(element => element.Name.LocalName == "CrispImage"
                && element.Attribute("Moniker")?.Value == "{Binding StatusMoniker}"));
        }

        private static Color RenderPixel(FrameworkElement element)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(80, 80, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var bytes = new byte[4];
            bitmap.CopyPixels(new Int32Rect(20, 20, 1, 1), bytes, 4, 0);
            return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
        }

        [STATestMethod]
        public void NewsSidebar_TracksThemeAndHighContrastWithoutChangingGeometry()
        {
            var panel = (Grid)SharedXamlReader.Parse(new XElement(_presentation + "Grid",
                new XAttribute("Style", "{StaticResource StartScreen.Sidebar}")).ToString());
            panel.Resources[SystemParameters.HighContrastKey] = false;
            foreach (var brush in new[] { Brushes.DimGray, Brushes.WhiteSmoke, Brushes.SteelBlue })
            {
                panel.Resources["TestThemeBrush"] = brush;
                Layout(panel, 360, 640);
                Assert.AreSame(brush, panel.Background);
                Assert.AreEqual(new Size(360, 640), panel.RenderSize);
            }
            panel.Resources[SystemParameters.HighContrastKey] = true;
            panel.Resources[SystemColors.WindowBrushKey] = SystemColors.WindowBrush;
            Layout(panel, 360, 640);
            Assert.AreSame(SystemColors.WindowBrush, panel.Background);
            Assert.AreEqual(new Size(360, 640), panel.RenderSize);
        }

        [STATestMethod]
        [DataRow(false, 640d, 12d, false)]
        [DataRow(false, 640d, 20d, false)]
        [DataRow(true, 300d, 12d, false)]
        [DataRow(true, 300d, 20d, false)]
        [DataRow(false, 640d, 12d, true)]
        [DataRow(false, 960d, 20d, true)]
        [DataRow(true, 300d, 20d, true)]
        public void WorkTemplate_KeepsTitlesStatusAndDatesInTheirOwnAreas(bool team, double width, double fontSize, bool shortTitle)
        {
            var document = Read("DevHubPanel.xaml");
            var resources = document.Root!.Element(_presentation + "UserControl.Resources")!
                .Element(_presentation + "ResourceDictionary")!.Elements()
                .Where(element => element.Name != _presentation + "ResourceDictionary.MergedDictionaries"
                    && element.Attribute(_xaml + "Key")?.Value != "ActivityFilterStyle")
                .Select(element => new XElement(element)).ToArray();
            foreach (var element in resources.SelectMany(element => element.DescendantsAndSelf()).Where(element =>
                element.Name.NamespaceName == "clr-namespace:StartScreen.ToolWindows.Controls"))
                element.Name = XName.Get(element.Name.LocalName, "clr-namespace:StartScreen.ToolWindows.Controls;assembly=StartScreen");
            foreach (var attribute in resources.SelectMany(element => element.DescendantsAndSelf().Attributes()).Where(attribute =>
                new[] { "MouseUp", "PreviewMouseRightButtonDown", "PreviewMouseRightButtonUp", "PreviewKeyDown" }.Contains(attribute.Name.LocalName)).ToArray())
                attribute.Remove();
            foreach (var image in resources.SelectMany(element => element.Descendants()).Where(element => element.Name.LocalName == "CrispImage").ToArray())
                image.ReplaceWith(new XElement(_presentation + "Border", new XAttribute("Width", "16"), new XAttribute("Height", "16")));
            var listDefinition = document.Descendants(_presentation + "ItemsControl")
                .Single(element => element.Attribute(_xaml + "Name")?.Value == (team ? "IncomingList" : "MyWorkList"));
            var content = new List<XElement> { new(listDefinition) };
            if (!team)
            {
                var heading = new XElement(document.Descendants(_presentation + "TextBlock")
                    .Single(element => element.Attribute(_xaml + "Name")?.Value == "MyWorkDateHeading")
                    .Ancestors(_presentation + "Border").First());
                heading.SetAttributeValue("Grid.Row", "0");
                var scroll = new XElement(listDefinition.Ancestors(_presentation + "ScrollViewer").First());
                scroll.SetAttributeValue("Grid.Row", "1");
                content = [new XElement(_presentation + "Grid.RowDefinitions",
                    new XElement(_presentation + "RowDefinition", new XAttribute("Height", "Auto")),
                    new XElement(_presentation + "RowDefinition", new XAttribute("Height", "*"))), heading, scroll];
            }
            var root = (Grid)SharedXamlReader.Parse(new XElement(_presentation + "Grid",
                new XAttribute(XNamespace.Xmlns + "x", _xaml.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "controls", "clr-namespace:StartScreen.ToolWindows.Controls;assembly=StartScreen"),
                new XElement(_presentation + "Grid.Resources", resources),
                content).ToString());
            var item = DevHubWorkItem.GetOwnPullRequests([new DevHubPullRequest
            {
                Title = shortTitle ? "Short title" : "A long pull request title that should wrap without overlapping its status or date",
                IsAuthoredByCurrentUser = true, Status = "open", WebUrl = "https://github.com/o/r/pull/1",
                RepoDisplayName = "o/r", Number = "#1", Author = "A developer", UpdatedAt = DateTime.UtcNow.AddDays(-1),
            }]).Single();
            var list = (ItemsControl)root.FindName(team ? "IncomingList" : "MyWorkList");
            var companion = DevHubWorkItem.GetIncoming([new DevHubPullRequest
            {
                Title = shortTitle ? "A much longer title that wraps across two lines of this team activity card" : "Short title",
                Status = "open", WebUrl = "https://github.com/o/r/pull/2", Author = "Another developer",
                RepoDisplayName = "o/r", Number = "#2", UpdatedAt = DateTime.UtcNow.AddDays(-1),
            }], [], []).Single();
            list.ItemsSource = team ? new[] { item, companion, item, companion } : new[] { item, companion };
            root.Resources["TestEnvironmentFontSize"] = fontSize;
            StartScreen.ToolWindows.Typography.SetEnvironmentSize(root, fontSize);
            for (var pass = 0; pass < 3; pass++)
                Layout(root, team ? width * 2 + 32 : width, 600);
            var cards = Descendants<Border>(list).Where(element => element.Name == "WorkItemBorder").ToArray();
            AssertActivitySeparators(list, team);
            var card = cards[0];
            var grid = ((Grid)card.Child).Children.OfType<Grid>().Single();
            var title = grid.Children.OfType<TextBlock>().Single(element => element.Text == item.DisplayTitle);
            var date = grid.Children.OfType<TextBlock>().Single(element => element.Text == item.ActivityDateText);
            var status = grid.Children.OfType<StackPanel>().Single();
            var author = Descendants<TextBlock>(card).Single(element => element.Text == item.AuthorText);
            var statusText = Descendants<TextBlock>(card).Single(element => element.Text == item.StatusText);
            if (team)
            {
                Assert.AreEqual(statusText.TranslatePoint(new Point(0, statusText.ActualHeight / 2), card).Y,
                    author.TranslatePoint(new Point(0, author.ActualHeight / 2), card).Y, 0.01);
                Assert.IsGreaterThan(statusText.TranslatePoint(new Point(statusText.ActualWidth, 0), card).X,
                    author.TranslatePoint(new Point(), card).X);
            }
            else
            {
                Assert.AreEqual(2d, author.TranslatePoint(new Point(), card).Y
                    - statusText.TranslatePoint(new Point(0, statusText.ActualHeight), card).Y, 0.01);
                Assert.AreEqual(statusText.TranslatePoint(new Point(), card).X, author.TranslatePoint(new Point(), card).X, 0.01);
            }
            Assert.IsGreaterThan(0d, title.ActualWidth);
            Assert.AreEqual(TextWrapping.Wrap, title.TextWrapping);
            Assert.AreEqual(2 * title.LineHeight, title.MaxHeight, 0.01);
            Assert.IsLessThanOrEqualTo(title.MaxHeight, title.ActualHeight);
            if (shortTitle)
                Assert.AreEqual(title.LineHeight, title.ActualHeight, 0.01);
            var separator = ((Grid)card.Child).Children.OfType<Border>().Single(element => element.Name == "ActivitySeparator");
            var dpiScale = VisualTreeHelper.GetDpi(separator).DpiScaleY;
            Assert.AreEqual(1d, separator.Height);
            Assert.AreEqual(Math.Round(dpiScale) / dpiScale, separator.ActualHeight, 0.01);
            Assert.AreEqual(grid.ActualWidth, separator.ActualWidth, 0.01);
            Assert.AreEqual(team ? new Thickness(8, 10, 8, 10) : new Thickness(8, 4, 8, 4), card.Padding);
            Assert.AreEqual(team ? 12d : 6d, separator.Margin.Top);
            // The content and separator edges can each round by half a device pixel.
            Assert.IsGreaterThanOrEqualTo(grid.TranslatePoint(new Point(0, grid.ActualHeight), card).Y
                + separator.Margin.Top - 1 / dpiScale - 0.01,
                separator.TranslatePoint(new Point(), card).Y);
            if (team)
            {
                Assert.HasCount(4, cards);
                foreach (var other in cards.Skip(1))
                    Assert.AreEqual(card.ActualHeight, other.ActualHeight, 0.01);
                for (var index = 0; index < cards.Length; index += 2)
                {
                    var leftRule = Descendants<Border>(cards[index]).Single(element => element.Name == "ActivitySeparator");
                    var rightRule = Descendants<Border>(cards[index + 1]).Single(element => element.Name == "ActivitySeparator");
                    Assert.AreEqual(leftRule.TranslatePoint(new Point(), list).Y, rightRule.TranslatePoint(new Point(), list).Y, 0.01);
                }
            }
            var typeIcon = ((Grid)card.Child).Children.OfType<System.Windows.Shapes.Path>().Single();
            Assert.AreEqual(16d, typeIcon.Width);
            Assert.IsGreaterThan(0d, typeIcon.ActualWidth);
            Assert.IsLessThanOrEqualTo(16d, typeIcon.ActualWidth);
            Assert.AreEqual(16d, typeIcon.ActualHeight, 0.01);
            Assert.IsGreaterThan(typeIcon.TranslatePoint(new Point(typeIcon.ActualWidth, 0), card).X,
                title.TranslatePoint(new Point(), card).X);
            Assert.AreEqual(grid.ActualWidth, date.TranslatePoint(new Point(date.ActualWidth, 0), grid).X, 0.01);
            Assert.IsGreaterThanOrEqualTo(date.DesiredSize.Width, date.ActualWidth);
            if (team)
            {
                Assert.AreEqual(3, Grid.GetRow(status));
                Assert.IsGreaterThan(title.TranslatePoint(new Point(0, title.ActualHeight), grid).Y,
                    status.TranslatePoint(new Point(), grid).Y);
            }
            else
            {
                Assert.AreEqual(1, Grid.GetColumn(status));
                Assert.IsGreaterThan(title.TranslatePoint(new Point(title.ActualWidth, 0), grid).X,
                    status.TranslatePoint(new Point(), grid).X);
                Assert.IsGreaterThanOrEqualTo(status.TranslatePoint(new Point(status.ActualWidth, 0), grid).X,
                    date.TranslatePoint(new Point(), grid).X);
            }
            if (!team)
            {
                var heading = Descendants<TextBlock>(root).Single(element => element.Name == "MyWorkDateHeading");
                var scroll = Descendants<ScrollViewer>(root).Single();
                Assert.AreEqual(TextAlignment.Right, heading.TextAlignment);
                Assert.AreEqual(TextAlignment.Right, date.TextAlignment);
                foreach (var height in new[] { 100d, 600d })
                {
                    for (var pass = 0; pass < 3; pass++)
                        Layout(root, width, height);
                    Assert.AreEqual(height == 100 ? Visibility.Visible : Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
                    Assert.AreEqual(heading.TranslatePoint(new Point(heading.ActualWidth, 0), root).X,
                        date.TranslatePoint(new Point(date.ActualWidth, 0), root).X, 0.01,
                        "The date header and values must share their right edge with or without a scrollbar.");
                }
            }
            if (!team && shortTitle)
            {
                list.ItemsSource = DevHubWorkItem.GetCopilotItems([new StartScreen.Models.CopilotActivitySession
                {
                    Id = "compact-session", Title = "Short conversation", IsCached = true,
                    WorkingDirectory = @"C:\Repos\StartScreen",
                }, new StartScreen.Models.CopilotActivitySession
                {
                    Id = "last-session", Title = "Last conversation", IsCached = true,
                    WorkingDirectory = @"C:\Repos\StartScreen",
                }], false).ToArray();
                for (var pass = 0; pass < 3; pass++)
                    Layout(root, width, 600);
                AssertActivitySeparators(list, false);
                var chatCard = Descendants<Border>(list).First(element => element.Name == "WorkItemBorder");
                Assert.AreEqual(new Thickness(0), chatCard.Margin);
                if (fontSize == 12)
                {
                    Assert.IsGreaterThanOrEqualTo(54d, chatCard.ActualHeight);
                    Assert.IsLessThanOrEqualTo(60d, chatCard.ActualHeight);
                }
                var presentation = new DevHubActivityList(list);
                foreach (var attention in new[] { false, true, false })
                {
                    foreach (var update in new[] { new[] { item, companion }, new[] { companion }, new[] { item, companion },
                        new[] { companion, item }, Array.Empty<DevHubWorkItem>(), new[] { companion } })
                    {
                        list = presentation.Show(DevHubWorkItemKind.PullRequest, attention, update);
                        for (var pass = 0; pass < 3; pass++)
                            Layout(root, width, 600);
                        AssertActivitySeparators(list, false);
                    }
                }
            }
        }

        [STATestMethod]
        [DataRow(800d, 480d, true)]
        [DataRow(1400d, 700d, true)]
        [DataRow(2200d, 900d, true)]
        [DataRow(3200d, 1000d, true)]
        [DataRow(300d, 480d, false)]
        [DataRow(3200d, 1000d, false)]
        public void NewsSidebar_FillsThePageViewportEvenWhenHorizontallyScrolled(double width, double height, bool showWork)
        {
            var document = Read("StartScreenControl.xaml");
            var definition = new XElement(document.Descendants(_presentation + "ScrollViewer")
                .Single(element => element.Attribute(_xaml + "Name")?.Value == "PageScroll"));
            definition.Descendants(_presentation + "ScrollViewer.Resources").Remove();
            foreach (var attribute in definition.DescendantsAndSelf().Attributes().Where(attribute =>
                new[] { "PreviewMouseWheel", "SizeChanged", "ScrollChanged" }.Contains(attribute.Name.LocalName)).ToArray())
                attribute.Remove();
            var layout = definition.Descendants(_presentation + "Grid").Single(element => element.Attribute(_xaml + "Name")?.Value == "ContentLayout");
            var contentWidth = Math.Max(showWork ? 1400 : 440, width - 24);
            var columns = StartScreen.ToolWindows.StartScreenControl.CalculateContentLayout(width - 24, showWork);
            layout.SetAttributeValue("Width", contentWidth);
            layout.Element(_presentation + "Grid.ColumnDefinitions")!.Elements().First().SetAttributeValue("Width", columns.WorkWidth);
            layout.Elements().Single(element => element.Name.LocalName == "DevHubPanel").ReplaceWith(new XElement(_presentation + "Border"));
            var sidebarDefinition = layout.Elements(_presentation + "Grid").Single();
            sidebarDefinition.SetAttributeValue("Grid.Column", showWork ? 1 : 0);
            sidebarDefinition.SetAttributeValue("Grid.ColumnSpan", showWork ? 1 : 2);
            var newsColumn = layout.Element(_presentation + "Grid.ColumnDefinitions")!.Elements().Last();
            Assert.AreEqual("440", newsColumn.Attribute("MinWidth")?.Value);
            Assert.IsNull(newsColumn.Attribute("MaxWidth"));
            sidebarDefinition.RemoveNodes();
            sidebarDefinition.Add(new XElement(_presentation + "ScrollViewer", new XAttribute("VerticalScrollBarVisibility", "Auto"),
                new XElement(_presentation + "Border", new XAttribute("Height", "1400"))));
            var page = (ScrollViewer)SharedXamlReader.Parse(definition.ToString());
            for (var pass = 0; pass < 3; pass++)
                Layout(page, width, height);
            var content = Descendants<Grid>(page).Single(element => element.Name == "ContentLayout");
            var sidebar = Descendants<Grid>(page).Single(element => element.Name == "NewsColumnPanel");
            var horizontal = Descendants<ScrollViewer>(page).Single(element => element.Name == "ContentScroll");
            horizontal.ScrollToRightEnd();
            Layout(page, width, height);
            Assert.AreEqual(height, page.ViewportHeight, 0.01);
            Assert.AreEqual(page.ViewportHeight, content.ActualHeight, 0.01);
            Assert.AreEqual(contentWidth - columns.WorkWidth, sidebar.ActualWidth, 0.01);
            Assert.IsGreaterThanOrEqualTo(440d, sidebar.ActualWidth);
            Assert.IsLessThanOrEqualTo(1000d, columns.WorkWidth);
            if (showWork)
                Assert.IsGreaterThanOrEqualTo(960d, columns.WorkWidth);
            Assert.AreEqual(width, sidebar.TranslatePoint(new Point(sidebar.ActualWidth, 0), page).X, 0.01);
            Assert.AreEqual(height, sidebar.TranslatePoint(new Point(0, sidebar.ActualHeight), page).Y, 0.01);
            Assert.AreEqual(0d, page.ScrollableHeight);
            Assert.IsGreaterThan(0d, ((ScrollViewer)sidebar.Children[0]).ScrollableHeight);
        }

        [STATestMethod]
        public void ExpandedSettings_StayScrollableInsideTheBoundedWorkspace()
        {
            var document = Read("DevHubPanel.xaml");
            var definition = new XElement(document.Descendants(_presentation + "Border")
                .Single(element => element.Attribute(_xaml + "Name")?.Value == "SettingsPanel"));
            definition.SetAttributeValue("Visibility", "Visible");
            var scroll = new XElement(definition.Elements(_presentation + "ScrollViewer").Single());
            scroll.RemoveNodes();
            scroll.Add(new XElement(_presentation + "Border", new XAttribute("Height", "1400")));
            definition.RemoveNodes();
            definition.Add(scroll);
            var root = (UserControl)SharedXamlReader.Parse(new XElement(_presentation + "UserControl", definition).ToString());
            for (var pass = 0; pass < 3; pass++)
                Layout(root, 640, 400);
            var settings = (Border)root.Content;
            var viewport = (ScrollViewer)settings.Child;
            Assert.IsLessThanOrEqualTo(400d, settings.ActualHeight);
            Assert.IsGreaterThan(0d, viewport.ViewportHeight);
            Assert.IsGreaterThan(0d, viewport.ScrollableHeight);
            viewport.ScrollToBottom();
            Layout(root, 640, 400);
            Assert.AreEqual(viewport.ScrollableHeight, viewport.VerticalOffset, 0.01);
        }

        [STATestMethod]
        [DataRow(800d, 12d)]
        [DataRow(1400d, 12d)]
        [DataRow(1746d, 12d)]
        [DataRow(800d, 20d)]
        [DataRow(1746d, 20d)]
        public void Header_WrapsCommandsAndStatusWithoutOverlapping(double width, double fontSize)
        {
            var document = Read("StartScreenControl.xaml");
            var actionBar = document.Descendants(_presentation + "WrapPanel")
                .Single(element => element.Attribute(_xaml + "Name")?.Value == "ActionBar");
            var definition = new XElement(actionBar.Ancestors(_presentation + "Border").First());
            definition.Descendants(_presentation + "Button.ContextMenu").Remove();
            foreach (var attribute in definition.DescendantsAndSelf().Attributes().Where(attribute =>
                new[] { "Click", "PreviewKeyDown", "PreviewMouseLeftButtonDown" }.Contains(attribute.Name.LocalName)).ToArray())
                attribute.Remove();
            foreach (var image in definition.Descendants().Where(element => element.Name.LocalName == "CrispImage").ToArray())
                image.ReplaceWith(new XElement(_presentation + "Border", new XAttribute("Width", "16"), new XAttribute("Height", "16")));
            var styles = document.Root!.Element(_presentation + "UserControl.Resources")!
                .Element(_presentation + "ResourceDictionary")!.Elements(_presentation + "Style")
                .Where(element => new[] { "ActionButtonStyle", "CommandButtonStyle", "IconButtonStyle" }.Contains(element.Attribute(_xaml + "Key")?.Value))
                .Select(element => new XElement(element)).ToArray();
            styles.Single(element => element.Attribute(_xaml + "Key")?.Value == "ActionButtonStyle").Attribute("BasedOn")!.Remove();
            var root = (Grid)SharedXamlReader.Parse(new XElement(_presentation + "Grid",
                new XAttribute(XNamespace.Xmlns + "x", _xaml.NamespaceName),
                new XElement(_presentation + "Grid.Resources", styles,
                    new XElement(_presentation + "BooleanToVisibilityConverter", new XAttribute(_xaml + "Key", "BoolToVisibilityConverter"))),
                definition).ToString());
            root.DataContext = new
            {
                HasServiceHealthStatus = true, ServiceHealthText = "Services healthy", IsUpdateAvailable = true,
                UpdateAvailableText = "Update to 18.5", UnifiedRefreshStatus = "Updated just now", IsRefreshingAny = false,
            };
            root.Resources["TestEnvironmentFontSize"] = fontSize;
            StartScreen.ToolWindows.Typography.SetEnvironmentSize(root, fontSize);
            for (var pass = 0; pass < 3; pass++)
                Layout(root, width, 400);
            var header = (Grid)((Border)root.Children[0]).Child;
            var launch = (StackPanel)header.Children[0];
            var status = (StackPanel)header.Children[1];
            Assert.IsGreaterThanOrEqualTo(launch.TranslatePoint(new Point(launch.ActualWidth, 0), root).X,
                status.TranslatePoint(new Point(), root).X);
            foreach (var button in Descendants<Button>(header))
            {
                Assert.IsGreaterThan(0d, button.ActualWidth);
                Assert.IsGreaterThanOrEqualTo(0d, button.TranslatePoint(new Point(), root).X);
                Assert.IsLessThanOrEqualTo(width, button.TranslatePoint(new Point(button.ActualWidth, 0), root).X);
            }
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is T result)
                    yield return result;
                foreach (var descendant in Descendants<T>(child))
                    yield return descendant;
            }
        }

        [TestMethod]
        public void PullRequestBranch_RemainsAvailableInRepositoryTooltip()
        {
            var item = DevHubWorkItem.GetOwnPullRequests([new DevHubPullRequest
            {
                Title = "Branch details", IsAuthoredByCurrentUser = true, Status = "open",
                WebUrl = "https://github.com/o/r/pull/1", RepoDisplayName = "o/r", Number = "#1",
                SourceBranch = "feature/polish", TargetBranch = "main",
            }]).Single();

            StringAssert.Contains(item.DetailToolTip, item.Detail);
            StringAssert.Contains(item.DetailToolTip, "feature/polish");
            StringAssert.Contains(item.DetailToolTip, "main");
        }

        private static void AssertActivitySeparators(ItemsControl list, bool team)
        {
            var cards = Descendants<Border>(list).Where(element => element.Name == "WorkItemBorder").ToArray();
            Assert.HasCount(list.Items.Count, cards);
            for (var index = 0; index < cards.Length; index++)
            {
                var separator = Descendants<Border>(cards[index]).Single(element => element.Name == "ActivitySeparator");
                Assert.AreEqual(team || index < cards.Length - 1 ? Visibility.Visible : Visibility.Collapsed, separator.Visibility);
            }
        }

        private static void Layout(FrameworkElement element, double width, double height)
        {
#pragma warning disable VSTHRD001 // Apply bindings on the isolated WPF test's STA thread.
            element.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
#pragma warning restore VSTHRD001
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
        }

        private static XDocument Read(string name)
        {
            using var stream = typeof(StartScreenDesignLayoutTests).Assembly.GetManifestResourceStream(name);
            return XDocument.Load(stream!);
        }
    }
}
