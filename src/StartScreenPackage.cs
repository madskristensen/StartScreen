global using System;
global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using Task = System.Threading.Tasks.Task;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;

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

            // Do NOT call ShowAsync from InitializeAsync — it forces the WPF tree to be built
            // on the UI thread during package load, which is what triggers the
            // "this extension delayed VS startup" InfoBar. ProvideToolWindowVisibility
            // (NoSolution) tells the shell to show the window when it is ready.

            // Disable the built-in VS Start Window on first run so only this
            // extension's Start Screen is shown (see GitHub issue #9).
            await DisableBuiltInStartWindowOnFirstRunAsync();

            // Subscribing to managed C# events does not require the UI thread, so stay off it.
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnBeforeOpenSolution += OnBeforeOpenSolution;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenFolder += OnBeforeOpenSolution;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution += OnSolutionClosed;
        }

        private static async Task DisableBuiltInStartWindowOnFirstRunAsync()
        {
            try
            {
                Options options = await Options.GetLiveInstanceAsync();

                if (options.HasDisabledBuiltInStartWindow)
                {
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var settingsManager = new ShellSettingsManager(ServiceProvider.GlobalProvider);
                WritableSettingsStore userSettings = settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);

                const string collection = @"ApplicationPrivateSettings\Microsoft\VisualStudio\IDE";
                const string property = "OnEnvironmentStartup";

                // Value format is "scope*type*value". Value 0 = empty environment,
                // which disables the built-in Start Window so only this
                // extension's Start Screen is shown.
                userSettings.SetString(collection, property, "0*System.Int64*0");

                options.HasDisabledBuiltInStartWindow = true;
                await options.SaveAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void OnBeforeOpenSolution(object sender, EventArgs e)
        {
            ToolWindows.StartScreenWindow.HideAsync().FireAndForget();
        }

        private async Task ShowStartScreenAsync()
        {
            await ToolWindows.StartScreenWindow.ShowAsync();
        }

        private void OnSolutionClosed(object sender, EventArgs e)
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
                Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnBeforeOpenSolution -= OnBeforeOpenSolution;
                Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenFolder -= OnBeforeOpenSolution;
                Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution -= OnSolutionClosed;
            }

            base.Dispose(disposing);
        }
    }
}
