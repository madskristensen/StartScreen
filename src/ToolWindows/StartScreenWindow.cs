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
        public override string GetTitle(int toolWindowId) => "Start";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            // Start loading data immediately (head start while window renders)
            var viewModel = new StartScreenViewModel();
            var cacheTask = viewModel.LoadFromCacheAsync();

            // Return control immediately for fast window paint
            var control = new StartScreenControl(viewModel, cacheTask);
            return Task.FromResult<FrameworkElement>(control);
        }

        [Guid("d0ffc7e5-4860-42ef-afbe-0dd5532e9906")]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.Home;
                Caption = "Start";
            }
        }
    }
}
