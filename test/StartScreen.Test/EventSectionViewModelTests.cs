using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class EventSectionViewModelTests
    {
        [STATestMethod]
        public void ConstructionResumeAndLayout_DoNotLoadEventsOrThumbnails()
        {
            using var context = CreateContext();
            var reads = 0;
            var thumbnails = 0;
            using var section = new EventSectionViewModel(getTaskFactory: () => context.Factory,
                getEventsAsync: (_, _) => { reads++; return Task.FromResult(Result("Unused")); },
                loadThumbnailsAsync: (_, _) => { thumbnails++; return Task.FromResult(0); });
            var notifications = new List<string?>();
            section.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            using var screen = new StartScreenViewModel(CancellationToken.None, eventSection: section);
            screen.ActivateBackgroundWork();
            screen.UpdateColumnCounts(2, 2);
            screen.CancelBackgroundWork();
            screen.ActivateBackgroundWork();
            Assert.AreEqual(0, reads);
            Assert.AreEqual(0, thumbnails);
            Assert.IsEmpty(screen.Events);
            Assert.IsFalse(section.RefreshState.HasRefreshed);
            Assert.IsEmpty(notifications, "An inactive section must not publish spurious loading transitions when hidden.");
        }

        [STATestMethod]
        [DataRow(false, false, null)]
        [DataRow(true, false, null)]
        [DataRow(false, true, "No upcoming events with confirmed dates are listed yet.")]
        [DataRow(true, false, "Could not update events. Showing the cached schedule.")]
        [DataRow(false, false, "Events updated, but the seven-day disk cache could not be saved.")]
        public void InlineStatus_HidesRoutineUpdatesWithoutReservingSpaceButRetainsNotices(bool cached, bool empty, string? message)
        {
            using var context = CreateContext();
            StartScreenViewModel? model = null;
            using var section = new EventSectionViewModel(getTaskFactory: () => context.Factory,
                getEventsAsync: (_, _) =>
                {
                    Assert.IsNull(model!.EventsStatus, "Loading must not introduce another routine status label.");
                    return Task.FromResult(new EventFeedResult
                    {
                        Events = empty ? Array.Empty<ConferenceEvent>() : [new ConferenceEvent { Name = "Confirmed" }],
                        IsCached = cached, IsStale = cached && message != null, Message = message,
                        LastSuccessfulRefreshUtc = DateTimeOffset.UtcNow,
                    });
                }, loadThumbnailsAsync: (_, _) => Task.FromResult(0));
            using var screen = model = new StartScreenViewModel(CancellationToken.None, eventSection: section);
            var document = ReadScreen();
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XNamespace controls = "clr-namespace:StartScreen.ToolWindows.Controls;assembly=StartScreen";
            var definition = document.Descendants(p + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding EventsStatus}");
            var root = (StackPanel)SharedXamlReader.Parse(new XElement(p + "StackPanel",
                new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "StackPanel.Resources", new XElement(controls + "NullToCollapsedConverter",
                    new XAttribute(x + "Key", "NullToCollapsedConverter"))),
                new XElement(definition), new XElement(p + "Border", new XAttribute("Height", "20"))).ToString());
            root.DataContext = screen;
            context.Factory.Run(() => section.ActivateTabAsync());
            root.Measure(new Size(320, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, 320, root.DesiredSize.Height));
            root.UpdateLayout();
            var status = (TextBlock)root.Children[0];
            Assert.AreEqual(message, screen.EventsStatus);
            Assert.AreEqual(message == null ? Visibility.Collapsed : Visibility.Visible, status.Visibility);
            Assert.AreEqual(message ?? string.Empty, status.Text);
            if (message == null)
            {
                Assert.AreEqual(0d, root.Children[1].TranslatePoint(new Point(), root).Y, 0.01);
                Assert.IsFalse(screen.UnifiedRefreshDetails.Contains("Events:"));
            }
        }

        [STATestMethod]
        public void Activation_LoadsAndReportsProgressAndPartialThumbnailFailure()
        {
            using var context = CreateContext();
            var reads = 0;
            var thumbnails = 0;
            StartScreenViewModel? screen = null;
            using var section = new EventSectionViewModel(getTaskFactory: () => context.Factory,
                getEventsAsync: (force, _) =>
                {
                    Assert.IsTrue(force);
                    Assert.IsTrue(screen!.IsRefreshingAny);
                    Assert.IsTrue(screen.IsRefreshProgressActive);
                    reads++;
                    return Task.FromResult(Result("Confirmed"));
                }, loadThumbnailsAsync: (items, _) =>
                {
                    Assert.AreEqual("Confirmed", screen!.Events.Single().Name);
                    Assert.AreEqual("Confirmed", items.Single().Name);
                    thumbnails++;
                    return Task.FromResult(1);
                });
            using var model = screen = new StartScreenViewModel(CancellationToken.None, eventSection: section);
            context.Factory.Run(() => section.ActivateTabAsync(force: true));
            Assert.AreEqual(1, reads);
            Assert.AreEqual(1, thumbnails);
            Assert.IsFalse(model.IsRefreshingEvents);
            Assert.IsFalse(model.IsRefreshProgressActive);
            Assert.IsTrue(section.RefreshState.IsPartial);
            StringAssert.Contains(model.EventsStatus, "thumbnails");
            StringAssert.Contains(model.UnifiedRefreshDetails, "Events:");
        }

        [STATestMethod]
        public void DuplicateActivation_IsCoalescedAndCanceledRequestsCannotOverwriteTheNextActivation()
        {
            using var context = CreateContext();
            var pending = new TaskCompletionSource<EventFeedResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            var thumbnails = 0;
            using var section = new EventSectionViewModel(getTaskFactory: () => context.Factory,
                getEventsAsync: (_, _) => ++reads == 1 ? pending.Task : Task.FromResult(Result("Current")),
                loadThumbnailsAsync: (_, _) => { thumbnails++; return Task.FromResult(0); });
            context.Factory.Run(async () =>
            {
                var original = section.ActivateTabAsync();
                await section.ActivateTabAsync();
                Assert.AreEqual(1, reads);
                section.Cancel();
                await section.ActivateTabAsync();
                pending.SetResult(Result("Canceled"));
                await original;
            });
            Assert.AreEqual(2, reads);
            Assert.AreEqual(1, thumbnails);
            Assert.AreEqual("Current", section.Events.Single().Name);
            Assert.IsFalse(section.IsRefreshingEvents);
        }

        [STATestMethod]
        public void FailureAfterSuccessfulActivation_PreservesTheVisibleScheduleAndShowsAnError()
        {
            using var context = CreateContext();
            var reads = 0;
            using var section = new EventSectionViewModel(getTaskFactory: () => context.Factory,
                getEventsAsync: (_, _) => ++reads == 1 ? Task.FromResult(Result("Retained"))
                    : Task.FromException<EventFeedResult>(new IOException("Offline")),
                loadThumbnailsAsync: (_, _) => Task.FromResult(0));
            context.Factory.Run(async () =>
            {
                await section.ActivateTabAsync();
                await section.ActivateTabAsync();
            });
            Assert.AreEqual("Retained", section.Events.Single().Name);
            Assert.IsTrue(section.RefreshState.IsStale);
            Assert.IsFalse(section.RefreshState.IsUnavailable);
            StringAssert.Contains(section.EventsStatus, "could not be updated");
        }

        [TestMethod]
        public void EventsTab_UsesItsOwnArticlesAndDoesNotBindToBlogsOrVideos()
        {
            var document = ReadScreen();
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var tab = document.Descendants(p + "TabItem").Single(item => item.Attribute(x + "Name")?.Value == "EventsTab");
            Assert.AreEqual("Events", tab.Attribute("Header")!.Value);
            var list = tab.Descendants(p + "ItemsControl").Single();
            Assert.AreEqual("{Binding Events}", list.Attribute("ItemsSource")!.Value);
            Assert.AreEqual("{StaticResource EventCardTemplate}", list.Attribute("ItemTemplate")!.Value);
            var card = document.Descendants(p + "DataTemplate").Single(item => item.Attribute(x + "Key")?.Value == "EventCardTemplate");
            var surface = card.Element(p + "Border")!;
            Assert.AreEqual("EventCard_Loaded", surface.Attribute("Loaded")!.Value);
            Assert.AreEqual("EventCard_MouseUp", surface.Attribute("MouseUp")!.Value);
            Assert.AreEqual("EventCard_KeyDown", surface.Attribute("KeyDown")!.Value);
            Assert.IsTrue(card.Descendants(p + "Image").Any(item => item.Attribute("Source")?.Value == "{Binding Thumbnail}"));
            foreach (var binding in new[] { "Name", "Description" })
                Assert.IsTrue(card.Descendants(p + "TextBlock").Any(item => item.Attribute("Text")?.Value == "{Binding " + binding + "}"));
            CollectionAssert.AreEqual(new[] { "DateRange", "Location" }, card.Descendants(p + "MultiBinding")
                .Single().Elements(p + "Binding").Select(item => item.Attribute("Path")!.Value).ToArray());
            var discount = card.Descendants(p + "Hyperlink").Single();
            Assert.AreEqual("{Binding SubscriberDiscountUrl}", discount.Attribute("NavigateUri")!.Value);
            Assert.AreEqual("EventLink_RequestNavigate", discount.Attribute("RequestNavigate")!.Value);
        }

        [STATestMethod]
        [DataRow(280d, 12d)]
        [DataRow(320d, 20d)]
        [DataRow(480d, 20d)]
        [DataRow(280d, 32d)]
        public void EventCards_KeepDateAndLocationOnOneLineWithoutClippingAndFollowThemeChanges(double width, double fontSize)
        {
            var document = ReadScreen();
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = new XElement(document.Descendants(p + "DataTemplate")
                .Single(item => item.Attribute(x + "Key")?.Value == "EventCardTemplate").Element(p + "Border")!);
            foreach (var attribute in definition.DescendantsAndSelf().Attributes().Where(attribute => new[]
                { "Loaded", "MouseLeftButtonDown", "MouseUp", "KeyDown", "RequestNavigate" }.Contains(attribute.Name.LocalName)).ToArray())
                attribute.Remove();
            definition.Descendants().Where(item => item.Name.LocalName == "CrispImage").Remove();
            var root = (Grid)SharedXamlReader.Parse(new XElement(p + "Grid", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "Grid.Resources", new XElement(p + "BooleanToVisibilityConverter",
                    new XAttribute(x + "Key", "BoolToVisibilityConverter"))), definition).ToString());
            StartScreen.ToolWindows.Typography.SetEnvironmentSize(root, fontSize);
            root.Resources["TestThemeBrush"] = SystemColors.WindowTextBrush;
            root.DataContext = new ConferenceEvent
            {
                Name = "Visual Studio Live! at Microsoft Headquarters 2027", Description = "Developer training on .NET, Azure, and AI-assisted development.",
                StartDate = new DateTime(2027, 8, 2), EndDate = new DateTime(2027, 8, 6),
                Location = "Newcastle upon Tyne, United Kingdom", SubscriberDiscountUrl = "https://example.com/benefits",
            };
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
            root.UpdateLayout();
            var card = (Border)root.Children[0];
            Assert.IsTrue(card.Focusable);
            var thumbnailFrame = Descendants<Border>(card).Single(item => item.Height == 75);
            Assert.AreEqual(100d, thumbnailFrame.ActualWidth);
            var blocks = Descendants<TextBlock>(card).ToArray();
            foreach (var block in blocks)
            {
                Assert.IsGreaterThan(0d, block.ActualHeight);
                Assert.IsLessThanOrEqualTo(width + 0.01, block.TranslatePoint(new Point(block.ActualWidth, 0), root).X);
                Assert.IsLessThanOrEqualTo(root.ActualHeight + 0.01, block.TranslatePoint(new Point(0, block.ActualHeight), root).Y);
            }
            var metadata = blocks.Single(item => item.Name == "EventMetadata");
            var conference = (ConferenceEvent)root.DataContext;
            Assert.AreEqual(conference.DateRange + "  |  " + conference.Location, metadata.Text);
            Assert.AreEqual(TextWrapping.NoWrap, metadata.TextWrapping);
            Assert.AreEqual(TextTrimming.CharacterEllipsis, metadata.TextTrimming);
            Assert.AreEqual(metadata.Text, metadata.ToolTip);
            var title = blocks.Single(item => item.Text.StartsWith("Visual Studio Live!", StringComparison.Ordinal));
            Assert.AreSame(SystemColors.WindowTextBrush, title.Foreground);
            root.Resources["TestThemeBrush"] = SystemColors.HighlightTextBrush;
            Assert.AreSame(SystemColors.HighlightTextBrush, title.Foreground);
        }

        private static EventFeedResult Result(string name) => new EventFeedResult
        {
            Events = [new ConferenceEvent { Name = name }], LastSuccessfulRefreshUtc = DateTimeOffset.UtcNow,
        };

        private static JoinableTaskContext CreateContext() => new JoinableTaskContext(Thread.CurrentThread,
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        private static XDocument ReadScreen()
        {
            using var stream = typeof(EventSectionViewModelTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            return XDocument.Load(stream!);
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is T match)
                    yield return match;
                foreach (var item in Descendants<T>(child))
                    yield return item;
            }
        }
    }
}
