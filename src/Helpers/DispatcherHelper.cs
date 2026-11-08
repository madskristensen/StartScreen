using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using Microsoft.VisualStudio.Threading;

namespace StartScreen.Helpers
{
    internal static class DispatcherHelper
    {
        internal static async Task YieldAsync(
            this JoinableTaskFactory factory, DispatcherPriority priority, CancellationToken cancellationToken)
        {
            // Keep deferred UI work joinable and cancellable even when disposal
            // waits without pumping dispatcher messages.
            await factory.WithPriority(Dispatcher.CurrentDispatcher, priority)
                .SwitchToMainThreadAsync(alwaysYield: true, cancellationToken);
        }
    }

    internal sealed class BackgroundUiDispatcher(JoinableTaskFactory factory, Dispatcher dispatcher, CancellationToken cancellationToken)
    {
        private readonly Queue<Action> _callbacks = new();
        private bool _isScheduled;

        internal void Enqueue(Action callback)
        {
            lock (_callbacks)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                _callbacks.Enqueue(callback);
                if (_isScheduled)
                    return;
                _isScheduled = true;
            }
            factory.RunAsync(DrainAsync).FileAndForget(nameof(BackgroundUiDispatcher));
        }

        private async Task DrainAsync()
        {
            try
            {
                while (true)
                {
                    await factory.WithPriority(dispatcher, DispatcherPriority.Background)
                        .SwitchToMainThreadAsync(alwaysYield: true, cancellationToken);
                    var elapsed = Stopwatch.StartNew();
                    for (var count = 0; count < 8 && elapsed.ElapsedMilliseconds < 8; count++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Action callback;
                        lock (_callbacks)
                        {
                            if (_callbacks.Count == 0)
                            {
                                _isScheduled = false;
                                return;
                            }
                            callback = _callbacks.Dequeue();
                        }
                        callback();
                    }
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                lock (_callbacks)
                {
                    _callbacks.Clear();
                    _isScheduled = false;
                }
                if (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                    throw;
            }
        }
    }
}