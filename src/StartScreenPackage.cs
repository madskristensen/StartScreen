global using System;
global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using Task = System.Threading.Tasks.Task;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;

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
            //VS.Events.SolutionEvents.OnBeforeOpenSolution += OnSolutionOpened;
            VS.Events.SolutionEvents.OnBeforeOpenSolution += OnBeforeOpenSolution;
            VS.Events.SolutionEvents.OnAfterOpenFolder += OnBeforeOpenSolution;
            VS.Events.SolutionEvents.OnAfterCloseSolution += OnSolutionClosed;
        }

        private void OnBeforeOpenSolution(string obj)
        {
            ToolWindows.StartScreenWindow.HideAsync().FireAndForget();
        }

        private async Task ShowStartScreenAsync()
        {
            await ToolWindows.StartScreenWindow.ShowAsync();
        }

        private void OnSolutionClosed()
        {
            if (VsShellUtilities.ShellIsShuttingDown)
            {
                return;
            }

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Delay(500);

                if (!await VS.Solutions.IsOpeningAsync() && !await VS.Solutions.IsOpenAsync())
                {
                    // Show Start Screen when solution is closed
                    await ShowStartScreenAsync();
                }
            }).FileAndForget(nameof(StartScreen));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                VS.Events.SolutionEvents.OnBeforeOpenSolution -= OnBeforeOpenSolution;
                VS.Events.SolutionEvents.OnAfterOpenFolder -= OnBeforeOpenSolution;
                VS.Events.SolutionEvents.OnAfterCloseSolution -= OnSolutionClosed;
            }

            base.Dispose(disposing);
        }
    }
}