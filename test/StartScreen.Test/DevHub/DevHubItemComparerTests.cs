using StartScreen.Models.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubItemComparerTests
    {
        [TestMethod]
        public void SamePullRequests_WithSameValues_ReturnsTrue()
        {
            var left = new List<DevHubPullRequest>
            {
                CreatePullRequest("#1")
            };
            var right = new List<DevHubPullRequest>
            {
                CreatePullRequest("#1")
            };

            Assert.IsTrue(DevHubItemComparer.SamePullRequests(left, right));
        }

        [TestMethod]
        public void SamePullRequests_WithChangedStatus_ReturnsFalse()
        {
            var left = new List<DevHubPullRequest>
            {
                CreatePullRequest("#1", status: "open")
            };
            var right = new List<DevHubPullRequest>
            {
                CreatePullRequest("#1", status: "draft")
            };

            Assert.IsFalse(DevHubItemComparer.SamePullRequests(left, right));
        }

        [TestMethod]
        public void SameIssues_WithSameLabels_ReturnsTrue()
        {
            var left = new List<DevHubIssue>
            {
                CreateIssue("#1")
            };
            var right = new List<DevHubIssue>
            {
                CreateIssue("#1")
            };

            Assert.IsTrue(DevHubItemComparer.SameIssues(left, right));
        }

        [TestMethod]
        public void SameIssues_WithChangedLabel_ReturnsFalse()
        {
            var left = new List<DevHubIssue>
            {
                CreateIssue("#1", labelName: "bug")
            };
            var right = new List<DevHubIssue>
            {
                CreateIssue("#1", labelName: "enhancement")
            };

            Assert.IsFalse(DevHubItemComparer.SameIssues(left, right));
        }

        [TestMethod]
        public void SameCiRuns_WithSameValues_ReturnsTrue()
        {
            var left = new List<DevHubCiRun>
            {
                CreateCiRun("build")
            };
            var right = new List<DevHubCiRun>
            {
                CreateCiRun("build")
            };

            Assert.IsTrue(DevHubItemComparer.SameCiRuns(left, right));
        }

        [TestMethod]
        public void SameCiRuns_WithChangedTimestamp_ReturnsFalse()
        {
            var left = new List<DevHubCiRun>
            {
                CreateCiRun("build", timestamp: new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc))
            };
            var right = new List<DevHubCiRun>
            {
                CreateCiRun("build", timestamp: new DateTime(2025, 1, 1, 2, 0, 0, DateTimeKind.Utc))
            };

            Assert.IsFalse(DevHubItemComparer.SameCiRuns(left, right));
        }

        [TestMethod]
        public void SamePullRequests_WithDifferentOrder_ReturnsFalse()
        {
            var left = new List<DevHubPullRequest>
            {
                CreatePullRequest("#1"),
                CreatePullRequest("#2")
            };
            var right = new List<DevHubPullRequest>
            {
                CreatePullRequest("#2"),
                CreatePullRequest("#1")
            };

            Assert.IsFalse(DevHubItemComparer.SamePullRequests(left, right));
        }

        private static DevHubPullRequest CreatePullRequest(string number, string status = "open")
        {
            return new DevHubPullRequest
            {
                ProviderName = "GitHub",
                RepoDisplayName = "owner/repo",
                RepoIdentifier = new RemoteRepoIdentifier("github.com", "owner", "repo"),
                Title = "Pull request",
                Number = number,
                NumericId = int.Parse(number.Substring(1)),
                Author = "user",
                TargetBranch = "main",
                SourceBranch = "feature",
                Status = status,
                CiStatus = "success",
                ApprovalCount = 1,
                UpdatedAt = new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                WebUrl = "https://github.com/owner/repo/pull/1",
                IsAuthoredByCurrentUser = true
            };
        }

        private static DevHubIssue CreateIssue(string number, string labelName = "bug")
        {
            return new DevHubIssue
            {
                ProviderName = "GitHub",
                RepoDisplayName = "owner/repo",
                RepoIdentifier = new RemoteRepoIdentifier("github.com", "owner", "repo"),
                Title = "Issue",
                Number = number,
                NumericId = int.Parse(number.Substring(1)),
                Author = "user",
                Labels = new List<DevHubLabel>
                {
                    new DevHubLabel { Name = labelName, Color = "d73a4a" }
                },
                State = "open",
                Priority = "P1",
                UpdatedAt = new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                WebUrl = "https://github.com/owner/repo/issues/1"
            };
        }

        private static DevHubCiRun CreateCiRun(string name, DateTime? timestamp = null)
        {
            return new DevHubCiRun
            {
                ProviderName = "GitHub",
                RepoDisplayName = "owner/repo",
                RepoIdentifier = new RemoteRepoIdentifier("github.com", "owner", "repo"),
                Name = name,
                Branch = "main",
                Status = "success",
                Timestamp = timestamp ?? new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc),
                WebUrl = "https://github.com/owner/repo/actions/runs/1"
            };
        }
    }
}
