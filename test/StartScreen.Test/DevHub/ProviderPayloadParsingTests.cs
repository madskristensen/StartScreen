using System.Text.Json;
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
        [DataRow("completed", "2026-09-12T09:57:45Z", 135d)]
        [DataRow("completed", "2026-09-12T11:57:45+02:00", 135d)]
        [DataRow("completed", null, null)]
        [DataRow("completed", "2026-09-12T10:01:00Z", null)]
        [DataRow("queued", "2026-09-12T09:57:45Z", null)]
        [DataRow("in_progress", "2026-09-12T09:57:45Z", null)]
        public void GitHubCiRuns_DurationRequiresCompletedRunAndValidStart(string status, string? started, double? seconds)
        {
            var json = JsonSerializer.Serialize(new { workflow_runs = new[] { new
            {
                name = "Build", status, conclusion = "success", run_started_at = started,
                updated_at = "2026-09-12T10:00:00Z", html_url = "https://github.com/owner/repo/actions/runs/1",
            } } });

            var run = GitHubDevHubProvider.ParseCiRuns(json, _gitHubRepo).Single();

            Assert.AreEqual(seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null, run.Duration);
        }

        [TestMethod]
        [DataRow("completed", "2026-09-12T09:57:45Z", "2026-09-12T10:00:00Z", 135d)]
        [DataRow("completed", null, "2026-09-12T10:00:00Z", null)]
        [DataRow("completed", "2026-09-12T09:57:45Z", null, null)]
        [DataRow("completed", "2026-09-12T10:01:00Z", "2026-09-12T10:00:00Z", null)]
        [DataRow("inProgress", "2026-09-12T09:57:45Z", "2026-09-12T10:00:00Z", null)]
        [DataRow("notStarted", null, null, null)]
        public void AzureDevOpsBuilds_DurationExcludesQueueTimeAndUnfinishedRuns(string status, string? start, string? finish, double? seconds)
        {
            var json = JsonSerializer.Serialize(new { value = new[] { new
            {
                status, result = "succeeded", startTime = start, finishTime = finish, queueTime = "2026-09-12T09:00:00Z",
            } } });

            var run = AzureDevOpsDevHubProvider.ParseAdoBuilds(json, _azureDevOpsRepo).Single();

            Assert.AreEqual(seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null, run.Duration);
        }

        [TestMethod]
        [DataRow("COMPLETED", 135d, 135d)]
        [DataRow("COMPLETED", null, null)]
        [DataRow("COMPLETED", -1d, null)]
        [DataRow("IN_PROGRESS", 135d, null)]
        public void BitbucketPipelines_DurationUsesReportedElapsedTimeNotBilledSeconds(string status, double? duration, double? seconds)
        {
            var json = JsonSerializer.Serialize(new { values = new[] { new
            {
                state = new { name = status }, build_number = 42, completed_on = "2026-09-12T10:00:00Z",
                duration_in_seconds = duration, build_seconds_used = 9999,
            } } });

            var run = BitbucketDevHubProvider.ParsePipelines(json, _bitbucketRepo).Single();

            Assert.AreEqual(seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null, run.Duration);
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
            Assert.AreEqual(0, result[0].ApprovalCount);
        }

        [TestMethod]
        [DataRow("active", true, "draft")]
        [DataRow("active", false, "open")]
        [DataRow("completed", true, "merged")]
        public void AzureDevOpsPullRequests_MapsDraftAndActualApprovals(string status, bool draft, string expected)
        {
            var json = "{\"value\":[{\"pullRequestId\":9,\"title\":\"Change\",\"status\":\"" + status + "\","
                + "\"isDraft\":" + (draft ? "true" : "false") + ","
                + "\"createdBy\":{\"displayName\":\"Author\",\"id\":\"ME\"},"
                + "\"reviewers\":[{\"vote\":0},{\"vote\":10},{\"vote\":5},{\"vote\":-5},{\"vote\":-10},{\"vote\":null},{}],"
                + "\"sourceRefName\":\"refs/heads/feature\",\"targetRefName\":\"refs/heads/main\","
                + "\"closedDate\":\"0001-01-01T00:00:00\",\"creationDate\":\"2026-09-11T10:00:00Z\"}]}";

            var result = AzureDevOpsDevHubProvider.ParseAdoPullRequests(json, _azureDevOpsRepo, "me").Single();

            Assert.AreEqual(expected, result.Status);
            Assert.AreEqual(2, result.ApprovalCount);
            Assert.IsTrue(result.IsAuthoredByCurrentUser);
            Assert.AreEqual(new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc), result.UpdatedAt);
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
        [DataRow("completed", "\"result\":\"succeeded\",\"finishTime\":\"2026-09-12T10:00:00Z\",\"startTime\":\"2026-09-12T09:00:00Z\",", 10, "success")]
        [DataRow("inProgress", "\"startTime\":\"2026-09-12T09:00:00Z\",", 9, "pending")]
        [DataRow("inProgress", "\"finishTime\":null,\"startTime\":\"2026-09-12T09:00:00Z\",", 9, "pending")]
        [DataRow("notStarted", "", 8, "pending")]
        [DataRow("notStarted", "\"finishTime\":null,\"startTime\":null,", 8, "pending")]
        [DataRow("completed", "\"result\":\"canceled\",", 8, "cancelled")]
        public void AzureDevOpsBuilds_UsesLatestAvailableTimestamp(string status, string executionTimes, int expectedHour, string expectedStatus)
        {
            var json = "{\"value\":[{" + executionTimes +
                $"\"status\":\"{status}\",\"queueTime\":\"2026-09-12T10:00:00+02:00\"}}]}}";

            var result = AzureDevOpsDevHubProvider.ParseAdoBuilds(json, _azureDevOpsRepo);

            Assert.HasCount(1, result);
            Assert.AreEqual(new DateTime(2026, 9, 12, expectedHour, 0, 0, DateTimeKind.Utc), result[0].Timestamp);
            Assert.AreEqual(DateTimeKind.Utc, result[0].Timestamp.Kind);
            Assert.AreEqual(expectedStatus, result[0].Status);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("\"definition\":null,\"_links\":null,")]
        [DataRow("\"definition\":{},\"_links\":{},")]
        [DataRow("\"definition\":{\"name\":null},\"_links\":{\"web\":null},")]
        [DataRow("\"_links\":{\"web\":{}},")]
        [DataRow("\"_links\":{\"web\":{\"href\":null}},")]
        public void AzureDevOpsBuilds_MissingOptionalMetadataUsesDefaults(string metadata)
        {
            var json = "{\"value\":[{" + metadata +
                "\"status\":\"notStarted\",\"queueTime\":\"2026-09-12T08:00:00Z\"}]}";

            var result = AzureDevOpsDevHubProvider.ParseAdoBuilds(json, _azureDevOpsRepo);

            Assert.HasCount(1, result);
            Assert.AreEqual("Build", result[0].Name);
            Assert.AreEqual("https://dev.azure.com/organization/project/_build", result[0].WebUrl);
            Assert.AreEqual("pending", result[0].Status);
            Assert.IsNull(result[0].Branch);
        }

        [TestMethod]
        [DataRow("{\"value\":[{\"status\":\"notStarted\"}]}")]
        [DataRow("{\"value\":[{\"status\":\"notStarted\",\"finishTime\":null,\"startTime\":null,\"queueTime\":null}]}")]
        public void AzureDevOpsBuilds_MissingAllTimestampsReportsInvalidPayload(string json)
        {
            Assert.ThrowsExactly<System.Text.Json.JsonException>(() =>
                AzureDevOpsDevHubProvider.ParseAdoBuilds(json, _azureDevOpsRepo));
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("\"status\":null,")]
        [DataRow("\"status\":123,")]
        [DataRow("\"status\":\"\",")]
        public void AzureDevOpsBuilds_MissingOrInvalidStatusReportsInvalidPayload(string status)
        {
            var json = "{\"value\":[{" + status + "\"queueTime\":\"2026-09-12T08:00:00Z\"}]}";

            var exception = Assert.ThrowsExactly<System.Text.Json.JsonException>(() =>
                AzureDevOpsDevHubProvider.ParseAdoBuilds(json, _azureDevOpsRepo));

            StringAssert.Contains(exception.Message, "status");
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
