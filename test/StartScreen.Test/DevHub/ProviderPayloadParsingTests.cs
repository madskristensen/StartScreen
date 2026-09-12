using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class ProviderPayloadParsingTests
    {
        private static readonly RemoteRepoIdentifier _gitHubRepo =
            new RemoteRepoIdentifier("github.com", "owner", "repo");

        private static readonly RemoteRepoIdentifier _bitbucketRepo =
            new RemoteRepoIdentifier("bitbucket.org", "owner", "repo");

        private static readonly RemoteRepoIdentifier _azureDevOpsRepo =
            new RemoteRepoIdentifier("dev.azure.com", "organization", "repo", "project");

        [TestMethod]
        public void GitHubPullRequests_MapsApiPayload()
        {
            const string json =
                "{\"items\":[{" +
                "\"repository_url\":\"https://api.github.com/repos/owner/repo\"," +
                "\"number\":25,\"title\":\"Fix startup\",\"user\":{\"login\":\"octocat\"}," +
                "\"draft\":true,\"updated_at\":\"2026-09-12T10:00:00Z\"," +
                "\"created_at\":\"2026-09-11T10:00:00Z\"," +
                "\"html_url\":\"https://github.com/owner/repo/pull/25\"}]}";

            List<DevHubPullRequest> result = GitHubDevHubProvider.ParsePullRequests(json, "octocat");

            Assert.HasCount(1, result);
            Assert.AreEqual("owner/repo", result[0].RepoDisplayName);
            Assert.AreEqual("#25", result[0].Number);
            Assert.AreEqual("draft", result[0].Status);
            Assert.IsTrue(result[0].IsAuthoredByCurrentUser);
        }

        [TestMethod]
        public void GitHubIssues_SkipsPullRequestsAndMapsLabels()
        {
            const string json =
                "{\"items\":[" +
                "{\"pull_request\":{},\"repository_url\":\"https://api.github.com/repos/owner/repo\"}," +
                "{\"repository_url\":\"https://api.github.com/repos/owner/repo\"," +
                "\"number\":12,\"title\":\"Bug\",\"user\":{\"login\":\"reporter\"}," +
                "\"labels\":[{\"name\":\"bug\",\"color\":\"d73a4a\"}]," +
                "\"updated_at\":\"2026-09-12T10:00:00Z\",\"created_at\":\"2026-09-11T10:00:00Z\"," +
                "\"html_url\":\"https://github.com/owner/repo/issues/12\"}]}";

            List<DevHubIssue> result = GitHubDevHubProvider.ParseIssues(json);

            Assert.HasCount(1, result);
            Assert.AreEqual("#12", result[0].Number);
            Assert.AreEqual("bug", result[0].LabelText);
        }

        [TestMethod]
        [DataRow("in_progress", null, "pending")]
        [DataRow("completed", "success", "success")]
        [DataRow("completed", "failure", "failure")]
        [DataRow("completed", "cancelled", "cancelled")]
        public void GitHubCiRuns_MapsStatuses(string status, string conclusion, string expected)
        {
            var conclusionJson = conclusion == null ? "null" : $"\"{conclusion}\"";
            var json =
                "{\"workflow_runs\":[{" +
                "\"name\":\"Build\",\"head_branch\":\"main\"," +
                $"\"status\":\"{status}\",\"conclusion\":{conclusionJson}," +
                "\"updated_at\":\"2026-09-12T10:00:00Z\"," +
                "\"html_url\":\"https://github.com/owner/repo/actions/runs/1\"}]}";

            List<DevHubCiRun> result = GitHubDevHubProvider.ParseCiRuns(json, _gitHubRepo);

            Assert.AreEqual(expected, result[0].Status);
            Assert.AreEqual("main", result[0].Branch);
        }

        [TestMethod]
        public void BitbucketPullRequests_MapsNestedRepositoryAndBranches()
        {
            const string json =
                "{\"values\":[{" +
                "\"id\":7,\"title\":\"Feature\",\"state\":\"OPEN\"," +
                "\"author\":{\"display_name\":\"User\",\"username\":\"user\"}," +
                "\"source\":{\"branch\":{\"name\":\"feature\"}}," +
                "\"destination\":{\"branch\":{\"name\":\"main\"},\"repository\":{\"full_name\":\"owner/repo\"}}," +
                "\"links\":{\"html\":{\"href\":\"https://bitbucket.org/owner/repo/pull-requests/7\"}}," +
                "\"updated_on\":\"2026-09-12T10:00:00Z\",\"created_on\":\"2026-09-11T10:00:00Z\"}]}";

            List<DevHubPullRequest> result = BitbucketDevHubProvider.ParsePullRequests(json, "user");

            Assert.HasCount(1, result);
            Assert.AreEqual("owner/repo", result[0].RepoDisplayName);
            Assert.AreEqual("feature", result[0].SourceBranch);
            Assert.AreEqual("main", result[0].TargetBranch);
            Assert.IsTrue(result[0].IsAuthoredByCurrentUser);
        }

        [TestMethod]
        public void BitbucketIssues_MapsPriorityAndReporter()
        {
            const string json =
                "{\"values\":[{" +
                "\"id\":8,\"title\":\"Issue\",\"state\":\"open\",\"priority\":\"major\"," +
                "\"reporter\":{\"display_name\":\"Reporter\"}," +
                "\"links\":{\"html\":{\"href\":\"https://bitbucket.org/owner/repo/issues/8\"}}," +
                "\"updated_on\":\"2026-09-12T10:00:00Z\",\"created_on\":\"2026-09-11T10:00:00Z\"}]}";

            List<DevHubIssue> result = BitbucketDevHubProvider.ParseRepoIssues(json, _bitbucketRepo);

            Assert.HasCount(1, result);
            Assert.AreEqual("major", result[0].Priority);
            Assert.AreEqual("Reporter", result[0].Author);
        }

        [TestMethod]
        public void BitbucketPipelines_MapsCompletedResult()
        {
            const string json =
                "{\"values\":[{" +
                "\"build_number\":42,\"state\":{\"name\":\"COMPLETED\",\"result\":{\"name\":\"SUCCESSFUL\"}}," +
                "\"target\":{\"ref_name\":\"main\"},\"completed_on\":\"2026-09-12T10:00:00Z\"}]}";

            List<DevHubCiRun> result = BitbucketDevHubProvider.ParsePipelines(json, _bitbucketRepo);

            Assert.HasCount(1, result);
            Assert.AreEqual("success", result[0].Status);
            Assert.AreEqual("Pipeline #42", result[0].Name);
        }

        [TestMethod]
        public void AzureDevOpsPullRequests_MapsBranchesStatusAndReviewers()
        {
            const string json =
                "{\"value\":[{" +
                "\"pullRequestId\":9,\"title\":\"Change\",\"status\":\"active\"," +
                "\"createdBy\":{\"displayName\":\"Author\"},\"reviewers\":[{},{}]," +
                "\"sourceRefName\":\"refs/heads/feature\",\"targetRefName\":\"refs/heads/main\"," +
                "\"creationDate\":\"2026-09-11T10:00:00Z\"}]}";

            List<DevHubPullRequest> result =
                AzureDevOpsDevHubProvider.ParseAdoPullRequests(json, _azureDevOpsRepo);

            Assert.HasCount(1, result);
            Assert.AreEqual("open", result[0].Status);
            Assert.AreEqual("feature", result[0].SourceBranch);
            Assert.AreEqual("main", result[0].TargetBranch);
            Assert.AreEqual(2, result[0].ApprovalCount);
        }

        [TestMethod]
        [DataRow("inProgress", null, "pending")]
        [DataRow("completed", "succeeded", "success")]
        [DataRow("completed", "failed", "failure")]
        [DataRow("completed", "canceled", "cancelled")]
        public void AzureDevOpsBuilds_MapsStatuses(string status, string resultValue, string expected)
        {
            var resultJson = resultValue == null ? "null" : $"\"{resultValue}\"";
            var json =
                "{\"value\":[{" +
                "\"definition\":{\"name\":\"Build\"},\"sourceBranch\":\"refs/heads/main\"," +
                $"\"status\":\"{status}\",\"result\":{resultJson}," +
                "\"startTime\":\"2026-09-12T09:00:00Z\",\"finishTime\":\"2026-09-12T10:00:00Z\"," +
                "\"_links\":{\"web\":{\"href\":\"https://dev.azure.com/build/1\"}}}]}";

            List<DevHubCiRun> result =
                AzureDevOpsDevHubProvider.ParseAdoBuilds(json, _azureDevOpsRepo);

            Assert.AreEqual(expected, result[0].Status);
            Assert.AreEqual("main", result[0].Branch);
        }

        [TestMethod]
        public void ProviderParsers_WithMissingCollections_ReturnEmpty()
        {
            Assert.IsEmpty(GitHubDevHubProvider.ParsePullRequests("{}", "user"));
            Assert.IsEmpty(GitHubDevHubProvider.ParseIssues("{}"));
            Assert.IsEmpty(GitHubDevHubProvider.ParseCiRuns("{}", _gitHubRepo));
            Assert.IsEmpty(BitbucketDevHubProvider.ParsePullRequests("{}", "user"));
            Assert.IsEmpty(BitbucketDevHubProvider.ParseRepoIssues("{}", _bitbucketRepo));
            Assert.IsEmpty(BitbucketDevHubProvider.ParsePipelines("{}", _bitbucketRepo));
            Assert.IsEmpty(AzureDevOpsDevHubProvider.ParseAdoPullRequests("{}", _azureDevOpsRepo));
            Assert.IsEmpty(AzureDevOpsDevHubProvider.ParseAdoBuilds("{}", _azureDevOpsRepo));
        }
    }
}
