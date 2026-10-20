using System.Windows.Threading;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;

namespace StartScreen.Test
{
    [TestClass]
    public class DispatcherHelperTests
    {
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
    }
}