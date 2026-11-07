using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using XamlReader = StartScreen.Test.SharedXamlReader;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class FeedbackViewModelTests
    {
        [STATestMethod]
        public void Feedback_StartupRefreshPopulatesTicketsBeforeTabActivation()
        {
            using var context = CreateContext();
            var requests = 0;
            var model = Model(context, (force, _) =>
            {
                requests++;
                return Task.FromResult(Snapshot(7));
            });
            try
            {
                model.ForceRefreshFeedback();
                Assert.AreEqual(0, requests);

                context.Factory.Run(() => model.RefreshFeedbackInBackgroundAsync());

                Assert.AreEqual(1, requests);
                Assert.HasCount(1, model.FeedbackTickets);
                Assert.IsNull(model.FeedbackStatus);
                StringAssert.Contains(model.UnifiedRefreshDetails, "Feedback: Updated just now");
                Assert.AreEqual("Updated just now", model.UnifiedRefreshStatus);
                Assert.IsFalse(model.IsRefreshingFeedback);

                var displayed = model.FeedbackTickets.Single();
                var notifications = 0;
                model.PropertyChanged += (_, _) => notifications++;

                model.EnsureFeedbackLoaded();
                model.EnsureFeedbackLoaded();

                Assert.AreEqual(1, requests, "Reactivating the tab must not request feedback again.");
                Assert.AreSame(displayed, model.FeedbackTickets.Single());
                Assert.AreEqual(0, notifications);
            }
            finally
            {
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        public void Startup_ShowsVerifiedCacheBeforeOnlineRefreshCompletes()
        {
            using var context = CreateContext();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = new TaskCompletionSource<FeedbackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cached = new FeedbackSnapshot(7, Snapshot(7).Tickets, DateTime.UtcNow.AddDays(-3));
            var requests = 0;
            var model = Model(context, (force, _) =>
            {
                Assert.IsFalse(force);
                requests++;
                return pending.Task;
            }, _ => Task.FromResult(cached));
            try
            {
                context.Factory.Run(async () =>
                {
                    Task refresh = model.RefreshFeedbackInBackgroundAsync();
                    Assert.AreEqual(7L, model.FeedbackTickets.Single().Id);
                    Assert.IsTrue(model.FeedbackRefreshState.IsCached);
                    Assert.IsTrue(model.IsRefreshingFeedback);
                    Assert.IsFalse(refresh.IsCompleted);
                    model.EnsureFeedbackLoaded();
                    Assert.AreEqual(1, requests);

                    pending.SetResult(new FeedbackSnapshot(7, Snapshot(8).Tickets, DateTime.UtcNow));
                    await refresh.WithCancellation(timeout.Token);

                    Assert.AreEqual(8L, model.FeedbackTickets.Single().Id);
                    Assert.IsFalse(model.FeedbackRefreshState.IsCached);
                    Assert.IsFalse(model.IsRefreshingFeedback);
                });
            }
            finally
            {
                pending.TrySetCanceled();
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        [DataRow(0)]
        [DataRow(5)]
        [DataRow(20)]
        [DataRow(30)]
        public void Feedback_LimitsCachedAndFreshDisplayToTwentyNewestTickets(int count)
        {
            using var context = CreateContext();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = new TaskCompletionSource<FeedbackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var now = DateTime.UtcNow;
            var tickets = Enumerable.Range(1, count).Select(id => new FeedbackTicket
            {
                Id = id,
                Title = "Ticket",
                IsCreatedByUser = true,
                UpdatedUtc = id == 1 ? now : now.AddDays(-1),
            }).ToArray();
            var cached = new FeedbackSnapshot(7, tickets, now.AddDays(-3));
            var fresh = new FeedbackSnapshot(7, tickets.AsEnumerable().Reverse().ToArray(), now);
            var expected = count == 0 ? Array.Empty<long>()
                : new long[] { 1 }.Concat(Enumerable.Range(2, count - 1).Reverse().Select(id => (long)id)).Take(20).ToArray();
            using var model = Model(context, (_, _) => pending.Task, _ => Task.FromResult(cached));
            try
            {
                context.Factory.Run(async () =>
                {
                    Task refresh = model.RefreshFeedbackInBackgroundAsync();
                    CollectionAssert.AreEqual(expected, model.FeedbackTickets.Select(ticket => ticket.Id).ToArray());
                    Assert.IsTrue(model.FeedbackRefreshState.IsCached);

                    pending.SetResult(fresh);
                    await refresh.WithCancellation(timeout.Token);

                    CollectionAssert.AreEqual(expected, model.FeedbackTickets.Select(ticket => ticket.Id).ToArray());
                    Assert.IsFalse(model.FeedbackRefreshState.IsCached);
                });
            }
            finally
            {
                pending.TrySetCanceled();
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void StartupFailure_RetainsVerifiedCacheOnlyForTransientFailures(bool authentication)
        {
            using var context = CreateContext();
            var cached = Snapshot(7);
            Exception failure = authentication ? new UnauthorizedAccessException("Test sign-in failure") : new HttpRequestException("Test network failure");
            var model = Model(context, (_, _) => Task.FromException<FeedbackSnapshot>(failure), _ => Task.FromResult(cached));
            try
            {
                context.Factory.Run(() => model.RefreshFeedbackInBackgroundAsync());

                Assert.AreEqual(!authentication, model.FeedbackRefreshState.IsCached);
                Assert.AreEqual(!authentication, model.FeedbackRefreshState.IsStale);
                Assert.AreEqual(authentication, model.FeedbackRefreshState.IsUnavailable);
                Assert.AreEqual(authentication, model.FeedbackRequiresSignIn);
                Assert.IsFalse(model.IsRefreshingFeedback);
                if (authentication)
                {
                    Assert.IsEmpty(model.FeedbackTickets);
                    StringAssert.Contains(model.FeedbackStatus, "Sign in or reauthenticate");
                }
                else
                {
                    Assert.AreSame(cached.Tickets.Single(), model.FeedbackTickets.Single());
                    StringAssert.Contains(model.FeedbackStatus, "Showing cached feedback");
                }
            }
            finally
            {
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        public void FeedbackSignInRecovery_ClearsAfterSuccessfulRetryAndWhenHidden()
        {
            using var context = CreateContext();
            var fail = true;
            using var model = Model(context, (_, _) => fail
                ? Task.FromException<FeedbackSnapshot>(new UnauthorizedAccessException("Test reauthentication required"))
                : Task.FromResult(Snapshot(7)));
            context.Factory.Run(() => model.RefreshFeedbackAsync(true));
            Assert.IsTrue(model.FeedbackRequiresSignIn);

            fail = false;
            context.Factory.Run(() => model.RefreshFeedbackAsync(true));
            Assert.IsFalse(model.FeedbackRequiresSignIn);
            Assert.HasCount(1, model.FeedbackTickets);

            fail = true;
            context.Factory.Run(() => model.RefreshFeedbackAsync(true));
            Assert.IsTrue(model.FeedbackRequiresSignIn);
            model.CancelBackgroundWork();
            Assert.IsFalse(model.FeedbackRequiresSignIn);
        }

        [STATestMethod]
        public void CanceledCacheRead_CannotPublishAfterReopening()
        {
            using var context = CreateContext();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = new TaskCompletionSource<FeedbackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cacheReads = 0;
            var onlineRequests = 0;
            var model = Model(context, (_, _) =>
            {
                onlineRequests++;
                return Task.FromResult(Snapshot(8));
            }, _ => ++cacheReads == 1 ? pending.Task : Task.FromResult(Snapshot(8)));
            try
            {
                var previous = context.Factory.RunAsync(() => model.RefreshFeedbackInBackgroundAsync());
                model.CancelBackgroundWork();
                model.ActivateBackgroundWork();
                context.Factory.Run(() => model.RefreshFeedbackInBackgroundAsync());
                pending.SetResult(Snapshot(7));
                previous.Join(timeout.Token);

                Assert.AreEqual(8L, model.FeedbackTickets.Single().Id);
                Assert.AreEqual(1, onlineRequests, "A canceled cache read must not start another online request.");
                Assert.IsFalse(model.IsRefreshingFeedback);
            }
            finally
            {
                pending.TrySetCanceled();
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        public void Feedback_TabActivationSharesTheTrackedStartupRefresh()
        {
            using var context = CreateContext();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = new TaskCompletionSource<FeedbackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            var model = Model(context, (force, _) =>
            {
                Assert.IsFalse(force);
                requests++;
                return pending.Task;
            });
            try
            {
                context.Factory.Run(async () =>
                {
                    Task refresh = model.RefreshFeedbackInBackgroundAsync();
                    model.EnsureFeedbackLoaded();
                    Assert.AreSame(refresh, model.RefreshFeedbackInBackgroundAsync());

                    Assert.AreEqual(1, requests);
                    Assert.IsTrue(model.IsRefreshingFeedback);
                    Assert.IsFalse(refresh.IsCompleted);
                    pending.SetResult(Snapshot(7));
                    await refresh.WithCancellation(timeout.Token);

                    Assert.AreEqual(7L, model.FeedbackTickets.Single().Id);
                    Assert.IsFalse(model.IsRefreshingFeedback);
                });
            }
            finally
            {
                pending.TrySetCanceled();
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        public void Feedback_ReopeningCanResumeLoadingBeforeStartupRefreshRuns()
        {
            using var context = CreateContext();
            var requests = 0;
            var model = Model(context, (force, _) =>
            {
                Assert.IsFalse(force);
                return Task.FromResult(Snapshot(++requests));
            });
            try
            {
                context.Factory.Run(() => model.RefreshFeedbackInBackgroundAsync());
                model.CancelBackgroundWork();
                Assert.IsEmpty(model.FeedbackTickets);
                model.ActivateBackgroundWork();
                model.EnsureFeedbackLoaded();

                Assert.AreEqual(2, requests);
                Assert.AreEqual(2L, model.FeedbackTickets.Single().Id);
                Assert.IsFalse(model.IsRefreshingFeedback);
            }
            finally
            {
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        public void ForceRefreshFeedback_BypassesTheCacheAfterTabActivation()
        {
            using var context = CreateContext();
            var requests = new List<bool>();
            var model = Model(context, (force, _) =>
            {
                requests.Add(force);
                return Task.FromResult(Snapshot(requests.Count));
            });
            try
            {
                model.EnsureFeedbackLoaded();
                Assert.AreEqual(1L, model.FeedbackTickets.Single().Id);

                model.EnsureFeedbackLoaded();
                model.ForceRefreshFeedback();
                model.EnsureFeedbackLoaded();

                CollectionAssert.AreEqual(new[] { false, true }, requests);
                Assert.AreEqual(2L, model.FeedbackTickets.Single().Id);
                Assert.IsFalse(model.IsRefreshingFeedback);
            }
            finally
            {
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        public void Refresh_ClearsPreviousAccountWhileLoadingAndTracksUnifiedProgress()
        {
            using var context = CreateContext();
            var pending = new TaskCompletionSource<FeedbackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            var forced = false;
            var model = Model(context, (force, _) =>
            {
                forced = force;
                return ++requests == 1 ? Task.FromResult(Snapshot(7)) : pending.Task;
            });
            try
            {
                context.Factory.Run(() => model.RefreshFeedbackAsync(false));
                Assert.AreEqual(7L, model.FeedbackTickets.Single().Id);

                var refresh = context.Factory.RunAsync(() => model.RefreshFeedbackAsync(true));

                Assert.IsTrue(forced);
                Assert.IsEmpty(model.FeedbackTickets);
                Assert.IsTrue(model.IsRefreshingFeedback);
                Assert.IsTrue(model.IsRefreshingAny);
                Assert.AreEqual("Feedback loading...", model.FeedbackStatus);
                Assert.AreEqual("Refreshing feedback...", model.UnifiedRefreshStatus);
                StringAssert.Contains(model.UnifiedRefreshDetails, "feedback");

                pending.SetResult(Snapshot(8));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                refresh.Join(timeout.Token);

                Assert.AreEqual(8L, model.FeedbackTickets.Single().Id);
                Assert.IsFalse(model.IsRefreshingAny);
            }
            finally
            {
                pending.TrySetCanceled();
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void Failure_IsVisibleAndDoesNotRetainPreviousTickets(bool authentication)
        {
            using var context = CreateContext();
            Exception failure = authentication ? new UnauthorizedAccessException("Test sign-in failure") : new HttpRequestException("Test network failure");
            var requests = 0;
            var model = Model(context, (_, _) => ++requests == 1 ? Task.FromResult(Snapshot(7)) : Task.FromException<FeedbackSnapshot>(failure));
            try
            {
                context.Factory.Run(() => model.RefreshFeedbackAsync(false));
                context.Factory.Run(() => model.RefreshFeedbackAsync(true));

                Assert.IsEmpty(model.FeedbackTickets);
                Assert.IsFalse(model.IsRefreshingAny);
                StringAssert.Contains(model.FeedbackStatus, authentication ? "Sign in or reauthenticate" : "unavailable");
                StringAssert.Contains(model.FeedbackStatus, "Refresh");
                Assert.DoesNotContain("No created", model.FeedbackStatus);
            }
            finally
            {
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        public void EmptySuccess_IsDistinctFromAFailedLoad()
        {
            using var context = CreateContext();
            var model = Model(context, (_, _) => Task.FromResult(new FeedbackSnapshot(7, [], DateTime.UtcNow)));
            try
            {
                context.Factory.Run(() => model.RefreshFeedbackAsync(false));

                Assert.IsEmpty(model.FeedbackTickets);
                Assert.AreEqual("No created or currently upvoted tickets found for this Visual Studio account.", model.FeedbackStatus);
                Assert.IsFalse(model.IsRefreshingAny);
            }
            finally
            {
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void CanceledRefresh_CannotOverwriteANewerAccount(bool hideAndReopen)
        {
            using var context = CreateContext();
            var pending = new TaskCompletionSource<FeedbackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            CancellationToken oldToken = default;
            var model = Model(context, (_, token) =>
            {
                if (++requests == 1)
                {
                    oldToken = token;
                    return pending.Task;
                }
                return Task.FromResult(Snapshot(8));
            });
            try
            {
                var old = context.Factory.RunAsync(() => model.RefreshFeedbackAsync(false));
                if (hideAndReopen)
                {
                    model.CancelBackgroundWork();
                    Assert.IsTrue(oldToken.IsCancellationRequested);
                    Assert.IsEmpty(model.FeedbackTickets);
                    model.ActivateBackgroundWork();
                }
                context.Factory.Run(() => model.RefreshFeedbackAsync(true));
                Assert.IsTrue(oldToken.IsCancellationRequested);
                pending.SetResult(Snapshot(7));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                old.Join(timeout.Token);

                Assert.AreEqual(8L, model.FeedbackTickets.Single().Id);
                Assert.IsFalse(model.IsRefreshingFeedback);
            }
            finally
            {
                pending.TrySetCanceled();
                model.CancelBackgroundWork();
            }
        }

        [TestMethod]
        public void FeedbackTab_BindsStatusAndTicketsAndUsesKeyboardAccessibleThemedLinks()
        {
            using var stream = typeof(FeedbackViewModelTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var tab = document.Descendants(p + "TabItem").Single(item => item.Attribute(x + "Name")?.Value == "FeedbackTab");

            Assert.AreEqual("Feedback", tab.Attribute("Header")?.Value);
            Assert.AreEqual("{StaticResource NewsTabItemStyle}", tab.Attribute("Style")?.Value);
            Assert.AreEqual("{Binding FeedbackStatus}", tab.Element(p + "StackPanel")!.Elements(p + "TextBlock").First().Attribute("Text")?.Value);
            Assert.IsTrue(tab.Descendants(p + "TextBlock").Any(item => item.Attribute("Text")?.Value == "{Binding FeedbackStatus}"));
            Assert.AreEqual("{Binding FeedbackTickets}", tab.Descendants(p + "ItemsControl").Single().Attribute("ItemsSource")?.Value);
            var recovery = tab.Descendants(p + "Hyperlink").Single(item => item.Attribute("Click")?.Value == "OpenAccountSettings_Click");
            Assert.AreEqual("{Binding FeedbackRequiresSignIn, Converter={StaticResource BoolToVisibilityConverter}}",
                recovery.Parent!.Attribute("Visibility")?.Value);
            Assert.AreEqual("{DynamicResource {x:Static platformui:EnvironmentColors.ControlLinkTextBrushKey}}", recovery.Attribute("Foreground")?.Value);
            var link = tab.Descendants(p + "Hyperlink").Single(item => item.Attribute("NavigateUri") != null);
            Assert.AreEqual("{Binding Url}", link.Attribute("NavigateUri")?.Value);
            Assert.AreEqual("FeedbackTicket_RequestNavigate", link.Attribute("RequestNavigate")?.Value);
            Assert.AreEqual("{DynamicResource {x:Static platformui:EnvironmentColors.ToolWindowTextBrushKey}}", link.Attribute("Foreground")?.Value);
            Assert.AreEqual("None", link.Attribute("TextDecorations")?.Value);
            Assert.IsNull(link.Attribute("Focusable"), "Links must retain WPF keyboard focus behavior.");
        }

        [STATestMethod]
        [DataRow(null, Visibility.Collapsed)]
        [DataRow("Feedback loading...", Visibility.Visible)]
        [DataRow("Developer Community is unavailable. Use Refresh to retry.", Visibility.Visible)]
        [DataRow("No created or currently upvoted tickets found for this Visual Studio account.", Visibility.Visible)]
        public void FeedbackStatus_CollapsesTheSummarySpaceButKeepsInformativeMessages(string? status, Visibility expected)
        {
            using var stream = typeof(FeedbackViewModelTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XNamespace controls = "clr-namespace:StartScreen.ToolWindows.Controls;assembly=StartScreen";
            var definition = new XElement(document.Descendants(p + "TextBlock")
                .Single(element => element.Attribute("Text")?.Value == "{Binding FeedbackStatus}"));
            var root = (StackPanel)XamlReader.Parse(new XElement(p + "StackPanel",
                new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(p + "StackPanel.Resources",
                    new XElement(controls + "NullToCollapsedConverter", new XAttribute(x + "Key", "NullToCollapsedConverter"))),
                definition).ToString());
            var text = (TextBlock)root.Children[0];
            var source = new { FeedbackStatus = status };
            foreach (var property in new[] { TextBlock.TextProperty, UIElement.VisibilityProperty })
            {
                var binding = BindingOperations.GetBinding(text, property)!;
                BindingOperations.SetBinding(text, property, new Binding(binding.Path.Path)
                {
                    Source = source,
                    Converter = binding.Converter,
                    Mode = BindingMode.OneWay,
                }).UpdateTarget();
            }
            text.Measure(new Size(330, double.PositiveInfinity));

            Assert.AreEqual(expected, text.Visibility);
            Assert.AreEqual(status ?? string.Empty, text.Text);
            if (expected == Visibility.Collapsed)
                Assert.AreEqual(new Size(0, 0), text.DesiredSize);
            else
                Assert.IsGreaterThan(0d, text.DesiredSize.Height);
        }

        [STATestMethod]
        [DataRow("under review", "Under Review", "2E6FC2", FeedbackTicketType.Problem)]
        [DataRow("new", "New", "2E6FC2", FeedbackTicketType.Problem)]
        [DataRow("triaged", "Triaged", "2E6FC2", FeedbackTicketType.Problem)]
        [DataRow("pending", "Fixed - Pending Release", "2E7D1C", FeedbackTicketType.Problem)]
        [DataRow("pending", "Completed - Preview", "2E7D1C", FeedbackTicketType.Suggestion)]
        [DataRow("consideration", "Under Consideration", "2E6FC2", FeedbackTicketType.Problem)]
        [DataRow("consideration", "Under Review", "2E6FC2", FeedbackTicketType.Suggestion)]
        [DataRow("fixed", "Completed - Release", "2E7D1C", FeedbackTicketType.Suggestion)]
        [DataRow("investigation", "Under Investigation", "925900", FeedbackTicketType.Problem)]
        [DataRow("roadmap", "On Roadmap", "925900", FeedbackTicketType.Problem)]
        [DataRow("duplicate", "Closed - Duplicate", "606060", FeedbackTicketType.Problem)]
        [DataRow("notenoughinfo", "Not enough info", "DC143C", FeedbackTicketType.Problem)]
        public void FeedbackCard_RendersBoundTitleDetailsAndBrowserLink(string apiState, string stateText, string color, FeedbackTicketType type)
        {
            using var stream = typeof(FeedbackViewModelTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var tab = document.Descendants(p + "TabItem").Single(item => item.Attribute(x + "Name")?.Value == "FeedbackTab");
            var definition = new XElement(tab.Descendants(p + "DataTemplate").Single().Element(p + "Grid")!.Element(p + "StackPanel")!);
            definition.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            XNamespace controls = "clr-namespace:StartScreen.ToolWindows.Controls;assembly=StartScreen";
            definition.AddFirst(new XElement(p + "StackPanel.Resources",
                new XElement(controls + "HexColorToBrushConverter", new XAttribute(x + "Key", "FeedbackStateBrushConverter")),
                new XElement(controls + "HexColorToForegroundConverter", new XAttribute(x + "Key", "FeedbackStateForegroundConverter"))));
            var linkDefinition = definition.Descendants(p + "Hyperlink").Single();
            linkDefinition.Attribute("RequestNavigate")!.Remove();
            linkDefinition.SetAttributeValue("Foreground", "{DynamicResource FeedbackTitleBrush}");
            var card = (StackPanel)XamlReader.Parse(definition.ToString());
            card.Resources[SystemParameters.HighContrastKey] = false;
            card.Resources["FeedbackTitleBrush"] = SystemColors.WindowTextBrush;
            var ticket = new FeedbackTicket
            {
                Id = 42,
                Title = "A & B",
                Url = "https://developercommunity.visualstudio.com/t/test/42",
                State = apiState,
                Type = type,
                Votes = 12,
                IsUpvotedByUser = true,
                UpdatedUtc = DateTime.UtcNow,
            };
            card.DataContext = ticket;
            card.Measure(new Size(330, double.PositiveInfinity));
            card.Arrange(new Rect(0, 0, 330, card.DesiredSize.Height));
            card.UpdateLayout();
            var title = (TextBlock)card.Children[0];
            var link = title.Inlines.OfType<Hyperlink>().Single();
            var run = link.Inlines.OfType<Run>().Single();
            BindingOperations.GetBindingExpression(run, Run.TextProperty)!.UpdateTarget();
            BindingOperations.GetBindingExpression(link, Hyperlink.NavigateUriProperty)!.UpdateTarget();

            Assert.AreEqual(ticket.Title, run.Text);
            Assert.AreEqual(ticket.Url, link.NavigateUri.AbsoluteUri);
            Assert.IsTrue(link.Focusable);
            Assert.AreSame(SystemColors.WindowTextBrush, link.Foreground);
            Assert.IsEmpty(link.TextDecorations);
            var metadata = (WrapPanel)card.Children[1];
            var pill = (Border)metadata.Children[0];
            var state = (TextBlock)pill.Child;
            Assert.AreEqual(stateText, state.Text);
            Assert.AreEqual(new CornerRadius(9), pill.CornerRadius);
            Assert.AreEqual((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#" + color),
                ((System.Windows.Media.SolidColorBrush)pill.Background).Color);
            var details = (TextBlock)metadata.Children[1];
            var date = (TextBlock)card.Children[2];
            Assert.AreEqual("Upvoted by you - 12 votes", details.Text);
            Assert.AreEqual(ticket.UpdatedText, date.Text);
            Assert.AreEqual(ticket.ToolTip, date.ToolTip);
            Assert.AreEqual(HorizontalAlignment.Stretch, card.HorizontalAlignment);
            Assert.AreEqual(HorizontalAlignment.Right, date.HorizontalAlignment);
            Assert.AreEqual(card.ActualWidth, date.TranslatePoint(new Point(date.ActualWidth, 0), card).X, 0.01);
            Assert.AreEqual(11d, details.FontSize);
            Assert.AreEqual(date.FontSize, details.FontSize);
            Assert.AreEqual(0.65, details.Opacity);
            Assert.AreEqual(date.Opacity, details.Opacity);

            card.Resources["FeedbackTitleBrush"] = SystemColors.HighlightTextBrush;
            Assert.AreSame(SystemColors.HighlightTextBrush, link.Foreground);

            card.Resources[SystemParameters.HighContrastKey] = true;
            card.UpdateLayout();
            Assert.AreSame(SystemColors.WindowBrush, pill.Background);
            Assert.AreSame(SystemColors.WindowTextBrush, pill.BorderBrush);
            Assert.AreSame(SystemColors.WindowTextBrush, state.Foreground);
            card.Resources[SystemColors.WindowBrushKey] = SystemColors.HighlightBrush;
            card.Resources[SystemColors.WindowTextBrushKey] = SystemColors.HighlightTextBrush;
            Assert.AreSame(SystemColors.HighlightBrush, pill.Background);
            Assert.AreSame(SystemColors.HighlightTextBrush, pill.BorderBrush);
            Assert.AreSame(SystemColors.HighlightTextBrush, state.Foreground);
        }

        [TestMethod]
        [DataRow("Fixed", "2E7D1C")]
        [DataRow("Closed - Fixed", "2E7D1C")]
        [DataRow("closed_fixed", "2E7D1C")]
        [DataRow("Completed - Release", "2E7D1C")]
        [DataRow("Under Consideration", "2E6FC2")]
        [DataRow("under_review", "2E6FC2")]
        [DataRow("UnderReview", "2E6FC2")]
        [DataRow("new", "2E6FC2")]
        [DataRow("triaged", "2E6FC2")]
        [DataRow("pending", "2E7D1C")]
        [DataRow("pending_release", "2E7D1C")]
        [DataRow("Fixed - Pending Release", "2E7D1C")]
        [DataRow("consideration", "2E6FC2")]
        [DataRow("Need More Info", "DC143C")]
        [DataRow("Needs More Information", "DC143C")]
        [DataRow("notenoughinfo", "DC143C")]
        [DataRow("Not Enough Info", "DC143C")]
        [DataRow("Closed - Out Of Scope", "606060")]
        [DataRow("Closed - Duplicate", "606060")]
        [DataRow("duplicate", "606060")]
        [DataRow("Under Investigation", "925900")]
        [DataRow("On Roadmap", "925900")]
        [DataRow("investigation", "925900")]
        [DataRow("roadmap", "925900")]
        [DataRow("Unknown status", "606060")]
        [DataRow("", "606060")]
        public void FeedbackStates_UseTheRequestedSemanticPalette(string state, string color)
        {
            var ticket = new FeedbackTicket { State = state };

            Assert.AreEqual(color, ticket.StateColor);
            Assert.AreEqual(state, ticket.State);
        }

        private static JoinableTaskContext CreateContext() => new JoinableTaskContext(Thread.CurrentThread,
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        private static StartScreenViewModel Model(JoinableTaskContext context, Func<bool, CancellationToken, Task<FeedbackSnapshot>> getFeedbackAsync,
            Func<CancellationToken, Task<FeedbackSnapshot>>? getCachedFeedbackAsync = null) =>
            new StartScreenViewModel(CancellationToken.None, getFeedbackAsync: getFeedbackAsync, getFeedbackTaskFactory: () => context.Factory,
                getCachedFeedbackAsync: getCachedFeedbackAsync);

        private static FeedbackSnapshot Snapshot(long id) => new FeedbackSnapshot(id,
            new[] { new FeedbackTicket { Id = id, Title = "Ticket", IsCreatedByUser = true } }, DateTime.UtcNow);
    }
}