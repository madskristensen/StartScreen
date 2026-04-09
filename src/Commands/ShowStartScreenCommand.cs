using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Threading.Tasks;

namespace StartScreen.Commands
{
    /// <summary>
    /// Command handler for showing the Start Screen tool window.
    /// </summary>
    [Command(PackageIds.ShowStartScreenCommand)]
    internal sealed class ShowStartScreenCommand : BaseCommand<ShowStartScreenCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ToolWindows.StartScreenWindow.ShowAsync();
        }
    }
}
