using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.ToolWindows
{
    /// <summary>
    /// Represents the Start Screen tool window.
    /// </summary>
    public class StartScreenWindow : BaseToolWindow<StartScreenWindow>
    {
        private StartScreenViewModel _viewModel;
        private StartScreenControl _control;
        private DevHubStartupLoad _devHubStartup;
        private static StartScreenWindow _currentWindow;

        internal static StartScreenViewModel CurrentViewModel { get; private set; }

        public const string Name = "⌂ Start";

        public override string GetTitle(int toolWindowId) => Name;

        public override Type PaneType => typeof(Pane);

        public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartScreenPackage.ShutdownToken.ThrowIfCancellationRequested();

            if (_control != null)
            {
                return _control;
            }

            try
            {
                StartupDiagnostics.Start();

                // Start loading data immediately (head start while window renders)
                _viewModel = new StartScreenViewModel(StartScreenPackage.ShutdownToken);
                _currentWindow = this;
                CurrentViewModel = _viewModel;
                StartScreenViewModel viewModel = _viewModel;
                JoinableTask loadTask =
                    StartScreenPackage.BackgroundTaskFactory.RunAsync(() => viewModel.LoadMruAsync());

                // Restore layout before the first paint while MRU data continues loading.
                Options options = await Options.GetLiveInstanceAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                StartScreenPackage.ShutdownToken.ThrowIfCancellationRequested();
                if (options.DevHubEnabled)
                {
                    var progress = new Progress<DevHubDashboard>(dashboard => _control?.ApplyStartupDevHubDashboard(dashboard));
                    _devHubStartup = new DevHubStartupLoad(new DevHubService(), StartScreenPackage.BackgroundTaskFactory,
                        StartScreenPackage.ShutdownToken, viewModel.BackgroundProgress, progress);
                    _devHubStartup.LoadTask.FileAndForget(nameof(StartScreenWindow));
                }
                _control = new StartScreenControl(_viewModel, loadTask, options, _devHubStartup);
                return _control;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                DisposeContent();
                throw;
            }
        }

        internal static void DisposeCurrent()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _currentWindow?.DisposeContent();
        }

        private void DisposeContent()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _control?.Dispose();
            _devHubStartup?.Dispose();
            _devHubStartup = null;
            _viewModel?.Dispose();
            if (ReferenceEquals(CurrentViewModel, _viewModel))
                CurrentViewModel = null;
            _control = null;
            _viewModel = null;
            if (ReferenceEquals(_currentWindow, this))
                _currentWindow = null;
        }

        [Guid("56d11d96-90e9-4d41-a3dd-fa3146284922")]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.Home;
                Caption = Name;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (Content is StartScreenControl control)
                        {
                            control.Dispose();
                            if (ReferenceEquals(_currentWindow?._control, control))
                                _currentWindow.DisposeContent();
                        }
                    });
                }
                base.Dispose(disposing);
            }
        }
    }
}
