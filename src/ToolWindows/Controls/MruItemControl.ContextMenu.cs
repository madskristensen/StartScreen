using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using StartScreen.Commands;
using StartScreen.Models;

namespace StartScreen.ToolWindows.Controls
{
    public partial class MruItemControl
    {
        internal static void SelectMruItem(ListBox list, MruItem item, bool toggle, bool preserveSelection)
        {
            if (item == null || !list.Items.Contains(item))
                throw new ArgumentException("The project is not in the current recent list.", nameof(item));
            if (list.SelectionMode == SelectionMode.Single)
            {
                list.SelectedItem = item;
                return;
            }
            if (toggle)
            {
                if (list.SelectedItems.Contains(item))
                    list.SelectedItems.Remove(item);
                else
                    list.SelectedItems.Add(item);
            }
            else if (!preserveSelection || !list.SelectedItems.Contains(item))
            {
                list.SelectedItems.Clear();
                list.SelectedItems.Add(item);
            }
        }

        private void RootBorder_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var list = FindMruList(this);
            if (list != null && MruItem != null)
                SelectMruItem(list, MruItem, false, true);
            FocusItem(true);
            e.Handled = true;
        }

        private void RootBorder_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            RequestContextMenu(RootBorder.PointToScreen(e.GetPosition(RootBorder)));
        }

        private void RequestContextMenu(Point point)
        {
            if (MruItem == null)
                return;
            var list = FindMruList(this);
            if (list != null)
                SelectMruItem(list, MruItem, false, true);
            FocusItem(true);
            var selection = list == null ? new[] { MruItem } : list.SelectedItems.Cast<MruItem>().ToArray();
            ShowProjectContextMenuAsync(point, selection).FileAndForget(nameof(MruItemControl));
        }

        private async Task ShowProjectContextMenuAsync(Point point, MruItem[] selection)
        {
            var item = MruItem;
            try
            {
                var shell = await VS.GetServiceAsync<SVsUIShell, IVsUIShell>()
                    ?? throw new InvalidOperationException("Visual Studio context menus are unavailable.");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!ReferenceEquals(item, MruItem))
                    return;
                MruItemMenu.Current = new MruItemMenu
                {
                    Item = item,
                    Selection = selection,
                    Worktrees = item.Worktrees.ToArray(),
                    IsPulling = _isGitPullRunning,
                    IsFetching = _isGitFetchRunning,
                    ExecuteActionAsync = id => ExecuteProjectActionAsync(id, selection),
                };
                try
                {
                    ErrorHandler.ThrowOnFailure(shell.UpdateCommandUI(1));
                    var guid = PackageGuids.StartScreen;
                    var points = new[] { new POINTS { x = (short)point.X, y = (short)point.Y } };
                    ErrorHandler.ThrowOnFailure(shell.ShowContextMenu(0, ref guid, PackageIds.MruItemMenu, points, null));
                }
                finally
                {
                    MruItemMenu.Current = null;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not show project actions. " + ex.Message);
            }
        }

        private async Task ExecuteProjectActionAsync(int id, MruItem[] selection)
        {
            try
            {
                switch (id)
                {
                    case PackageIds.MruOpen: await OpenItemAsync(); break;
                    case PackageIds.MruOpenNewInstance: OpenInNewInstanceMenuItem_Click(this, null); break;
                    case PackageIds.MruOpenFolder: OpenFolderMenuItem_Click(this, null); break;
                    case PackageIds.MruOpenTerminal: OpenTerminalMenuItem_Click(this, null); break;
                    case PackageIds.MruGitPull: await PullGitAsync(); break;
                    case PackageIds.MruGitFetch: await RunGitOperationAsync(fetch: true); break;
                    case PackageIds.MruDeleteChats: await DeleteCopilotChatAsync(); break;
                    case PackageIds.MruCopyPath: CopyPathMenuItem_Click(this, null); break;
                    case PackageIds.MruPin: PinMenuItem_Click(this, null); break;
                    case PackageIds.MruRemove: RemoveMenuItem_Click(this, null); break;
                    case PackageIds.MruAddTags: AddTagsRequested?.Invoke(this, selection); break;
                    case PackageIds.MruRemoveTags: RemoveTagsRequested?.Invoke(this, selection); break;
                    case PackageIds.MruPruneWorktrees: PruneWorktreesRequested?.Invoke(this, selection.Single()); break;
                    case PackageIds.MruNewChat: NewChatRequested?.Invoke(this, selection.Single()); break;
                    default: throw new InvalidOperationException("Unknown project action.");
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not complete the project action. " + ex.Message);
            }
        }
    }
}
