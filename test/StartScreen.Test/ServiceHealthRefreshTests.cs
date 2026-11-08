using System.Reflection;
using System.Windows.Threading;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class ServiceHealthRefreshTests
    {
        [STATestMethod]
        [DataRow((int)ServiceHealthState.Healthy)]
        [DataRow((int)ServiceHealthState.Incident)]
        [DataRow((int)ServiceHealthState.Unknown)]
        public void InitialRefresh_GreenIsVisibleBeforeCompletionAndOnlyChangesForNonGreen(int stateValue)
        {
            using var context = CreateContext();
            var pending = new TaskCompletionSource<ServiceHealthStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var model = new StartScreenViewModel(CancellationToken.None, _ => pending.Task, () => context.Factory);
            var changed = new List<string?>();
            model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            try
            {
                var refresh = context.Factory.RunAsync(() => model.RefreshServiceHealthAsync(CancellationToken.None));
                Assert.IsFalse(refresh.IsCompleted);
                Assert.IsTrue(model.HasServiceHealthStatus);
                Assert.IsTrue(model.IsServiceHealthy);
                Assert.AreEqual("Service operational", model.ServiceHealthText);
                Assert.DoesNotContain(nameof(StartScreenViewModel.ServiceHealthText), changed);
                Assert.DoesNotContain(nameof(StartScreenViewModel.IsServiceHealthy), changed);

                var status = Status((ServiceHealthState)stateValue);
                pending.SetResult(status);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                refresh.Join(timeout.Token);

                Assert.IsTrue(model.HasServiceHealthStatus);
                Assert.AreEqual(stateValue == (int)ServiceHealthState.Healthy, model.IsServiceHealthy);
                Assert.AreEqual(status.StatusText, model.ServiceHealthText);
                Assert.AreEqual(stateValue != (int)ServiceHealthState.Healthy,
                    changed.Contains(nameof(StartScreenViewModel.ServiceHealthText)));
                Assert.DoesNotContain(nameof(StartScreenViewModel.HasServiceHealthStatus), changed);
            }
            finally
            {
                pending.TrySetCanceled();
                model.Dispose();
            }
        }

        [STATestMethod]
        [DataRow((int)ServiceHealthState.Healthy)]
        [DataRow((int)ServiceHealthState.Incident)]
        [DataRow((int)ServiceHealthState.Unknown)]
        public void Refresh_UnchangedStatusStaysVisibleWithoutNotifications(int stateValue)
        {
            using var context = CreateContext();
            var state = (ServiceHealthState)stateValue;
            var status = Status(state);
            var pending = new TaskCompletionSource<ServiceHealthStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            var model = new StartScreenViewModel(CancellationToken.None,
                _ => ++requests == 1 ? Task.FromResult(status) : pending.Task, () => context.Factory);
            try
            {
                context.Factory.Run(() => model.RefreshServiceHealthAsync(CancellationToken.None));
                var changed = new List<string?>();
                model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(StartScreenViewModel.UnifiedRefreshStatus)
                        && e.PropertyName != nameof(StartScreenViewModel.UnifiedRefreshDetails)
                        && e.PropertyName != nameof(StartScreenViewModel.IsRefreshingAny)
                        && e.PropertyName != nameof(StartScreenViewModel.IsRefreshProgressActive)
                        && e.PropertyName != nameof(StartScreenViewModel.RefreshProgressValue)
                        && e.PropertyName != nameof(StartScreenViewModel.RefreshProgressDetails))
                        changed.Add(e.PropertyName);
                };

                var refresh = context.Factory.RunAsync(() => model.RefreshServiceHealthAsync(CancellationToken.None));

                Assert.IsFalse(refresh.IsCompleted);
                Assert.IsTrue(model.HasServiceHealthStatus);
                Assert.AreEqual(state == ServiceHealthState.Healthy, model.IsServiceHealthy);
                Assert.AreEqual(status.StatusText, model.ServiceHealthText);
                Assert.IsEmpty(changed);

                pending.SetResult(status);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                refresh.Join(timeout.Token);

                Assert.IsTrue(model.HasServiceHealthStatus);
                Assert.IsEmpty(changed);
                Assert.AreEqual(2, requests);
            }
            finally
            {
                pending.TrySetCanceled();
                model.CancelBackgroundWork();
            }
        }

        [TestMethod]
        public void SetServiceHealth_OnlyNotifiesChangedTooltipDetails()
        {
            var model = new StartScreenViewModel();
            model.SetServiceHealth("Operational", "Checked earlier", true);
            var changed = new List<string?>();
            model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            model.SetServiceHealth("Operational", "Checked earlier", true);
            Assert.IsEmpty(changed);

            model.SetServiceHealth("Operational", "Checked just now", true);
            CollectionAssert.AreEqual(new[] { nameof(StartScreenViewModel.ServiceHealthDetails) }, changed);
            Assert.AreEqual("Checked just now", model.ServiceHealthDetails);
            Assert.IsTrue(model.HasServiceHealthStatus);
            Assert.IsTrue(model.IsServiceHealthy);
        }

        [STATestMethod]
        public void AutomaticRefresh_RechecksEveryFiveMinutesWithoutManualRefresh()
        {
            using var context = CreateContext();
            var requests = 0;
            var model = new StartScreenViewModel(CancellationToken.None, _ =>
                Task.FromResult(Status(++requests == 2 ? ServiceHealthState.Incident : ServiceHealthState.Healthy)), () => context.Factory);
            try
            {
                context.Factory.Run(() => model.RefreshServiceHealthAsync(CancellationToken.None));
                Assert.AreEqual(1, requests);
                Assert.IsTrue(model.IsServiceHealthy);
                Assert.IsTrue(GetTimer(model).IsEnabled);
                Assert.AreEqual(TimeSpan.FromMinutes(5), GetTimer(model).Interval);

                PumpNextRefresh(model);

                Assert.AreEqual(2, requests);
                Assert.IsFalse(model.IsServiceHealthy);
                Assert.AreEqual("Visual Studio service incident", model.ServiceHealthText);
                Assert.IsTrue(GetTimer(model).IsEnabled);
                Assert.AreEqual(TimeSpan.FromMinutes(5), GetTimer(model).Interval);

                PumpNextRefresh(model);

                Assert.AreEqual(3, requests);
                Assert.IsTrue(model.IsServiceHealthy);
                Assert.AreEqual("Service operational", model.ServiceHealthText);
                Assert.IsTrue(GetTimer(model).IsEnabled);
            }
            finally
            {
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void AutomaticRefresh_RetriesUnknownAndStaleResults(bool unknown)
        {
            using var context = CreateContext();
            var initialState = unknown ? ServiceHealthState.Unknown : ServiceHealthState.Healthy;
            var requests = 0;
            var model = new StartScreenViewModel(CancellationToken.None, _ => Task.FromResult(++requests == 1
                ? Status(initialState, stale: true) : Status(ServiceHealthState.Healthy)), () => context.Factory);
            try
            {
                context.Factory.Run(() => model.RefreshServiceHealthAsync(CancellationToken.None));

                Assert.IsFalse(model.IsServiceHealthy);
                Assert.IsTrue(model.HasServiceHealthStatus);
                Assert.AreEqual(initialState == ServiceHealthState.Unknown
                    ? "Visual Studio service status unknown" : "Stale - Service operational", model.ServiceHealthText);
                Assert.IsTrue(GetTimer(model).IsEnabled);

                PumpNextRefresh(model);

                Assert.AreEqual(2, requests);
                Assert.IsTrue(model.IsServiceHealthy);
                Assert.IsTrue(GetTimer(model).IsEnabled);
            }
            finally
            {
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        public void AutomaticRefresh_RetriesUnexpectedFailures()
        {
            using var context = CreateContext();
            var requests = 0;
            var model = new StartScreenViewModel(CancellationToken.None, _ => ++requests == 1
                ? Task.FromException<ServiceHealthStatus>(new InvalidOperationException("Test refresh failure"))
                : Task.FromResult(Status(ServiceHealthState.Healthy)), () => context.Factory);
            try
            {
                context.Factory.Run(() => model.RefreshServiceHealthAsync(CancellationToken.None));

                Assert.IsFalse(model.IsServiceHealthy);
                Assert.IsTrue(model.HasServiceHealthStatus);
                Assert.AreEqual("Service status unavailable", model.ServiceHealthText);
                StringAssert.Contains(model.ServiceHealthDetails, "Retrying automatically");
                Assert.IsTrue(GetTimer(model).IsEnabled);

                PumpNextRefresh(model);

                Assert.AreEqual(2, requests);
                Assert.IsTrue(model.IsServiceHealthy);
            }
            finally
            {
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Refresh_CanceledResultsCannotOverwriteNewerStatus(bool hideAndReopen)
        {
            using var context = CreateContext();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<ServiceHealthStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            CancellationToken oldToken = default;
            var model = new StartScreenViewModel(CancellationToken.None, token =>
            {
                if (++requests == 1)
                {
                    oldToken = token;
                    started.SetResult(true);
                    return pending.Task;
                }
                return Task.FromResult(Status(ServiceHealthState.Healthy));
            }, () => context.Factory);
            try
            {
                JoinableTask oldRefresh = context.Factory.RunAsync(() => model.RefreshServiceHealthAsync(CancellationToken.None));
                Assert.IsFalse(oldRefresh.IsCompleted);
                context.Factory.Run(() => started.Task.WithCancellation(timeout.Token));
                if (hideAndReopen)
                {
                    model.CancelBackgroundWork();
                    Assert.IsTrue(oldToken.IsCancellationRequested);
                    model.ActivateBackgroundWork();
                }

                context.Factory.Run(() => model.RefreshServiceHealthAsync(CancellationToken.None));
                Assert.IsTrue(oldToken.IsCancellationRequested);
                Assert.IsTrue(model.IsServiceHealthy);

                pending.SetResult(Status(ServiceHealthState.Incident));
                oldRefresh.Join(timeout.Token);

                Assert.AreEqual(2, requests);
                Assert.IsTrue(model.IsServiceHealthy);
                Assert.AreEqual("Service operational", model.ServiceHealthText);
                Assert.IsTrue(GetTimer(model).IsEnabled);
                Assert.AreEqual(TimeSpan.FromMinutes(5), GetTimer(model).Interval);
            }
            finally
            {
                pending.TrySetCanceled();
                model.CancelBackgroundWork();
            }
        }

        [STATestMethod]
        public void CancelBackgroundWork_StopsScheduledRefresh()
        {
            using var context = CreateContext();
            var model = new StartScreenViewModel(CancellationToken.None,
                _ => Task.FromResult(Status(ServiceHealthState.Healthy)), () => context.Factory);
            context.Factory.Run(() => model.RefreshServiceHealthAsync(CancellationToken.None));
            Assert.IsTrue(GetTimer(model).IsEnabled);

            model.CancelBackgroundWork();

            Assert.IsFalse(GetTimer(model).IsEnabled);
            Assert.IsTrue(model.GetBackgroundCancellationToken().IsCancellationRequested);
        }

        [STATestMethod]
        public void Shutdown_PreventsScheduledRefreshFromFetchingAgain()
        {
            using var context = CreateContext();
            using var shutdown = new CancellationTokenSource();
            var requests = 0;
            var model = new StartScreenViewModel(shutdown.Token, _ =>
            {
                requests++;
                return Task.FromResult(Status(ServiceHealthState.Healthy));
            }, () => context.Factory);
            try
            {
                context.Factory.Run(() => model.RefreshServiceHealthAsync(CancellationToken.None));
                shutdown.Cancel();

                PumpNextRefresh(model);

                Assert.AreEqual(1, requests);
                Assert.IsFalse(GetTimer(model).IsEnabled);
            }
            finally
            {
                model.CancelBackgroundWork();
            }
        }

        private static JoinableTaskContext CreateContext()
        {
            return new JoinableTaskContext(Thread.CurrentThread,
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        }

        private static ServiceHealthStatus Status(ServiceHealthState state, bool stale = false)
        {
            var now = DateTimeOffset.UtcNow;
            return new ServiceHealthStatus(state, "Test service status", now,
                state == ServiceHealthState.Unknown ? null : stale ? now.AddHours(-1) : now);
        }

        private static DispatcherTimer GetTimer(StartScreenViewModel model)
        {
            var timer = typeof(StartScreenViewModel).GetField("_serviceHealthRefreshTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model);
            Assert.IsInstanceOfType<DispatcherTimer>(timer);
            return (DispatcherTimer)timer;
        }

        private static void PumpNextRefresh(StartScreenViewModel model)
        {
            var timer = GetTimer(model);
            Assert.IsTrue(timer.IsEnabled);
            var frame = new DispatcherFrame();
            var fired = false;
            EventHandler onTick = (_, _) =>
            {
                fired = true;
                if (!model.WorkspaceRefreshState.IsLoading)
                    frame.Continue = false;
            };
            System.ComponentModel.PropertyChangedEventHandler onChanged = (_, _) =>
            {
                if (fired && !model.WorkspaceRefreshState.IsLoading)
                    frame.Continue = false;
            };
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timeout.Tick += (_, _) => frame.Continue = false;
            timer.Tick += onTick;
            model.PropertyChanged += onChanged;
            timer.Interval = TimeSpan.FromMilliseconds(1);
            timeout.Start();
            try
            {
                Dispatcher.PushFrame(frame);
                Assert.IsTrue(fired, "The scheduled service-health refresh must fire.");
                Assert.IsFalse(model.WorkspaceRefreshState.IsLoading, "The scheduled refresh must finish before its result is checked.");
            }
            finally
            {
                timer.Tick -= onTick;
                model.PropertyChanged -= onChanged;
                timeout.Stop();
            }
        }
    }
}