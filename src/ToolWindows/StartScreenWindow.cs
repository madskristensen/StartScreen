using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Imaging;

namespace StartScreen.ToolWindows
{
    /// <summary>
    /// Represents the Start Screen tool window.
    /// </summary>
    public class StartScreenWindow : BaseToolWindow<StartScreenWindow>
    {
        private StartScreenViewModel _viewModel;
        private StartScreenControl _control;

        public override string GetTitle(int toolWindowId) => "Start";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            if (_control != null)
            {
                return Task.FromResult<FrameworkElement>(_control);
            }

            // Start loading data immediately (head start while window renders)
            _viewModel = new StartScreenViewModel();
            var cacheTask = _viewModel.LoadFromCacheAsync();

            // Return control immediately for fast window paint
            _control = new StartScreenControl(_viewModel, cacheTask);
            return Task.FromResult<FrameworkElement>(_control);
        }

        [Guid("d0ffc7e5-4860-42ef-afbe-0dd5532e9906")]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.Home;
            }
        }
    }
}
