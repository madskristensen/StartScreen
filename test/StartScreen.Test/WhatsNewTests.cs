using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.Services;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class WhatsNewTests
    {
        private string _directory = null!;

        [TestInitialize]
        public void Initialize()
        {
            _directory = Path.Combine(Path.GetTempPath(), "StartScreen.WhatsNew.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Directory.Delete(_directory, recursive: true);
        }

        [TestMethod]
        public async Task ReadAsync_UsesIndexOrderAndGroupsAreasWithoutIncludingUnlistedArticles()
        {
            WriteIndex("z.md", "a.md", "c.md", "b.md");
            WriteItem("z.md", "Productivity", "First");
            WriteItem("a.md", "Productivity", "Second");
            WriteItem("c.md", "Copilot", "Third");
            WriteItem("b.md", "Copilot", "Fourth");
            WriteItem("unlisted.md", "Other", "Not shown");

            var content = await ReadAsync();

            Assert.IsNull(content.Status);
            CollectionAssert.AreEqual(new[] { "Productivity", "Copilot" }, content.Areas.Select(area => area.Area).ToArray());
            CollectionAssert.AreEqual(new[] { "First", "Second", "Third", "Fourth" },
                content.Areas.SelectMany(area => area.Items).Select(item => item.Title).ToArray());
            Assert.AreEqual("Summary", content.Areas[0].Items[0].Description);
        }

        [TestMethod]
        public async Task ReadAsync_RepeatedAreasKeepFirstAppearanceAndWithinAreaOrder()
        {
            WriteIndex("z.md", "a.md", "b.md");
            WriteItem("z.md", "Z area", "First");
            WriteItem("a.md", "A area", "Second");
            WriteItem("b.md", "Z area", "Third");

            var content = await ReadAsync();

            CollectionAssert.AreEqual(new[] { "Z area", "A area" }, content.Areas.Select(area => area.Area).ToArray());
            CollectionAssert.AreEqual(new[] { "First", "Third" }, content.Areas[0].Items.Select(item => item.Title).ToArray());
        }

        [TestMethod]
        public async Task ReadAsync_LoadsAllFeaturesInALongIndex()
        {
            var files = Enumerable.Range(0, 35).Select(index => $"feature-{index}.md").ToArray();
            WriteIndex(files);
            for (var index = 0; index < files.Length; index++)
                WriteItem(files[index], index < 20 ? "Productivity" : "Testing", $"Feature {index}");

            var content = await ReadAsync();

            Assert.HasCount(2, content.Areas);
            Assert.HasCount(20, content.Areas[0].Items);
            Assert.HasCount(15, content.Areas[1].Items);
            CollectionAssert.AreEqual(Enumerable.Range(0, 35).Select(index => $"Feature {index}").ToArray(),
                content.Areas.SelectMany(area => area.Items).Select(item => item.Title).ToArray());
        }

        [TestMethod]
        public async Task ReadAsync_UsesLocalizedContentAndFallsBackPerMissingArticle()
        {
            WriteIndex("first.md", "second.md");
            WriteItem("first.md", "English", "English title");
            WriteItem("second.md", "English", "Fallback title");
            WriteItem("first.md", "Localized", "Localized title", "de-DE");

            var content = await WhatsNewService.ReadAsync(_directory, CultureInfo.GetCultureInfo("de-DE"));

            Assert.IsNull(content.Status);
            CollectionAssert.AreEqual(new[] { "Localized title", "Fallback title" },
                content.Areas.SelectMany(area => area.Items).Select(item => item.Title).ToArray());
        }

        [TestMethod]
        [DataRow("fr-CA")]
        [DataRow("")]
        public async Task ReadAsync_UnsupportedOrInvariantLocaleFallsBackToEnglish(string locale)
        {
            WriteIndex("first.md");
            WriteItem("first.md", "Productivity", "English title");

            var content = await WhatsNewService.ReadAsync(_directory, CultureInfo.GetCultureInfo(locale));

            Assert.AreEqual("English title", content.Areas[0].Items[0].Title);
            Assert.IsNull(content.Status);
        }

        [TestMethod]
        public async Task ReadAsync_ParsesQuotedAndMultilineYamlWithoutReadingTheArticleBody()
        {
            WriteIndex("first.md");
            WriteRawItem("first.md", "---\narea: Testing\ntitle: 'A title: with punctuation'\ndescription: >-\n  A folded\n  description.\nfeatureId: different-from-file-name\n---\n" + new string('x', 100000));

            var content = await ReadAsync();

            Assert.IsNull(content.Status);
            Assert.AreEqual("A title: with punctuation", content.Areas[0].Items[0].Title);
            Assert.AreEqual("A folded description.", content.Areas[0].Items[0].Description);
        }

        [TestMethod]
        [DataRow("title: Title\ndescription: Summary")]
        [DataRow("area: Testing\ntitle: Title")]
        [DataRow("area: Testing\ndescription: Summary")]
        [DataRow("area: [broken")]
        public async Task ReadAsync_InvalidMetadataReportsPartialContentWithoutDiscardingValidItems(string metadata)
        {
            WriteIndex("bad.md", "good.md");
            WriteRawItem("bad.md", "---\n" + metadata + "\n---\n");
            WriteItem("good.md", "Testing", "Good feature");

            var content = await ReadAsync();

            StringAssert.Contains(content.Status, "could not be loaded");
            Assert.HasCount(1, content.Areas);
            Assert.AreEqual("Good feature", content.Areas[0].Items[0].Title);
        }

        [TestMethod]
        [DataRow("missing.md")]
        [DataRow("..\\outside.md")]
        public async Task ReadAsync_MissingOrInvalidIndexedFileReportsUnavailableItems(string file)
        {
            WriteIndex(file);

            var content = await ReadAsync();

            Assert.IsEmpty(content.Areas);
            StringAssert.Contains(content.Status, "could not be loaded");
        }

        [TestMethod]
        public async Task ReadAsync_EmptyIndexReportsNoContent()
        {
            File.WriteAllText(Path.Combine(_directory, "index.yml"), "files: []");

            var content = await ReadAsync();

            Assert.IsEmpty(content.Areas);
            StringAssert.Contains(content.Status, "No What's New items");
        }

        [TestMethod]
        public async Task ReadAsync_MissingManifestThrowsRatherThanReturningSuccessfulEmptyContent()
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => ReadAsync());
        }

        [TestMethod]
        public async Task ReadAsync_CanceledReadDoesNotReturnContent()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                WhatsNewService.ReadAsync(_directory, CultureInfo.InvariantCulture, cancellation.Token));
        }

        [TestMethod]
        public async Task ReadAsync_DoesNotReadThumbnailFiles()
        {
            WriteIndex("first.md");
            WriteRawItem("first.md", "---\narea: Testing\ntitle: Feature\ndescription: Summary\nthumbnailImage: ../media/thumb.png\n---\n");
            var media = Path.Combine(_directory, "media");
            Directory.CreateDirectory(media);
            var path = Path.Combine(media, "thumb.png");
            File.WriteAllText(path, "Not an image");
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var content = await ReadAsync();

            Assert.IsNull(content.Status);
            Assert.AreEqual("Feature", content.Areas[0].Items[0].Title);
            Assert.AreEqual("Summary", content.Areas[0].Items[0].Description);
        }

        [TestMethod]
        [DataRow("../media/missing.png")]
        [DataRow("../../outside.png")]
        public async Task ReadAsync_IgnoresThumbnailMetadata(string thumbnail)
        {
            WriteIndex("first.md");
            WriteRawItem("first.md", $"---\narea: Testing\ntitle: Feature\ndescription: Summary\nthumbnailImage: {thumbnail}\n---\n");

            var content = await ReadAsync();

            Assert.AreEqual("Feature", content.Areas[0].Items[0].Title);
            Assert.AreEqual("Summary", content.Areas[0].Items[0].Description);
            Assert.IsNull(content.Status);
        }

        [STATestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void TabSelection_LoadsOnlyOnceAndSharesAnInProgressRead(bool hasItems)
        {
            using var context = new JoinableTaskContext(Thread.CurrentThread,
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var model = new StartScreenViewModel([]);
            var requests = 0;
            var pending = new TaskCompletionSource<WhatsNewContent>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<WhatsNewContent> Read(CancellationToken token)
            {
                requests++;
                return pending.Task;
            }
            Assert.IsEmpty(model.WhatsNewAreas);
            Assert.IsNull(model.WhatsNewStatus);
            Assert.AreEqual(0, requests);

            context.Factory.Run(async () =>
            {
                Task loading = model.EnsureWhatsNewLoadedAsync(Read, context.Factory);
                Assert.AreEqual(1, requests);
                Assert.AreEqual("Loading What's New...", model.WhatsNewStatus);
                Assert.AreSame(loading, model.EnsureWhatsNewLoadedAsync(Read, context.Factory));
                Assert.AreEqual(1, requests);
                var area = new WhatsNewArea { Area = "Testing", Items = [new WhatsNewItem { Title = "Feature", Description = "Summary" }] };
                pending.SetResult(new WhatsNewContent { Areas = hasItems ? [area] : [], Status = hasItems ? null : "No items available" });
                await loading.WithCancellation(timeout.Token);

                await model.EnsureWhatsNewLoadedAsync(Read, context.Factory);
                Assert.AreEqual(1, requests, "Returning to the tab must not reread even an empty installation.");
                Assert.HasCount(hasItems ? 1 : 0, model.WhatsNewAreas);
                Assert.AreEqual(hasItems ? null : "No items available", model.WhatsNewStatus);
                model.CancelBackgroundWork();
                model.ActivateBackgroundWork();
                await model.EnsureWhatsNewLoadedAsync(Read, context.Factory);
                Assert.AreEqual(1, requests, "Reopening the screen must retain the already rendered content.");
            });
        }

        [STATestMethod]
        public void TabSelection_FailureIsReportedWithoutRepeatedReads()
        {
            using var context = new JoinableTaskContext(Thread.CurrentThread,
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var model = new StartScreenViewModel([]);
            var requests = 0;
            Task<WhatsNewContent> Read(CancellationToken token)
            {
                requests++;
                return Task.FromException<WhatsNewContent>(new FileNotFoundException("Missing test content"));
            }

            context.Factory.Run(() => model.EnsureWhatsNewLoadedAsync(Read, context.Factory));
            context.Factory.Run(() => model.EnsureWhatsNewLoadedAsync(Read, context.Factory));

            Assert.AreEqual(1, requests);
            Assert.IsEmpty(model.WhatsNewAreas);
            StringAssert.Contains(model.WhatsNewStatus, "could not be loaded");
        }

        [STATestMethod]
        public void TabSelection_CanceledReadCannotPublishAndCanResumeOnNextSelection()
        {
            using var context = new JoinableTaskContext(Thread.CurrentThread,
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var model = new StartScreenViewModel([]);
            var requests = 0;
            var pending = new TaskCompletionSource<WhatsNewContent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var content = new WhatsNewContent
            {
                Areas = [new WhatsNewArea { Area = "Testing", Items = [new WhatsNewItem { Title = "Feature", Description = "Summary" }] }],
            };
            Task<WhatsNewContent> Read(CancellationToken token)
            {
                requests++;
                return requests == 1 ? pending.Task : Task.FromResult(content);
            }

            context.Factory.Run(async () =>
            {
                Task loading = model.EnsureWhatsNewLoadedAsync(Read, context.Factory);
                model.CancelBackgroundWork();
                pending.SetResult(content);
                await loading.WithCancellation(timeout.Token);
                Assert.IsEmpty(model.WhatsNewAreas);

                model.ActivateBackgroundWork();
                await model.EnsureWhatsNewLoadedAsync(Read, context.Factory);

                Assert.AreEqual(2, requests);
                Assert.HasCount(1, model.WhatsNewAreas);
                Assert.IsNull(model.WhatsNewStatus);
            });
        }

        [TestMethod]
        public void ApplyContent_PublishesGroupsAndStatusUsingAStableCollection()
        {
            var viewModel = new StartScreenViewModel([]);
            var collection = viewModel.WhatsNewAreas;
            var area = new WhatsNewArea { Area = "Testing", Items = [new WhatsNewItem { Title = "Feature", Description = "Summary" }] };
            var notifications = new List<string?>();
            viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

            viewModel.ApplyWhatsNewContent(new WhatsNewContent { Areas = [area], Status = "Partial content" });

            Assert.AreSame(collection, viewModel.WhatsNewAreas);
            Assert.AreSame(area, viewModel.WhatsNewAreas.Single());
            Assert.AreEqual("Partial content", viewModel.WhatsNewStatus);
            CollectionAssert.Contains(notifications, nameof(StartScreenViewModel.WhatsNewStatus));
        }

        [TestMethod]
        public void Tab_UsesAreaSeparatorsAndTextOnlyFeatures()
        {
            using var stream = typeof(WhatsNewTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var tab = document.Descendants(p + "TabItem").Single(element => element.Attribute(x + "Name")?.Value == "WhatsNewTab");

            Assert.AreEqual("What's New", tab.Attribute("Header")?.Value);
            Assert.IsFalse(document.Descendants().Any(element => element.Attribute(x + "Name")?.Value == "ReleaseNotesTab"));
            CollectionAssert.AreEqual(new[] { "{Binding WhatsNewAreas}", "{Binding Items}" },
                tab.Descendants(p + "ItemsControl").Select(element => element.Attribute("ItemsSource")?.Value).ToArray());
            CollectionAssert.AreEqual(new[] { "{Binding WhatsNewStatus}", "{Binding Area}", "{Binding Title}", "{Binding Description}" },
                tab.Descendants(p + "TextBlock").Select(element => element.Attribute("Text")?.Value).Where(text => text != null).ToArray());
            Assert.IsTrue(tab.Descendants(p + "Border").Any(element => element.Attribute("Style")?.Value == "{StaticResource StartScreen.Separator}"));
            Assert.IsFalse(tab.Descendants(p + "Image").Any());
            var panel = tab.Descendants(p + "UniformGrid").Single();
            Assert.AreEqual("2", panel.Descendants(p + "Setter").First().Attribute("Value")?.Value);
            var trigger = panel.Descendants(p + "DataTrigger").Single();
            Assert.AreEqual("1", trigger.Attribute("Value")?.Value);
            Assert.AreEqual("1", trigger.Element(p + "Setter")!.Attribute("Value")?.Value);
            var link = tab.Descendants(p + "Hyperlink").Single();
            Assert.AreEqual("ReleaseNotesLink_Click", link.Attribute("Click")?.Value);
            Assert.AreSame(link.Parent, tab.Element(p + "StackPanel")!.Elements().First());
            Assert.AreEqual("{Binding DiscoverySectionVersion, Mode=OneWay}", link.Element(p + "Run")!.Attribute("Text")?.Value);
        }

        [STATestMethod]
        [DataRow(240d, 12d)]
        [DataRow(360d, 12d)]
        [DataRow(600d, 12d)]
        [DataRow(240d, 20d)]
        public void FeatureRow_WrapsWithinSidebarAndUsesFullWidthForText(double width, double fontSize)
        {
            using var stream = typeof(WhatsNewTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var tab = document.Descendants(p + "TabItem").Single(element => element.Attribute(x + "Name")?.Value == "WhatsNewTab");
            var definition = new XElement(tab.Descendants(p + "TextBlock")
                .Single(element => element.Attribute("Text")?.Value == "{Binding Title}").Parent!);
            var row = (StackPanel)SharedXamlReader.Parse(definition.ToString());
            row.DataContext = new WhatsNewItem
            {
                Title = "A useful feature for the editor",
                Description = "A short feature description that wraps naturally inside the narrow News sidebar.",
            };
            Typography.SetEnvironmentSize(row, fontSize);
            row.Measure(new Size(width, double.PositiveInfinity));
            row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
            row.UpdateLayout();

            var title = (TextBlock)row.Children[0];
            var description = (TextBlock)row.Children[1];
            Assert.AreEqual(0d, title.TranslatePoint(new Point(), row).X, 0.01);
            Assert.AreEqual(row.ActualWidth, title.ActualWidth, 0.01);
            Assert.AreEqual(row.ActualWidth, description.ActualWidth, 0.01);
            Assert.IsLessThanOrEqualTo(440d, row.ActualWidth);
            Assert.AreEqual(FontWeights.Bold, title.FontWeight);
            Assert.AreEqual(TextWrapping.Wrap, description.TextWrapping);
            Assert.AreEqual(FontWeights.Normal, description.FontWeight);
            if (fontSize == 12d)
                Assert.IsLessThanOrEqualTo(110d, row.ActualHeight);
        }

        [STATestMethod]
        [DataRow(440d, 1, 1)]
        [DataRow(900d, 2, 2)]
        [DataRow(1300d, 3, 2)]
        public void AreaLayout_UsesAtMostTwoBoundedColumnsAndPreservesFeatureOrder(double width, int newsColumns, int expectedColumns)
        {
            using var stream = typeof(WhatsNewTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var definition = new XElement(document.Descendants(p + "ItemsControl")
                .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding Items}"));
            var wrapper = new XElement(p + "TabControl",
                new XElement(p + "TabItem", new XAttribute("Header", "What's New"), definition));
            var tab = (TabControl)SharedXamlReader.Parse(wrapper.ToString());
            var items = Enumerable.Range(1, 3).Select(index => new WhatsNewItem
            {
                Title = $"Feature {index}",
                Description = "A feature description that wraps inside its column instead of spanning the entire screen.",
            }).ToArray();

            void Layout(double availableWidth, int columns)
            {
                tab.DataContext = new { Items = items, NewsColumnCount = columns };
                for (var pass = 0; pass < 2; pass++)
                {
                    tab.Measure(new Size(availableWidth, double.PositiveInfinity));
                    tab.Arrange(new Rect(0, 0, availableWidth, tab.DesiredSize.Height));
                    tab.UpdateLayout();
                }
            }

            Layout(width, newsColumns);
            var panel = Descendants(tab).OfType<System.Windows.Controls.Primitives.UniformGrid>().Single();
            var rows = Descendants(panel).OfType<StackPanel>().Where(row => row.Children.OfType<TextBlock>().Count() == 2).ToArray();
            Assert.AreEqual(expectedColumns, panel.Columns);
            Assert.IsLessThanOrEqualTo(880d, panel.ActualWidth);
            Assert.HasCount(3, rows);
            CollectionAssert.AreEqual(new[] { "Feature 1", "Feature 2", "Feature 3" }, rows.Select(row =>
                ((TextBlock)row.Children[0]).Text).ToArray());
            foreach (var row in rows)
                Assert.IsLessThanOrEqualTo(440d, row.ActualWidth);
            var first = rows[0].TranslatePoint(new Point(), panel);
            var second = rows[1].TranslatePoint(new Point(), panel);
            if (expectedColumns == 2)
            {
                Assert.AreEqual(first.Y, second.Y, 0.01);
                Assert.IsGreaterThan(first.X, second.X);
            }
            else
            {
                Assert.AreEqual(first.X, second.X, 0.01);
                Assert.IsGreaterThan(first.Y, second.Y);
            }
            Assert.IsGreaterThan(first.Y, rows[2].TranslatePoint(new Point(), panel).Y);

            Layout(440, 1);
            Assert.AreEqual(1, panel.Columns, "Resizing back to the sidebar must restore one column.");
            Assert.IsGreaterThan(rows[0].TranslatePoint(new Point(), panel).Y, rows[1].TranslatePoint(new Point(), panel).Y);
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (var descendant in Descendants(child))
                    yield return descendant;
            }
        }

        private Task<WhatsNewContent> ReadAsync()
        {
            return WhatsNewService.ReadAsync(_directory, CultureInfo.GetCultureInfo("en-US"));
        }

        private void WriteIndex(params string[] files)
        {
            File.WriteAllText(Path.Combine(_directory, "index.yml"), "featureIds: [unrelated-id]\nfiles:\n" +
                string.Join("\n", files.Select(file => "- " + file)));
        }

        private void WriteItem(string file, string area, string title, string locale = "en-us")
        {
            WriteRawItem(file, $"---\narea: {area}\ntitle: {title}\ndescription: Summary\n---\nArticle body is not displayed.", locale);
        }

        private void WriteRawItem(string file, string text, string locale = "en-us")
        {
            var directory = Path.Combine(_directory, locale);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, file), text);
        }
    }
}
