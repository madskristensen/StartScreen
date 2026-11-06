using StartScreen.Models.DevHub;

namespace StartScreen.Services
{
    internal static class CopilotWorkItemPrompt
    {
        internal static bool CanStartChat(DevHubWorkItem item) => item != null
            && (item.Kind == DevHubWorkItemKind.Issue || item.IsFailedBuild);

        internal static string Create(DevHubWorkItem item)
        {
            if (!CanStartChat(item))
                throw new InvalidOperationException("Select an issue or a failed build to start a Copilot chat.");
            var url = PullRequestNavigationService.GetValidatedUri(item.WebUrl).AbsoluteUri;
            var workspace = "Confirm the current workspace matches the repository before making changes. "
                + "If you cannot access the linked details, ask me for them rather than guessing.";
            if (item.Kind == DevHubWorkItemKind.Issue)
                return "Fix " + url + Environment.NewLine
                    + "Read the issue, investigate the cause, implement the fix, and run the relevant tests. " + workspace;

            var details = string.IsNullOrWhiteSpace(item.FailureUrl) ? string.Empty
                : Environment.NewLine + "Failure details: " + PullRequestNavigationService.GetValidatedUri(item.FailureUrl).AbsoluteUri;
            return "Investigate the failed build " + url + Environment.NewLine
                + "Build: " + item.Title + Environment.NewLine
                + "Repository: " + item.Detail + Environment.NewLine + item.StatusDetails + details + Environment.NewLine
                + "Read the failing jobs and logs, identify the root cause, and propose a fix with relevant validation. " + workspace;
        }
    }
}