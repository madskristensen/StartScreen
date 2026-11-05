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
        public string CopilotStatus { get; private set; }
        public string CopilotStatusDetails { get; private set; }
        public string ActivityText => IsCopilot ? $"{UpdatedAt.ToLocalTime():MMM d} in {Detail}" : string.Empty;
        public string ActivityDateText => IsPullRequest ? DevHubPullRequest.FormatActivityDate(UpdatedAt, DateTime.Now) : string.Empty;
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
                UpdatedAt = session.LastActivity.UtcDateTime,
            }).ToArray();
        }

        internal static IReadOnlyList<DevHubWorkItem> GetNeedsYou(IEnumerable<DevHubPullRequest> pullRequests)
        {
            return (pullRequests ?? Enumerable.Empty<DevHubPullRequest>())
                .Where(pr => !pr.IsDraft && (pr.IsReviewRequestedForCurrentUser
                    || (pr.IsAuthoredByCurrentUser && (pr.CiStatus == "failure" || pr.Status == "conflicts"))))
                .OrderByDescending(pr => pr.IsReviewRequestedForCurrentUser)
                .ThenByDescending(pr => pr.UpdatedAt)
                .Select(FromPullRequest)
                .ToList();
        }

        internal static IReadOnlyList<DevHubWorkItem> GetChanges(
            IEnumerable<DevHubPullRequest> pullRequests, IEnumerable<DevHubIssue> issues,
            IEnumerable<DevHubCiRun> builds, DateTime since)
        {
            var prs = (pullRequests ?? Enumerable.Empty<DevHubPullRequest>()).ToArray();
            var attentionUrls = new HashSet<string>(GetNeedsYou(prs).Select(item => item.WebUrl), StringComparer.OrdinalIgnoreCase);
            return prs.Where(pr => !pr.IsAuthoredByCurrentUser && !attentionUrls.Contains(pr.WebUrl))
                .Select(FromPullRequest)
                .Concat((issues ?? Enumerable.Empty<DevHubIssue>()).Select(issue => new DevHubWorkItem
                {
                    Title = issue.Title,
                    Detail = $"Issue updated / {issue.RepoDisplayName} / {issue.Number}",
                    WebUrl = issue.WebUrl,
                    UpdatedAt = issue.UpdatedAt,
                }))
                .Concat((builds ?? Enumerable.Empty<DevHubCiRun>()).Select(build => new DevHubWorkItem
                {
                    Title = build.Name,
                    Detail = $"Build {build.Status} / {build.RepoDisplayName} / {build.Branch}",
                    WebUrl = build.WebUrl,
                    UpdatedAt = build.Timestamp,
                }))
                .Where(item => item.UpdatedAt > since && !string.IsNullOrEmpty(item.WebUrl))
                .OrderByDescending(item => item.UpdatedAt)
                .Take(10)
                .ToList();
        }

        internal static IReadOnlyList<DevHubWorkItem> GetOwnPullRequests(IEnumerable<DevHubPullRequest> pullRequests)
        {
            return (pullRequests ?? Enumerable.Empty<DevHubPullRequest>())
                .Where(pr => pr.IsAuthoredByCurrentUser && (pr.Status == "open" || pr.IsDraft || pr.Status == "conflicts")
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
                WebUrl = pr.WebUrl,
                IsPullRequest = true,
                UpdatedAt = pr.UpdatedAt,
            };
        }
    }
}