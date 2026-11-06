namespace StartScreen.Commands
{
    [Command(PackageIds.DevHubNewCopilotChat)]
    internal sealed class DevHubNewCopilotChat : BaseCommand<DevHubNewCopilotChat>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubWorkItemMenu.Update(Command, PackageIds.DevHubNewCopilotChat);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubNewCopilotChat);
    }
}
