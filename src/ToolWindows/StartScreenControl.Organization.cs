using System.Collections.Generic;
using System.Windows;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenControl
    {
        private static async Task ReportOrganizationFailureAsync(Exception ex)
        {
            await ex.LogAsync();
            await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not edit project tags. " + ex.Message);
        }

        private void MruItemControl_AddTagsRequested(object sender, IReadOnlyList<MruItem> items) => ShowProjectTagsDialog(items, false);

        private void MruItemControl_RemoveTagsRequested(object sender, IReadOnlyList<MruItem> items) => ShowProjectTagsDialog(items, true);

        private void MruItemControl_PruneWorktreesRequested(object sender, MruItem item)
        {
            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                try
                {
                    if (!await VS.MessageBox.ShowConfirmAsync("Prune deleted worktrees",
                        $"Remove stale worktree registrations for '{item.Name}' now? Existing working files and branches will not be deleted. "
                        + "Pruning discards recovery information for missing checkouts. Do not continue if a checkout was moved or is on a disconnected drive. Locked worktrees are preserved."))
                        return;
                    using var work = ViewModel.BackgroundProgress.Start(step: "Pruning worktree registrations...");
                    await ViewModel.PruneWorktreesAsync(item, _lifetimeCancellation.Token);
                    await VS.StatusBar.ShowMessageAsync("Pruned stale worktree registrations.");
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    if (!_lifetimeCancellation.IsCancellationRequested)
                        await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not prune worktree registrations. " + ex.GetBaseException().Message);
                }
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void MruItemControl_NewChatRequested(object sender, MruItem item)
        {
            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                try
                {
                    using var work = ViewModel.BackgroundProgress.Start(step: $"Starting chat in {item.Name}...");
                    var directory = item.Type == MruItemType.Folder ? item.Path : System.IO.Path.GetDirectoryName(item.Path);
                    await DevHubPanelControl.StartWorkspaceChatAsync(directory, _lifetimeCancellation.Token);
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    if (!_lifetimeCancellation.IsCancellationRequested)
                        await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not start a chat in this workspace. " + ex.GetBaseException().Message);
                }
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void ShowProjectTagsDialog(IReadOnlyList<MruItem> items, bool remove)
        {
            try
            {
                if (items.Count == 0)
                    throw new InvalidOperationException("Select at least one project to edit its tags.");
                new WorkspaceOrganizationDialog(ViewModel, items, remove).ShowModal();
            }
            catch (Exception ex)
            {
                ReportOrganizationFailureAsync(ex).FileAndForget(nameof(StartScreenControl));
            }
        }

        private void TagFilter_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is StartScreenViewModel.TagFilterOption tag)
                ViewModel.ToggleTagFilter(tag.Name);
        }

        private void ClearTagFilter_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.TagFilter = "";
        }

    }
}
