using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StartScreen.Models.DevHub
{
    internal sealed class DevHubWorkItem
    {
        public string Title { get; private set; }
        public bool IsCopilot => CopilotSessionId != null;
        public string DisplayTitle => IsCopilot
            ? string.Join(" ", (Title ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            : Title;
        public string Detail { get; private set; }
        public string StatusText { get; private set; }
        public string StatusCategory => DevHubPullRequest.GetStatusCategory(StatusText);
        public string AttentionReason { get; private set; }
        public string NextActionText { get; private set; }
        public string AuthorText { get; private set; }
        public string StatusDetails { get; private set; }
        public bool IsCompleted { get; private set; }
        public bool IsAssignedToCurrentUser { get; private set; }
        public bool NeedsAttention => !string.IsNullOrEmpty(AttentionReason);
        public string Section { get; private set; }
        public RemoteRepoIdentifier RepoIdentifier { get; private set; }
        public string BranchDisplayText { get; private set; }
        public IReadOnlyList<DevHubLabel> Labels { get; private set; } = Array.Empty<DevHubLabel>();
        public DevHubWorkItemKind Kind => IsCopilot ? DevHubWorkItemKind.Copilot
            : IsPullRequest ? DevHubWorkItemKind.PullRequest : IsBuild ? DevHubWorkItemKind.Build : DevHubWorkItemKind.Issue;
        public bool IsBuild { get; private set; }
        public bool IsRepositoryItem => !IsCopilot;
        public string CopilotStatus { get; private set; }
        public string CopilotStatusDetails { get; private set; }
        public string ActivityText => IsCopilot ? $"{UpdatedAt.ToLocalTime():MMM d} in {Detail}" : string.Empty;
        public string ActivityDateText => DevHubPullRequest.FormatActivityDate(UpdatedAt, DateTime.Now);
        public string ActivityDateToolTip => UpdatedAt == default ? string.Empty : UpdatedAt.ToLocalTime().ToString("f");
        public string ActionText => CopilotSessionId != null ? "Open in Copilot" : IsPullRequest ? "Open pull request" : "Open in browser";
        public string CopilotSessionId { get; private set; }
        public string WebUrl { get; private set; }
        public bool IsPullRequest { get; private set; }
        public DateTime UpdatedAt { get; private set; }

        internal static IReadOnlyList<DevHubWorkItem> GetCopilotItems(IEnumerable<CopilotActivitySession> sessions, bool needsAttention)
        {
            var matching = sessions.Where(session => session.NeedsAttention == needsAttention)
                .OrderByDescending(session => session.IsRunning)
                .ThenByDescending(session => session.LastActivity);
            var visible = needsAttention ? matching : matching.Where(session => session.IsRunning)
                .Concat(matching.Where(session => !session.IsRunning).Take(10));
            return visible.Select(session => new DevHubWorkItem
            {
                Title = session.Title,
                Detail = string.IsNullOrEmpty(session.WorkingDirectory) ? "Workspace not yet known"
                    : Path.GetFileName(session.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar)),
                CopilotStatus = session.StatusBadge,
                CopilotStatusDetails = session.Status,
                CopilotSessionId = session.Id,
                AttentionReason = session.NeedsAttention ? session.Status : null,
                Section = "Copilot sessions",
                UpdatedAt = session.LastActivity.UtcDateTime,
            }).ToArray();
        }

        internal static IReadOnlyList<DevHubWorkItem> GetNeedsYou(IEnumerable<DevHubPullRequest> pullRequests)
        {
            return (pullRequests ?? Enumerable.Empty<DevHubPullRequest>())
                .Where(pr => pr.NeedsAttention)
                .OrderByDescending(pr => pr.IsReviewRequestedForCurrentUser)
                .ThenByDescending(pr => pr.UpdatedAt)
                .Select(FromPullRequest)
                .ToList();
        }

        internal static IReadOnlyList<DevHubWorkItem> GetMyWork(
            IEnumerable<DevHubPullRequest> pullRequests, IEnumerable<DevHubIssue> issues,
            IEnumerable<CopilotActivitySession> sessions, DevHubSortOrder order = DevHubSortOrder.MostRecent,
            bool includeCompleted = false, IEnumerable<DevHubCiRun> builds = null)
        {
            var copilot = (sessions ?? Enumerable.Empty<CopilotActivitySession>()).ToArray();
            var prs = (pullRequests ?? Enumerable.Empty<DevHubPullRequest>()).ToArray();
            var tasks = (issues ?? Enumerable.Empty<DevHubIssue>()).ToArray();
            var active = Sort(GetOwnPullRequests(prs), order)
                .Concat(Sort(tasks
                    .Where(issue => !issue.IsClosed && (issue.IsAuthoredByCurrentUser || issue.IsAssignedToCurrentUser)
                        && !string.IsNullOrEmpty(issue.WebUrl)).Select(FromIssue), order))
                .Concat(Sort((builds ?? Enumerable.Empty<DevHubCiRun>())
                    .Where(build => build.IsTriggeredByCurrentUser && !string.IsNullOrEmpty(build.WebUrl)).Select(FromBuild), order))
                .Concat(GetCopilotItems(copilot, true).Concat(GetCopilotItems(copilot, false))
                    .OrderByDescending(item => item.CopilotStatus == "Running")
                    .ThenByDescending(item => item.UpdatedAt));
            if (!includeCompleted)
                return active.ToArray();

            var since = DateTime.UtcNow.AddDays(-14);
            var completed = prs.Where(pr => pr.IsAuthoredByCurrentUser && pr.IsMerged && !pr.IsOpen
                    && pr.UpdatedAt >= since && !string.IsNullOrEmpty(pr.WebUrl)).Select(FromPullRequest)
                .Concat(tasks.Where(issue => issue.IsClosed && (issue.IsAuthoredByCurrentUser || issue.IsAssignedToCurrentUser)
                    && issue.UpdatedAt >= since && !string.IsNullOrEmpty(issue.WebUrl)).Select(FromIssue))
                .OrderByDescending(item => item.UpdatedAt).ToArray();
            foreach (var item in completed)
                item.Section = "Recently completed";
            return active.Concat(completed).ToArray();
        }

        internal static IReadOnlyList<DevHubWorkItem> GetIncoming(
            IEnumerable<DevHubPullRequest> pullRequests, IEnumerable<DevHubIssue> issues,
            IEnumerable<DevHubCiRun> builds, DevHubSortOrder order = DevHubSortOrder.MostRecent)
        {
            var items = (pullRequests ?? Enumerable.Empty<DevHubPullRequest>())
                .Where(pr => pr.IsOpen && !pr.IsAuthoredByCurrentUser && !string.IsNullOrEmpty(pr.WebUrl))
                .Select(FromPullRequest)
                .Concat((issues ?? Enumerable.Empty<DevHubIssue>())
                    .Where(issue => !issue.IsClosed && !issue.IsAuthoredByCurrentUser && !issue.IsAssignedToCurrentUser
                        && !string.IsNullOrEmpty(issue.WebUrl)).Select(FromIssue))
                .Concat((builds ?? Enumerable.Empty<DevHubCiRun>())
                    .Where(build => !build.IsTriggeredByCurrentUser && !string.IsNullOrEmpty(build.WebUrl)).Select(FromBuild));
            var result = Sort(items, order).OrderByDescending(item => item.NeedsAttention).ToArray();
            foreach (var item in result)
                item.Section = item.NeedsAttention ? "Requests for you" : "Repository activity";
            return result;
        }

        internal static IReadOnlyList<DevHubWorkItem> Filter(IEnumerable<DevHubWorkItem> items,
            DevHubWorkItemKind kind, bool needsAttention)
        {
            var matching = items.Where(item => (kind == DevHubWorkItemKind.All || item.Kind == kind)
                && (!needsAttention || item.NeedsAttention));
            return (needsAttention ? matching.OrderByDescending(item => item.UpdatedAt) : matching).ToArray();
        }

        private static IEnumerable<DevHubWorkItem> Sort(IEnumerable<DevHubWorkItem> items, DevHubSortOrder order)
        {
            return DevHubItemSorter.Sort(items, order, item => item.RepoIdentifier, item => item.UpdatedAt);
        }

        private static DevHubWorkItem FromIssue(DevHubIssue issue)
        {
            return new DevHubWorkItem
            {
                Title = issue.Title,
                Detail = $"{issue.RepoDisplayName} / {issue.Number}",
                StatusText = string.Equals(issue.StatusText, "open", StringComparison.OrdinalIgnoreCase) ? null : issue.StatusText,
                AttentionReason = issue.AttentionReason,
                NextActionText = issue.IsBlocked && issue.NeedsAttention ? "Unblock this task" : null,
                StatusDetails = issue.StatusDetails,
                IsCompleted = issue.IsClosed,
                IsAssignedToCurrentUser = issue.IsAssignedToCurrentUser && !issue.IsClosed,
                RepoIdentifier = issue.RepoIdentifier,
                Labels = issue.Labels ?? new List<DevHubLabel>(),
                Section = "Issues and tasks",
                WebUrl = issue.WebUrl,
                UpdatedAt = issue.UpdatedAt,
            };
        }

        private static DevHubWorkItem FromBuild(DevHubCiRun build)
        {
            return new DevHubWorkItem
            {
                Title = build.Name,
                Detail = $"{build.RepoDisplayName} / {build.Branch}",
                StatusText = $"Build {build.Status}",
                AttentionReason = build.IsTriggeredByCurrentUser && build.Status == "failure" ? "Build failing" : null,
                NextActionText = build.Status == "failure" ? build.FailureSummary : null,
                StatusDetails = build.FailureSummary,
                RepoIdentifier = build.RepoIdentifier,
                WebUrl = build.WebUrl,
                IsBuild = true,
                Section = "Builds",
                UpdatedAt = build.Timestamp,
            };
        }

        internal static IReadOnlyList<DevHubWorkItem> GetOwnPullRequests(IEnumerable<DevHubPullRequest> pullRequests)
        {
            return (pullRequests ?? Enumerable.Empty<DevHubPullRequest>())
                .Where(pr => pr.IsAuthoredByCurrentUser && pr.IsOpen
                    && !string.IsNullOrEmpty(pr.WebUrl))
                .OrderByDescending(pr => pr.UpdatedAt)
                .Select(FromPullRequest)
                .ToArray();
        }

        private static DevHubWorkItem FromPullRequest(DevHubPullRequest pr)
        {
            return new DevHubWorkItem
            {
                Title = pr.Title,
                Detail = $"{pr.RepoDisplayName} / {pr.Number}",
                StatusText = pr.StatusText,
                AttentionReason = pr.AttentionReason,
                AuthorText = pr.AuthorText,
                StatusDetails = pr.StatusDetails,
                IsCompleted = !pr.IsOpen,
                RepoIdentifier = pr.RepoIdentifier,
                BranchDisplayText = pr.BranchDisplayText,
                Section = "Pull requests",
                WebUrl = pr.WebUrl,
                IsPullRequest = true,
                UpdatedAt = pr.UpdatedAt,
            };
        }
    }

    internal enum DevHubWorkItemKind
    {
        All,
        PullRequest,
        Issue,
        Copilot,
        Build
    }
}