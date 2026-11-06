using System.Reflection;
using System.Windows.Threading;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class RefreshStateAndLifecycleTests
    {
        [TestMethod]
        public void RepeatedInstances_DisposeRemovesFeedSubscriptionAndCannotReactivate()
        {
            for (var i = 0; i < 5; i++)
            {
                var model = new StartScreenViewModel();
                using var timer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
                var timerField = typeof(StartScreenViewModel).GetField("_autoRefreshTimer",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                timerField.SetValue(model, timer);
                Assert.IsTrue(FeedHandlers().Any(handler => ReferenceEquals(handler.Target, model)));
                CancellationToken active = model.GetBackgroundCancellationToken();
                model.CancelBackgroundWork();
                model.ActivateBackgroundWork();
                Assert.IsTrue(active.IsCancellationRequested);
                Assert.IsFalse(model.GetBackgroundCancellationToken().IsCancellationRequested);

                model.Dispose();
                model.Dispose();
                model.CancelBackgroundWork();
                model.ActivateBackgroundWork();
                model.ForceRefreshNews();
                model.ForceRefreshYouTube();

                Assert.IsTrue(model.GetBackgroundCancellationToken().IsCancellationRequested);
                Assert.IsFalse(model.IsRefreshingAny);
                Assert.IsFalse(FeedHandlers().Any(handler => ReferenceEquals(handler.Target, model)));
                Assert.IsNull(timerField.GetValue(model));
                Assert.ThrowsExactly<ObjectDisposedException>(() => timer.Change(Timeout.Infinite, Timeout.Infinite));
            }
        }

        [STATestMethod]
        public void Dispose_StopsAndReleasesScheduledServiceTimerAndSelectedItemSubscription()
        {
            using var context = new JoinableTaskContext(Thread.CurrentThread,
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var now = DateTimeOffset.UtcNow;
            using var model = new StartScreenViewModel(CancellationToken.None,
                _ => Task.FromResult(new ServiceHealthStatus(ServiceHealthState.Healthy, "Operational", now, now)),
                () => context.Factory);
            var item = new MruItem { Name = "Before" };
            model.SelectedMruItem = item;
            context.Factory.Run(() => model.RefreshServiceHealthAsync(CancellationToken.None));
            var timerField = typeof(StartScreenViewModel).GetField("_serviceHealthRefreshTimer",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var timer = (DispatcherTimer)timerField.GetValue(model)!;
            Assert.IsTrue(timer.IsEnabled);
            Assert.AreEqual(now, model.WorkspaceRefreshState.LastSuccessfulRefreshUtc);

            model.Dispose();
            var changed = new List<string?>();
            model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
            item.Name = "After";
            model.SelectedMruItem = new MruItem { Name = "Must not resubscribe" };
            model.ActivateBackgroundWork();

            Assert.IsEmpty(changed);
            Assert.IsFalse(timer.IsEnabled);
            Assert.IsNull(timerField.GetValue(model));
            Assert.IsFalse(model.IsRefreshingAny);
            Assert.AreSame(item, model.SelectedMruItem);
        }

        [STATestMethod]
        public void DisposeDuringRefresh_CancelsButKeepsSourceAliveUntilOperationCompletes()
        {
            using var context = new JoinableTaskContext(Thread.CurrentThread,
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var pending = new TaskCompletionSource<FeedbackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken requestToken = default;
            using var model = new StartScreenViewModel(CancellationToken.None,
                getFeedbackAsync: (_, token) => { requestToken = token; return pending.Task; },
                getFeedbackTaskFactory: () => context.Factory);
            var refresh = context.Factory.RunAsync(() => model.RefreshFeedbackAsync(false));
            Assert.IsTrue(model.IsRefreshingFeedback);

            model.Dispose();
            Assert.IsTrue(requestToken.IsCancellationRequested);
            pending.SetResult(new FeedbackSnapshot(1, [], DateTime.UtcNow));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            refresh.Join(timeout.Token);

            Assert.IsEmpty(model.FeedbackTickets);
            Assert.IsFalse(model.IsRefreshingFeedback);
            Assert.IsNull(typeof(StartScreenViewModel).GetField("_feedbackRefreshCancellation",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model));
        }

        [TestMethod]
        public void Footer_UsesLatestSuccessfulTimestampAcrossMixedSectionsAndRetainsItOnFailure()
        {
            using var model = new StartScreenViewModel();
            var earlier = DateTimeOffset.UtcNow.AddHours(-2);
            var latest = earlier.AddHours(1);
            model.SetRefreshState(model.DevHubRefreshState, "Arbitrary text", successfulUtc: earlier);
            model.SetRefreshState(model.NewsRefreshState, "No English update prefix", successfulUtc: latest);
            model.SetRefreshState(model.VideoRefreshState, "Updated just now", cached: true, stale: true);
            model.SetRefreshState(model.FeedbackRefreshState, "Updated just now", unavailable: true);
            model.SetRefreshState(model.WorkspaceRefreshState, "Partial result", partial: true);

            Assert.AreEqual($"Updated at {latest.ToLocalTime():t}", model.UnifiedRefreshStatus);
            model.SetRefreshState(model.NewsRefreshState, "Refresh failed", cached: true, stale: true);
            Assert.AreEqual(latest, model.NewsRefreshState.LastSuccessfulRefreshUtc);
            Assert.AreEqual($"Updated at {latest.ToLocalTime():t}", model.UnifiedRefreshStatus);
            Assert.IsTrue(model.VideoRefreshState.IsCached);
            Assert.IsTrue(model.VideoRefreshState.IsStale);
            Assert.IsTrue(model.FeedbackRefreshState.IsUnavailable);
            Assert.IsTrue(model.WorkspaceRefreshState.IsPartial);
            StringAssert.Contains(model.UnifiedRefreshDetails, "Workspace: Partial result");
        }

        [TestMethod]
        public void Footer_DoesNotParseBindingStringsOrFailureMessagesAsSuccess()
        {
            using var model = new StartScreenViewModel
            {
                LastNewsRefreshText = "Updated just now",
                LastYouTubeRefreshText = "Updated 1 minute ago",
                LastDevHubRefreshText = "Updated just now",
            };
            Assert.AreEqual(string.Empty, model.UnifiedRefreshStatus);
            model.SetRefreshState(model.NewsRefreshState, "Updated just now", unavailable: true);
            Assert.AreEqual("Not updated yet", model.UnifiedRefreshStatus);
            model.IsRefreshingNews = true;
            Assert.IsTrue(model.NewsRefreshState.IsLoading);
            Assert.AreEqual("Updating...", model.UnifiedRefreshStatus);
            model.IsRefreshingNews = false;
            Assert.AreEqual("Not updated yet", model.UnifiedRefreshStatus);
        }

        [TestMethod]
        public void DevHub_CachedPartialAndUnavailableNeverInventSuccessfulRefreshTime()
        {
            using var model = new StartScreenViewModel();
            var dashboard = new DevHubDashboard { FetchedAt = DateTime.UtcNow };
            model.SetDevHubRefreshState(dashboard, cached: true);
            Assert.IsTrue(model.DevHubRefreshState.IsCached);
            Assert.IsNull(model.DevHubRefreshState.LastSuccessfulRefreshUtc);
            model.SetDevHubRefreshState(dashboard, error: "Updated just now");
            Assert.IsTrue(model.DevHubRefreshState.IsUnavailable);
            Assert.AreEqual("Not updated yet", model.UnifiedRefreshStatus);
            dashboard.Sources["test"] = new DevHubActivitySnapshot { Status = DevHubActivityStatus.Partial };
            model.SetDevHubRefreshState(dashboard);
            Assert.IsTrue(model.DevHubRefreshState.IsPartial);
            Assert.IsTrue(model.DevHubRefreshState.IsStale);
            Assert.IsNull(model.DevHubRefreshState.LastSuccessfulRefreshUtc);
            dashboard.IsRefreshing = true;
            model.SetDevHubRefreshState(dashboard);
            Assert.AreEqual("Updating...", model.UnifiedRefreshStatus);
            dashboard.IsRefreshing = false;
            dashboard.Sources.Clear();
            model.SetDevHubRefreshState(dashboard);
            Assert.IsNotNull(model.DevHubRefreshState.LastSuccessfulRefreshUtc);
            Assert.AreEqual("Updated just now", model.UnifiedRefreshStatus);
        }

        private static Delegate[] FeedHandlers()
        {
            return ((Delegate?)typeof(FeedStore).GetField("FeedsChanged", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null))?.GetInvocationList() ?? [];
        }
    }
}