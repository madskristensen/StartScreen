using System.Windows.Threading;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;

namespace StartScreen.Test
{
    [TestClass]
    public class DispatcherHelperTests
    {
        [STATestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Cleanup_ShutdownCompletesWithoutPumpingQueuedUiWorkAsync(bool preCanceled)
        {
            var dispatcher = new QueuedSynchronizationContext();
            using var context = new JoinableTaskContext(Thread.CurrentThread, dispatcher);
            using var shutdown = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var invoked = false;
            if (preCanceled)
                shutdown.Cancel();
            var cleanup = context.Factory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                return await context.Factory.RunOnMainThreadForCleanupAsync(shutdown.Token, () => invoked = true);
            });
            await TaskScheduler.Default;
            if (!preCanceled)
            {
                await dispatcher.Posted.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(cleanup.IsCompleted);
                shutdown.Cancel();
            }

            Assert.IsFalse(await cleanup.Task.WithCancellation(timeout.Token));
            Assert.IsFalse(invoked);
            Assert.AreEqual(preCanceled ? 0 : 1, dispatcher.PostCount);
        }

        [STATestMethod]
        public void Cleanup_NormalCompletionRunsCallbackOnUiThread()
        {
            using var context = new JoinableTaskContext(Thread.CurrentThread,
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var uiThread = Environment.CurrentManagedThreadId;
            var callbackThread = 0;

            var completed = context.Factory.Run(async () =>
            {
                await TaskScheduler.Default;
                return await context.Factory.RunOnMainThreadForCleanupAsync(CancellationToken.None,
                    () => callbackThread = Environment.CurrentManagedThreadId);
            });

            Assert.IsTrue(completed);
            Assert.AreEqual(uiThread, callbackThread);
        }

        [STATestMethod]
        [DataRow(DispatcherPriority.ContextIdle)]
        [DataRow(DispatcherPriority.Loaded)]
        public void YieldAsync_ShutdownCancelsWithoutDispatcherPump(DispatcherPriority priority)
        {
            using var context = new JoinableTaskContext(
                Thread.CurrentThread, new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var cancellation = new CancellationTokenSource();
            var resumed = false;
            JoinableTask work = context.Factory.RunAsync(async () =>
            {
                await context.Factory.YieldAsync(priority, cancellation.Token);
                resumed = true;
            });

            Assert.IsFalse(work.IsCompleted, "Deferred work must yield before running.");
            cancellation.Cancel();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var exception = Assert.Throws<OperationCanceledException>(() => work.Join(timeout.Token));
            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.IsTrue(work.Task.IsCanceled);
            Assert.IsFalse(resumed, "Canceled work must not resume UI updates.");
        }

        [STATestMethod]
        public void YieldAsync_JoinCompletesWithoutDispatcherPump()
        {
            using var context = new JoinableTaskContext(
                Thread.CurrentThread, new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var originalThread = Environment.CurrentManagedThreadId;
            var resumedThread = 0;
            JoinableTask work = context.Factory.RunAsync(async () =>
            {
                await context.Factory.YieldAsync(DispatcherPriority.ContextIdle, CancellationToken.None);
                resumedThread = Environment.CurrentManagedThreadId;
            });

            Assert.IsFalse(work.IsCompleted);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            work.Join(timeout.Token);

            Assert.IsTrue(work.IsCompleted);
            Assert.AreEqual(originalThread, resumedThread, "Joining must preserve UI thread affinity.");
        }

        internal sealed class QueuedSynchronizationContext : SynchronizationContext
        {
            internal TaskCompletionSource<bool> Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int PostCount { get; private set; }

            public override void Post(SendOrPostCallback d, object? state)
            {
                PostCount++;
                Posted.TrySetResult(true);
            }
        }
    }
}