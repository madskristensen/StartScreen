using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using XamlReader = StartScreen.Test.SharedXamlReader;
using System.Xml.Linq;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class StartScreenInlineBindingTests
    {
        [STATestMethod]
        [DataRow(12d)]
        [DataRow(20d)]
        public void CopilotLoadingCaption_FitsTheHeaderWithoutTrimming(double fontSize)
        {
            var document = ReadLayoutXaml("StartScreenControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = new XElement(document.Descendants(p + "TextBlock")
                .Single(element => element.Attribute(x + "Name")?.Value == "LastRefreshText"));
            definition.Attribute("Style")!.Remove();
            var status = (TextBlock)XamlReader.Parse(definition.ToString());
            var binding = BindingOperations.GetBinding(status, TextBlock.TextProperty)!;
            BindingOperations.SetBinding(status, TextBlock.TextProperty, new Binding(binding.Path.Path)
            {
                Source = new { UnifiedRefreshStatus = StartScreen.ToolWindows.Controls.DevHubPanel.CopilotLoadingStep },
                Mode = binding.Mode,
            }).UpdateTarget();
            status.FontSize = fontSize;
            status.Measure(new Size(500, 100));
            status.Arrange(new Rect(0, 0, status.DesiredSize.Width, status.DesiredSize.Height));
            var untrimmed = new TextBlock { Text = status.Text, FontFamily = status.FontFamily, FontSize = fontSize };
            untrimmed.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            Assert.AreEqual("Reading VS Copilot...", status.Text);
            Assert.IsLessThanOrEqualTo(status.ActualWidth, untrimmed.DesiredSize.Width,
                $"The {untrimmed.DesiredSize.Width:F1}px caption must fit the {status.ActualWidth:F1}px header at font size {fontSize}.");
        }

        [TestMethod]
        public void NewsFeedSettings_AreAvailableOnlyInTheSettingsPopup()
        {
            var document = ReadLayoutXaml("StartScreenControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var link = document.Descendants(p + "Hyperlink")
                .Single(element => element.Attribute("Click")?.Value == "NewsFeedsSettingsLink_Click");

            Assert.AreEqual("Customize news feeds...", link.Value.Trim());
            Assert.AreEqual("StartScreenSettingsPopup", link.Ancestors(p + "Popup").Single().Attribute(x + "Name")?.Value);
        }

        [STATestMethod]
        public void NewsPin_StaysAtCardRightEdgeDespiteNativeButtonMinimums()
        {
            var document = ReadLayoutXaml("NewsItemControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var pinDefinition = new XElement(document.Descendants(p + "Button")
                .Single(element => element.Attribute(x + "Name")?.Value == "PinButton"));
            pinDefinition.Attribute("Click")!.Remove();
            pinDefinition.SetAttributeValue("Visibility", "Visible");
            pinDefinition.Elements().Single().ReplaceWith(new XElement(p + "Border", new XAttribute("Width", "16"), new XAttribute("Height", "16")));
            var cardDefinition = new XElement(document.Descendants(p + "Border")
                .Single(element => element.Attribute(x + "Name")?.Value == "RootBorder"));
            cardDefinition.RemoveNodes();
            cardDefinition.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            var events = new[] { "MouseEnter", "MouseLeave", "MouseLeftButtonDown", "MouseUp", "PreviewKeyDown", "GotKeyboardFocus", "LostKeyboardFocus" };
            cardDefinition.Attributes().Where(attribute => events.Contains(attribute.Name.LocalName)).Remove();
            cardDefinition.Add(new XElement(p + "Border.Resources",
                new XElement(p + "Style", new XAttribute("TargetType", "Button"),
                    new XElement(p + "Setter", new XAttribute("Property", "MinWidth"), new XAttribute("Value", "80")),
                    new XElement(p + "Setter", new XAttribute("Property", "MinHeight"), new XAttribute("Value", "34")))),
                new XElement(p + "Grid", pinDefinition));
            var card = (Border)XamlReader.Parse(cardDefinition.ToString());

            card.Measure(new Size(330, 130));
            card.Arrange(new Rect(0, 0, 330, 130));
            card.UpdateLayout();

            var pin = (Button)((Grid)card.Child).Children[0];
            var icon = (Border)pin.Content;
            Assert.AreEqual(24d, pin.ActualWidth, 0.01);
            Assert.AreEqual(24d, pin.ActualHeight, 0.01);
            Assert.AreEqual(8d, card.ActualWidth - icon.TranslatePoint(new Point(icon.ActualWidth, 0), card).X, 0.01);
        }

        [STATestMethod]
        [DataRow(12d, false)]
        [DataRow(20d, false)]
        [DataRow(12d, true)]
        [DataRow(20d, true)]
        public void HeaderButtons_HaveMatchingHeightsAndSettingsRemainsSquare(double fontSize, bool refreshing)
        {
            var document = ReadLayoutXaml("StartScreenControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var refresh = document.Descendants(p + "Button")
                .Single(element => element.Attribute(x + "Name")?.Value == "UnifiedRefreshButton");
            var panelDefinition = new XElement(refresh.Parent!);
            panelDefinition.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            panelDefinition.Elements().Where(element => element.Name != p + "Button").Remove();
            panelDefinition.Descendants(p + "Button").Attributes("Click").Remove();
            foreach (var image in panelDefinition.Descendants().Where(element => element.Name.LocalName == "CrispImage").ToArray())
                image.ReplaceWith(new XElement(p + "Border", new XAttribute("Width", "16"), new XAttribute("Height", "16")));
            var styles = document.Root!.Element(p + "UserControl.Resources")!.Element(p + "ResourceDictionary")!.Elements(p + "Style")
                .Where(element => new[] { "ActionButtonStyle", "IconButtonStyle" }.Contains(element.Attribute(x + "Key")?.Value))
                .Select(element => new XElement(element)).ToArray();
            styles.Single(element => element.Attribute(x + "Key")?.Value == "ActionButtonStyle").Attribute("BasedOn")!.Remove();
            panelDefinition.AddFirst(new XElement(p + (panelDefinition.Name.LocalName + ".Resources"), styles));
            var panel = (Panel)XamlReader.Parse(panelDefinition.ToString());
            panel.DataContext = new { IsRefreshingAny = refreshing };
            var buttons = panel.Children.OfType<Button>().ToArray();
            buttons[0].FontSize = fontSize;

            for (var pass = 0; pass < 2; pass++)
            {
                panel.Measure(new Size(400, 100));
                panel.Arrange(new Rect(0, 0, 400, panel.DesiredSize.Height));
                panel.UpdateLayout();
            }

            Assert.HasCount(2, buttons);
            Assert.IsGreaterThan(24d, buttons[0].ActualHeight);
            Assert.AreEqual(buttons[0].ActualHeight, buttons[1].ActualHeight, 0.01);
            Assert.AreEqual(buttons[1].ActualHeight, buttons[1].ActualWidth, 0.01);
            Assert.AreEqual(buttons[0].TranslatePoint(new Point(), panel).Y, buttons[1].TranslatePoint(new Point(), panel).Y, 0.01);
            Assert.AreEqual(!refreshing, buttons[0].IsEnabled);
            Assert.IsTrue(buttons[1].IsEnabled);
        }

        [TestMethod]
        public void ActionButtons_InheritNativeVsThemingAndAttachGlyphFollowsButtonState()
        {
            var document = ReadLayoutXaml("StartScreenControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var style = document.Descendants(p + "Style")
                .Single(element => element.Attribute(x + "Key")?.Value == "ActionButtonStyle");

            Assert.AreEqual("{StaticResource {x:Static shell:VsResourceKeys.ThemedDialogButtonStyleKey}}", style.Attribute("BasedOn")!.Value);
            Assert.IsFalse(style.Elements(p + "Setter").Any(setter => new[]
            {
                "Template", "Background", "Foreground", "BorderBrush", "FocusVisualStyle",
            }.Contains(setter.Attribute("Property")?.Value)));
            Assert.IsFalse(style.Elements(p + "Style.Triggers").Any());

            var clearScope = ReadLayoutXaml("DevHubPanel.xaml").Descendants(p + "Hyperlink")
                .Single(element => element.Attribute("Click")?.Value == "ClearFilter_Click");
            Assert.AreEqual("Clear filter", clearScope.Value);
            Assert.AreEqual("{DynamicResource {x:Static platformui:EnvironmentColors.ControlLinkTextBrushKey}}", clearScope.Attribute("Foreground")!.Value);

            var attach = document.Descendants(p + "Button")
                .Single(element => element.Attribute(x + "Name")?.Value == "AttachToProcessButton");
            Assert.AreEqual("{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}",
                attach.Descendants(p + "Path").Single().Attribute("Fill")!.Value);
            Assert.AreEqual("{Binding BorderBrush, RelativeSource={RelativeSource AncestorType=Button}}",
                attach.Descendants(p + "Border").Single().Attribute("BorderBrush")!.Value);
        }

        [STATestMethod]
        [DataRow(320d, 12d)]
        [DataRow(320d, 20d)]
        [DataRow(520d, 12d)]
        [DataRow(520d, 20d)]
        public void RepositoryScopeStrip_ShowsExplicitScopeFitsLongNamesAndClearsForMultipleProjects(double width, double fontSize)
        {
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            Assert.IsFalse(document.Descendants(p + "Button").Any(element => element.Attribute("Content")?.Value == "All repositories"));
            var definition = new XElement(document.Descendants(p + "Border")
                .Single(element => element.Attribute(x + "Name")?.Value == "RepositoryScopeStrip"));
            definition.Descendants(p + "Hyperlink").Attributes("Click").Remove();
            var container = (Grid)XamlReader.Parse(new XElement(p + "Grid", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "Grid.Resources", new XElement(p + "BooleanToVisibilityConverter",
                    new XAttribute(x + "Key", "BoolToVisibilityConverter"))), definition).ToString());
            var strip = (Border)container.Children[0];
            StartScreen.ToolWindows.Typography.SetEnvironmentSize(strip, fontSize);
            var item = new Models.MruItem { Name = "VS", RemoteUrl = "https://dev.azure.com/organization/project/_git/a-very-long-repository-name-that-does-not-fit-on-one-line" };
            using var model = new StartScreenViewModel([item]);
            strip.DataContext = model;
            var label = (TextBlock)strip.FindName("RepositoryScopeLabel");
            var name = (TextBlock)strip.FindName("RepositoryScopeName");
            var clear = (TextBlock)strip.FindName("ClearRepositoryScope");
            void Layout()
            {
                strip.Measure(new Size(width, double.PositiveInfinity));
                strip.Arrange(new Rect(0, 0, width, strip.DesiredSize.Height));
                strip.UpdateLayout();
            }
            Layout();
            Assert.AreEqual(Visibility.Collapsed, strip.Visibility);
            Assert.AreEqual(0d, strip.DesiredSize.Height, "The unfiltered scope must not reserve vertical space.");
            Assert.AreEqual(Visibility.Collapsed, name.Visibility);
            Assert.AreEqual(Visibility.Collapsed, clear.Visibility);

            strip.Resources["TestThemeBrush"] = SystemColors.WindowBrush;
            model.SetMruSelection([item]);
            Layout();
            Assert.AreEqual(Visibility.Visible, strip.Visibility);
            Assert.AreEqual("Filtered to repository:", label.Text);
            Assert.AreEqual(model.SelectedRepository.DisplayName, name.Text);
            Assert.AreEqual(name.Text, name.ToolTip);
            Assert.AreEqual(TextTrimming.CharacterEllipsis, name.TextTrimming);
            Assert.AreEqual(Visibility.Visible, clear.Visibility);
            Assert.AreEqual(new Thickness(1), strip.BorderThickness);
            Assert.AreSame(SystemColors.WindowBrush, strip.Background);
            Assert.IsGreaterThan(0d, name.ActualWidth);
            Assert.IsLessThanOrEqualTo(clear.TranslatePoint(new Point(), strip).X, name.TranslatePoint(new Point(name.ActualWidth, 0), strip).X);
            Assert.IsLessThanOrEqualTo(width, clear.TranslatePoint(new Point(clear.ActualWidth, 0), strip).X);
            strip.Resources["TestThemeBrush"] = SystemColors.HighlightBrush;
            Assert.AreSame(SystemColors.HighlightBrush, strip.Background, "Scope colors must follow theme changes.");

            model.SetMruSelection([item, new Models.MruItem()]);
            Layout();
            Assert.AreEqual(Visibility.Collapsed, strip.Visibility);
            Assert.AreEqual(0d, strip.DesiredSize.Height, "Clearing the scope must also remove its spacing.");
            Assert.AreEqual(Visibility.Collapsed, name.Visibility);
            Assert.AreEqual(Visibility.Collapsed, clear.Visibility);
            Assert.AreEqual(new Thickness(0), strip.BorderThickness);
        }

        [STATestMethod]
        [DataRow(280d, 280d)]
        [DataRow(320d, 320d)]
        [DataRow(360d, 360d)]
        [DataRow(450d, 450d)]
        [DataRow(480d, 480d)]
        [DataRow(0d, 300d)]
        [DataRow(-1d, 300d)]
        [DataRow(279d, 300d)]
        [DataRow(481d, 300d)]
        [DataRow(double.NaN, 300d)]
        [DataRow(double.PositiveInfinity, 300d)]
        [DataRow(double.NegativeInfinity, 300d)]
        public void RecentItems_UseSavedWidthOnFirstLayoutAndKeepItAsContentLoads(double savedWidth, double expectedWidth)
        {
            var document = ReadLayoutXaml("StartScreenControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var columnDefinition = document.Descendants(p + "ColumnDefinition")
                .Single(element => element.Attribute(x + "Name")?.Value == "LeftColumn");
            var grid = new Grid();
            foreach (var column in columnDefinition.Parent!.Elements())
                grid.ColumnDefinitions.Add((ColumnDefinition)XamlReader.Parse(column.ToString()));
            var mruColumn = grid.ColumnDefinitions[0];
            StartScreenControl.ApplyInitialMruWidth(mruColumn, savedWidth);
            var items = new ListBox();
            grid.Children.Add(items);
            var splitter = new GridSplitter { Width = 5 };
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);
            var content = new Border();
            Grid.SetColumn(content, 2);
            grid.Children.Add(content);
            void Layout()
            {
                grid.Measure(new Size(1400, 800));
                grid.Arrange(new Rect(0, 0, 1400, 800));
                grid.UpdateLayout();
            }

            Layout();
            Assert.AreEqual(expectedWidth, mruColumn.ActualWidth, 0.01, "The saved width must be used on the first layout.");
            double initialContentLeft = content.TranslatePoint(new Point(), grid).X;
            Assert.AreEqual(expectedWidth + splitter.Width, initialContentLeft, 0.01);

            items.Items.Add(new TextBlock { Text = new string('x', 200) });
            content.Child = new TextBlock { Text = "Repository activity and news loaded" };
            Layout();

            Assert.AreEqual(expectedWidth, mruColumn.ActualWidth, 0.01);
            Assert.AreEqual(initialContentLeft, content.TranslatePoint(new Point(), grid).X, 0.01,
                "Loading secondary content must not shift the column boundary.");
        }

        internal static Border ParseMruCard()
        {
            var document = ReadLayoutXaml("MruItemControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = new XElement(document.Descendants(p + "Border").Single(element => element.Attribute(x + "Name")?.Value == "RootBorder"));
            definition.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            definition.Elements(p + "Border.Style").Remove();
            definition.Elements(p + "Border.ToolTip").Remove();
            definition.Elements(p + "Border.ContextMenu").Remove();
            foreach (var attribute in definition.DescendantsAndSelf().Attributes().Where(attribute =>
                attribute.Value.EndsWith("_Click", StringComparison.Ordinal) || attribute.Value.StartsWith("RootBorder_", StringComparison.Ordinal)
                || attribute.Value.StartsWith("{DynamicResource", StringComparison.Ordinal)).ToArray())
                attribute.Remove();
            var name = definition.Descendants(p + "TextBlock").Single(element => element.Attribute(x + "Name")?.Value == "NameText");
            name.Attribute("TextDecorations")!.Remove();
            name.Attribute("Opacity")!.Remove();
            foreach (var image in definition.Descendants().Where(element => element.Name.LocalName == "CrispImage").ToArray())
                image.ReplaceWith(new XElement(p + "Border", image.Attributes().Where(attribute => new[]
                {
                    "Width", "Height", "Margin", "VerticalAlignment",
                }.Contains(attribute.Name.LocalName)).Select(attribute => new XAttribute(attribute))));
            var styles = document.Root!.Element(p + "UserControl.Resources")!.Element(p + "ResourceDictionary")!.Elements(p + "Style")
                .Select(element => new XElement(element)).ToArray();
            styles.SelectMany(element => element.Descendants(p + "Setter")).Where(element =>
                element.Attribute("Property")?.Value == "Foreground").Remove();
            definition.AddFirst(new XElement(p + "Border.Resources",
                new XElement(p + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "BoolToVisibilityConverter")), styles));
            return (Border)XamlReader.Parse(definition.ToString());
        }

        [STATestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RecentItems_HaveComfortableRowsAndRoomForReviewDetails(bool hasReviews)
        {
            var card = ParseMruCard();
            var item = new Models.MruItem { Name = "hubitat-drivers", GitBranch = "main" };
            item.ApplyRepositorySummary("Loaded", "Review details", 0, hasReviews ? 2 : 0);
            card.DataContext = item;

            card.Measure(new Size(320, double.PositiveInfinity));
            card.Arrange(new Rect(0, 0, 320, card.DesiredSize.Height));
            card.UpdateLayout();

            var details = (Grid)card.FindName("RepositoryStatusRow");
            Assert.AreEqual(hasReviews ? Visibility.Visible : Visibility.Collapsed, details.Visibility);
            if (hasReviews)
            {
                Assert.AreEqual(48d, card.ActualHeight, 0.01);
                Assert.IsLessThanOrEqualTo(card.ActualHeight - card.Padding.Bottom,
                    details.TranslatePoint(new Point(0, details.ActualHeight), card).Y);
            }
            else
                Assert.AreEqual(48d, card.ActualHeight, 0.01);
        }

        [STATestMethod]
        public void RepositoryCounts_ShowOnlyPositiveCountsAndHidePlaceholderAndUnavailableRows()
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
            foreach (var worktreeIcon in rowDefinition.Descendants().Where(element => element.Name.LocalName == "CrispImage").ToArray())
                worktreeIcon.ReplaceWith(new XElement(p + "Border", new XAttribute("Width", "12"), new XAttribute("Height", "12")));
            Assert.AreEqual("RepositoryReviews_Click", rowDefinition.Elements(p + "Button").First().Attribute("Click")!.Value);
            foreach (var click in badgeDefinition.DescendantsAndSelf().Concat(rowDefinition.DescendantsAndSelf()).Attributes("Click").ToArray())
                click.Remove();
            var styles = document.Root!.Element(p + "UserControl.Resources")!.Element(p + "ResourceDictionary")!.Elements(p + "Style")
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
            Assert.AreEqual(3, row.Children.Count);
            Assert.AreEqual("WorkspaceTagPills", ((Border)row.Children[1]).Name);
            Assert.AreEqual("WorktreeIndicator", ((StackPanel)row.Children[2]).Name);
            var item = new Models.MruItem { RemoteUrl = "https://github.com/o/r.git" };
            grid.DataContext = item;
#pragma warning disable VSTHRD001 // Drain bindings on the current STA thread, not a thread switch.
            void UpdateBindings() => grid.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
#pragma warning restore VSTHRD001
            UpdateBindings();
            Assert.AreEqual(Visibility.Collapsed, badge.Visibility);
            Assert.AreEqual(Visibility.Collapsed, row.Visibility);

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

            item.ApplyRepositorySummary("Sign in to see repository activity", "Sign in");
            UpdateBindings();
            Assert.AreEqual(Visibility.Collapsed, badge.Visibility);
            Assert.AreEqual(Visibility.Collapsed, row.Visibility);

            item.ApplyRepositorySummary("Repository activity unavailable", "Refresh to try again");
            UpdateBindings();
            Assert.AreEqual(Visibility.Collapsed, badge.Visibility);
            Assert.AreEqual(Visibility.Collapsed, row.Visibility);
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
        public void RecentItems_UseIndependentBoundedScrollbarsWithoutMovingRepositoryDetails()
        {
            var document = ReadLayoutXaml("StartScreenControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = new XElement(document.Descendants(p + "ListBox")
                .Single(element => element.Attribute(x + "Name")?.Value == "MruPanel"));
            Assert.IsNull(definition.Attribute("MaxHeight"));
            Assert.AreEqual("Auto", definition.Attribute("ScrollViewer.VerticalScrollBarVisibility")!.Value);
            var actualPage = document.Descendants(p + "ScrollViewer")
                .Single(element => element.Attribute(x + "Name")?.Value == "PageScroll");
            Assert.IsFalse(actualPage.Descendants(p + "ListBox").Any());
            Assert.AreEqual("2", actualPage.Attribute("Grid.Column")!.Value);
            Assert.AreEqual("1", actualPage.Parent!.Attribute("Grid.Row")!.Value);
            definition.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            definition.Attribute("ItemsSource")!.Remove();
            foreach (var attribute in definition.Attributes().Where(attribute => attribute.Value.Contains("_", StringComparison.Ordinal)).ToArray())
                attribute.Remove();
            definition.Element(p + "ListBox.ItemTemplate")!.ReplaceWith(new XElement(p + "ListBox.ItemTemplate",
                new XElement(p + "DataTemplate", new XElement(p + "Border", new XAttribute("Height", "32")))));
            var styles = document.Root!.Element(p + "UserControl.Resources")!.Element(p + "ResourceDictionary")!.Elements(p + "Style")
                .Where(element => new[] { "ScrollTrackButtonStyle", "DiscreetVerticalScrollBarStyle" }.Contains(element.Attribute(x + "Key")?.Value))
                .Select(element => new XElement(element)).ToArray();
            foreach (var attribute in styles.SelectMany(element => element.DescendantsAndSelf().Attributes())
                .Where(attribute => attribute.Value.StartsWith("{DynamicResource", StringComparison.Ordinal)).ToArray())
                attribute.Remove();
            var pageDefinition = new XElement(actualPage);
            pageDefinition.RemoveNodes();
            pageDefinition.Add(new XElement(actualPage.Element(p + "ScrollViewer.Resources")!));
            pageDefinition.Add(new XElement(p + "Border", new XAttribute("Height", "2000")));
            pageDefinition.Attribute("PreviewMouseWheel")!.Remove();
            pageDefinition.SetAttributeValue("Grid.Column", "1");
            var gridDefinition = new XElement(p + "Grid", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "Grid.Resources", styles),
                new XElement(p + "Grid.ColumnDefinitions",
                    new XElement(p + "ColumnDefinition", new XAttribute("Width", "360")),
                    new XElement(p + "ColumnDefinition", new XAttribute("Width", "*"))),
                definition, pageDefinition);
            var grid = (Grid)XamlReader.Parse(gridDefinition.ToString());
            var list = (ListBox)grid.Children[0];
            var page = (ScrollViewer)grid.Children[1];
            var items = Enumerable.Range(0, 60).Select(index => new Models.MruItem
            {
                Name = "Project" + index,
                Path = $@"C:\Project{index}\App.sln",
                IsPinned = index < 3,
                LastAccessed = DateTime.Now.AddDays(-index),
            }).ToArray();
            var view = CollectionViewSource.GetDefaultView(items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Models.MruItem.DisplayGroup)));
            list.ItemsSource = view;
            void Layout()
            {
                grid.Measure(new Size(900, 300));
                grid.Arrange(new Rect(0, 0, 900, 300));
                grid.UpdateLayout();
            }

            Layout();

            Assert.AreEqual(300d, list.ActualHeight, 0.01);
            Assert.IsGreaterThan(0d, page.ScrollableHeight);
            var inner = System.Windows.Media.VisualTreeHelper.GetChild(list, 0) as Border;
            Assert.IsNotNull(inner);
            Assert.IsInstanceOfType<ScrollViewer>(inner.Child);
            var innerScroll = (ScrollViewer)inner.Child;
            Assert.IsGreaterThan(0d, innerScroll.ScrollableHeight);
            var mruBar = (System.Windows.Controls.Primitives.ScrollBar)innerScroll.Template.FindName("PART_VerticalScrollBar", innerScroll);
            var pageBar = (System.Windows.Controls.Primitives.ScrollBar)page.Template.FindName("PART_VerticalScrollBar", page);
            Assert.AreEqual(10d, mruBar.ActualWidth, 0.01);
            Assert.AreEqual(10d, pageBar.ActualWidth, 0.01);
            var track = (System.Windows.Controls.Primitives.Track)mruBar.Template.FindName("PART_Track", mruBar);
            Assert.AreEqual(mruBar.Maximum, track.Maximum, 0.01);
            Assert.AreEqual(mruBar.ViewportSize, track.ViewportSize, 0.01);
            System.Windows.Controls.Primitives.ScrollBar.PageDownCommand.Execute(null, mruBar);
            Layout();
            Assert.IsGreaterThan(0d, innerScroll.VerticalOffset);
            Assert.AreEqual(0d, page.VerticalOffset, 0.01);

            innerScroll.ScrollToEnd();
            Layout();

            Assert.AreEqual(innerScroll.ScrollableHeight, innerScroll.VerticalOffset, 0.01);
            Assert.AreEqual(0d, page.VerticalOffset, 0.01);
            innerScroll.ScrollToHome();
            Layout();

            page.ScrollToEnd();
            Layout();

            Assert.AreEqual(page.ScrollableHeight, page.VerticalOffset, 0.01);
            Assert.AreEqual(0d, innerScroll.VerticalOffset);
        }

        [TestMethod]
        public void AzureDevOpsSettings_OfferVsAuthenticationAndSeparateOptionalPatEntry()
        {
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var links = document.Descendants(p + "Hyperlink").ToArray();
            StringAssert.Contains(links.Single(link => link.Attribute("Click")?.Value == "ConnectAdo_Click"
                && link.Attribute(x + "Name") == null).Value, "Visual Studio account (Entra ID)");
            StringAssert.Contains(links.Single(link => link.Attribute("Click")?.Value == "ShowCloudAdoPatEntry_Click").Value, "PAT");
            var entry = document.Descendants(p + "StackPanel").Single(element => element.Attribute(x + "Name")?.Value == "AdoPatEntryPanel");
            Assert.AreEqual("Collapsed", entry.Attribute("Visibility")!.Value);
            var input = entry.Descendants(p + "PasswordBox").Single();
            Assert.AreEqual("AdoPatBox_KeyDown", input.Attribute("KeyDown")!.Value);
            var help = document.Descendants(p + "TextBlock").Single(element => element.Attribute(x + "Name")?.Value == "AdoAuthenticationHelpText");
            Assert.AreEqual("Collapsed", help.Attribute("Visibility")!.Value);
            Assert.AreEqual("Wrap", help.Attribute("TextWrapping")!.Value);
        }

        [STATestMethod]
        [DataRow(360d)]
        [DataRow(520d)]
        public void DevHubSettings_KeepStatusVisibleAndAdvancedOptionsCollapsed(double width)
        {
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = new XElement(document.Descendants(p + "Border")
                .Single(element => element.Attribute(x + "Name")?.Value == "SettingsPanel"));
            definition.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            definition.SetAttributeValue("Visibility", "Visible");
            var providerControls = definition.Descendants(p + "CheckBox").ToArray();
            Assert.HasCount(3, providerControls);
            foreach (var control in providerControls)
            {
                Assert.AreEqual("ProviderEnabled_Changed", control.Attribute("Checked")!.Value);
                Assert.AreEqual("ProviderEnabled_Changed", control.Attribute("Unchecked")!.Value);
            }
            var events = new[] { "Click", "SelectionChanged", "KeyDown", "PasswordChanged", "TextChanged", "LostFocus", "Checked", "Unchecked" };
            foreach (var attribute in definition.DescendantsAndSelf().Attributes()
                .Where(attribute => events.Contains(attribute.Name.LocalName)
                    || attribute.Value.StartsWith("{DynamicResource", StringComparison.Ordinal)).ToArray())
            {
                if (attribute.Value.Contains("CommandBarTextActiveBrushKey", StringComparison.Ordinal))
                    attribute.Value = "{DynamicResource SettingsForeground}";
                else if (attribute.Value.Contains("CommandBarMouseOverBackgroundBeginBrushKey", StringComparison.Ordinal))
                    attribute.Value = "{DynamicResource SettingsHover}";
                else if (attribute.Value.Contains("ControlLinkTextBrushKey", StringComparison.Ordinal))
                    attribute.Value = "{DynamicResource SettingsFocus}";
                else if (attribute.Parent?.Name == p + "Setter")
                    attribute.Parent.Remove();
                else
                    attribute.Remove();
            }
            foreach (var image in definition.Descendants().Where(element => element.Name.LocalName == "CrispImage").ToArray())
                image.ReplaceWith(new XElement(p + "Border", new XAttribute("Width", "12"), new XAttribute("Height", "12")));

            var panel = (Border)XamlReader.Parse(definition.ToString());
            panel.Resources["SettingsForeground"] = System.Windows.Media.Brushes.White;
            panel.Resources["SettingsHover"] = System.Windows.Media.Brushes.DimGray;
            panel.Resources["SettingsFocus"] = System.Windows.Media.Brushes.SkyBlue;
            var connections = (Expander)panel.FindName("AdoConnectionOptions");
            var search = (Expander)panel.FindName("SearchQueryOptions");
            var query = (TextBox)panel.FindName("SearchQueryTextBox");
            var servers = (ItemsControl)panel.FindName("AdoServersList");
            var display = (Grid)panel.FindName("DisplaySettingsGrid");
            var defaultTab = (ComboBox)panel.FindName("DefaultTabComboBox");
            var sortOrder = (ComboBox)panel.FindName("SortOrderComboBox");
            var github = (CheckBox)panel.FindName("GitHubProviderEnabled");
            var ado = (CheckBox)panel.FindName("AdoProviderEnabled");
            var bitbucket = (CheckBox)panel.FindName("BitbucketProviderEnabled");
            github.IsChecked = ado.IsChecked = bitbucket.IsChecked = true;
            void Layout()
            {
                panel.Measure(new Size(width, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
                panel.UpdateLayout();
            }

            Layout();

            Assert.AreEqual(new Thickness(16), panel.Padding);
            Assert.IsFalse(connections.IsExpanded);
            Assert.IsFalse(search.IsExpanded);
            Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)search.Template.FindName("ExpandSite", search)).Visibility);
            foreach (var expander in new[] { connections, search })
            {
                var header = (System.Windows.Controls.Primitives.ToggleButton)expander.Template.FindName("HeaderToggle", expander);
                var chevron = (System.Windows.Shapes.Path)header.Template.FindName("Chevron", header);
                Assert.AreEqual(System.Windows.Media.Brushes.White, expander.Foreground);
                Assert.AreEqual(expander.Foreground, header.Foreground);
                Assert.AreEqual(header.Foreground, chevron.Stroke);
                Assert.AreEqual(expander.Header, header.Content);
            }
            Assert.AreEqual(Visibility.Visible, servers.Visibility);
            Assert.AreSame(connections.Parent, servers.Parent, "Connection status must remain outside the collapsed controls.");
            Assert.IsTrue(connections.IsEnabled);
            Assert.IsTrue(servers.IsEnabled);
            github.IsChecked = false;
            ado.IsChecked = false;
            Layout();
            Assert.IsFalse(((FrameworkElement)panel.FindName("ConnectedGitHubStatus")).IsEnabled);
            Assert.IsFalse(((FrameworkElement)panel.FindName("SettingsConnectGitHubTextBlock")).IsEnabled);
            Assert.IsFalse(((FrameworkElement)panel.FindName("GitHubPatEntryPanel")).IsEnabled);
            Assert.IsFalse(connections.IsEnabled);
            Assert.IsFalse(servers.IsEnabled);
            Assert.IsTrue(bitbucket.IsChecked);
            github.IsChecked = ado.IsChecked = true;
            Layout();
            Assert.AreEqual(Visibility.Visible, defaultTab.Visibility);
            Assert.AreEqual(Visibility.Visible, sortOrder.Visibility);
            Assert.AreSame(display, defaultTab.Parent);
            Assert.AreSame(display, sortOrder.Parent);
            Assert.AreEqual(1, Grid.GetColumn(defaultTab));
            Assert.AreEqual(1, Grid.GetColumn(sortOrder));
            Assert.AreEqual(defaultTab.ActualWidth, sortOrder.ActualWidth, 0.01);
            Assert.IsGreaterThan(100d, defaultTab.ActualWidth, "Preference controls must remain usable in the narrow pane.");
            var collapsedHeight = panel.DesiredSize.Height;

            ((System.Windows.Controls.Primitives.ToggleButton)connections.Template.FindName("HeaderToggle", connections)).IsChecked = true;
            ((System.Windows.Controls.Primitives.ToggleButton)search.Template.FindName("HeaderToggle", search)).IsChecked = true;
            Layout();

            Assert.IsTrue(connections.IsExpanded);
            Assert.IsTrue(search.IsExpanded);
            Assert.AreEqual(Visibility.Visible, ((FrameworkElement)search.Template.FindName("ExpandSite", search)).Visibility);
            Assert.AreEqual(Visibility.Visible, ((TextBlock)panel.FindName("AddAdoServerLink")).Visibility);
            Assert.IsGreaterThan(collapsedHeight, panel.DesiredSize.Height);

            panel.Resources["SettingsForeground"] = System.Windows.Media.Brushes.Black;
            Layout();
            foreach (var expander in new[] { connections, search })
            {
                var header = (System.Windows.Controls.Primitives.ToggleButton)expander.Template.FindName("HeaderToggle", expander);
                var chevron = (System.Windows.Shapes.Path)header.Template.FindName("Chevron", header);
                Assert.AreEqual(System.Windows.Media.Brushes.Black, expander.Foreground);
                Assert.AreEqual(expander.Foreground, header.Foreground);
                Assert.AreEqual(header.Foreground, chevron.Stroke);
            }
        }

        [STATestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ServiceHealth_ShowsInitialGreenAndKeepsItVisibleWhileChecking(bool healthy)
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
            Assert.IsTrue(model.HasServiceHealthStatus);
            Assert.IsTrue(model.IsServiceHealthy);
            Assert.AreEqual("Service operational", model.ServiceHealthText);
            Assert.AreEqual(Visibility.Visible, panel.Visibility);

            model.SetServiceHealth("Checking service status...", "Checking", false, isChecking: true);
            expression.UpdateTarget();
            Assert.AreEqual(Visibility.Visible, panel.Visibility);
            Assert.IsTrue(model.IsServiceHealthy);
            Assert.IsEmpty(changed);

            model.SetServiceHealth("Completed", "Checked recently", healthy);
            expression.UpdateTarget();
            Assert.IsTrue(model.HasServiceHealthStatus);
            Assert.DoesNotContain(nameof(StartScreenViewModel.HasServiceHealthStatus), changed);
            Assert.AreEqual(healthy, model.IsServiceHealthy);
            Assert.AreEqual(Visibility.Visible, panel.Visibility);

            changed.Clear();
            model.SetServiceHealth("Checking service status...", "Refreshing", false, isChecking: true);
            expression.UpdateTarget();
            Assert.IsTrue(model.HasServiceHealthStatus);
            Assert.IsEmpty(changed);
            Assert.AreEqual(Visibility.Visible, panel.Visibility);
            Assert.AreEqual(healthy, model.IsServiceHealthy);
            Assert.AreEqual("Completed", model.ServiceHealthText);

            model.SetServiceHealth("Service status unavailable", "Refresh to try again", false);
            expression.UpdateTarget();
            Assert.AreEqual(Visibility.Visible, panel.Visibility);
            Assert.IsFalse(model.IsServiceHealthy);
        }

        [STATestMethod]
        [DataRow(700d, false)]
        [DataRow(700d, true)]
        [DataRow(1200d, false)]
        [DataRow(1200d, true)]
        [DataRow(2400d, false)]
        [DataRow(2400d, true)]
        public void ActivitySections_StackWorkWithLeftTabsAndRightHeadingsBesideNews(double width, bool repositorySelected)
        {
            var screen = ReadLayoutXaml("StartScreenControl.xaml");
            var work = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var content = screen.Descendants(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == "ContentLayout");
            var layout = StartScreenControl.CalculateContentLayout(width, true);
            Assert.IsFalse(layout.Stacked);
            var grid = new Grid();
            grid.DataContext = new
            {
                HasSelectedMruItem = repositorySelected,
                RepositoryScopeLabel = repositorySelected ? "Filtered to repository:" : "Across all repositories",
                RepositoryScopeName = repositorySelected ? "owner/hubitat-drivers" : "",
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.WorkWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bladesDefinition = work.Descendants(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == "WorkBlades");
            var blades = new Grid { Margin = new Thickness(0, 0, 24, 0) };
            foreach (var row in bladesDefinition.Element(p + "Grid.RowDefinitions")!.Elements())
                blades.RowDefinitions.Add((RowDefinition)XamlReader.Parse(row.ToString()));
            grid.Children.Add(blades);
            var headings = new List<TextBlock>();
            var headers = new List<DockPanel>();
            var filterRows = new List<FrameworkElement>();
            foreach (var definition in bladesDefinition.Elements(p + "Grid"))
            {
                var blade = new StackPanel();
                if (definition.Attribute("Margin") != null)
                    blade.Margin = ReadThickness(definition, "Margin");
                Grid.SetRow(blade, int.Parse(definition.Attribute("Grid.Row")!.Value));
                var heading = new XElement(definition.Elements(p + "DockPanel").First());
                heading.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
                foreach (var element in heading.Descendants())
                {
                    element.Attribute("ButtonBase.Click")?.Remove();
                    element.Attribute("Checked")?.Remove();
                }
                var filterStyle = new XElement(work.Descendants(p + "Style")
                    .Single(element => element.Attribute(x + "Key")?.Value == "ActivityFilterStyle"));
                heading.AddFirst(new XElement(p + "DockPanel.Resources",
                    filterStyle));
                var header = (DockPanel)XamlReader.Parse(heading.ToString());
                var text = header.Children.OfType<TextBlock>().Single();
                headings.Add(text);
                headers.Add(header);
                blade.Children.Add(header);
                var filters = header.Children.OfType<WrapPanel>().Single();
                filterRows.Add(filters);
                blade.Children.Add(new TextBlock { Text = "Example activity", TextWrapping = TextWrapping.Wrap });
                blades.Children.Add(blade);
            }
            var newsDefinition = content.Elements(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == "NewsColumnPanel");
            var newsPanel = new StackPanel();
            Grid.SetColumn(newsPanel, 1);
            grid.Children.Add(newsPanel);
            var newsHeaderDefinition = new XElement(newsDefinition.Descendants(p + "DockPanel").First());
            newsHeaderDefinition.Elements(p + "TextBlock").Where(element => element.Attribute("DockPanel.Dock")?.Value == "Right").Remove();
            var newsHeader = (DockPanel)XamlReader.Parse(newsHeaderDefinition.ToString());
            var newsHeading = newsHeader.Children.OfType<TextBlock>().Single();
            newsPanel.Children.Add(newsHeader);
            var contentWidth = Math.Max(1400, width);
            grid.Measure(new Size(contentWidth, 600));
            grid.Arrange(new Rect(0, 0, contentWidth, 600));
            grid.UpdateLayout();

            Assert.HasCount(2, headings);
            Assert.IsGreaterThan(headings[0].TranslatePoint(new Point(), grid).Y, headings[1].TranslatePoint(new Point(), grid).Y);
            Assert.AreEqual(headings[0].TranslatePoint(new Point(headings[0].ActualWidth, 0), grid).X,
                headings[1].TranslatePoint(new Point(headings[1].ActualWidth, 0), grid).X, 0.01);
            Assert.IsGreaterThan(headings[1].TranslatePoint(new Point(), grid).X + headings[1].ActualWidth, newsHeading.TranslatePoint(new Point(), grid).X);
            Assert.IsGreaterThanOrEqualTo(400d, headers[0].ActualWidth);
            Assert.AreEqual(filterRows[0].TranslatePoint(new Point(), grid).X, filterRows[1].TranslatePoint(new Point(), grid).X, 0.01);
            foreach (var header in headers)
            {
                var filters = header.Children.OfType<WrapPanel>().Single();
                var label = header.Children.OfType<TextBlock>().Single();
                Assert.IsLessThanOrEqualTo(label.TranslatePoint(new Point(), header).X,
                    filters.TranslatePoint(new Point(filters.ActualWidth, 0), header).X);
            }
            var scope = new XElement(bladesDefinition.Descendants(p + "Border").Single(element => element.Attribute(x + "Name")?.Value == "RepositoryScopeStrip"));
            scope.Descendants(p + "Hyperlink").Single().Attribute("Click")!.Remove();
            var scopeRoot = (Grid)XamlReader.Parse(new XElement(p + "Grid", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "Grid.Resources", new XElement(p + "BooleanToVisibilityConverter",
                    new XAttribute(x + "Key", "BoolToVisibilityConverter"))), scope).ToString());
            var scopePanel = (Border)scopeRoot.Children[0];
            scopeRoot.DataContext = grid.DataContext;
#pragma warning disable VSTHRD001 // Apply inherited bindings on the isolated layout test's STA thread.
            scopeRoot.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
#pragma warning restore VSTHRD001
            scopePanel.Measure(new Size(layout.WorkWidth, double.PositiveInfinity));
            Assert.AreEqual(repositorySelected ? Visibility.Visible : Visibility.Collapsed, scopePanel.Visibility);
            Assert.AreEqual(repositorySelected ? "Filtered to repository:" : "Across all repositories", ((TextBlock)scopeRoot.FindName("RepositoryScopeLabel")).Text);
            var scopeName = (TextBlock)scopeRoot.FindName("RepositoryScopeName");
            Assert.AreEqual(repositorySelected ? Visibility.Visible : Visibility.Collapsed, scopeName.Visibility);
            Assert.AreEqual(repositorySelected ? "owner/hubitat-drivers" : "", scopeName.Text);
            var scroll = screen.Descendants(p + "ScrollViewer").Single(element => element.Attribute(x + "Name")?.Value == "ContentScroll");
            Assert.AreEqual("Hidden", scroll.Attribute("HorizontalScrollBarVisibility")?.Value);
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

        private static StackPanel CreateActivityFilters(XDocument document, XElement tab)
        {
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = new XElement(tab.Descendants(p + "WrapPanel").Single());
            definition.Attribute("ButtonBase.Click")?.Remove();
            foreach (var element in definition.Descendants())
            {
                element.Attribute("Checked")?.Remove();
                element.Attribute("Unchecked")?.Remove();
            }
            var style = new XElement(document.Descendants(p + "Style")
                .Single(element => element.Attribute(x + "Key")?.Value == "ActivityFilterStyle"));
            style.Descendants(p + "Setter").Where(element =>
                element.Attribute("Value")?.Value.StartsWith("{DynamicResource", StringComparison.Ordinal) == true).Remove();
            return (StackPanel)XamlReader.Parse(new XElement(p + "StackPanel",
                new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "StackPanel.Resources", style), definition).ToString());
        }

        [STATestMethod]
        public void ActivityTabs_UseThemeAwareUnderlinesWithoutChangingSizeOnSelection()
        {
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var style = new XElement(document.Descendants(p + "Style")
                .Single(element => element.Attribute(x + "Key")?.Value == "ActivityFilterStyle"));
            foreach (var setter in style.Descendants(p + "Setter").Where(element =>
                element.Attribute("Value")?.Value.StartsWith("{DynamicResource", StringComparison.Ordinal) == true))
                setter.SetAttributeValue("Value", "{DynamicResource TestTabBrush}");
            var root = (StackPanel)XamlReader.Parse(new XElement(p + "StackPanel",
                new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "StackPanel.Resources", style),
                new XElement(p + "RadioButton", new XAttribute("Content", "Attention (30)"),
                    new XAttribute("Style", "{StaticResource ActivityFilterStyle}"))).ToString());
            root.Resources["TestTabBrush"] = SystemColors.HighlightBrush;
            var tab = (RadioButton)root.Children[0];
            root.Measure(new Size(400, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, 400, root.DesiredSize.Height));
            root.UpdateLayout();
            var tabRoot = (Grid)tab.Template.FindName("TabRoot", tab);
            var indicator = (System.Windows.Shapes.Rectangle)tab.Template.FindName("SelectionIndicator", tab);
            var size = tab.DesiredSize;

            Assert.IsNull(tabRoot.Background);
            Assert.IsEmpty(style.Descendants(p + "Border"));
            Assert.AreEqual(2d, indicator.Height);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
            tab.IsChecked = true;
            root.Measure(new Size(400, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, 400, root.DesiredSize.Height));
            root.UpdateLayout();

            Assert.AreEqual(size, tab.DesiredSize);
            Assert.AreSame(SystemColors.HighlightBrush, indicator.Fill);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.IsNull(tabRoot.Background);
            Assert.AreEqual(tabRoot.ActualHeight, indicator.TranslatePoint(new Point(0, indicator.ActualHeight), tabRoot).Y, 0.01);
            root.Resources["TestTabBrush"] = SystemColors.HotTrackBrush;
            Assert.AreSame(SystemColors.HotTrackBrush, indicator.Fill);
        }

        [STATestMethod]
        [DataRow(0, 320d)]
        [DataRow(1, 320d)]
        [DataRow(2, 320d)]
        [DataRow(3, 320d)]
        [DataRow(4, 320d)]
        [DataRow(0, 500d)]
        [DataRow(1, 500d)]
        [DataRow(2, 500d)]
        [DataRow(3, 500d)]
        [DataRow(4, 500d)]
        public void NewsTabs_AlignWithoutClippingOrColoringContent(int selectedIndex, double width)
        {
            var document = ReadLayoutXaml("StartScreenControl.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var panel = document.Descendants(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == "NewsColumnPanel");
            var headingDefinition = new XElement(panel.Descendants(p + "DockPanel").First());
            var tabsDefinition = new XElement(panel.Descendants(p + "TabControl").Single());
            tabsDefinition.Descendants(p + "ScrollViewer.Resources").Remove();
            tabsDefinition.Attribute("SelectionChanged")!.Remove();
            tabsDefinition.Attribute("PreviewKeyDown")!.Remove();
            foreach (var item in tabsDefinition.Elements(p + "TabItem"))
            {
                var header = item.Attribute("Header")?.Value ?? item.Element(p + "TabItem.Header")!
                    .Descendants(p + "TextBlock").First().Attribute("Text")!.Value;
                var margin = item.Element(p + "StackPanel")!.Attribute("Margin")?.Value ?? "0";
                var name = item.Attribute(x + "Name")!.Value;
                XElement content = new XElement(p + "TextBlock", new XAttribute("Text", "Visible content"));
                if (name == "BlogsTab" || name == "VideosTab")
                {
                    var cardDocument = ReadLayoutXaml(name == "BlogsTab" ? "NewsItemControl.xaml" : "YouTubeVideoControl.xaml");
                    var card = cardDocument.Descendants(p + "Border").Single(element => element.Attribute(x + "Name")?.Value == "RootBorder");
                    var padding = card.Attribute("Padding")?.Value ?? SharedXamlReader.ReadResources().Descendants(p + "Thickness")
                        .Single(element => element.Attribute(x + "Key")?.Value == "StartScreen.CardPadding").Value;
                    content = new XElement(p + "Border", new XAttribute("Padding", padding), content);
                }
                else if (name == "EventsTab")
                {
                    var status = item.Descendants(p + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding EventsStatus}");
                    content.SetAttributeValue("Margin", status.Attribute("Margin")!.Value);
                }
                else if (name == "FeedbackTab")
                {
                    var status = item.Descendants(p + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding FeedbackStatus}");
                    content.SetAttributeValue("Margin", status.Attribute("Margin")!.Value);
                }
                item.RemoveNodes();
                item.SetAttributeValue("Header", header);
                item.Add(new XElement(p + "StackPanel", new XAttribute("Margin", margin),
                    content));
            }
            var style = new XElement(document.Descendants(p + "Style").Single(element => element.Attribute(x + "Key")?.Value == "NewsTabItemStyle"));
            foreach (var setter in style.Descendants(p + "Setter").Where(element =>
                element.Attribute("Value")?.Value.StartsWith("{DynamicResource", StringComparison.Ordinal) == true))
                setter.SetAttributeValue("Value", setter.Parent == style ? "{DynamicResource ContentBrush}" : "{DynamicResource HeaderBrush}");
            var root = (StackPanel)XamlReader.Parse(new XElement(p + "StackPanel",
                new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "StackPanel.Resources", style), headingDefinition, tabsDefinition).ToString());
            root.Resources["ContentBrush"] = SystemColors.ControlTextBrush;
            root.Resources["HeaderBrush"] = SystemColors.HighlightBrush;
            var heading = ((DockPanel)root.Children[0]).Children.OfType<TextBlock>().Single();
            var tabs = (TabControl)root.Children[1];
            tabs.SelectedIndex = selectedIndex;
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
            root.UpdateLayout();
            var first = (TabItem)tabs.Items[0];
            var firstHeader = (ContentPresenter)first.Template.FindName("TabContent", first);
            var selected = (StackPanel)tabs.SelectedContent;
            var contentElement = selected.Children[0];
            var contentText = contentElement is Border cardBorder ? (TextBlock)cardBorder.Child : (TextBlock)contentElement;
            var selectedTab = (TabItem)tabs.Items[selectedIndex];
            var selectedRoot = (Grid)selectedTab.Template.FindName("TabRoot", selectedTab);
            var indicator = (System.Windows.Shapes.Rectangle)selectedTab.Template.FindName("SelectionIndicator", selectedTab);

            Assert.AreEqual(heading.TranslatePoint(new Point(), root).X, firstHeader.TranslatePoint(new Point(), root).X, 0.01);
            Assert.AreEqual(heading.TranslatePoint(new Point(), root).X, contentText.TranslatePoint(new Point(), root).X, 0.01);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.IsNull(selectedRoot.Background);
            Assert.AreSame(SystemColors.ControlTextBrush, contentText.Foreground);
            Assert.AreSame(SystemColors.HighlightBrush, indicator.Fill);
            foreach (TabItem item in tabs.Items)
            {
                var header = (ContentPresenter)item.Template.FindName("TabContent", item);
                var label = new TextBlock
                {
                    Text = (string)item.Header, FontSize = item.FontSize,
                    FontFamily = item.FontFamily, FontWeight = item.FontWeight,
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.IsGreaterThanOrEqualTo(label.DesiredSize.Width - 0.01, header.ActualWidth,
                    $"The {item.Header} header must show its full label.");
            }
        }

        [STATestMethod]
        [DataRow("MyWorkBlade", true)]
        [DataRow("MyWorkBlade", false)]
        [DataRow("IncomingBlade", false)]
        public void WorkCards_AlignWithHeadingsAndTabsInAttentionAndTypeViews(string bladeName, bool attention)
        {
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var blade = document.Descendants(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == bladeName);
            var headerDefinition = blade.Elements(p + "DockPanel").First();
            var heading = new TextBlock { Text = bladeName, Margin = ReadThickness(headerDefinition, "Margin") };
            var filters = CreateActivityFilters(document, blade);
            filters.Margin = new Thickness(8, 0, 0, 0);
            var filterRow = (WrapPanel)filters.Children[0];
            var own = bladeName == "MyWorkBlade";
            ((RadioButton)filterRow.Children[attention || !own ? 0 : 2]).IsChecked = true;
            var pr = new Models.DevHub.DevHubPullRequest
            {
                Title = "Example work", Status = "open", WebUrl = "https://example.com/pr/1",
                IsAuthoredByCurrentUser = own, CiStatus = "failure",
                UpdatedAt = DateTime.UtcNow,
            };
            var activity = new ToolWindows.Controls.DevHubActivityViewModel();
            activity.SetMyWorkFilter(attention ? Models.DevHub.DevHubWorkItemKind.All : Models.DevHub.DevHubWorkItemKind.PullRequest, attention);
            activity.UpdateRepositoryActivity([pr], [], [], [], Models.DevHub.DevHubSortOrder.MostRecent, false);
            var card = document.Descendants(p + "Border").Single(element => element.Attribute(x + "Name")?.Value == "WorkItemBorder");
            var template = (DataTemplate)XamlReader.Parse(new XElement(p + "DataTemplate",
                new XElement(p + "Border", new XAttribute("Style", card.Attribute("Style")!.Value),
                    new XAttribute("Margin", card.Attribute("Margin")!.Value),
                    new XElement(p + "TextBlock", new XAttribute("Text", "{Binding DisplayTitle}")))).ToString());
            var view = CollectionViewSource.GetDefaultView(own ? activity.MyWorkItems : activity.IncomingItems);
            var list = new ItemsControl { ItemsSource = view, ItemTemplate = template };
            var emptyDefinition = blade.Descendants(p + "TextBlock").Single(element =>
                element.Attribute(x + "Name")?.Value == (own ? "MyWorkEmpty" : "IncomingEmpty"));
            var footer = new TextBlock { Text = "Below the cards", Margin = ReadThickness(emptyDefinition, "Margin") };
            var root = new StackPanel();
            root.Children.Add(heading);
            root.Children.Add(filters);
            root.Children.Add(list);
            root.Children.Add(footer);
            root.Measure(new Size(400, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, 400, root.DesiredSize.Height));
            root.UpdateLayout();
            var presenter = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(0);
            var border = (Border)System.Windows.Media.VisualTreeHelper.GetChild(presenter, 0);
            var title = (TextBlock)border.Child;
            var firstTab = (RadioButton)filterRow.Children[0];
            var tabText = (ContentPresenter)firstTab.Template.FindName("TabContent", firstTab);
            var tabRoot = (Grid)firstTab.Template.FindName("TabRoot", firstTab);

            Assert.IsNull(view.Groups);
            Assert.AreEqual(heading.TranslatePoint(new Point(), root).X, title.TranslatePoint(new Point(), root).X, 0.01);
            Assert.AreEqual(heading.TranslatePoint(new Point(), root).X, tabText.TranslatePoint(new Point(), root).X, 0.01);
            Assert.AreEqual(heading.TranslatePoint(new Point(), root).X, tabRoot.TranslatePoint(new Point(), root).X, 0.01);
            Assert.AreEqual(footer.TranslatePoint(new Point(), root).X, title.TranslatePoint(new Point(), root).X, 0.01);
        }

        [TestMethod]
        public void CopilotDiagnostics_AreNotDisplayedInTheWorkHeading()
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var heading = document.Descendants(presentation + "TextBlock")
                .Single(element => element.Attribute("Text")?.Value == "My work");

            Assert.IsNotNull(heading);
            Assert.IsFalse(document.Descendants().Any(element => element.Attribute(xaml + "Name")?.Value
                is "CopilotActivityStatusText" or "CopilotActivityStatusGap"));
            Assert.IsFalse(document.Descendants().Attributes().Any(attribute => attribute.Value.Contains("CopilotActivityStatus")));
        }

        [STATestMethod]
        public void WorkHeading_PreservesItsMeasuredBaselineWithoutDiagnostics()
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var line = new XElement(document.Descendants(presentation + "TextBlock")
                .Single(element => element.Attribute(xaml + "Name")?.Value == "WorkHeadingLine"));
            line.SetAttributeValue(XNamespace.Xmlns + "x", xaml.NamespaceName);
            line.Descendants(presentation + "TextBlock").Single().SetAttributeValue("Text", "Your work");
            line.Descendants(presentation + "Run.Foreground").Remove();
            var textBlock = (TextBlock)XamlReader.Parse(line.ToString());
            var heading = (TextBlock)textBlock.Inlines.OfType<InlineUIContainer>().First().Child;
            textBlock.Measure(new Size(536, double.PositiveInfinity));
            textBlock.Arrange(new Rect(textBlock.DesiredSize));

            var headingBaseline = heading.TranslatePoint(new Point(0, heading.BaselineOffset), textBlock).Y;
            Assert.AreEqual(textBlock.BaselineOffset, headingBaseline, 0.01);
            Assert.AreEqual(BaselineAlignment.Baseline, textBlock.Inlines.OfType<Run>().Last().BaselineAlignment);
            Assert.AreEqual(11d, textBlock.Inlines.OfType<Run>().Last().FontSize);
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
            Assert.AreEqual(24d, textBlock.Margin.Right);
            Assert.IsFalse(item.DisplayTitle.Contains('\n'));
            Assert.IsFalse(item.DisplayTitle.Contains('\r'));
        }

        [TestMethod]
        public void WorkViews_HaveMyWorkAttentionAndTeamTypeFilters()
        {
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var tabs = document.Descendants(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == "WorkBlades")
                .Elements(p + "Grid").ToArray();

            CollectionAssert.AreEqual(new[] { "MyWorkBlade", "IncomingBlade" }, tabs.Select(tab => tab.Attribute(x + "Name")?.Value).ToArray());
            Assert.IsEmpty(document.Descendants(p + "TabControl"));
            foreach (var tab in tabs)
            {
                Assert.IsEmpty(tab.Descendants(p + "CheckBox").Where(element => element.Attribute("Content")?.Value == "Needs attention"));
                var list = tab.Descendants(p + "ItemsControl").Single();
                Assert.AreEqual("{StaticResource WorkItemTemplate}", list.Attribute("ItemTemplate")?.Value);
                Assert.IsEmpty(list.Elements(p + "ItemsControl.GroupStyle"), "Work blades must not display redundant section headings.");
            }
            CollectionAssert.AreEqual(new[] { "Attention", "Copilot", "PRs", "Issues", "Builds" }, tabs[0].Descendants(p + "RadioButton")
                .Select(button => button.Attribute("Content")?.Value).ToArray());
            CollectionAssert.AreEqual(new[] { "PRs", "Issues", "Builds" }, tabs[1].Descendants(p + "RadioButton")
                .Select(button => button.Attribute("Content")?.Value).ToArray());
            foreach (var tab in tabs)
            {
                var selected = tab.Descendants(p + "RadioButton").Single(button => button.Attribute("IsChecked")?.Value == "True");
                Assert.AreEqual(tab == tabs[0] ? "Copilot" : "PRs", selected.Attribute("Content")?.Value);
                Assert.AreEqual("ActivityFilter_Changed", selected.Attribute("Checked")?.Value);
            }
            Assert.AreNotEqual(tabs[0].Descendants(p + "RadioButton").First().Attribute("GroupName")?.Value,
                tabs[1].Descendants(p + "RadioButton").First().Attribute("GroupName")?.Value);
            var teamHeading = tabs[1].Descendants(p + "TextBlock").Single(element => element.Attribute(x + "Name")?.Value == "IncomingHeading");
            Assert.AreEqual("Team activity", teamHeading.Attribute("AutomationProperties.Name")?.Value);
            Assert.AreEqual("Team activity", teamHeading.Element(p + "Run")?.Attribute("Text")?.Value);
            var focusOptions = document.Descendants(p + "ComboBox").Single(element => element.Attribute(x + "Name")?.Value == "DefaultTabComboBox");
            CollectionAssert.AreEqual(new[] { "My work", "Team activity" }, focusOptions.Elements(p + "ComboBoxItem")
                .Select(element => element.Attribute("Content")?.Value).ToArray());
        }

        [STATestMethod]
        public void PullRequests_KeyboardNavigationIncludesOwnAndIncomingSections()
        {
            var root = new StackPanel();
            var own = new StackPanel();
            var first = new Border { Focusable = true };
            var second = new Border { Focusable = true };
            own.Children.Add(first);
            root.Children.Add(own);
            root.Children.Add(second);

            var borders = ToolWindows.Controls.DevHubPanel.CollectFocusableBorders(root);

            CollectionAssert.AreEqual(new[] { first, second }, borders);
        }

        [STATestMethod]
        [DataRow(260d)]
        [DataRow(460d)]
        public void ActivityFilters_WrapWithoutClippingAndKeepSelectionsIndependent(double width)
        {
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var tabs = document.Descendants(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == "WorkBlades")
                .Elements(p + "Grid").ToArray();
            var own = CreateActivityFilters(document, tabs[0]);
            var incoming = CreateActivityFilters(document, tabs[1]);
            var root = new StackPanel();
            root.Children.Add(own);
            root.Children.Add(incoming);
            var ownButtons = ((WrapPanel)own.Children[0]).Children.OfType<RadioButton>().ToArray();
            var incomingButtons = ((WrapPanel)incoming.Children[0]).Children.OfType<RadioButton>().ToArray();

            Assert.IsTrue(ownButtons[1].IsChecked);
            Assert.IsTrue(incomingButtons[0].IsChecked);
            ownButtons[0].Content = "Attention (123)";
            Assert.AreEqual("PRs", incomingButtons[0].Content);
            ownButtons[0].IsChecked = true;
            incomingButtons[2].IsChecked = true;
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
            root.UpdateLayout();

            Assert.IsTrue(ownButtons[0].IsChecked);
            Assert.IsTrue(incomingButtons[2].IsChecked);
            Assert.IsFalse(ownButtons[1].IsChecked);
            Assert.IsFalse(incomingButtons[0].IsChecked);
            foreach (var view in new[] { own, incoming })
            {
                var panel = (WrapPanel)view.Children[0];
                foreach (FrameworkElement filter in panel.Children)
                    Assert.IsLessThanOrEqualTo(width + 0.1, filter.TranslatePoint(new Point(), root).X + filter.ActualWidth);
            }
        }

        [TestMethod]
        public void PullRequestCards_ShareStatusBadgesAndUseNativeContextMenusInsteadOfIconActions()
        {
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var badges = document.Descendants(p + "TextBlock")
                .Where(element => element.Attribute("Text")?.Value == "{Binding StatusText}").ToArray();
            var actions = document.Descendants(p + "ContentControl")
                .Where(element => element.Attribute("ContentTemplate")?.Value == "{StaticResource PullRequestActionsTemplate}").ToArray();

            Assert.HasCount(2, badges);
            Assert.IsEmpty(actions);
            Assert.IsTrue(badges.All(element => element.Attribute("Style")?.Value == "{StaticResource PullRequestStatusStyle}"));
            Assert.IsTrue(actions.All(element => element.Attribute("Content")?.Value == "{Binding}"));
            Assert.HasCount(1, document.Descendants(p + "Setter")
                .Where(element => element.Attribute("Property")?.Value == "ContentTemplate"
                    && element.Attribute("Value")?.Value == "{StaticResource PullRequestMetadataTemplate}"));
            Assert.IsFalse(document.Descendants(p + "DataTemplate")
                .Any(element => element.Attribute(x + "Key")?.Value == "PullRequestActionsTemplate"));
            Assert.IsFalse(document.Descendants(p + "TextBlock").Any(element =>
                element.Attribute("Text")?.Value == "{Binding ActionText}"));
            Assert.IsFalse(document.Descendants().Any(element => element.Attribute("Text")?.Value == "Review in VS"));
        }

        [TestMethod]
        public void WorkItemMenus_AreReachableByMouseAndKeyboardWithoutAWpfContextMenu()
        {
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var template = document.Descendants(p + "DataTemplate")
                .Single(element => element.Attribute(x + "Key")?.Value == "WorkItemTemplate");
            var card = template.Elements(p + "Border").Single();
            Assert.AreEqual("True", card.Attribute("Focusable")?.Value);
            Assert.AreEqual("CopilotItem_RightButtonDown", card.Attribute("PreviewMouseRightButtonDown")?.Value);
            Assert.AreEqual("CopilotItem_RightButtonUp", card.Attribute("PreviewMouseRightButtonUp")?.Value);
            Assert.AreEqual("CopilotItem_PreviewKeyDown", card.Attribute("PreviewKeyDown")?.Value);
            Assert.IsFalse(template.Descendants(p + "ContextMenu").Any());
            Assert.IsFalse(template.Descendants(p + "Button").Any());
        }

        [TestMethod]
        public void PullRequestBadges_UseCopilotLikeTypographyAndThemeAwareStatusColors()
        {
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var textStyle = document.Descendants(p + "Style")
                .Single(element => element.Attribute(x + "Key")?.Value == "PullRequestStatusStyle");
            var style = SharedXamlReader.ReadResources().Descendants(p + "Style")
                .Single(element => element.Attribute(x + "Key")?.Value == "StartScreen.StatusIcon");

            Assert.AreEqual("{StaticResource StartScreen.StatusText}", textStyle.Attribute("BasedOn")?.Value);
            Assert.AreEqual("{DynamicResource {x:Static platformui:EnvironmentColors.ToolWindowTextBrushKey}}",
                textStyle.Elements(p + "Setter").Single(element => element.Attribute("Property")?.Value == "Foreground").Attribute("Value")?.Value);
            Assert.AreEqual("{StaticResource StartScreen.Metadata}", SharedXamlReader.ReadResources().Descendants(p + "Style")
                .Single(element => element.Attribute(x + "Key")?.Value == "StartScreen.StatusText").Attribute("BasedOn")?.Value);
            Assert.AreEqual("Bold", SharedXamlReader.Setter("StartScreen.StatusText", "FontWeight"));
            var colors = new Dictionary<string, string>
            {
                ["Error"] = "ProgressBarColors.IndicatorFillCriticalBrushKey",
                ["Success"] = "CommonDocumentColors.StatusBannerSuccessBorderTextBrushKey",
                ["Warning"] = "ProgressBarColors.IndicatorFillWarningBrushKey",
                ["Muted"] = "EnvironmentColors.ToolWindowTextBrushKey",
            };
            foreach (var color in colors)
            {
                var trigger = style.Descendants(p + "DataTrigger").Single(element => element.Attribute("Value")?.Value == color.Key);
                Assert.AreEqual("{Binding StatusCategory}", trigger.Attribute("Binding")?.Value);
                Assert.AreEqual($"{{DynamicResource {{x:Static platformui:{color.Value}}}}}",
                    trigger.Elements(p + "Setter").Single(element => element.Attribute("Property")?.Value == "Stroke").Attribute("Value")?.Value);
            }
            Assert.IsFalse(style.Descendants(p + "DataTrigger").Any(element => element.Attribute("Binding")?.Value == "{Binding NeedsAttention}"));
        }

        [STATestMethod]
        [DataRow("failure", "Error")]
        [DataRow("success", "Success")]
        [DataRow("pending", "Information")]
        [DataRow("cancelled", "Muted")]
        public void BuildBadges_UseSemanticColorsAndRespondToThemeChanges(string status, string category)
        {
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = new XElement(SharedXamlReader.ReadResources().Descendants(p + "Style").Single(element => element.Attribute(x + "Key")?.Value == "StartScreen.StatusIcon"));
            definition.Attribute(x + "Key")!.Remove();
            definition.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            foreach (var setter in definition.Descendants(p + "Setter").Where(element => element.Attribute("Property")?.Value == "Stroke" && !element.Attribute("Value")!.Value.Contains("SystemColors")))
            {
                var tone = setter.Parent!.Name == p + "DataTrigger" ? setter.Parent.Attribute("Value")!.Value : "Information";
                setter.SetAttributeValue("Value", $"{{DynamicResource Test{tone}Brush}}");
            }
            var badge = new System.Windows.Shapes.Path { Style = (Style)XamlReader.Parse(definition.ToString()), Tag = false };
            badge.Resources["TestErrorBrush"] = SystemColors.ControlTextBrush;
            badge.Resources["TestSuccessBrush"] = SystemColors.HighlightBrush;
            badge.Resources["TestWarningBrush"] = SystemColors.HotTrackBrush;
            badge.Resources["TestMutedBrush"] = SystemColors.GrayTextBrush;
            badge.Resources["TestInformationBrush"] = SystemColors.WindowTextBrush;
            var build = new Models.DevHub.DevHubCiRun { Status = status, WebUrl = "https://example.com/build/1", IsTriggeredByCurrentUser = true };
            var item = Models.DevHub.DevHubWorkItem.GetMyWork([], [], [], builds: [build]).Single();
            badge.DataContext = item;
            badge.Measure(new Size(400, double.PositiveInfinity));
            badge.Arrange(new Rect(0, 0, 400, badge.DesiredSize.Height));
            badge.UpdateLayout();
            Assert.AreEqual(category, item.StatusCategory);
            Assert.AreSame(badge.Resources[$"Test{category}Brush"], badge.Stroke);
            badge.Resources[$"Test{category}Brush"] = SystemColors.HighlightTextBrush;
            Assert.AreSame(SystemColors.HighlightTextBrush, badge.Stroke);
        }

        [STATestMethod]
        [DataRow(200d, null)]
        [DataRow(400d, null)]
        [DataRow(200d, "madskristensen")]
        [DataRow(400d, "madskristensen")]
        [DataRow(520d, "madskristensen")]
        [DataRow(200d, "A very long author display name that must fit within the available card width")]
        [DataRow(520d, "A very long author display name that must fit within the available card width")]
        public void PullRequestMetadata_PersonalAuthorsStayBelowStatusAtEveryWidth(double width, string? authorName)
        {
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var style = new XElement(document.Descendants(p + "Style")
                .Single(element => element.Attribute(x + "Key")?.Value == "PullRequestStatusStyle"));
            foreach (var setter in style.Descendants(p + "Setter").Where(element => element.Attribute("Property")?.Value == "Foreground"))
                setter.SetAttributeValue("Value", "{DynamicResource TestStatusBrush}");
            var metadata = new XElement(document.Descendants(p + "DataTemplate")
                .Single(element => element.Attribute(x + "Key")?.Value == "PullRequestMetadataTemplate").Element(p + "Grid")!);
            var details = new XElement(document.Descendants(p + "Grid")
                .Single(element => element.Attribute(x + "Name")?.Value == "WorkItemDetails"));
            details.Elements().Single(element => element.Attribute(x + "Name")?.Value == "CopilotActivityIcon").Remove();
            var pillDefinition = metadata.Descendants(p + "Border").Single(element => element.Attribute("Style")?.Value == "{StaticResource StartScreen.ActivityStatus}");
            Assert.AreEqual("Transparent", SharedXamlReader.Setter("StartScreen.ActivityStatus", "Background"));
            var root = (StackPanel)XamlReader.Parse(new XElement(p + "StackPanel",
                new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "StackPanel.Resources", style), details, metadata).ToString());
            root.Resources["TestStatusBrush"] = SystemColors.HighlightBrush;
            root.DataContext = Models.DevHub.DevHubWorkItem.GetMyWork([new Models.DevHub.DevHubPullRequest
            {
                Author = authorName, UpdatedAt = DateTime.UtcNow, RepoDisplayName = "owner/repo", Number = "#1",
                Status = "open", IsAuthoredByCurrentUser = true, WebUrl = "https://example.com/pull/1",
            }], [], []).Single();
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
            root.UpdateLayout();

            var content = (Grid)root.Children[1];
            var pill = (Border)content.Children[0];
            var badge = ((StackPanel)pill.Child).Children.OfType<TextBlock>().Single();
            var author = (TextBlock)content.Children[1];
            Assert.AreEqual(new CornerRadius(0), pill.CornerRadius);
            Assert.AreEqual(new Thickness(0), pill.Padding);
            Assert.AreEqual(new Thickness(0), pill.BorderThickness);
            Assert.AreEqual("Waiting for review", badge.Text);
            Assert.AreEqual(authorName == null ? Visibility.Collapsed : Visibility.Visible, author.Visibility);
            if (authorName != null)
            {
                Assert.AreEqual($"by {authorName}", author.Text);
                Assert.AreEqual(author.Text, author.ToolTip);
                Assert.AreEqual(badge.TranslatePoint(new Point(), root).X, author.TranslatePoint(new Point(), root).X, 0.01);
                Assert.IsGreaterThanOrEqualTo(pill.TranslatePoint(new Point(0, pill.ActualHeight), root).Y + 2,
                    author.TranslatePoint(new Point(), root).Y);
                Assert.IsLessThanOrEqualTo(width + 0.1, author.TranslatePoint(new Point(), root).X + author.ActualWidth);
            }
            Assert.AreEqual(badge.ActualWidth + 18, pill.ActualWidth, 0.01);
            Assert.AreSame(badge.Foreground, pill.BorderBrush);
            root.Resources["TestStatusBrush"] = SystemColors.WindowTextBrush;
            root.UpdateLayout();
            Assert.AreSame(SystemColors.WindowTextBrush, badge.Foreground);
            Assert.AreSame(badge.Foreground, pill.BorderBrush);
        }

        [STATestMethod]
        [DataRow("pr", 200d)]
        [DataRow("pr", 400d)]
        [DataRow("issue", 200d)]
        [DataRow("issue", 400d)]
        [DataRow("build", 200d)]
        [DataRow("build", 400d)]
        public void RepositoryWorkItemTitles_ReserveTwoLinesWithFullTitleOnHover(string kind, double width)
        {
            var title = "A very long activity title\r\nwith another line that must not increase the card height";
            var item = kind == "pr" ? Models.DevHub.DevHubWorkItem.GetNeedsYou(
                [new Models.DevHub.DevHubPullRequest { Title = title, Status = "open", IsReviewRequestedForCurrentUser = true, UpdatedAt = DateTime.UtcNow }]).Single()
                : kind == "issue" ? Models.DevHub.DevHubWorkItem.GetMyWork([], [new Models.DevHub.DevHubIssue
                {
                    Title = title, State = "open", IsAssignedToCurrentUser = true, WebUrl = "https://example.com/issues/1",
                }], []).Single()
                : Models.DevHub.DevHubWorkItem.GetMyWork([], [], [], builds: [new Models.DevHub.DevHubCiRun
                {
                    Name = title, IsTriggeredByCurrentUser = true, WebUrl = "https://example.com/build/1",
                }]).Single();

            var textBlock = CreateWorkItemTitle(item);
            textBlock.Measure(new Size(width, double.PositiveInfinity));

            Assert.AreEqual(TextWrapping.Wrap, textBlock.TextWrapping);
            Assert.AreEqual(TextTrimming.CharacterEllipsis, textBlock.TextTrimming);
            Assert.AreEqual(2 * textBlock.LineHeight, textBlock.MaxHeight, 0.01);
            Assert.IsTrue(double.IsNaN(textBlock.Height));
            Assert.IsLessThanOrEqualTo(width, textBlock.DesiredSize.Width);
            Assert.AreEqual(title.Replace("\r\n", " "), item.DisplayTitle);
            Assert.AreEqual(title, textBlock.ToolTip);
            Assert.AreEqual(title, item.Title);
            Assert.AreEqual(24d, textBlock.Margin.Right);
            textBlock.Arrange(new Rect(0, 0, width, textBlock.DesiredSize.Height));
            Assert.AreEqual(width - 24, textBlock.ActualWidth, 0.01);
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
            Assert.AreEqual("{Binding CopilotMoniker}", icon.Attribute("Moniker")?.Value);
            Assert.AreEqual("{Binding IsCopilot, Converter={StaticResource BoolToVisibilityConverter}}", icon.Attribute("Visibility")?.Value);
            Assert.AreSame(title.Parent, metadata.Parent!.Parent);
            var siblings = title.Parent!.Elements().ToList();
            Assert.IsGreaterThan(siblings.IndexOf(title), siblings.IndexOf(metadata.Parent));
            Assert.AreEqual("{Binding IsCopilot, Converter={StaticResource BoolToVisibilityConverter}}", metadata.Attribute("Visibility")?.Value);
            var details = title.ElementsAfterSelf(presentation + "Grid").Single();
            Assert.AreEqual("{Binding Detail}", details.Elements(presentation + "TextBlock").First().Attribute("Text")?.Value);
            var date = title.Parent.Elements(presentation + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding ActivityDateText}");
            Assert.AreEqual("Right", date.Attribute("HorizontalAlignment")?.Value);
            Assert.IsFalse(metadata.Elements(presentation + "TextBlock").Any(element => element.Attribute("Text")?.Value == "{Binding ActivityText}"));
            var badge = metadata.Elements(presentation + "TextBlock")
                .Single(element => element.Attribute("Text")?.Value == "{Binding CopilotStatus}");
            Assert.AreEqual("{StaticResource StartScreen.StatusText}", badge.Element(presentation + "TextBlock.Style")!
                .Element(presentation + "Style")!.Attribute("BasedOn")?.Value);
            foreach (var state in new[] { "Running", "Waiting" })
            {
                var trigger = metadata.Elements(presentation + "Path").Single().Descendants(presentation + "DataTrigger")
                    .Single(element => element.Attribute("Value")?.Value == state);
                Assert.IsTrue(trigger.Elements(presentation + "Setter").Any(setter => setter.Attribute("Property")?.Value == "Stroke"
                    && setter.Attribute("Value")!.Value.StartsWith("{DynamicResource", StringComparison.Ordinal)));
            }
            Assert.IsFalse(document.Descendants(presentation + "TextBlock")
                .Any(element => element.Attribute("Text")?.Value == "{Binding ActionText}"));
            var card = title.Ancestors(presentation + "Border").First();
            Assert.AreEqual("WorkItem_MouseUp", card.Attribute("MouseUp")?.Value);
            Assert.AreEqual("{StaticResource StartScreen.CardSurface}", card.Attribute("Style")?.Value);
            Assert.AreEqual("0", SharedXamlReader.Setter("StartScreen.CardSurface", "CornerRadius"));
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
            var document = ReadLayoutXaml("DevHubPanel.xaml");
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = document.Descendants(p + "StackPanel")
                .Single(element => element.Attribute(x + "Name")?.Value == "CopilotMetadata")
                .Element(p + "Path")!;
            var icon = (System.Windows.Shapes.Path)XamlReader.Parse(definition.ToString());
            icon.DataContext = item;
#pragma warning disable VSTHRD001 // Apply deferred bindings on the isolated layout test's STA thread.
            icon.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
#pragma warning restore VSTHRD001
            icon.Measure(new Size(400, 100));
            Assert.AreEqual(expectedVisibility, icon.Visibility);
            if (expectedVisibility == Visibility.Collapsed)
            {
                Assert.AreEqual(0d, badge.DesiredSize.Width);
                Assert.AreEqual(new Size(), icon.DesiredSize);
            }
            else
            {
                Assert.IsGreaterThan(0d, badge.DesiredSize.Width);
                Assert.IsGreaterThan(0d, icon.DesiredSize.Width);
            }
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
            }
            foreach (var setter in style.Setters.OfType<Setter>()
                .Concat(style.Triggers.OfType<DataTrigger>().SelectMany(trigger => trigger.Setters.OfType<Setter>())))
            {
                if (setter.Value is Binding setterBinding)
                    setter.Value = new Binding(setterBinding.Path.Path) { Source = item, Mode = setterBinding.Mode };
            }
            textBlock.Style = style;
            var titleBinding = BindingOperations.GetBinding(textBlock, TextBlock.TextProperty)!;
            BindingOperations.SetBinding(textBlock, TextBlock.TextProperty,
                new Binding(titleBinding.Path.Path) { Source = item, Mode = titleBinding.Mode }).UpdateTarget();
            textBlock.DataContext = item;
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
        public void SolutionsOnly_IsTheFirstRecentMenuCommandSeparatedFromClearingActions()
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            Assert.IsFalse(document.Descendants(presentation + "CheckBox")
                .Any(element => element.Attribute("Content")?.Value == "Solutions only"));
            using var menuStream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("VSCommandTable.vsct");
            Assert.IsNotNull(menuStream);
            var menu = XDocument.Load(menuStream);
            XNamespace vsct = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
            var groups = menu.Descendants(vsct + "Group")
                .Where(group => group.Element(vsct + "Parent")?.Attribute("id")?.Value == "ClearRecentMenu")
                .OrderBy(group => Convert.ToInt32(group.Attribute("priority")!.Value, 16)).ToList();
            Assert.AreEqual("RecentFiltersGroup", groups[0].Attribute("id")?.Value);
            Assert.AreEqual("ClearRecentGroup", groups[1].Attribute("id")?.Value);
            var filters = menu.Descendants(vsct + "Button")
                .Where(button => button.Element(vsct + "Parent")?.Attribute("id")?.Value == "RecentFiltersGroup").ToList();
            Assert.HasCount(1, filters);
            Assert.AreEqual("SolutionsOnlyCommand", filters[0].Attribute("id")?.Value);
            Assert.AreEqual("Solutions only", filters[0].Element(vsct + "Strings")?.Element(vsct + "ButtonText")?.Value);
            Assert.AreEqual(PackageIds.SolutionsOnlyCommand, Convert.ToInt32(menu.Descendants(vsct + "IDSymbol")
                .Single(symbol => symbol.Attribute("name")?.Value == "SolutionsOnlyCommand").Attribute("value")!.Value, 16));
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

        [TestMethod]
        public void UnifiedRefreshStatus_StaysOnOneLineAndExposesSeparateDetailsInTooltip()
        {
            using var stream = typeof(StartScreenInlineBindingTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var status = document.Descendants(presentation + "TextBlock")
                .Single(element => element.Attribute(xaml + "Name")?.Value == "LastRefreshText");

            Assert.AreEqual("{Binding UnifiedRefreshStatus}", status.Attribute("Text")?.Value);
            Assert.AreEqual("{Binding UnifiedRefreshDetails}", status.Attribute("ToolTip")?.Value);
            Assert.AreEqual("NoWrap", status.Attribute("TextWrapping")?.Value);
            Assert.AreEqual("220", status.Attribute("Width")?.Value);
            Assert.AreEqual("CharacterEllipsis", status.Attribute("TextTrimming")?.Value);
            Assert.AreEqual("Right", status.Attribute("TextAlignment")?.Value);
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