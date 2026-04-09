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

        public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            // Create ViewModel and load cache data asynchronously on background thread
            var viewModel = new StartScreenViewModel();
            await viewModel.LoadFromCacheAsync();

            // Create and return the control (UI is already populated from cache)
            var control = new StartScreenControl
            {
                DataContext = viewModel
            };

            return control;
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
