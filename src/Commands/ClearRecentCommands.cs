using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using StartScreen.ToolWindows;
using System;
using System.Threading.Tasks;

namespace StartScreen.Commands
{
    [Command(PackageIds.ClearAllRecentCommand)]
    internal sealed class ClearAllRecentCommand : BaseCommand<ClearAllRecentCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            Command.Enabled = StartScreenWindow.CurrentViewModel?.CanClearMruItems(MruClearMode.All) == true;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            if (StartScreenWindow.CurrentViewModel != null)
            {
                await StartScreenWindow.CurrentViewModel.ClearMruItemsAsync(MruClearMode.All);
            }
        }
    }

    [Command(PackageIds.ClearAllButPinnedCommand)]
    internal sealed class ClearAllButPinnedCommand : BaseCommand<ClearAllButPinnedCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            Command.Enabled = StartScreenWindow.CurrentViewModel?.CanClearMruItems(MruClearMode.AllButPinned) == true;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            if (StartScreenWindow.CurrentViewModel != null)
            {
                await StartScreenWindow.CurrentViewModel.ClearMruItemsAsync(MruClearMode.AllButPinned);
            }
        }
    }

    [Command(PackageIds.ClearMissingRecentCommand)]
    internal sealed class ClearMissingRecentCommand : BaseCommand<ClearMissingRecentCommand>
    {
        protected override void BeforeQueryStatus(EventArgs e)
        {
            Command.Enabled = StartScreenWindow.CurrentViewModel?.CanClearMruItems(MruClearMode.Missing) == true;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            if (StartScreenWindow.CurrentViewModel != null)
            {
                await StartScreenWindow.CurrentViewModel.ClearMruItemsAsync(MruClearMode.Missing);
            }
        }
    }
}
