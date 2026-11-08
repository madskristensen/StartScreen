using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using StartScreen.Commands;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;

namespace StartScreen.ToolWindows.Controls
{
    public partial class DevHubPanel
    {
        private DevHubWorkItem _rightClickedWorkItem;

        private async Task OpenCopilotSessionAsync(CopilotActivitySession session, CopilotSessionOrigin destination)
        {
            try
            {
                if (!CopilotSessionLauncher.Supports(session.Origin, destination))
                    throw new InvalidOperationException("This cross-app session destination has not been verified.");
                if (destination == CopilotSessionOrigin.VisualStudio)
                {
                    await OpenCopilotSessionAsync(session.Id);
                    return;
                }
                if (destination == CopilotSessionOrigin.GitHub)
                {
                    if (!Guid.TryParseExact(session.Id, "D", out var id) || !CopilotCloudSessions.IsTaskUrl(session.WebUrl, id))
                        throw new InvalidDataException("The cloud session has no compatible GitHub URL.");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                    await OpenUrlAsync(session.WebUrl);
                    return;
                }
                if (session.Origin == CopilotSessionOrigin.VisualStudio)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                    session = _copilotAdapter?.IsReady == true ? _copilotAdapter.Sessions.FirstOrDefault(current => current.Id == session.Id) : null;
                    if (session == null)
                        throw new InvalidOperationException("Visual Studio session ownership is unknown. Refresh and try again.");
                }
                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                var executable = CopilotSessionLauncher.FindCliExecutable();
                // Recheck immediately before launch, not just when constructing the menu.
                var reason = await GetCliUnavailableReasonAsync(session);
                if (reason != null)
                    throw new InvalidOperationException(reason);
                var start = CopilotSessionLauncher.CreateStartInfo(session, destination, executable);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("The app could not be started.");
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_isDisposed && !StartScreenPackage.ShutdownToken.IsCancellationRequested)
                    await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not open this Copilot session. " + ex.Message);
            }
        }

        private static async Task<string> GetCliUnavailableReasonAsync(CopilotActivitySession session)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            try
            {
                return CopilotSessionLauncher.CliUnavailableReason(session);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException
                || ex is System.ComponentModel.Win32Exception)
            {
                await ex.LogAsync();
                return "Session ownership could not be verified. Open it in its original app. Details are in the Activity Log.";
            }
        }

        private void CopilotItem_RightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _rightClickedWorkItem = (sender as FrameworkElement)?.DataContext as DevHubWorkItem;
            if (sender is FrameworkElement element && _rightClickedWorkItem != null)
            {
                element.Focus();
                e.Handled = true;
            }
        }

        private void CopilotItem_RightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is DevHubWorkItem item
                && ReferenceEquals(item, _rightClickedWorkItem))
            {
                _rightClickedWorkItem = null;
                e.Handled = true;
                ShowActivityMenuAsync(item, element.PointToScreen(e.GetPosition(element))).FileAndForget(nameof(DevHubPanel));
            }
        }

        private void CopilotItem_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)
                && sender is FrameworkElement element && element.DataContext is DevHubWorkItem item)
            {
                e.Handled = true;
                ShowActivityMenuAsync(item, element.PointToScreen(new Point(0, element.ActualHeight))).FileAndForget(nameof(DevHubPanel));
            }
        }

        private async Task ShowCopilotMenuAsync(CopilotActivitySession session, Point point)
        {
            try
            {
                await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                var reason = CopilotSessionLauncher.Supports(session.Origin, CopilotSessionOrigin.Cli)
                    ? await GetCliUnavailableReasonAsync(session) : null;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                if (_isDisposed || !IsVisible)
                    return;
                var shell = await VS.GetServiceAsync<SVsUIShell, IVsUIShell>()
                    ?? throw new InvalidOperationException("Visual Studio context menus are unavailable.");
                CopilotSessionMenu.Current = new CopilotSessionMenu
                {
                    Session = session,
                    CliUnavailableReason = reason,
                    OpenAsync = destination => OpenCopilotSessionAsync(session, destination),
                };
                if (reason != null)
                    OutputLog.Info("Copilot", reason);
                try
                {
                    ErrorHandler.ThrowOnFailure(shell.UpdateCommandUI(1));
                    var guid = PackageGuids.StartScreen;
                    var points = new[] { new POINTS { x = (short)point.X, y = (short)point.Y } };
                    ErrorHandler.ThrowOnFailure(shell.ShowContextMenu(0, ref guid, PackageIds.CopilotSessionMenu, points, null));
                }
                finally
                {
                    CopilotSessionMenu.Current = null;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_isDisposed && !StartScreenPackage.ShutdownToken.IsCancellationRequested)
                    await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not show session actions. " + ex.Message);
            }
        }
    }
}