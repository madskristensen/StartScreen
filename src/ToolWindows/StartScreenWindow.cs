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
            // Create ViewModel — MRU data will be read from IVsMRUItemsStore during background refresh
            var viewModel = new StartScreenViewModel();
            viewModel.LoadFromCacheSync();

            // Create and return the control (UI is already populated from cache)
            var control = new StartScreenControl
            {
                DataContext = viewModel
            };

            return control;
        }

        [Guid("213f7e97-d4de-4187-9163-a3c61916997a")]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.Home;
            }
        }
    }
}
