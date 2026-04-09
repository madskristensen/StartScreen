global using System;
global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using Task = System.Threading.Tasks.Task;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;

namespace StartScreen
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ToolWindows.StartScreenWindow.Pane), Window = WindowGuids.DocumentWell, DocumentLikeTool = true, Style = VsDockStyle.MDI)]
    [ProvideToolWindowVisibility(typeof(ToolWindows.StartScreenWindow.Pane), VSConstants.UICONTEXT.NoSolution_string)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuids.StartScreenString)]
    public sealed class StartScreenPackage : ToolkitPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();

            // Register tool window
            this.RegisterToolWindows();

            // Don't await ShowAsync here — it deadlocks because the shell isn't ready yet.
            // ProvideToolWindowVisibility handles showing automatically in the NoSolution context.
            // Schedule a deferred show as backup in case the visibility attribute didn't trigger.

            //var options = await Options.GetLiveInstanceAsync();
            //if (options.LoadOnStartup)
            //{
            JoinableTaskFactory.RunAsync(async () =>
            {
                // Yield to let the shell finish initializing
                await Task.Yield();
                await ShowStartScreenAsync();
            }).FileAndForget(nameof(StartScreen));
            //}

            // Switch to main thread to subscribe to solution events
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Subscribe to solution events for auto-show/hide behavior
            VS.Events.SolutionEvents.OnAfterOpenSolution += OnSolutionOpened;
            VS.Events.SolutionEvents.OnAfterCloseSolution += OnSolutionClosed;
        }

        private async Task ShowStartScreenAsync()
        {
            await ToolWindows.StartScreenWindow.ShowAsync();
        }

        private void OnSolutionOpened(Solution obj)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Don't close if it's just the Miscellaneous Files project (no actual solution)
                if (obj == null || string.IsNullOrEmpty(obj.FullPath))
                {
                    return;
                }

                // Hide Start Screen when a real solution is opened
                try
                {
                    var uiShell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
                    if (uiShell != null)
                    {
                        var paneGuid = typeof(ToolWindows.StartScreenWindow.Pane).GUID;
                        uiShell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fFindFirst, ref paneGuid, out IVsWindowFrame frame);
                        frame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                    }
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            }).FileAndForget(nameof(StartScreen));
        }

        private void OnSolutionClosed()
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                // Show Start Screen when solution is closed
                await ShowStartScreenAsync();
            }).FileAndForget(nameof(StartScreen));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                VS.Events.SolutionEvents.OnAfterOpenSolution -= OnSolutionOpened;
                VS.Events.SolutionEvents.OnAfterCloseSolution -= OnSolutionClosed;
            }

            base.Dispose(disposing);
        }
    }
}