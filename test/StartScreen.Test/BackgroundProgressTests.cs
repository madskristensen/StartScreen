using System.Windows.Threading;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.Services;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class BackgroundProgressTests
    {
        [TestMethod]
        public void Reset_WhenAlreadyIdleDoesNotNotifyBindings()
        {
            var progress = new BackgroundProgress();
            var changes = 0;
            progress.Changed += (_, _) => changes++;
            progress.Reset();
            Assert.AreEqual(0, changes);
            using var operation = progress.Start();
            operation.Dispose();
            progress.Reset();
            var afterReset = changes;
            progress.Reset();
            Assert.AreEqual(afterReset, changes);
        }

        [TestMethod]
        public void OverlappingWork_RetainsFinishedItemsUntilTheBatchEnds()
        {
            var progress = new BackgroundProgress();
            using var activity = progress.Start(4);
            activity.Report((1, 4));
            using var news = progress.Start();
            Assert.AreEqual(20d, progress.PercentComplete);
            news.Dispose();
            Assert.IsTrue(progress.IsBusy);
            Assert.AreEqual(40d, progress.PercentComplete);
            Assert.AreEqual("2 of 5 work items finished.", progress.Details);
            activity.Report((4, 4));
            activity.Dispose();
            Assert.IsFalse(progress.IsBusy);
            Assert.AreEqual(100d, progress.PercentComplete);

            using var next = progress.Start(2);
            Assert.AreEqual(0d, progress.PercentComplete);
            Assert.AreEqual("0 of 2 work items finished.", progress.Details);
        }

        [TestMethod]
        public void DiscoveredWork_ExpandsTheRealTotalWithoutInventingCompletion()
        {
            var progress = new BackgroundProgress();
            using var activity = progress.Start(4);
            activity.Report((2, 4));
            Assert.AreEqual(50d, progress.PercentComplete);
            activity.Report((2, 8));
            Assert.AreEqual(25d, progress.PercentComplete);
            Assert.AreEqual("2 of 8 work items finished.", progress.Details);
            activity.Report((1, 4));
            Assert.AreEqual(25d, progress.PercentComplete);
            activity.Report((8, 8));
            Assert.AreEqual(100d, progress.PercentComplete);
        }

        [TestMethod]
        public void StoppedDetailedWork_DoesNotFinishItsUnprocessedRequests()
        {
            var progress = new BackgroundProgress();
            using var canceled = progress.Start(5);
            canceled.Report((2, 5));
            using var pending = progress.Start(4);
            pending.Report((1, 4));
            canceled.Dispose();
            Assert.IsTrue(progress.IsBusy);
            Assert.AreEqual("3 of 6 work items finished.", progress.Details);
            Assert.AreEqual(50d, progress.PercentComplete);
        }

        [TestMethod]
        public void Reset_IgnoresLateReportsAndDisposalFromThePreviousBatch()
        {
            var progress = new BackgroundProgress();
            using var previous = progress.Start(10);
            previous.Report((4, 10));
            progress.Reset();
            using var current = progress.Start(4);
            current.Report((1, 4));
            previous.Report((10, 10));
            previous.Complete();
            previous.Dispose();
            Assert.IsTrue(progress.IsBusy);
            Assert.AreEqual(25d, progress.PercentComplete);
            Assert.AreEqual("1 of 4 work items finished.", progress.Details);
        }

        [TestMethod]
        public async Task ParallelReports_AggregateActualCountsSafelyAsync()
        {
            var progress = new BackgroundProgress();
            var operations = Enumerable.Range(0, 8).Select(_ => progress.Start(100)).ToArray();
            await Task.WhenAll(operations.Select(operation => Task.Run(() =>
            {
                for (var item = 1; item <= 100; item++)
                    operation.Report((item, 100));
                operation.Dispose();
            })));
            Assert.IsFalse(progress.IsBusy);
            Assert.AreEqual(100d, progress.PercentComplete);
            Assert.AreEqual("800 of 800 work items finished.", progress.Details);
        }

        [TestMethod]
        public void InvalidCounts_AreRejectedExplicitly()
        {
            var progress = new BackgroundProgress();
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => progress.Start(0));
            using var operation = progress.Start();
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => operation.Report((-1, 2)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => operation.Report((3, 2)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => operation.Report((0, 0)));
        }

        [TestMethod]
        public void ViewModel_CombinesSectionsAndDetailedActivityAndNotifiesBindings()
        {
            using var model = new StartScreenViewModel();
            var changes = new List<string?>();
            model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            using var activity = model.BackgroundProgress.Start(4);
            activity.Report((1, 4));
            model.IsRefreshingNews = true;
            Assert.AreEqual(20d, model.RefreshProgressValue);
            model.IsRefreshingNews = false;
            Assert.AreEqual(40d, model.RefreshProgressValue);
            model.IsRefreshingYouTube = true;
            Assert.AreEqual(200d / 6, model.RefreshProgressValue, 0.001);
            model.IsRefreshingYouTube = false;
            activity.Complete();
            Assert.IsFalse(model.IsRefreshProgressActive);
            Assert.AreEqual(100d, model.RefreshProgressValue);
            CollectionAssert.Contains(changes, nameof(StartScreenViewModel.RefreshProgressValue));
            CollectionAssert.Contains(changes, nameof(StartScreenViewModel.IsRefreshProgressActive));
            CollectionAssert.Contains(changes, nameof(StartScreenViewModel.RefreshProgressDetails));
        }

        [STATestMethod]
        public void FeedbackAndServiceHealth_CountTheirActualAsyncCompletion()
        {
            using var context = new JoinableTaskContext(Thread.CurrentThread,
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var feedback = new TaskCompletionSource<FeedbackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var health = new TaskCompletionSource<ServiceHealthStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var model = new StartScreenViewModel(CancellationToken.None,
                _ => health.Task, () => context.Factory,
                getFeedbackAsync: (_, _) => feedback.Task, getFeedbackTaskFactory: () => context.Factory);
            using var activity = model.BackgroundProgress.Start(4);
            activity.Report((1, 4));
            var feedbackRefresh = context.Factory.RunAsync(() => model.RefreshFeedbackAsync(false));
            var healthRefresh = context.Factory.RunAsync(() => model.RefreshServiceHealthAsync(CancellationToken.None));
            Assert.IsTrue(model.IsRefreshProgressActive);
            Assert.AreEqual(100d / 6, model.RefreshProgressValue, 0.001);
            feedback.SetResult(new FeedbackSnapshot(1, [], DateTime.UtcNow));
            var now = DateTimeOffset.UtcNow;
            health.SetResult(new ServiceHealthStatus(ServiceHealthState.Healthy, "Operational", now, now));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            feedbackRefresh.Join(timeout.Token);
            healthRefresh.Join(timeout.Token);
            Assert.IsFalse(model.IsRefreshingFeedback);
            Assert.IsTrue(model.IsRefreshProgressActive);
            Assert.AreEqual(50d, model.RefreshProgressValue);
            activity.Complete();
            Assert.IsFalse(model.IsRefreshProgressActive);
            Assert.AreEqual(100d, model.RefreshProgressValue);
        }

        [TestMethod]
        public void ViewModel_CancellationClearsTheBarAndCannotReviveOldWork()
        {
            using var model = new StartScreenViewModel();
            using var activity = model.BackgroundProgress.Start(4);
            model.IsRefreshingNews = true;
            model.CancelBackgroundWork();
            activity.Report((4, 4));
            Assert.IsFalse(model.IsRefreshProgressActive);
            Assert.AreEqual(0d, model.RefreshProgressValue);
            model.ActivateBackgroundWork();
            using var next = model.BackgroundProgress.Start(4);
            next.Report((1, 4));
            activity.Dispose();
            Assert.IsTrue(model.IsRefreshProgressActive);
            Assert.AreEqual(25d, model.RefreshProgressValue);
        }
    }
}