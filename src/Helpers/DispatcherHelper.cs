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
}