using StartScreen.Models.DevHub;
using StartScreen.Services;

namespace StartScreen.Commands
{
    internal sealed class DevHubWorkItemMenu
    {
        internal static DevHubWorkItemMenu Current { get; set; }
        internal DevHubWorkItem Item { get; set; }
        internal bool IsScoped { get; set; }
        internal string CopilotSessionId { get; set; }
        internal Func<int, Task> ExecuteActionAsync { get; set; }

        internal static void Update(OleMenuCommand command, int id)
        {
            var target = Current;
            command.Visible = target != null && (id switch
            {
                PackageIds.DevHubFixWithCopilot => CopilotWorkItemPrompt.CanStartChat(target.Item),
                PackageIds.DevHubNewCopilotChat => CopilotWorkItemPrompt.CanStartChat(target.Item) && target.CopilotSessionId != null,
                PackageIds.DevHubReviewInVisualStudio => target.Item.IsPullRequest
                    && Uri.TryCreate(target.Item.WebUrl, UriKind.Absolute, out var uri)
                    && PullRequestNavigationService.IsNativePullRequestUrl(uri),
                PackageIds.DevHubRequestCopilotReview => target.Item.IsPullRequest && !target.Item.IsCompleted
                    && CopilotReviewService.Supports(target.Item.WebUrl),
                PackageIds.DevHubScopeToRepo => !target.IsScoped,
                PackageIds.DevHubShowAllRepos => target.IsScoped,
                PackageIds.DevHubSnoozeItemDay => !target.Item.IsSnoozed,
                _ => true,
            });
            command.Enabled = command.Visible && (id == PackageIds.DevHubShowAllRepos
                || (id == PackageIds.DevHubScopeToRepo ? target.Item.RepoIdentifier != null : !string.IsNullOrWhiteSpace(target.Item.WebUrl)));
            if (id == PackageIds.DevHubScopeToRepo)
                command.Text = "Scope to " + target?.Item.RepoIdentifier?.Repo;
            if (id == PackageIds.DevHubOpenInBrowser)
                command.Text = target?.Item.IsPullRequest == true ? "Review on the web" : "Open in Browser";
            if (id == PackageIds.DevHubFixWithCopilot)
                command.Text = target?.CopilotSessionId != null ? "Continue in Copilot"
                    : target?.Item.IsFailedBuild == true ? "Investigate with Copilot" : "Fix with Copilot";
            if (id == PackageIds.DevHubPinItem)
                command.Text = target?.Item.IsPinned == true ? "Unpin from Attention" : "Pin to Attention";
            if (id == PackageIds.DevHubSnoozeItem)
                command.Text = target?.Item.IsSnoozed == true ? "Unsnooze item" : "Snooze for 1 hour";
        }

    [Command(PackageIds.DevHubReviewInVisualStudio)]
    internal sealed class DevHubReviewInVisualStudio : BaseCommand<DevHubReviewInVisualStudio>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubWorkItemMenu.Update(Command, PackageIds.DevHubReviewInVisualStudio);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubReviewInVisualStudio);
    }

    [Command(PackageIds.DevHubRequestCopilotReview)]
    internal sealed class DevHubRequestCopilotReview : BaseCommand<DevHubRequestCopilotReview>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubWorkItemMenu.Update(Command, PackageIds.DevHubRequestCopilotReview);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubRequestCopilotReview);
    }

        internal static Task ExecuteAsync(int id)
        {
            var target = Current ?? throw new InvalidOperationException("The activity context menu is no longer available.");
            return target.ExecuteActionAsync(id);
        }
    }

    [Command(PackageIds.DevHubOpenInBrowser)]
    internal sealed class DevHubOpenInBrowser : BaseCommand<DevHubOpenInBrowser>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubWorkItemMenu.Update(Command, PackageIds.DevHubOpenInBrowser);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubOpenInBrowser);
    }

    [Command(PackageIds.DevHubFixWithCopilot)]
    internal sealed class DevHubFixWithCopilot : BaseCommand<DevHubFixWithCopilot>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubWorkItemMenu.Update(Command, PackageIds.DevHubFixWithCopilot);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubFixWithCopilot);
    }

    [Command(PackageIds.DevHubCopyUrl)]
    internal sealed class DevHubCopyUrl : BaseCommand<DevHubCopyUrl>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubWorkItemMenu.Update(Command, PackageIds.DevHubCopyUrl);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubCopyUrl);
    }

    [Command(PackageIds.DevHubScopeToRepo)]
    internal sealed class DevHubScopeToRepo : BaseCommand<DevHubScopeToRepo>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubWorkItemMenu.Update(Command, PackageIds.DevHubScopeToRepo);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubScopeToRepo);
    }

    [Command(PackageIds.DevHubShowAllRepos)]
    internal sealed class DevHubShowAllRepos : BaseCommand<DevHubShowAllRepos>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubWorkItemMenu.Update(Command, PackageIds.DevHubShowAllRepos);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubShowAllRepos);
    }

    [Command(PackageIds.DevHubPinItem)]
    internal sealed class DevHubPinItem : BaseCommand<DevHubPinItem>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubWorkItemMenu.Update(Command, PackageIds.DevHubPinItem);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubPinItem);
    }

    [Command(PackageIds.DevHubSnoozeItem)]
    internal sealed class DevHubSnoozeItem : BaseCommand<DevHubSnoozeItem>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubWorkItemMenu.Update(Command, PackageIds.DevHubSnoozeItem);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubSnoozeItem);
    }

    [Command(PackageIds.DevHubSnoozeItemDay)]
    internal sealed class DevHubSnoozeItemDay : BaseCommand<DevHubSnoozeItemDay>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubWorkItemMenu.Update(Command, PackageIds.DevHubSnoozeItemDay);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubSnoozeItemDay);
    }
}