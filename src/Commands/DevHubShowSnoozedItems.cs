namespace StartScreen.Commands
{
    [Command(PackageIds.DevHubShowSnoozedItems)]
    internal sealed class DevHubShowSnoozedItems : BaseCommand<DevHubShowSnoozedItems>
    {
        protected override void BeforeQueryStatus(EventArgs e) => DevHubViewMenu.Update(Command);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => DevHubViewMenu.ExecuteAsync();
    }
}
