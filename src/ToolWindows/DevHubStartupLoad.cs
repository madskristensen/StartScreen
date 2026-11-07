using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.ToolWindows
{
    internal sealed class DevHubStartupLoad : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private int _isConsumed;
        private bool _isDisposed;

        internal DevHubStartupLoad(DevHubService service, JoinableTaskFactory taskFactory,
            CancellationToken cancellationToken, BackgroundProgress backgroundProgress,
            IProgress<DevHubDashboard> progress)
        {
            Service = service;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken startupToken = _cancellation.Token;
            LoadTask = taskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                using var work = backgroundProgress.Start(step: "Reading cached activity...");
                return await Service.LoadFromCacheAsync(startupToken, progress: progress, workProgress: work, stepProgress: work);
            });
        }

        internal DevHubService Service { get; }
        internal JoinableTask<DevHubDashboard> LoadTask { get; }

        internal async Task<DevHubDashboard> LoadAsync(CancellationToken cancellationToken,
            Func<Task<DevHubDashboard>> reload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _isConsumed, 1) == 0)
            {
                try
                {
                    await LoadTask;
                    cancellationToken.ThrowIfCancellationRequested();
                    return Service.CurrentDashboard;
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && _cancellation.IsCancellationRequested)
                {
                    await ex.LogAsync();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return await reload();
        }

        internal void Cancel()
        {
            if (!_isDisposed)
                _cancellation.Cancel();
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;
            Cancel();
            _isDisposed = true;
            _cancellation.Dispose();
        }
    }
}
