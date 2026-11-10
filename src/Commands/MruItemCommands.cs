using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Commands
{
    internal sealed class MruItemMenu
    {
        internal static MruItemMenu Current { get; set; }
        internal MruItem Item { get; set; }
        internal IReadOnlyList<MruItem> Selection { get; set; }
        internal bool IsPulling { get; set; }
        internal bool IsFetching { get; set; }
        internal Func<int, Task> ExecuteActionAsync { get; set; }
        internal IReadOnlyList<GitWorktreeInfo> Worktrees { get; set; } = Array.Empty<GitWorktreeInfo>();

        internal static void Update(OleMenuCommand command, int id)
        {
            var target = Current;
            command.Visible = target != null && (id switch
            {
                PackageIds.MruOpen or PackageIds.MruOpenNewInstance or PackageIds.MruOpenFolder
                    or PackageIds.MruOpenTerminal or PackageIds.MruCopyPath => target.Selection.Count == 1,
                PackageIds.MruDeleteChats => target.Item.HasCopilotChatSessions,
                PackageIds.MruWorktreesMenu => target.Selection.Count == 1
                    && target.Worktrees.Any(worktree => !worktree.IsCurrent && !worktree.IsBare),
                PackageIds.MruPruneWorktrees => target.Selection.Count == 1
                    && target.Worktrees.Any(worktree => worktree.IsPrunable && !worktree.IsLocked && !worktree.IsBare && !worktree.IsCurrent),
                PackageIds.MruNewChat => target.Selection.Count == 1,
                _ => true,
            });
            command.Enabled = command.Visible && (id switch
            {
                PackageIds.MruGitPull or PackageIds.MruGitFetch => !target.IsPulling && !target.IsFetching
                    && target.Item.HasGitBranch && !target.Item.HasCurrentOperation,
                PackageIds.MruRemoveTags => target.Selection.Any(item => item.HasTags),
                PackageIds.MruNewChat => target.Item.Exists,
                _ => true,
            });
            if (id == PackageIds.MruPin)
                command.Text = target?.Item.IsPinned == true ? "Unpin" : "Pin";
            if (id == PackageIds.MruGitPull)
                command.Text = target?.IsPulling == true ? "Pulling..." : "Git Pull";
            if (id == PackageIds.MruGitFetch)
                command.Text = target?.IsFetching == true ? "Fetching..." : "Git Fetch";
            if (id == PackageIds.MruAddTags || id == PackageIds.MruRemoveTags)
            {
                var count = target?.Selection.Count ?? 0;
                command.Text = (id == PackageIds.MruAddTags ? "Add tags" : "Remove tags")
                    + (count > 1 ? $" ({count} projects)..." : "...");
            }
        }

        internal static Task ExecuteAsync(int id)
        {
            var target = Current ?? throw new InvalidOperationException("The project context menu is no longer available.");
            return target.ExecuteActionAsync(id);
        }
    }

    internal abstract class MruItemCommand<T> : BaseCommand<T> where T : BaseCommand<T>, new()
    {
        protected override void BeforeQueryStatus(EventArgs e) => MruItemMenu.Update(Command, Command.CommandID.ID);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => MruItemMenu.ExecuteAsync(Command.CommandID.ID);
    }

    [Command(PackageIds.MruOpen)]
    internal sealed class MruOpen : MruItemCommand<MruOpen> { }
    [Command(PackageIds.MruOpenNewInstance)]
    internal sealed class MruOpenNewInstance : MruItemCommand<MruOpenNewInstance> { }
    [Command(PackageIds.MruOpenFolder)]
    internal sealed class MruOpenFolder : MruItemCommand<MruOpenFolder> { }
    [Command(PackageIds.MruOpenTerminal)]
    internal sealed class MruOpenTerminal : MruItemCommand<MruOpenTerminal> { }
    [Command(PackageIds.MruGitPull)]
    internal sealed class MruGitPull : MruItemCommand<MruGitPull> { }
    [Command(PackageIds.MruDeleteChats)]
    internal sealed class MruDeleteChats : MruItemCommand<MruDeleteChats> { }
    [Command(PackageIds.MruCopyPath)]
    internal sealed class MruCopyPath : MruItemCommand<MruCopyPath> { }
    [Command(PackageIds.MruPin)]
    internal sealed class MruPin : MruItemCommand<MruPin> { }
    [Command(PackageIds.MruRemove)]
    internal sealed class MruRemove : MruItemCommand<MruRemove> { }
    [Command(PackageIds.MruAddTags)]
    internal sealed class MruAddTags : MruItemCommand<MruAddTags> { }
    [Command(PackageIds.MruRemoveTags)]
    internal sealed class MruRemoveTags : MruItemCommand<MruRemoveTags> { }

    [Command(PackageIds.MruPruneWorktrees)]
    internal sealed class MruPruneWorktrees : MruItemCommand<MruPruneWorktrees> { }
    [Command(PackageIds.MruNewChat)]
    internal sealed class MruNewChat : MruItemCommand<MruNewChat> { }

    [Command(PackageIds.MruWorktreesMenu)]
    internal sealed class MruWorktrees : BaseCommand<MruWorktrees>
    {
        protected override void BeforeQueryStatus(EventArgs e) => MruItemMenu.Update(Command, PackageIds.MruWorktreesMenu);
    }

    [Command(PackageIds.MruOpenWorktree)]
    internal sealed class MruOpenWorktree : BaseDynamicCommand<MruOpenWorktree, GitWorktreeInfo>
    {
        protected override IReadOnlyList<GitWorktreeInfo> GetItems() => WorktreeCommands.GetItems();
        protected override void BeforeQueryStatus(OleMenuCommand command, EventArgs e, GitWorktreeInfo item) => WorktreeCommands.Update(command, item);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e, GitWorktreeInfo item)
            => WorktreeCommands.OpenAsync(item, WorktreeCommands.IsNewInstanceRequested(Keyboard.Modifiers));
    }

    internal static class WorktreeCommands
    {
        internal static IReadOnlyList<GitWorktreeInfo> GetItems() => MruItemMenu.Current?.Selection.Count == 1
            ? MruItemMenu.Current.Worktrees : Array.Empty<GitWorktreeInfo>();

        internal static bool IsNewInstanceRequested(ModifierKeys modifiers) => (modifiers & ModifierKeys.Shift) != 0;

        internal static void Update(OleMenuCommand command, GitWorktreeInfo item)
        {
            command.Text = item.MenuText;
            command.Checked = item.IsCurrent;
            command.Visible = true;
            command.Enabled = item.CanOpen;
        }

        internal static async Task OpenAsync(GitWorktreeInfo item, bool newInstance)
        {
            try
            {
                if (!item.CanOpen || !System.IO.Directory.Exists(item.Path))
                    throw new System.IO.DirectoryNotFoundException("This worktree is not an available working folder.");
                if (newInstance)
                    VsCommandService.OpenInNewInstance(item.Path);
                else
                    await VsCommandService.OpenPathAsync(item.Path);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not open worktree. " + ex.Message);
            }
        }
    }
}
