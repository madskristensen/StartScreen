using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Xml.Linq;
using StartScreen.Models.DevHub;
using XamlReader = StartScreen.Test.SharedXamlReader;

namespace StartScreen.Test
{
    [TestClass]
    public class CardMetadataLayoutTests
    {
        [STATestMethod]
        [DataRow("NewsItemControl.xaml", 200d, true)]
        [DataRow("NewsItemControl.xaml", 400d, true)]
        [DataRow("NewsItemControl.xaml", 200d, false)]
        [DataRow("NewsItemControl.xaml", 400d, false)]
        [DataRow("YouTubeVideoControl.xaml", 200d, true)]
        [DataRow("YouTubeVideoControl.xaml", 400d, true)]
        [DataRow("YouTubeVideoControl.xaml", 200d, false)]
        [DataRow("YouTubeVideoControl.xaml", 400d, false)]
        public void NewsMetadata_StaysLeftAndTitlesUseThemeText(string resource, double width, bool isNew)
        {
            using var stream = typeof(CardMetadataLayoutTests).Assembly.GetManifestResourceStream(resource);
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = document.Descendants(p + "TextBlock").Single(element =>
                element.Attribute("Text")?.Value == "{Binding Source}"
                || element.Attribute("Text")?.Value.StartsWith("{Binding PublishDate,", StringComparison.Ordinal) == true).Parent!;
            var title = new XElement(document.Descendants(p + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding Title}"));
            Assert.AreEqual("{DynamicResource {x:Static platformui:EnvironmentColors.ToolWindowTextBrushKey}}", title.Attribute("Foreground")?.Value);
            title.SetAttributeValue("Foreground", "{DynamicResource TitleBrush}");
            var root = (StackPanel)XamlReader.Parse(new XElement(p + "StackPanel",
                new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "StackPanel.Resources", new XElement(p + "BooleanToVisibilityConverter",
                    new XAttribute(x + "Key", "BoolToVisibilityConverter"))), new XElement(definition), title).ToString());
            root.Resources["TitleBrush"] = SystemColors.ControlTextBrush;
            var published = new DateTime(2026, 6, 15);
            var source = $"{published:MMM d} in Test Feed";
            root.DataContext = new { Title = "News title", PublishDate = published, IsNew = isNew, Source = source };
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
            root.UpdateLayout();
            var row = (StackPanel)root.Children[0];
            var metadata = (TextBlock)row.Children[1];
            var badge = (TextBlock)row.Children[0];
            var heading = (TextBlock)root.Children[1];

            Assert.AreEqual(resource == "NewsItemControl.xaml" ? source : published.ToString("MMM d"), metadata.Text);
            Assert.AreEqual(isNew ? Visibility.Visible : Visibility.Collapsed, badge.Visibility);
            Assert.AreEqual(isNew ? badge.DesiredSize.Width : 0d, metadata.TranslatePoint(new Point(), row).X, 0.01);
            Assert.AreSame(SystemColors.ControlTextBrush, heading.Foreground);
            root.Resources["TitleBrush"] = SystemColors.HighlightTextBrush;
            Assert.AreSame(SystemColors.HighlightTextBrush, heading.Foreground);
        }

        [STATestMethod]
        [DataRow(200d)]
        [DataRow(360d)]
        [DataRow(600d)]
        public void FeedbackCardColumn_StaysLeftAnchoredRatherThanCenteringAtItsWidthLimit(double width)
        {
            using var stream = typeof(CardMetadataLayoutTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var definitions = document.Descendants(p + "DataTemplate").Select(template => template.Element(p + "Grid"))
                .Where(grid => grid?.Element(p + "Grid.ColumnDefinitions")?.Element(p + "ColumnDefinition")?.Attribute("MaxWidth")?.Value == "360").ToArray();
            Assert.HasCount(1, definitions);
            foreach (var definition in definitions)
            {
                var content = definition!.Elements().Last();
                var margin = content.Attribute("Margin")!.Value;
                var row = (Grid)XamlReader.Parse(new XElement(p + "Grid", new XElement(definition.Element(p + "Grid.ColumnDefinitions")!),
                    new XElement(p + "Border", new XAttribute("Margin", margin), new XAttribute("Height", "10"),
                        new XAttribute("HorizontalAlignment", content.Attribute("HorizontalAlignment")!.Value))).ToString());
                row.Measure(new Size(width, double.PositiveInfinity));
                row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
                row.UpdateLayout();
                var card = (Border)row.Children[0];

                Assert.AreEqual(card.Margin.Left, card.TranslatePoint(new Point(), row).X, 0.01);
                Assert.AreEqual(Math.Min(width, 360) - card.Margin.Left - card.Margin.Right, card.ActualWidth, 0.01);
            }
        }

        [STATestMethod]
        [DataRow(null, null)]
        [DataRow(135d, "2m 15s")]
        public void BuildDuration_RendersOnlyWhenAvailable(double? seconds, string? expected)
        {
            using var stream = typeof(CardMetadataLayoutTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XNamespace controls = "clr-namespace:StartScreen.ToolWindows.Controls;assembly=StartScreen";
            var definition = document.Descendants(p + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding DurationText}");
            var root = (StackPanel)XamlReader.Parse(new XElement(p + "StackPanel",
                new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "StackPanel.Resources", new XElement(controls + "NullToCollapsedConverter",
                    new XAttribute(x + "Key", "NullToCollapsedConverter"))), new XElement(definition)).ToString());
            var build = new DevHubCiRun { Duration = seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null };
            var text = (TextBlock)root.Children[0];
            foreach (var property in new[] { TextBlock.TextProperty, UIElement.VisibilityProperty })
            {
                var binding = (Binding)BindingOperations.GetBinding(text, property)!;
                text.SetBinding(property, new Binding(binding.Path.Path) { Source = build, Converter = binding.Converter }).UpdateTarget();
            }
            root.Measure(new Size(300, double.PositiveInfinity));

            Assert.AreEqual(expected ?? string.Empty, text.Text);
            Assert.AreEqual(expected == null ? Visibility.Collapsed : Visibility.Visible, text.Visibility);
            Assert.AreEqual(11d, text.FontSize);
            Assert.AreEqual("Build duration", text.ToolTip);
            if (expected == null)
                Assert.AreEqual(new Size(), text.DesiredSize);
        }

        [TestMethod]
        public void WorkCards_OmitCardWideMetadataTooltipsButKeepTargetedHelp()
        {
            using var stream = typeof(CardMetadataLayoutTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var card = document.Descendants(p + "Border").Single(element => element.Attribute(x + "Name")?.Value == "WorkItemBorder");

            Assert.IsNull(card.Attribute("ToolTip"));
            Assert.IsEmpty(card.Elements(p + "Border.ToolTip"));
            var title = card.Descendants(p + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding DisplayTitle}");
            Assert.AreEqual("{Binding Title}", title.Descendants(p + "Setter").Single(element => element.Attribute("Property")?.Value == "ToolTip").Attribute("Value")?.Value);
            Assert.IsTrue(document.Descendants(p + "Button").Any(element => element.Attribute("ToolTip")?.Value == "Open in Visual Studio"));

            var pr = DevHubWorkItem.GetMyWork([new DevHubPullRequest
            {
                Title = "Fix startup", Status = "open", IsAuthoredByCurrentUser = true, RepoDisplayName = "owner/repo",
                Number = "#1", CiStatus = "failure", WebUrl = "https://example.com/pull/1",
            }], [], []).Single();
            Assert.AreEqual(pr.Detail, pr.DetailToolTip);
            Assert.DoesNotContain("Checks:", pr.DetailToolTip);
            var build = DevHubWorkItem.GetIncoming([], [], [new DevHubCiRun
            {
                Name = "Build", RepoDisplayName = "owner/repo", Branch = "main", WebUrl = "https://example.com/build/1",
            }]).Single();
            Assert.AreEqual("Branch: main", build.DetailToolTip);
        }

        [STATestMethod]
        public void PlainIssueMetadata_ReservesNoEmptyActionRowAboveLabels()
        {
            using var stream = typeof(CardMetadataLayoutTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XNamespace controls = "clr-namespace:StartScreen.ToolWindows.Controls;assembly=StartScreen";
            var definition = new XElement(document.Descendants(p + "DataTemplate")
                .Single(element => element.Attribute(x + "Key")?.Value == "RepositoryItemMetadataTemplate").Elements().Single());
            var style = new XElement(document.Descendants(p + "Style").Single(element => element.Attribute(x + "Key")?.Value == "PullRequestStatusStyle"));
            foreach (var attribute in definition.DescendantsAndSelf().Attributes().Concat(style.Descendants().Attributes()).Where(attribute =>
                attribute.Value.StartsWith("{DynamicResource", StringComparison.Ordinal)))
                attribute.Value = "{DynamicResource MetadataBrush}";
            var root = (StackPanel)XamlReader.Parse(new XElement(p + "StackPanel", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "StackPanel.Resources", style,
                    new XElement(p + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "BoolToVisibilityConverter")),
                    new XElement(controls + "NullToCollapsedConverter", new XAttribute(x + "Key", "NullToCollapsedConverter"))), definition).ToString());
            root.Resources["MetadataBrush"] = SystemColors.ControlTextBrush;
            root.DataContext = DevHubWorkItem.GetIncoming([], [new DevHubIssue
            {
                Title = "Issue", State = "open", WebUrl = "https://example.com/issues/1",
                Labels = [new DevHubLabel { Name = "bug" }],
            }], []).Single();
            foreach (FrameworkElement element in ((Panel)root.Children[0]).Children)
            {
                var binding = BindingOperations.GetBinding(element, UIElement.VisibilityProperty)!;
                element.SetBinding(UIElement.VisibilityProperty, new Binding(binding.Path.Path)
                {
                    Source = root.DataContext,
                    Converter = binding.Converter,
                }).UpdateTarget();
            }
            root.Measure(new Size(300, double.PositiveInfinity));

            Assert.AreEqual(0d, root.DesiredSize.Height);
            Assert.IsEmpty(definition.Descendants(p + "Button"));
            var details = document.Descendants(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == "WorkItemDetails");
            var labels = document.Descendants(p + "ItemsControl").Single(element => element.Attribute("ItemsSource")?.Value == "{Binding Labels}");
            var detailsMargin = (Thickness)new ThicknessConverter().ConvertFromString(details.Attribute("Margin")!.Value)!;
            var labelsMargin = (Thickness)new ThicknessConverter().ConvertFromString(labels.Attribute("Margin")!.Value)!;
            Assert.IsLessThanOrEqualTo(5d, detailsMargin.Bottom + labelsMargin.Top);
        }

        [STATestMethod]
        [DataRow(220d)]
        [DataRow(500d)]
        public void CopilotLogo_SitsBesideTheWorkspaceWithoutAnotherRow(double width)
        {
            using var stream = typeof(CardMetadataLayoutTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var icon = document.Descendants().Single(element => element.Attribute(x + "Name")?.Value == "CopilotActivityIcon");
            Assert.AreEqual("WorkItemDetails", icon.Parent!.Attribute(x + "Name")?.Value);
            Assert.AreEqual("{x:Static helpers:CopilotMonikers.GitHubCopilot}", icon.Attribute("Moniker")?.Value);
            var row = new XElement(icon.Parent);
            var image = row.Elements().Single(element => element.Attribute(x + "Name")?.Value == "CopilotActivityIcon");
            image.ReplaceWith(new XElement(p + "Border", image.Attributes().Where(attribute => attribute.Name.LocalName != "Moniker")
                .Select(attribute => new XAttribute(attribute))));
            var root = (StackPanel)XamlReader.Parse(new XElement(p + "StackPanel", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "StackPanel.Resources", new XElement(p + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "BoolToVisibilityConverter"))), row).ToString());
            root.DataContext = new { Detail = "StartScreen", DetailToolTip = "StartScreen", IsCopilot = true, ActivityDateText = "today", ActivityDateToolTip = "Updated today" };
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
            root.UpdateLayout();
            var grid = (Grid)root.Children[0];
            var workspace = (TextBlock)grid.Children[0];
            var date = (TextBlock)grid.Children[1];
            var logo = (Border)grid.Children[2];

            Assert.AreEqual(Visibility.Visible, logo.Visibility);
            Assert.AreEqual(logo.ActualWidth + logo.Margin.Right, workspace.TranslatePoint(new Point(), grid).X, 0.01);
            Assert.AreEqual(logo.TranslatePoint(new Point(0, logo.ActualHeight / 2), grid).Y,
                workspace.TranslatePoint(new Point(0, workspace.ActualHeight / 2), grid).Y, 0.01);
            Assert.AreEqual(width, date.TranslatePoint(new Point(date.ActualWidth, 0), grid).X, 0.01);
        }

        [STATestMethod]
        public void NewsCards_UseWorkCardPaddingWithoutStretchingToFillTheirRow()
        {
            using var newsStream = typeof(CardMetadataLayoutTests).Assembly.GetManifestResourceStream("NewsItemControl.xaml");
            using var workStream = typeof(CardMetadataLayoutTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            var news = XDocument.Load(newsStream!);
            var work = XDocument.Load(workStream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var newsBorder = news.Descendants(p + "Border").Single(element => element.Attribute(x + "Name")?.Value == "RootBorder");
            var workBorder = work.Descendants(p + "Border").Single(element => element.Attribute(x + "Name")?.Value == "WorkItemBorder");
            Assert.AreEqual("{StaticResource StartScreen.CardSurface}", workBorder.Attribute("Style")?.Value);
            Assert.AreEqual(workBorder.Attribute("Style")!.Value, newsBorder.Attribute("Style")?.Value);
            Assert.AreEqual("{StaticResource StartScreen.CardPadding}", SharedXamlReader.Setter("StartScreen.CardSurface", "Padding"));
            var padding = ((Border)XamlReader.Parse("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Style=\"{StaticResource StartScreen.CardSurface}\"/>")).Padding;
            var card = new UserControl
            {
                VerticalAlignment = (VerticalAlignment)Enum.Parse(typeof(VerticalAlignment), news.Root!.Attribute("VerticalAlignment")!.Value),
                Content = new Border { Padding = padding, Child = new Border { Height = 30 } },
            };
            var row = new Grid { Height = 160 };
            row.Children.Add(card);
            row.Measure(new Size(360, 160));
            row.Arrange(new Rect(0, 0, 360, 160));
            row.UpdateLayout();

            Assert.AreEqual(VerticalAlignment.Top, card.VerticalAlignment);
            Assert.AreEqual(50d, card.ActualHeight, 0.01);
        }
    }
}