using StartScreen.Models;

namespace StartScreen.Commands
{
    internal sealed class CopilotSessionMenu
    {
        internal static CopilotSessionMenu Current { get; set; }
        internal CopilotActivitySession Session { get; set; }
        internal string CliUnavailableReason { get; set; }
        internal Func<CopilotSessionOrigin, Task> OpenAsync { get; set; }

        internal static void Update(OleMenuCommand command, CopilotSessionOrigin destination)
        {
            var target = Current;
            command.Visible = target != null && Services.CopilotSessionLauncher.Supports(target.Session.Origin, destination);
            command.Enabled = command.Visible && (destination != CopilotSessionOrigin.Cli || target.CliUnavailableReason == null);
            if (destination == CopilotSessionOrigin.Cli)
                command.Text = target?.CliUnavailableReason == null ? "Open in CLI" : "Open in CLI (session not released)";
        }

        internal static Task ExecuteAsync(CopilotSessionOrigin destination)
        {
            var target = Current ?? throw new InvalidOperationException("The Copilot context menu is no longer available.");
            return target.OpenAsync(destination);
        }
    }

    [Command(PackageIds.OpenCopilotInVisualStudio)]
    internal sealed class OpenCopilotInVisualStudio : BaseCommand<OpenCopilotInVisualStudio>
    {
        protected override void BeforeQueryStatus(EventArgs e) => CopilotSessionMenu.Update(Command, CopilotSessionOrigin.VisualStudio);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => CopilotSessionMenu.ExecuteAsync(CopilotSessionOrigin.VisualStudio);
    }

    [Command(PackageIds.OpenCopilotInCode)]
    internal sealed class OpenCopilotInCode : BaseCommand<OpenCopilotInCode>
    {
        protected override void BeforeQueryStatus(EventArgs e) => CopilotSessionMenu.Update(Command, CopilotSessionOrigin.VisualStudioCode);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => CopilotSessionMenu.ExecuteAsync(CopilotSessionOrigin.VisualStudioCode);
    }

    [Command(PackageIds.OpenCopilotInCli)]
    internal sealed class OpenCopilotInCli : BaseCommand<OpenCopilotInCli>
    {
        protected override void BeforeQueryStatus(EventArgs e) => CopilotSessionMenu.Update(Command, CopilotSessionOrigin.Cli);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => CopilotSessionMenu.ExecuteAsync(CopilotSessionOrigin.Cli);
    }

    [Command(PackageIds.OpenCopilotOnGitHub)]
    internal sealed class OpenCopilotOnGitHub : BaseCommand<OpenCopilotOnGitHub>
    {
        protected override void BeforeQueryStatus(EventArgs e) => CopilotSessionMenu.Update(Command, CopilotSessionOrigin.GitHub);
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e) => CopilotSessionMenu.ExecuteAsync(CopilotSessionOrigin.GitHub);
    }
}