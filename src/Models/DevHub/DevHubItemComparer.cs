using System;
using System.Collections.Generic;

namespace StartScreen.Models.DevHub
{
    internal static class DevHubItemComparer
    {
        public static bool SamePullRequests(IReadOnlyList<DevHubPullRequest> left, IReadOnlyList<DevHubPullRequest> right)
        {
            return SequenceEqual(left, right, SamePullRequest);
        }

        public static bool SameIssues(IReadOnlyList<DevHubIssue> left, IReadOnlyList<DevHubIssue> right)
        {
            return SequenceEqual(left, right, SameIssue);
        }

        public static bool SameCiRuns(IReadOnlyList<DevHubCiRun> left, IReadOnlyList<DevHubCiRun> right)
        {
            return SequenceEqual(left, right, SameCiRun);
        }

        private static bool SequenceEqual<T>(IReadOnlyList<T> left, IReadOnlyList<T> right, Func<T, T, bool> itemEquals)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                if (!itemEquals(left[i], right[i]))
                    return false;
            }

            return true;
        }

        private static bool SamePullRequest(DevHubPullRequest left, DevHubPullRequest right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;

            return StringEquals(left.ProviderName, right.ProviderName)
                && StringEquals(left.RepoDisplayName, right.RepoDisplayName)
                && Equals(left.RepoIdentifier, right.RepoIdentifier)
                && StringEquals(left.Title, right.Title)
                && StringEquals(left.Number, right.Number)
                && left.NumericId == right.NumericId
                && StringEquals(left.Author, right.Author)
                && StringEquals(left.TargetBranch, right.TargetBranch)
                && StringEquals(left.SourceBranch, right.SourceBranch)
                && StringEquals(left.Status, right.Status)
                && StringEquals(left.CiStatus, right.CiStatus)
                && left.ApprovalCount == right.ApprovalCount
                && left.UpdatedAt == right.UpdatedAt
                && left.CreatedAt == right.CreatedAt
                && StringEquals(left.WebUrl, right.WebUrl)
                && left.IsAuthoredByCurrentUser == right.IsAuthoredByCurrentUser;
        }

        private static bool SameIssue(DevHubIssue left, DevHubIssue right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;

            return StringEquals(left.ProviderName, right.ProviderName)
                && StringEquals(left.RepoDisplayName, right.RepoDisplayName)
                && Equals(left.RepoIdentifier, right.RepoIdentifier)
                && StringEquals(left.Title, right.Title)
                && StringEquals(left.Number, right.Number)
                && left.NumericId == right.NumericId
                && StringEquals(left.Author, right.Author)
                && SameLabels(left.Labels, right.Labels)
                && StringEquals(left.State, right.State)
                && StringEquals(left.Priority, right.Priority)
                && left.UpdatedAt == right.UpdatedAt
                && left.CreatedAt == right.CreatedAt
                && StringEquals(left.WebUrl, right.WebUrl);
        }

        private static bool SameCiRun(DevHubCiRun left, DevHubCiRun right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;

            return StringEquals(left.ProviderName, right.ProviderName)
                && StringEquals(left.RepoDisplayName, right.RepoDisplayName)
                && Equals(left.RepoIdentifier, right.RepoIdentifier)
                && StringEquals(left.Name, right.Name)
                && StringEquals(left.Branch, right.Branch)
                && StringEquals(left.Status, right.Status)
                && left.Timestamp == right.Timestamp
                && StringEquals(left.WebUrl, right.WebUrl);
        }

        private static bool SameLabels(IReadOnlyList<DevHubLabel> left, IReadOnlyList<DevHubLabel> right)
        {
            return SequenceEqual(left, right, SameLabel);
        }

        private static bool SameLabel(DevHubLabel left, DevHubLabel right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;

            return StringEquals(left.Name, right.Name)
                && StringEquals(left.Color, right.Color);
        }

        private static bool StringEquals(string left, string right)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }
}
