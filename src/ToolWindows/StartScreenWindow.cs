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

        public const string Name = "⌂ Start";

        public override string GetTitle(int toolWindowId) => Name;

        public override Type PaneType => typeof(Pane);

        public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            if (_control != null)
            {
                return _control;
            }

            try
            {
                // Start loading data immediately (head start while window renders)
                _viewModel = new StartScreenViewModel();
                Task loadTask = _viewModel.LoadMruAsync();

                // Return control immediately for fast window paint
                _control = new StartScreenControl(_viewModel, loadTask);
                return _control;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                throw;
            }
        }

        [Guid("66da8633-f469-4bb6-acc2-56aab30b750a")]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.Home;
                Caption = StartScreenWindow.Name;
            }
        }
    }
}
