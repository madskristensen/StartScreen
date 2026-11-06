using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using StartScreen.Models;
using StartScreen.Models.DevHub;

namespace StartScreen.Services
{
    internal static class WorkspaceVisitHistory
    {
        private static readonly SemaphoreSlim _gate = new(1, 1);
        internal static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartScreen", "workspace-visits.json");

        internal static Snapshot Capture(MruItem item, IEnumerable<DevHubPullRequest> pullRequests,
            IEnumerable<DevHubIssue> issues, string context, DateTime visitedAtUtc)
        {
            var prs = WorkspaceActivity.MatchBranch(item, pullRequests, includeClosed: true);
            var links = new HashSet<string>(prs.SelectMany(pr => pr.LinkedWorkItems?.Keys.AsEnumerable() ?? Enumerable.Empty<string>()), StringComparer.Ordinal);
            return new Snapshot
            {
                Path = NormalizePath(item.Path), Repository = RemoteRepoIdentifier.TryParse(item.RemoteUrl)?.ToString(),
                Branch = item.GitBranch, Context = context, VisitedAtUtc = visitedAtUtc,
                PullRequests = prs.Take(100).Select(pr => new PullRequestState
                {
                    Url = pr.WebUrl, Number = pr.Number, Status = pr.IsMerged ? "Merged" : pr.Status,
                    Review = pr.HasChangesRequested ? "Changes requested" : pr.IsApproved ? "Approved" : null,
                    Checks = pr.CiStatus, Commit = pr.HeadCommitId, Threads = pr.UnresolvedThreadCount,
                }).ToList(),
                Issues = issues.Where(issue => !string.IsNullOrEmpty(issue.WebUrl) && links.Contains(issue.WebUrl))
                    .GroupBy(issue => issue.WebUrl, StringComparer.Ordinal).Take(100)
                    .ToDictionary(group => group.Key, group => group.First().StatusText, StringComparer.Ordinal),
            };
        }

        internal static string Compare(Snapshot previous, Snapshot current, bool incomplete)
        {
            if (previous == null || current == null || string.IsNullOrEmpty(current.Context)
                || !string.Equals(previous.Path, current.Path, StringComparison.OrdinalIgnoreCase)
                || previous.Repository != current.Repository || previous.Branch != current.Branch || previous.Context != current.Context)
                return null;
            var changes = new List<string>();
            foreach (var pr in current.PullRequests)
            {
                var old = previous.PullRequests.FirstOrDefault(candidate => candidate.Url == pr.Url);
                if (old == null)
                {
                    changes.Add($"PR {pr.Number} is newly present in loaded activity.");
                    continue;
                }
                if (!string.IsNullOrEmpty(old.Status) && !string.IsNullOrEmpty(pr.Status) && old.Status != pr.Status)
                    changes.Add($"PR {pr.Number}: {old.Status} -> {pr.Status}.");
                if (!string.IsNullOrEmpty(old.Checks) && !string.IsNullOrEmpty(pr.Checks) && old.Checks != pr.Checks)
                    changes.Add($"PR {pr.Number} checks: {old.Checks} -> {pr.Checks}.");
                if (!string.IsNullOrEmpty(old.Review) && !string.IsNullOrEmpty(pr.Review) && old.Review != pr.Review)
                    changes.Add($"PR {pr.Number} review: {old.Review} -> {pr.Review}.");
                if (old.Threads.HasValue && pr.Threads.HasValue && old.Threads != pr.Threads)
                    changes.Add($"PR {pr.Number} unresolved discussions: {old.Threads} -> {pr.Threads}.");
                if (!string.IsNullOrEmpty(old.Commit) && !string.IsNullOrEmpty(pr.Commit) && old.Commit != pr.Commit)
                    changes.Add($"PR {pr.Number} has a different head commit.");
            }
            foreach (var issue in current.Issues)
            {
                if (previous.Issues.TryGetValue(issue.Key, out var old) && !string.IsNullOrEmpty(old)
                    && !string.IsNullOrEmpty(issue.Value) && old != issue.Value)
                    changes.Add($"Linked work item: {old} -> {issue.Value} ({issue.Key}).");
            }
            if (changes.Count == 0)
                return null;
            var lines = new List<string> { $"Since opened {previous.VisitedAtUtc.ToLocalTime():g}:" };
            lines.AddRange(changes.Take(4).Select(change => change.Length > 240 ? change.Substring(0, 237) + "..." : change));
            if (changes.Count > 4)
                lines.Add($"And {changes.Count - 4} more changes in loaded activity.");
            if (incomplete)
                lines.Add("Partial comparison: cached or incomplete activity.");
            return string.Join(Environment.NewLine, lines);
        }

        internal static async System.Threading.Tasks.Task<List<Snapshot>> ReadAsync(string path, CancellationToken cancellationToken)
        {
            return await AtomicJsonCache.ReadAsync<List<Snapshot>>(path, cancellationToken) ?? new List<Snapshot>();
        }

        internal static async Task SaveAsync(string path, Snapshot snapshot, CancellationToken cancellationToken)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.Context))
                throw new ArgumentException("A verified workspace activity snapshot is required.", nameof(snapshot));
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var entries = await ReadAsync(path, cancellationToken);
                var existing = entries.FirstOrDefault(entry => string.Equals(entry.Path, snapshot.Path, StringComparison.OrdinalIgnoreCase));
                if (existing != null && existing.VisitedAtUtc > snapshot.VisitedAtUtc)
                    return;
                var updated = entries.Where(entry => !string.Equals(entry.Path, snapshot.Path, StringComparison.OrdinalIgnoreCase))
                    .Append(snapshot).OrderByDescending(entry => entry.VisitedAtUtc).Take(500).ToArray();
                await AtomicJsonCache.WriteAsync(path, updated, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        internal static string NormalizePath(string path) => System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);

        internal sealed class Snapshot
        {
            public string Path { get; set; }
            public string Repository { get; set; }
            public string Branch { get; set; }
            public string Context { get; set; }
            public DateTime VisitedAtUtc { get; set; }
            public List<PullRequestState> PullRequests { get; set; } = new();
            public Dictionary<string, string> Issues { get; set; } = new();
        }

        internal sealed class PullRequestState
        {
            public string Url { get; set; }
            public string Number { get; set; }
            public string Status { get; set; }
            public string Review { get; set; }
            public string Checks { get; set; }
            public string Commit { get; set; }
            public int? Threads { get; set; }
        }
    }
}
