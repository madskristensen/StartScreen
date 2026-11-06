using System.Collections.Generic;
using System.Linq;
using StartScreen.Models;
using StartScreen.Models.DevHub;

namespace StartScreen.Services
{
    internal static class WorkspaceActivity
    {
        internal static IReadOnlyList<DevHubPullRequest> MatchBranch(MruItem item, IEnumerable<DevHubPullRequest> pullRequests, bool includeClosed = false)
        {
            var repository = RemoteRepoIdentifier.TryParse(item.RemoteUrl);
            if (repository == null || !item.HasGitBranch || item.IsDetachedHead)
                return Array.Empty<DevHubPullRequest>();
            return pullRequests.Where(pr => (pr.IsOpen || includeClosed) && !string.IsNullOrEmpty(pr.WebUrl) && repository.Equals(pr.SourceRepoIdentifier)
                    && string.Equals(pr.SourceBranch, item.GitBranch, StringComparison.Ordinal))
                .GroupBy(pr => pr.WebUrl, StringComparer.Ordinal).Select(group => group.First())
                .OrderByDescending(pr => pr.UpdatedAt).ToArray();
        }

        internal static string Format(MruItem item, IEnumerable<DevHubPullRequest> pullRequests, bool incomplete)
        {
            var matching = MatchBranch(item, pullRequests);
            if (matching.Count == 0)
                return null;
            var lines = new List<string>();
            foreach (var pr in matching.Take(2))
            {
                lines.Add($"Branch PR {pr.Number}: {Brief(pr.Title)}");
                lines.Add(pr.StatusText);
                if (!string.IsNullOrEmpty(pr.CiStatus))
                {
                    var sameCommit = !string.IsNullOrEmpty(item.HeadCommitId) && !string.IsNullOrEmpty(pr.ChecksCommitId)
                        && string.Equals(item.HeadCommitId, pr.ChecksCommitId, StringComparison.OrdinalIgnoreCase);
                    lines.Add($"PR checks: {pr.CiStatus} ({(sameCommit ? "local HEAD" : string.IsNullOrEmpty(pr.ChecksCommitId) ? "commit not verified" : "different commit")})");
                }
                if (!string.IsNullOrEmpty(item.HeadCommitId) && !string.IsNullOrEmpty(pr.HeadCommitId)
                    && !string.Equals(item.HeadCommitId, pr.HeadCommitId, StringComparison.OrdinalIgnoreCase))
                    lines.Add("Local HEAD differs from the PR head.");
                if (pr.UnresolvedThreadCount > 0)
                    lines.Add($"Unresolved discussions: {pr.UnresolvedThreadCount}");
                if (pr.LinkedWorkItems != null)
                {
                    foreach (var link in pr.LinkedWorkItems.Take(2))
                        lines.Add("Linked: " + Brief(string.IsNullOrWhiteSpace(link.Value) ? link.Key : link.Value));
                    if (pr.LinkedWorkItems.Count > 2)
                        lines.Add($"And {pr.LinkedWorkItems.Count - 2} more linked items.");
                }
            }
            if (matching.Count > 2)
                lines.Add($"And {matching.Count - 2} more branch PRs in loaded activity.");
            lines.Add(incomplete ? "Cached or incomplete activity. Refresh for current details."
                : "From loaded activity; not a complete PR inventory.");
            return string.Join(Environment.NewLine, lines);
        }

        private static string Brief(string text)
        {
            var line = string.Join(" ", (text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            return line.Length > 160 ? line.Substring(0, 157) + "..." : line;
        }
    }
}
