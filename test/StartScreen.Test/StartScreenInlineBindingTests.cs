using System.Windows.Controls;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Xml.Linq;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class StartScreenInlineBindingTests
    {
        [STATestMethod]
        public void RepositoryCounts_ShowInlinePositiveBadgeAndHideZeroStatusRow()
        {
            var document = ReadLayoutXaml("MruItemControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var badgeDefinition = new XElement(document.Descendants(p + "Button")
                .Single(element => element.Attribute(x + "Name")?.Value == "RepositoryPullRequestsButton"));
            Assert.AreEqual("RepositorySummary_Click", badgeDefinition.Attribute("Click")!.Value);
            Assert.AreEqual("2", badgeDefinition.Attribute("Grid.Column")!.Value);
            var icon = badgeDefinition.Descendants().Single(element => element.Name.LocalName == "CrispImage");
            Assert.AreEqual("{x:Static catalog:KnownMonikers.PullRequest}", icon.Attribute("Moniker")!.Value);
            icon.ReplaceWith(new XElement(p + "Border", new XAttribute("Width", "12"), new XAttribute("Height", "12")));
            var rowDefinition = new XElement(document.Descendants(p + "Grid")
                .Single(element => element.Attribute(x + "Name")?.Value == "RepositoryStatusRow"));
            Assert.AreEqual("RepositoryReviews_Click", rowDefinition.Elements(p + "Button").First().Attribute("Click")!.Value);
            foreach (var click in badgeDefinition.DescendantsAndSelf().Concat(rowDefinition.DescendantsAndSelf()).Attributes("Click").ToArray())
                click.Remove();
            var styles = document.Root!.Element(p + "UserControl.Resources")!.Elements(p + "Style")
                .Select(element => new XElement(element)).ToArray();
            foreach (var setter in styles.SelectMany(element => element.Elements(p + "Setter"))
                .Where(element => element.Attribute("Property")?.Value == "Foreground").ToArray())
                setter.Remove();
            var root = new XElement(p + "Grid", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "Grid.Resources",
                    new XElement(p + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "BoolToVisibilityConverter")), styles),
                badgeDefinition, rowDefinition);
            var grid = (Grid)XamlReader.Parse(root.ToString());
            var badge = (Button)grid.Children[0];
            var row = (Grid)grid.Children[1];
            var reviews = (Button)row.Children[0];
            var status = (Button)row.Children[1];
            var item = new Models.MruItem { RemoteUrl = "https://github.com/o/r.git" };
            grid.DataContext = item;
            void UpdateBindings() => grid.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            UpdateBindings();
            Assert.AreEqual(Visibility.Collapsed, badge.Visibility);
            Assert.AreEqual(Visibility.Visible, row.Visibility);
            Assert.AreEqual("View repository activity", status.Content);

            item.ApplyRepositorySummary("No open PRs", "Loaded", 0, 0);
            UpdateBindings();
            Assert.AreEqual(Visibility.Collapsed, badge.Visibility);
            Assert.AreEqual(Visibility.Collapsed, row.Visibility);

            item.ApplyRepositorySummary("12 open PRs", "Loaded", 12, 0);
            UpdateBindings();
            Assert.AreEqual(Visibility.Visible, badge.Visibility);
            Assert.AreEqual(Visibility.Collapsed, row.Visibility);
            Assert.AreEqual("12", ((StackPanel)badge.Content).Children.OfType<TextBlock>().Single().Text);
            Assert.AreEqual("12 open PRs", badge.ToolTip);
            badge.ApplyTemplate();
            Assert.IsNotNull(System.Windows.Media.VisualTreeHelper.GetChild(badge, 0) as ContentPresenter);

            item.ApplyRepositorySummary("Reviews requested", "Loaded", 0, 2);
            UpdateBindings();
            Assert.AreEqual(Visibility.Collapsed, badge.Visibility);
            Assert.AreEqual(Visibility.Visible, row.Visibility);
            Assert.AreEqual(Visibility.Visible, reviews.Visibility);
            Assert.AreEqual("2 reviews", reviews.Content);
            Assert.AreEqual(Visibility.Collapsed, status.Visibility);

            item.ApplyRepositorySummary("Sign in to see repository activity", "Sign in");
            UpdateBindings();
            Assert.AreEqual(Visibility.Collapsed, badge.Visibility);
            Assert.AreEqual(Visibility.Visible, row.Visibility);
            Assert.AreEqual(Visibility.Visible, status.Visibility);
            Assert.AreEqual("Sign in to see repository activity", status.Content);
        }

        [STATestMethod]
        public void RecentItems_ReserveSpaceBetweenPinButtonsAndScrollbar()
        {
            var document = ReadLayoutXaml("StartScreenControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var list = document.Descendants(p + "ListBox")
                .Single(element => element.Attribute(x + "Name")?.Value == "MruPanel");
            var definition = new XElement(list.Element(p + "ListBox.ItemContainerStyle")!.Elements().Single());
            definition.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            var style = (Style)XamlReader.Parse(definition.ToString());
            var item = new ListBoxItem { Style = style };

            Assert.IsGreaterThanOrEqualTo(12d, item.Margin.Right);
            Assert.AreEqual(0d, item.Margin.Left);
            Assert.AreEqual(HorizontalAlignment.Stretch, item.HorizontalContentAlignment);
        }

        [STATestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ServiceHealth_HidesUncheckedAndCheckingStatusButShowsCompletedResults(bool healthy)
        {
            var document = ReadLayoutXaml("StartScreenControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = new XElement(document.Descendants(p + "StackPanel")
                .Single(element => element.Attribute("ToolTip")?.Value == "{Binding ServiceHealthDetails}"));
            definition.RemoveNodes();
            var root = new XElement(p + "Grid",
                new XElement(p + "Grid.Resources",
                    new XElement(p + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "BoolToVisibilityConverter"))),
                definition);
            var grid = (Grid)XamlReader.Parse(root.ToString());
            var panel = (StackPanel)grid.Children[0];
            Assert.AreEqual(Visibility.Collapsed, panel.Visibility);
            var model = new StartScreenViewModel();
            var changed = new List<string?>();
            model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            var binding = BindingOperations.GetBinding(panel, UIElement.VisibilityProperty)!;
            var expression = BindingOperations.SetBinding(panel, UIElement.VisibilityProperty, new Binding(binding.Path.Path)
            {
                Source = model,
                Mode = BindingMode.OneWay,
                Converter = binding.Converter,
                FallbackValue = binding.FallbackValue,
                TargetNullValue = binding.TargetNullValue,
            });
            expression.UpdateTarget();
            Assert.IsFalse(model.HasServiceHealthStatus);
            Assert.AreEqual(Visibility.Collapsed, panel.Visibility);

            model.SetServiceHealth("Checking service status...", "Checking", false, isChecking: true);
            expression.UpdateTarget();
            Assert.AreEqual(Visibility.Collapsed, panel.Visibility);

            model.SetServiceHealth("Completed", "Checked recently", healthy);
            expression.UpdateTarget();
            Assert.IsTrue(model.HasServiceHealthStatus);
            CollectionAssert.Contains(changed, nameof(StartScreenViewModel.HasServiceHealthStatus));
            Assert.AreEqual(healthy, model.IsServiceHealthy);
            Assert.AreEqual(Visibility.Visible, panel.Visibility);

            changed.Clear();
            model.SetServiceHealth("Checking service status...", "Refreshing", false, isChecking: true);
            expression.UpdateTarget();
            Assert.IsFalse(model.HasServiceHealthStatus);
            CollectionAssert.Contains(changed, nameof(StartScreenViewModel.HasServiceHealthStatus));
            Assert.AreEqual(Visibility.Collapsed, panel.Visibility);

            model.SetServiceHealth("Service status unavailable", "Refresh to try again", false);
            expression.UpdateTarget();
            Assert.AreEqual(Visibility.Visible, panel.Visibility);
            Assert.IsFalse(model.IsServiceHealthy);
        }

        [STATestMethod]
        [DataRow(1200d, false)]
        [DataRow(700d, true)]
        public void ActivitySections_AlignTabsAndFirstCardsAtWideAndStackedWidths(double width, bool stacked)
        {
            var screen = ReadLayoutXaml("StartScreenControl.xaml");
            var work = ReadLayoutXaml("DevHubPanel.xaml");
            var news = ReadLayoutXaml("NewsItemControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var content = screen.Descendants(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == "ContentLayout");
            var layout = StartScreenControl.CalculateContentLayout(width, true);
            Assert.AreEqual(stacked, layout.Stacked);
            var grid = new Grid();
            Grid.SetIsSharedSizeScope(grid, bool.Parse(content.Attribute("Grid.IsSharedSizeScope")!.Value));
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.WorkWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var row in content.Element(p + "Grid.RowDefinitions")!.Elements())
                grid.RowDefinitions.Add((RowDefinition)XamlReader.Parse(row.ToString()));

            var headerDefinition = new XElement(content.Elements(p + "StackPanel").Single());
            headerDefinition.Attribute("Visibility")!.Remove();
            headerDefinition.Descendants(p + "Button").Remove();
            headerDefinition.Descendants(p + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding WorkSectionTitle}")
                .SetAttributeValue("Text", "Your work");
            var status = headerDefinition.Descendants(p + "Run").Single();
            status.SetAttributeValue("Text", string.Empty);
            status.SetAttributeValue("FontSize", "12");
            status.Element(p + "Run.Foreground")!.Remove();
            var workHeader = (StackPanel)XamlReader.Parse(headerDefinition.ToString());
            var workHeading = workHeader.Children.OfType<DockPanel>().Single().Children.OfType<TextBlock>().Single();
            grid.Children.Add(workHeader);

            var newsDefinition = content.Elements(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == "NewsColumnPanel");
            var newsPanel = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = stacked ? new Thickness(0, 24, 0, 0) : new Thickness(0) };
            foreach (var row in newsDefinition.Element(p + "Grid.RowDefinitions")!.Elements())
                newsPanel.RowDefinitions.Add((RowDefinition)XamlReader.Parse(row.ToString()));
            Grid.SetRow(newsPanel, stacked ? 2 : 0);
            Grid.SetRowSpan(newsPanel, stacked ? 1 : 2);
            Grid.SetColumn(newsPanel, stacked ? 0 : 1);
            Grid.SetColumnSpan(newsPanel, stacked ? 2 : 1);
            grid.Children.Add(newsPanel);
            var newsHeaderDefinition = new XElement(newsDefinition.Element(p + "DockPanel")!);
            newsHeaderDefinition.Elements(p + "TextBlock").Where(element => element.Attribute("DockPanel.Dock")?.Value == "Right").Remove();
            var newsHeader = (DockPanel)XamlReader.Parse(newsHeaderDefinition.ToString());
            var newsHeading = newsHeader.Children.OfType<TextBlock>().Single();
            newsPanel.Children.Add(newsHeader);

            var workTitle = CreateWorkItemTitle(Models.DevHub.DevHubWorkItem.GetCopilotItems(
                [new Models.CopilotActivitySession { Id = "session", Title = "Example" }], false).Single());
            var workTemplate = work.Descendants(p + "DataTemplate").Single(element => element.Attribute(x + "Key")?.Value == "WorkItemTemplate");
            var workCard = new Border { Padding = ReadThickness(workTemplate.Element(p + "Border")!, "Padding"), Child = workTitle };
            var newsTitleDefinition = new XElement(news.Descendants(p + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding Title}"));
            newsTitleDefinition.SetAttributeValue("Text", "Example");
            newsTitleDefinition.Attribute("Foreground")!.Remove();
            var newsTitle = (TextBlock)XamlReader.Parse(newsTitleDefinition.ToString());
            var newsCard = new Border { Padding = ReadThickness(news.Root!.Element(p + "Border")!, "Padding"), Child = newsTitle };
            var overview = work.Descendants(p + "TabItem").Single(element => element.Attribute(x + "Name")?.Value == "OverviewTab");
            var blogs = screen.Descendants(p + "TabItem").Single(element => element.Attribute(x + "Name")?.Value == "BlogsTab");
            var workBody = new StackPanel { Margin = ReadThickness(overview.Element(p + "StackPanel")!, "Margin") };
            var newsBody = new StackPanel { Margin = ReadThickness(blogs.Element(p + "StackPanel")!, "Margin") };
            workBody.Children.Add(workCard);
            newsBody.Children.Add(newsCard);
            var workTabs = CreateLayoutTabs(work, "DevHubTabItemStyle", workBody);
            var newsTabs = CreateLayoutTabs(screen, "NewsTabItemStyle", newsBody);
            Grid.SetRow(workTabs, 1);
            Grid.SetRow(newsTabs, 1);
            grid.Children.Add(workTabs);
            newsPanel.Children.Add(newsTabs);
            grid.Measure(new Size(width, double.PositiveInfinity));
            grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));
            grid.UpdateLayout();

            double Top(FrameworkElement element) => element.TranslatePoint(new Point(), grid).Y;
            Assert.AreEqual(Top(workTabs) - Top(workHeading), Top(newsTabs) - Top(newsHeading), 0.01, "Heading-to-tab distance must match.");
            Assert.AreEqual(Top(workCard) - Top(workTabs), Top(newsCard) - Top(newsTabs), 0.01, "Tab-to-card distance must match.");
            Assert.AreEqual(Top(workTitle) - Top(workTabs), Top(newsTitle) - Top(newsTabs), 0.01, "First card titles must align too.");
            if (!stacked)
            {
                Assert.AreEqual(Top(workTabs), Top(newsTabs), 0.01);
                Assert.AreEqual(Top(workTitle), Top(newsTitle), 0.01);
            }
        }

        private static XDocument ReadLayoutXaml(string resource)
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream(resource);
            Assert.IsNotNull(stream);
            return XDocument.Load(stream);
        }

        private static Thickness ReadThickness(XElement element, string property)
        {
            return (Thickness)new ThicknessConverter().ConvertFromInvariantString(element.Attribute(property)!.Value)!;
        }

        private static TabControl CreateLayoutTabs(XDocument document, string styleName, FrameworkElement content)
        {
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = new XElement(document.Descendants(p + "Style").Single(element => element.Attribute(x + "Key")?.Value == styleName));
            definition.Attribute(x + "Key")!.Remove();
            definition.Descendants(p + "Setter").Where(element => element.Attribute("Value")?.Value.StartsWith("{DynamicResource", StringComparison.Ordinal) == true).Remove();
            var tabs = new TabControl { BorderThickness = new Thickness(0), Padding = new Thickness(0) };
            tabs.Items.Add(new TabItem { Header = "Example", Style = (Style)XamlReader.Parse(definition.ToString()), Content = content });
            tabs.SelectedIndex = 0;
            return tabs;
        }

        [STATestMethod]
        public void CopilotStatus_CollapsesEmptyGapButPreservesErrorMessages()
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var status = document.Descendants(presentation + "Run")
                .Single(element => element.Attribute(xaml + "Name")?.Value == "CopilotActivityStatusText");
            var gap = document.Descendants(presentation + "Border")
                .Single(element => element.Attribute(xaml + "Name")?.Value == "CopilotActivityStatusGap");
            var border = (Border)XamlReader.Parse(new XElement(gap).ToString());
            border.Tag = string.Empty;
            Assert.AreEqual(Visibility.Collapsed, border.Visibility);
            border.Tag = "Local Copilot activity unavailable.";
            Assert.AreEqual(Visibility.Visible, border.Visibility);

            var fragment = new XElement(status);
            fragment.Element(presentation + "Run.Foreground")!.Remove();
            var run = (Run)XamlReader.Parse(fragment.ToString());
            var binding = BindingOperations.GetBinding(run, Run.TextProperty)!;
            Assert.AreEqual(BindingMode.OneWay, binding.Mode);
            BindingOperations.SetBinding(run, Run.TextProperty, new Binding(binding.Path.Path)
            {
                Mode = binding.Mode,
                Source = new { CopilotActivityStatus = (string)border.Tag },
            }).UpdateTarget();
            Assert.AreEqual(border.Tag, run.Text);
        }

        [TestMethod]
        public void CopilotStatus_IsBesideWorkHeadingAtOriginalFontSize()
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var status = document.Descendants(presentation + "Run")
                .Single(element => element.Attribute(xaml + "Name")?.Value == "CopilotActivityStatusText");
            var heading = document.Descendants(presentation + "TextBlock")
                .Single(element => element.Attribute("Text")?.Value == "{Binding WorkSectionTitle}");

            Assert.AreSame(heading.Parent!.Parent, status.Parent);
            Assert.AreEqual(presentation + "TextBlock", status.Parent!.Name);
            Assert.AreEqual("Baseline", heading.Parent.Attribute("BaselineAlignment")?.Value);
            Assert.AreEqual("Baseline", status.Attribute("BaselineAlignment")?.Value);
            Assert.AreEqual("{Binding FontSize, ElementName=DevHubPanelControl, Mode=OneWay}", status.Attribute("FontSize")?.Value);
            Assert.AreEqual("{Binding CopilotActivityStatus, ElementName=DevHubPanelControl, Mode=OneWay}", status.Attribute("Text")?.Value);
        }

        [STATestMethod]
        public void CopilotStatus_SharesTheMeasuredHeadingBaseline()
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var line = new XElement(document.Descendants(presentation + "TextBlock")
                .Single(element => element.Attribute(xaml + "Name")?.Value == "WorkHeadingLine"));
            line.Descendants(presentation + "TextBlock").Single().SetAttributeValue("Text", "Your work");
            var status = line.Element(presentation + "Run")!;
            status.SetAttributeValue("Text", "Discovering local Copilot sessions...");
            status.SetAttributeValue("FontSize", "12");
            status.Element(presentation + "Run.Foreground")!.Remove();
            var textBlock = (TextBlock)XamlReader.Parse(line.ToString());
            var heading = (TextBlock)textBlock.Inlines.OfType<InlineUIContainer>().First().Child;
            textBlock.Measure(new Size(536, double.PositiveInfinity));
            textBlock.Arrange(new Rect(textBlock.DesiredSize));

            var headingBaseline = heading.TranslatePoint(new Point(0, heading.BaselineOffset), textBlock).Y;
            Assert.AreEqual(textBlock.BaselineOffset, headingBaseline, 0.01);
            Assert.AreEqual(BaselineAlignment.Baseline, textBlock.Inlines.OfType<Run>().Last().BaselineAlignment);
            Assert.AreEqual(12d, textBlock.Inlines.OfType<Run>().Last().FontSize);
        }

        [STATestMethod]
        public void CopilotTitle_StaysOnOneLineAndPreservesFullTitleOnHover()
        {
            var original = string.Join("\r\n", Enumerable.Repeat("A long Copilot conversation title with lots of details", 10));
            var item = Models.DevHub.DevHubWorkItem.GetCopilotItems(
                [new Models.CopilotActivitySession { Id = "session", Title = original }], false).Single();
            var textBlock = CreateWorkItemTitle(item);
            var reference = new TextBlock { Text = "Short title", FontSize = textBlock.FontSize, FontWeight = textBlock.FontWeight, Margin = textBlock.Margin };

            textBlock.Measure(new Size(300, double.PositiveInfinity));
            reference.Measure(new Size(300, double.PositiveInfinity));

            Assert.AreEqual(TextWrapping.NoWrap, textBlock.TextWrapping);
            Assert.AreEqual(TextTrimming.CharacterEllipsis, textBlock.TextTrimming);
            Assert.AreEqual(reference.DesiredSize.Height, textBlock.DesiredSize.Height);
            Assert.IsLessThanOrEqualTo(300d, textBlock.DesiredSize.Width);
            Assert.AreEqual(original, textBlock.ToolTip);
            Assert.AreEqual(original, item.Title);
            Assert.IsFalse(item.DisplayTitle.Contains('\n'));
            Assert.IsFalse(item.DisplayTitle.Contains('\r'));
        }

        [STATestMethod]
        public void RepositoryWorkItemTitles_KeepWrapping()
        {
            var title = "Pull request title\r\nwith another line";
            var item = Models.DevHub.DevHubWorkItem.GetNeedsYou(
                [new Models.DevHub.DevHubPullRequest { Title = title, IsReviewRequestedForCurrentUser = true }]).Single();

            var textBlock = CreateWorkItemTitle(item);

            Assert.AreEqual(TextWrapping.Wrap, textBlock.TextWrapping);
            Assert.AreEqual(title, item.DisplayTitle);
        }

        [TestMethod]
        public void CopilotCards_HaveBadgeAndDateBelowTitleWithoutAnActionLink()
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var title = document.Descendants(presentation + "TextBlock")
                .Single(element => element.Attribute("Text")?.Value == "{Binding DisplayTitle}");
            var metadata = document.Descendants(presentation + "StackPanel")
                .Single(element => element.Attribute(xaml + "Name")?.Value == "CopilotMetadata");
            var icon = document.Descendants()
                .Single(element => element.Attribute(xaml + "Name")?.Value == "CopilotActivityIcon");
            Assert.AreEqual("{x:Static helpers:CopilotMonikers.GitHubCopilot}", icon.Attribute("Moniker")?.Value);
            Assert.AreEqual("{Binding IsCopilot, Converter={StaticResource BoolToVisibilityConverter}}", icon.Attribute("Visibility")?.Value);
            Assert.AreSame(title.Parent, metadata.Parent);
            var siblings = title.Parent!.Elements().ToList();
            Assert.IsGreaterThan(siblings.IndexOf(title), siblings.IndexOf(metadata));
            Assert.AreEqual("{Binding IsCopilot, Converter={StaticResource BoolToVisibilityConverter}}", metadata.Attribute("Visibility")?.Value);
            Assert.IsTrue(metadata.Elements(presentation + "TextBlock").Any(element => element.Attribute("Text")?.Value == "{Binding ActivityText}"));
            var badge = metadata.Elements(presentation + "TextBlock")
                .Single(element => element.Attribute("Text")?.Value == "{Binding CopilotStatus}");
            Assert.AreEqual("10", badge.Attribute("FontSize")?.Value);
            foreach (var state in new[] { "Running", "Waiting" })
            {
                var trigger = badge.Descendants(presentation + "DataTrigger")
                    .Single(element => element.Attribute("Value")?.Value == state);
                Assert.IsTrue(trigger.Elements(presentation + "Setter").Any(setter => setter.Attribute("Property")?.Value == "Foreground"
                    && setter.Attribute("Value")!.Value.StartsWith("{DynamicResource", StringComparison.Ordinal)));
            }
            var action = document.Descendants(presentation + "TextBlock")
                .Single(element => element.Attribute("Text")?.Value == "{Binding ActionText}");
            var hideAction = action.Descendants(presentation + "DataTrigger").Single();
            Assert.AreEqual("{Binding IsCopilot}", hideAction.Attribute("Binding")?.Value);
            Assert.AreEqual("True", hideAction.Attribute("Value")?.Value);
            Assert.AreEqual("Collapsed", hideAction.Element(presentation + "Setter")?.Attribute("Value")?.Value);
            var card = title.Ancestors(presentation + "Border").First();
            Assert.AreEqual("WorkItem_MouseUp", card.Attribute("MouseUp")?.Value);
            Assert.AreEqual("4", card.Attribute("CornerRadius")?.Value);
            Assert.IsFalse(document.Descendants().Any(element => element.Attribute(xaml + "Name")?.Value == "ChangesHeading"));
        }

        [STATestMethod]
        [DataRow(null, false, "", Visibility.Collapsed)]
        [DataRow(true, true, "", Visibility.Collapsed)]
        [DataRow(true, false, "Running", Visibility.Visible)]
        [DataRow(false, false, "Idle", Visibility.Visible)]
        public void CopilotBadge_HidesUnknownStateWithoutLeavingAGap(bool? responding, bool cached,
            string expectedText, Visibility expectedVisibility)
        {
            var item = Models.DevHub.DevHubWorkItem.GetCopilotItems([new Models.CopilotActivitySession
            {
                Id = "session",
                IsResponding = responding,
                IsCached = cached,
            }], false).Single();

            var badge = CreateWorkItemTextBlock(item, "CopilotStatus");
            badge.Measure(new Size(400, 100));

            Assert.AreEqual(expectedText, badge.Text);
            Assert.AreEqual(expectedVisibility, badge.Visibility);
            if (expectedVisibility == Visibility.Collapsed)
                Assert.AreEqual(0d, badge.DesiredSize.Width);
            else
                Assert.IsGreaterThan(0d, badge.DesiredSize.Width);
        }

        private static TextBlock CreateWorkItemTitle(Models.DevHub.DevHubWorkItem item)
        {
            return CreateWorkItemTextBlock(item, "DisplayTitle");
        }

        private static TextBlock CreateWorkItemTextBlock(Models.DevHub.DevHubWorkItem item, string property)
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var title = new XElement(document.Descendants(presentation + "TextBlock")
                .Single(element => element.Attribute("Text")?.Value == $"{{Binding {property}}}"));
            title.Descendants(presentation + "Setter")
                .Where(setter => setter.Attribute("Property")?.Value == "Foreground").Remove();
            var textBlock = (TextBlock)XamlReader.Parse(new XElement(title).ToString());
            var style = (Style)XamlReader.Parse(title.Element(presentation + "TextBlock.Style")!.Elements().Single().ToString());
            foreach (var trigger in style.Triggers.OfType<DataTrigger>())
            {
                var binding = (Binding)trigger.Binding;
                trigger.Binding = new Binding(binding.Path.Path) { Source = item, Mode = binding.Mode };
                foreach (var setter in trigger.Setters.OfType<Setter>())
                {
                    if (setter.Value is Binding setterBinding)
                        setter.Value = new Binding(setterBinding.Path.Path) { Source = item, Mode = setterBinding.Mode };
                }
            }
            textBlock.Style = style;
            var titleBinding = BindingOperations.GetBinding(textBlock, TextBlock.TextProperty)!;
            BindingOperations.SetBinding(textBlock, TextBlock.TextProperty,
                new Binding(titleBinding.Path.Path) { Source = item, Mode = titleBinding.Mode }).UpdateTarget();
            textBlock.ApplyTemplate();
            return textBlock;
        }

        [TestMethod]
        public void Overview_HasNoAttentionBanner()
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

            Assert.IsFalse(document.Descendants().Any(element => element.Attribute(xaml + "Name")?.Value == "OverviewAttentionButton"));
            Assert.IsFalse(document.Descendants().Any(element => element.Attribute("Text")?.Value == "At a glance"));
        }

        [TestMethod]
        public void SolutionsOnly_IsInPageFooterOutsideScrollingContent()
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var filter = document.Descendants(presentation + "CheckBox")
                .Single(element => element.Attribute("Content")?.Value == "Solutions only");
            var pageScroll = document.Descendants(presentation + "ScrollViewer")
                .Single(element => element.Attribute(xaml + "Name")?.Value == "PageScroll");

            Assert.AreSame(pageScroll.Parent, filter.Parent);
            Assert.AreEqual("1", pageScroll.Attribute("Grid.Row")?.Value);
            Assert.AreEqual("2", filter.Attribute("Grid.Row")?.Value);
            Assert.AreEqual("Left", filter.Attribute("HorizontalAlignment")?.Value);
            Assert.AreEqual("{Binding ShowSolutionsOnly, Mode=TwoWay}", filter.Attribute("IsChecked")?.Value);
            Assert.AreEqual("Show only solutions in Recent. Pinned items are always included.", filter.Attribute("ToolTip")?.Value);
        }

        [TestMethod]
        [DataRow("BlogsTab")]
        [DataRow("VideosTab")]
        public void NewsTab_UsesHeaderRefreshWithoutInlineTimestamp(string tabName)
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var tab = document.Descendants(presentation + "TabItem")
                .Single(element => element.Attribute(xaml + "Name")?.Value == tabName);
            var refresh = document.Descendants(presentation + "Button")
                .Single(element => element.Attribute(xaml + "Name")?.Value == "UnifiedRefreshButton");

            Assert.IsFalse(tab.Descendants(presentation + "Run").Any());
            Assert.IsFalse(tab.Descendants(presentation + "Hyperlink").Any());
            Assert.AreEqual("UnifiedRefreshButton_Click", refresh.Attribute("Click")?.Value);
            Assert.AreEqual("Refresh Dev Hub, news, and videos", refresh.Attribute("ToolTip")?.Value);
        }

        [STATestMethod]
        [DataRow(nameof(StartScreenViewModel.ServiceHealthText))]
        [DataRow(nameof(StartScreenViewModel.UpdateAvailableText))]
        [DataRow(nameof(StartScreenViewModel.DiscoverySectionVersion))]
        public void InlineDisplayBinding_IsOneWayAndActivatesWithoutWritingToViewModel(string propertyName)
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var inline = document.Descendants(presentation + "Run")
                .Single(element => element.Attribute("Text")?.Value.StartsWith("{Binding " + propertyName, StringComparison.Ordinal) == true);
            var fragment = new XElement(presentation + "TextBlock",
                new XElement(presentation + "Hyperlink", new XElement(inline)));
            var textBlock = (TextBlock)XamlReader.Parse(fragment.ToString());
            var run = (Run)((Hyperlink)textBlock.Inlines.FirstInline).Inlines.FirstInline;
            var binding = BindingOperations.GetBinding(run, Run.TextProperty);
            Assert.IsNotNull(binding);
            Assert.AreEqual(BindingMode.OneWay, binding.Mode);

            var model = new StartScreenViewModel
            {
                LastNewsRefreshText = "News updated just now",
                LastYouTubeRefreshText = "Videos updated just now",
            };
            BindingOperations.ClearBinding(run, Run.TextProperty);
            var expression = BindingOperations.SetBinding(run, Run.TextProperty, new Binding(binding.Path.Path)
            {
                Mode = binding.Mode,
                Source = model,
            });
            expression.UpdateTarget();

            Assert.AreEqual(typeof(StartScreenViewModel).GetProperty(propertyName)!.GetValue(model), run.Text);
            if (propertyName == nameof(StartScreenViewModel.ServiceHealthText))
            {
                model.SetServiceHealth("Service status changed", "Updated details", false);
                expression.UpdateTarget();
                Assert.AreEqual("Service status changed", run.Text);
            }
        }
    }
}