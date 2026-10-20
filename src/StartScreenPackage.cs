global using System;
global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using Task = System.Threading.Tasks.Task;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;
using Microsoft.VisualStudio.Threading;
using StartScreen.ToolWindows;

namespace StartScreen
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(StartScreenWindow.Pane), Window = WindowGuids.DocumentWell, Style = VsDockStyle.MDI)]
    [ProvideToolWindowVisibility(typeof(StartScreenWindow.Pane), PackageGuids.autoloadString)]
    [ProvideAutoLoad(PackageGuids.autoloadString, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideUIContextRule(PackageGuids.autoloadString,
        "StartScreenUIContext",
        "NoSolution & !FolderOpened",
        ["NoSolution", "FolderOpened"],
        [VSConstants.UICONTEXT.NoSolution_string, VSConstants.UICONTEXT.FolderOpened_string])]
    [Guid(PackageGuids.StartScreenString)]
    public sealed class StartScreenPackage : ToolkitPackage
    {
        private readonly CancellationTokenSource _shutdownCancellation;
        internal static JoinableTaskFactory BackgroundTaskFactory { get; private set; }
        internal static CancellationToken ShutdownToken { get; private set; }

        public StartScreenPackage()
        {
            BackgroundTaskFactory = JoinableTaskFactory;
            _shutdownCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                DisposalToken, VsShellUtilities.ShutdownToken);
            ShutdownToken = _shutdownCancellation.Token;
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();

            // Register tool window
            this.RegisterToolWindows();

            // Disable the built-in VS Start Window on first run so only this
            // extension's Start Screen is shown (see GitHub issue #9).
            await DisableBuiltInStartWindowOnFirstRunAsync();

            // Subscribing to managed C# events does not require the UI thread, so stay off it.
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

                // ShellSettingsManager is free threaded, so we can use it here without switching to the UI thread.
                var settingsManager = new ShellSettingsManager(ServiceProvider.GlobalProvider);
                WritableSettingsStore userSettings = settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);

                const string collection = @"ApplicationPrivateSettings\Microsoft\VisualStudio\IDE";
                const string property = "OnEnvironmentStartup_Dev16";

                // Value format is "scope*type*value". Value 10 = show empty environment,
                // which disables the built-in Start Window so only this
                // extension's Start Screen is shown.
                userSettings.SetString(collection, property, "0*System.Int64*10");

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
            HideStartScreenAsync().FireAndForget();
        }

        private void OnSolutionClosed(object sender, EventArgs e)
        {
            if (!ShutdownToken.IsCancellationRequested)
            {
                ShowStartScreenAsync().FileAndForget(nameof(StartScreenPackage));
            }
        }

        private async Task HideStartScreenAsync()
        {
            await StartScreenWindow.HideAsync();
        }

        private async Task ShowStartScreenAsync(int delay = 500)
        {
            try
            {
                await Task.Delay(delay, ShutdownToken);

                if (!await VS.Solutions.IsOpeningAsync() && !await VS.Solutions.IsOpenAsync())
                {
                    ShutdownToken.ThrowIfCancellationRequested();
                    // Show Start Screen when solution is closed
                    await StartScreenWindow.ShowAsync();
                }
            }
            catch (OperationCanceledException ex) when (ShutdownToken.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution -= OnSolutionClosed;
                _shutdownCancellation.Cancel();
            }

            base.Dispose(disposing);

            if (disposing)
            {
                _shutdownCancellation.Dispose();
            }
        }
    }
}
