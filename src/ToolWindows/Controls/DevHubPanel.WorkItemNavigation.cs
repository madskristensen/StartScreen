using System.Threading;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell.Interop;
using StartScreen.Commands;
using StartScreen.Models.DevHub;
using StartScreen.Services;

namespace StartScreen.ToolWindows.Controls
{
    public partial class DevHubPanel
    {
        private async Task ShowActivityMenuAsync(DevHubWorkItem item, Point point)
        {
            if (item.IsCopilot)
            {
                await ShowCopilotMenuAsync(item.CopilotSession, point);
                return;
            }
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                if (_isDisposed || !IsVisible)
                    return;
                var shell = await VS.GetServiceAsync<SVsUIShell, IVsUIShell>()
                    ?? throw new InvalidOperationException("Visual Studio context menus are unavailable.");
                DevHubWorkItemMenu.Current = new DevHubWorkItemMenu
                {
                    Item = item,
                    IsScoped = _scopedRepo != null,
                    ExecuteActionAsync = id => ExecuteWorkItemActionAsync(item, id),
                };
                try
                {
                    ErrorHandler.ThrowOnFailure(shell.UpdateCommandUI(1));
                    var guid = PackageGuids.StartScreen;
                    var points = new[] { new POINTS { x = (short)point.X, y = (short)point.Y } };
                    ErrorHandler.ThrowOnFailure(shell.ShowContextMenu(0, ref guid, PackageIds.DevHubWorkItemMenu, points, null));
                }
                finally
                {
                    DevHubWorkItemMenu.Current = null;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not show activity actions. " + ex.Message);
            }
        }

        private async Task ExecuteWorkItemActionAsync(DevHubWorkItem item, int id)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                switch (id)
                {
                    case PackageIds.DevHubOpenInBrowser:
                        await OpenUrlAsync(item.WebUrl);
                        break;
                    case PackageIds.DevHubReviewInVisualStudio:
                        await PullRequestNavigationService.OpenAsync(item.WebUrl);
                        break;
                    case PackageIds.DevHubFixWithCopilot:
                        await StartWorkItemChatAsync(CopilotWorkItemPrompt.Create(item));
                        break;
                    case PackageIds.DevHubRequestCopilotReview:
                        var confirmed = await VS.MessageBox.ShowConfirmAsync("Request Copilot Review",
                            $"Request a GitHub Copilot review for '{item.Title}'? This posts a review on the pull request and may consume Copilot usage.");
                        if (confirmed)
                        {
                            await CopilotReviewService.RequestAsync(item.WebUrl, StartScreenPackage.ShutdownToken);
                            await VS.StatusBar.ShowMessageAsync("Copilot review requested on GitHub.");
                        }
                        break;
                    case PackageIds.DevHubCopyUrl:
                        Clipboard.SetText(item.WebUrl);
                        break;
                    case PackageIds.DevHubScopeToRepo:
                        ScopeToRepo(item.RepoIdentifier);
                        break;
                    case PackageIds.DevHubShowAllRepos:
                        ClearFilter_Click(this, new RoutedEventArgs());
                        break;
                    default:
                        throw new InvalidOperationException("Unknown activity action.");
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!StartScreenPackage.ShutdownToken.IsCancellationRequested)
                    await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not complete the activity action. " + ex.Message);
            }
        }

        private async Task StartWorkItemChatAsync(string prompt)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(StartScreenPackage.ShutdownToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            _copilotComponentModel = await VS.GetServiceAsync<SComponentModel, IComponentModel>()
                ?? throw new NotSupportedException("Visual Studio's component model is unavailable.");
            var backends = GetCopilotExport(CopilotHarnessAdapter.BackendsContract);
            var registry = GetCopilotExport(CopilotHarnessAdapter.NavigatorRegistryContract);
            var shell = await VS.GetServiceAsync<SVsUIShell, IVsUIShell>();
            var chatWindow = new Guid("40ea2e6b-2121-4bb8-a43e-c83c04b51041");
            ErrorHandler.ThrowOnFailure(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref chatWindow, out IVsWindowFrame frame));
            ErrorHandler.ThrowOnFailure(frame.Show());
            object navigator;
            while ((navigator = CopilotHarnessAdapter.Read(registry, "Current")) == null)
                await System.Threading.Tasks.Task.Delay(100, timeout.Token);
            await CopilotChatLauncher.StartAsync(backends, navigator, prompt, StartScreenPackage.ShutdownToken);
        }
    }
}