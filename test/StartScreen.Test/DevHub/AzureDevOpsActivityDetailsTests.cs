using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class AzureDevOpsActivityDetailsTests
    {
        private const string RepositoryId = "e2d789d1-67ab-405a-9704-c72759720867";
        private const string ProjectId = "f2b6134c-c2d8-4358-98e3-c6edb39cbb60";
        private const string Identity = "{\"authenticatedUser\":{\"id\":\"me\"}}";
        private static readonly RemoteRepoIdentifier _repo = new("dev.azure.com", "org", "repo", "project");

        [TestMethod]
        [DataRow(false, "threads")]
        [DataRow(false, "builds")]
        [DataRow(false, "wiql")]
        [DataRow(false, "creatorId=")]
        [DataRow(true, "threads")]
        [DataRow(true, "builds")]
        [DataRow(true, "wiql")]
        [DataRow(true, "creatorId=")]
        public async Task RepositoryPullRequests_RenderBeforeOtherRequestsCompleteAsync(bool scoped, string pendingEndpoint)
        {
            using var directory = new TemporaryDirectory();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var client = Client(async request =>
            {
                var uri = request.RequestUri!;
                requests.Enqueue(uri.AbsoluteUri);
                if (uri.AbsoluteUri.Contains(pendingEndpoint))
                {
                    started.TrySetResult(true);
                    await release.Task.WithCancellation(timeout.Token);
                }
                if (uri.AbsolutePath.EndsWith("/pullrequests"))
                    return uri.Query.Contains("creatorId=") || uri.Query.Contains("reviewerId=")
                        ? Values() : Values(PullRequest(1, author: "other", draft: true));
                if (uri.AbsolutePath.EndsWith("/builds"))
                    return Values(Build(1));
                return DefaultResponse(uri);
            });
            var provider = new AzureDevOpsDevHubProvider(client,
                (_, _, _) => Task.FromResult(new DevHubCredential("user", "test-token")), (_, _) => { },
                _ => Task.FromResult<IReadOnlyList<string>>(["https://dev.azure.com/org/project/_git/repo"]), () => []);
            var service = new DevHubService(new DevHubCacheService(directory.Path), [provider],
                _ => Task.FromResult<IReadOnlyList<string>>(["https://dev.azure.com/org/project/_git/repo"]),
                () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);
            var activity = new DevHubActivityViewModel();
            var progress = new InlineProgress(dashboard =>
            {
                activity.UpdateRepositoryActivity(dashboard.PullRequests, dashboard.Issues, dashboard.CiRuns,
                    [], DevHubSortOrder.MostRecent, false);
                if (activity.IncomingItems.Any(item => item.Title == "Change 1"))
                    rendered.TrySetResult(true);
            });
            var refresh = scoped
                ? service.ScopeToRepoAsync(_repo, timeout.Token, progress: progress)
                : service.RefreshAsync(timeout.Token, progress);
            try
            {
                var ready = Task.WhenAll(started.Task, rendered.Task);
                var completed = await Task.WhenAny(ready, refresh, Task.Delay(TimeSpan.FromSeconds(8)));
                Assert.AreSame(ready, completed, "PR publication or pending request missing. Requests: " + string.Join("; ", requests));
                Assert.IsFalse(refresh.IsCompleted, "Team PR rendering must not wait for other activity or optional details.");
                Assert.AreEqual(DevHubWorkItemKind.PullRequest, activity.IncomingKind);
                Assert.AreEqual("Change 1", activity.IncomingItems.Single().Title);
                Assert.IsFalse(activity.IsIncomingEmpty);
            }
            finally
            {
                release.TrySetResult(true);
            }
            var result = await refresh;
            Assert.AreEqual(DevHubActivityStatus.Success, result.Status);
            Assert.AreEqual("Change 1", result.PullRequests.Single().Title);
            Assert.HasCount(1, result.CiRuns);
            Assert.AreEqual(0, result.PullRequests.Single().UnresolvedThreadCount);
        }

        [TestMethod]
        public async Task PersonalActivity_QueriesAliasedCloudProjectOnlyOnceAsync()
        {
            var buildRequests = 0;
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/builds"))
                    buildRequests++;
                return DefaultResponse(uri);
            });
            var provider = new AzureDevOpsDevHubProvider(client,
                (_, _, _) => Task.FromResult(new DevHubCredential("user", "test-token")), (_, _) => { },
                _ => Task.FromResult<IReadOnlyList<string>>(new[]
                {
                    "https://dev.azure.com/org/project/_git/repo",
                    "https://org.visualstudio.com/DefaultCollection/project/_git/another-repo",
                }));

            await provider.GetUserCiRunsAsync(new DevHubActivityOptions(), CancellationToken.None);

            Assert.AreEqual(1, buildRequests);
        }

        [TestMethod]
        public async Task PersonalPullRequestsAndIssues_DefaultRequestsAreLimitedToTwentyPerFeedAsync()
        {
            var pullLimits = new List<int>();
            var issueLimits = new List<int>();
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/pullrequests"))
                    pullLimits.Add(QueryInt(uri, "$top"));
                if (uri.AbsolutePath.EndsWith("/wiql"))
                    issueLimits.Add(QueryInt(uri, "$top"));
                return DefaultResponse(uri);
            });
            var provider = Provider(client);

            await provider.GetUserPullRequestsAsync(new DevHubActivityOptions(), CancellationToken.None);
            await provider.GetUserIssuesAsync(new DevHubActivityOptions(), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { 20, 20 }, pullLimits);
            CollectionAssert.AreEqual(new[] { 20 }, issueLimits);
        }

        [TestMethod]
        public async Task PullRequestMetadata_UsesRepositoryGuidFromFeedAsync()
        {
            var threadsRead = false;
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/pullrequests"))
                    return uri.Query.Contains("reviewerId=") ? Values() : Values(PullRequest(1, draft: true));
                if (uri.AbsolutePath.EndsWith("/threads"))
                {
                    Assert.AreEqual($"/org/project/_apis/git/repositories/{RepositoryId}/pullRequests/1/threads", uri.AbsolutePath);
                    threadsRead = true;
                    return Json("{\"value\":[]}");
                }
                return DefaultResponse(uri);
            });

            var result = await Provider(client).FetchActivityAsync(DevHubActivityKind.PullRequests, new DevHubActivityOptions(), null!, CancellationToken.None);

            Assert.IsTrue(threadsRead);
            Assert.AreEqual(DevHubActivityStatus.Success, result.Status);
            Assert.AreEqual(0, result.PullRequests.Single().UnresolvedThreadCount);
        }

        [TestMethod]
        public async Task PersonalPullRequests_PagesAuthoredBeyondOneHundredAndFindsProjectReviewRequestsAsync()
        {
            var pages = new List<int>();
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.Contains("connectionData"))
                    return Json(Identity);
                if (uri.AbsolutePath.EndsWith("/threads"))
                    return Json("{\"value\":[]}");
                Assert.AreEqual("/org/project/_apis/git/pullrequests", uri.AbsolutePath);
                if (uri.Query.Contains("reviewerId=me"))
                    return Values(PullRequest(999, author: "other", reviewer: true, repository: "outside-recent-feed"));
                var skip = QueryInt(uri, "$skip");
                pages.Add(skip);
                Assert.IsLessThanOrEqualTo(100, QueryInt(uri, "$top"));
                return Values(Enumerable.Range(skip + 1, skip == 100 ? 49 : 100)
                    .Select(id => PullRequest(id, draft: true)).ToArray());
            });

            var results = await Provider(client).GetUserPullRequestsAsync(new DevHubActivityOptions(150), CancellationToken.None);

            Assert.HasCount(150, results);
            CollectionAssert.AreEqual(new[] { 0, 100 }, pages);
            var review = results.Single(pr => pr.NumericId == 999);
            Assert.IsTrue(review.IsReviewRequestedForCurrentUser);
            Assert.IsFalse(review.IsAuthoredByCurrentUser);
            Assert.AreEqual("outside-recent-feed", review.RepoIdentifier.Repo);
            Assert.Contains(pr => pr.NumericId == 149, results);
        }

        [TestMethod]
        public async Task ReviewerDiscovery_SurvivesAnIndependentAuthoredQueryFailureAsync()
        {
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/pullrequests"))
                    return uri.Query.Contains("creatorId=") ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                        : Values(PullRequest(99, author: "other", reviewer: true, repository: "not-in-mru"));
                return DefaultResponse(uri);
            });

            var results = await Provider(client).GetUserPullRequestsAsync(new DevHubActivityOptions(), CancellationToken.None);

            var review = results.Single();
            Assert.AreEqual(99, review.NumericId);
            Assert.IsTrue(review.IsReviewRequestedForCurrentUser);
            Assert.IsFalse(review.IsAuthoredByCurrentUser);
            Assert.AreEqual("not-in-mru", review.RepoIdentifier.Repo);
        }

        [TestMethod]
        public async Task RepoFeeds_PagePullRequestsAndBuildContinuationUpToThreeHundredAsync()
        {
            var prPages = new List<int>();
            var buildPages = 0;
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/pullrequests"))
                {
                    if (uri.Query.Contains("creatorId") || uri.Query.Contains("reviewerId"))
                        return Values();
                    var skip = QueryInt(uri, "$skip");
                    prPages.Add(skip);
                    Assert.AreEqual(100, QueryInt(uri, "$top"));
                    return Values(Enumerable.Range(skip + 1, 100).Select(id => PullRequest(id, author: "other", draft: true)).ToArray());
                }
                if (uri.AbsolutePath.EndsWith("/builds"))
                {
                    var page = buildPages++;
                    Assert.AreEqual(100, QueryInt(uri, "$top"));
                    Assert.AreEqual(page > 0, uri.Query.Contains("continuationToken="));
                    var response = Values(Enumerable.Range(page * 100 + 1, 100).Select(id => Build(id)).ToArray());
                    response.Headers.Add("x-ms-continuationtoken", "next-" + (page + 1));
                    return response;
                }
                return DefaultResponse(uri);
            });

            var detail = await Provider(client).GetRepoDetailAsync(_repo, new DevHubActivityOptions(300), CancellationToken.None);

            Assert.IsNotNull(detail);
            Assert.HasCount(300, detail.PullRequests);
            Assert.HasCount(300, detail.CiRuns);
            CollectionAssert.AreEqual(new[] { 0, 100, 200 }, prPages);
            Assert.AreEqual(3, buildPages);
        }

        [TestMethod]
        public async Task PersonalWorkItems_UseIdCursorAndHundredItemBatchesAsync()
        {
            var queries = new List<string>();
            var batches = new List<int>();
            using var client = Client(async request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/wiql"))
                {
                    var body = await ReadQueryAsync(request);
                    queries.Add(body);
                    Assert.Contains("System.CreatedBy", body);
                    Assert.Contains("NOT IN ('Shipped')", body);
                    var start = queries.Count == 1 ? 300 : 200;
                    var size = QueryInt(uri, "$top");
                    Assert.IsLessThanOrEqualTo(100, size);
                    return Json(JsonSerializer.Serialize(new { workItems = Enumerable.Range(start - size + 1, size).Reverse().Select(id => new { id }) }));
                }
                if (uri.AbsolutePath.EndsWith("/workitems"))
                {
                    var ids = QueryValue(uri, "ids").Split(',').Select(int.Parse).ToList();
                    batches.Add(ids.Count);
                    return Values(ids.Select(id => WorkItem(id)).ToArray());
                }
                return DefaultResponse(uri);
            });

            var issues = await Provider(client).GetUserIssuesAsync(new DevHubActivityOptions(150), CancellationToken.None);

            Assert.HasCount(150, issues);
            CollectionAssert.AreEqual(new[] { 100, 50 }, batches);
            Assert.Contains("[System.Id] < 201", queries[1]);
            Assert.AreEqual(150, issues.Select(issue => issue.NumericId).Distinct().Count());
        }

        [TestMethod]
        public async Task RepoIssues_IndependentIncomingQueryRetainsOwnershipMetadataAndStrictGitLinksAsync()
        {
            var personalQueries = 0;
            var incomingQueries = 0;
            using var client = Client(async request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/wiql"))
                {
                    var body = await ReadQueryAsync(request);
                    var personal = body.Contains("System.CreatedBy");
                    if (personal)
                        personalQueries++;
                    else
                        incomingQueries++;
                    return Json(personal ? "{\"workItems\":[{\"id\":1}]}" : "{\"workItems\":[{\"id\":3},{\"id\":2},{\"id\":1}]}");
                }
                if (uri.AbsolutePath.EndsWith("/workitems"))
                    return Values(WorkItem(1), WorkItem(2, author: "other", assigned: "other"), WorkItem(3, repositoryId: "another-repository"));
                return DefaultResponse(uri);
            });

            var detail = await Provider(client).GetRepoDetailAsync(_repo, new DevHubActivityOptions(), CancellationToken.None);

            Assert.IsNotNull(detail);
            Assert.AreEqual(1, personalQueries);
            Assert.AreEqual(1, incomingQueries);
            Assert.HasCount(2, detail.Issues);
            var personal = detail.Issues.Single(issue => issue.NumericId == 1);
            Assert.IsTrue(personal.IsAuthoredByCurrentUser);
            Assert.IsTrue(personal.IsAssignedToCurrentUser);
            var incoming = detail.Issues.Single(issue => issue.NumericId == 2);
            Assert.IsFalse(incoming.IsAuthoredByCurrentUser);
            Assert.IsFalse(incoming.IsAssignedToCurrentUser);
            Assert.AreEqual("Bug", incoming.WorkItemType);
            Assert.AreEqual("Assigned person", incoming.AssignedTo);
            Assert.AreEqual("2", incoming.Priority);
            Assert.AreEqual("project\\Sprint 4", incoming.Iteration);
            Assert.IsTrue(incoming.IsBlocked);
            Assert.AreEqual(_repo, incoming.RepoIdentifier);
        }

        [TestMethod]
        public async Task CompletedWorkItems_UseCustomStateCategoryAndRetainClosedAssignmentsAsync()
        {
            var queries = new List<string>();
            using var client = Client(async request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/wiql"))
                {
                    var body = await ReadQueryAsync(request);
                    queries.Add(body);
                    return Json(body.Contains("@Today - 14") ? "{\"workItems\":[{\"id\":4},{\"id\":3},{\"id\":2}]}" : "{\"workItems\":[{\"id\":1}]}");
                }
                if (uri.AbsolutePath.EndsWith("/workitems"))
                    return QueryValue(uri, "ids") != "1" ? Values(
                        WorkItem(2, state: "Shipped", author: "other", closedDate: DateTime.UtcNow.AddDays(-1)),
                        WorkItem(3, state: "Shipped", closedDate: DateTime.UtcNow.AddDays(-30)),
                        WorkItem(4, state: "Shipped"))
                        : Values(WorkItem(1, historicalClosedDate: true));
                return DefaultResponse(uri);
            });

            var issues = await Provider(client).GetUserIssuesAsync(new DevHubActivityOptions(includeCompleted: true), CancellationToken.None);

            Assert.HasCount(2, issues);
            Assert.IsFalse(issues.Single(issue => issue.NumericId == 1).IsClosed, "A historical closed date must not close reopened work.");
            var closed = issues.Single(issue => issue.NumericId == 2);
            Assert.IsTrue(closed.IsClosed);
            Assert.IsTrue(closed.IsAssignedToCurrentUser);
            Assert.IsFalse(closed.IsAuthoredByCurrentUser);
            var completedQuery = queries.Single(query => query.Contains("@Today - 14"));
            Assert.Contains("IN ('Shipped')", completedQuery);
            StringAssert.Contains(completedQuery, "[Microsoft.VSTS.Common.ClosedDate] >= @Today - 14");
            Assert.DoesNotContain("System.ChangedDate", completedQuery);
            Assert.DoesNotContain(issue => issue.NumericId == 3 || issue.NumericId == 4, issues,
                "Recently edited old closures and closures without a verifiable completion date must not count as recent completions.");
        }

        [TestMethod]
        public async Task CompletedPullRequests_AreSeparateFromUnrestrictedActiveQueriesAsync()
        {
            var activeQueried = false;
            var completedQueried = false;
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/pullrequests"))
                {
                    if (uri.Query.Contains("status=completed"))
                    {
                        completedQueried = true;
                        Assert.Contains("queryTimeRangeType=closed", uri.Query);
                        Assert.Contains("minTime=", uri.Query);
                        return Values(PullRequest(2, status: "completed", closed: DateTime.UtcNow.AddDays(-1)));
                    }
                    if (uri.Query.Contains("creatorId="))
                    {
                        activeQueried = true;
                        Assert.DoesNotContain("minTime=", uri.Query);
                        return Values(PullRequest(1, draft: true));
                    }
                    return Values();
                }
                return DefaultResponse(uri);
            });

            var results = await Provider(client).GetUserPullRequestsAsync(new DevHubActivityOptions(includeCompleted: true), CancellationToken.None);

            Assert.IsTrue(activeQueried);
            Assert.IsTrue(completedQueried);
            Assert.HasCount(2, results);
            Assert.IsTrue(results.Single(pr => pr.NumericId == 1).IsOpen);
            var merged = results.Single(pr => pr.NumericId == 2);
            Assert.IsTrue(merged.IsMerged);
            Assert.IsFalse(merged.IsOpen);
        }

        [TestMethod]
        [DataRow("approved", true, true)]
        [DataRow("rejected", true, false)]
        [DataRow("unknown-configuration", true, null)]
        [DataRow("approved", false, null)]
        [DataRow("forbidden", true, null)]
        public async Task MergeReadiness_RequiresKnownPoliciesAndCurrentIterationChecksAsync(string policy, bool checks, bool? expected)
        {
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                var path = uri.AbsolutePath;
                if (path.EndsWith("/pullrequests"))
                    return uri.Query.Contains("reviewerId=") ? Values() : Values(PullRequest(1));
                if (path.EndsWith("/pullRequests/1"))
                    return Json(PullRequest(1));
                if (path.EndsWith("/iterations"))
                    return Json("{\"value\":[{\"id\":2,\"sourceRefCommit\":{\"commitId\":\"current\"}}]}");
                if (path.EndsWith("/statuses"))
                    return checks ? Json("{\"value\":[{\"id\":1,\"iterationId\":1,\"state\":\"failed\",\"context\":{\"name\":\"ci\"}},{\"id\":2,\"iterationId\":2,\"state\":\"succeeded\",\"context\":{\"name\":\"ci\"},\"targetUrl\":\"https://dev.azure.com/org/check\"}]}") : Values();
                if (path.EndsWith("/evaluations"))
                {
                    if (policy == "forbidden")
                        return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    return Json("{\"value\":[{\"configuration\":" + (policy == "unknown-configuration" ? "{}" : "{\"isBlocking\":true,\"isEnabled\":true}") + ",\"status\":\"" + policy + "\"}]}");
                }
                return DefaultResponse(uri);
            });

            var results = await Provider(client).GetUserPullRequestsAsync(new DevHubActivityOptions(), CancellationToken.None);

            var pr = results.Single();
            Assert.AreEqual(expected, pr.CanMerge);
            Assert.AreEqual(0, pr.UnresolvedThreadCount);
            if (!expected.HasValue)
            {
                StringAssert.Contains(pr.MergeBlockReason, "could not be verified");
                StringAssert.Contains(pr.NextActionText, "could not be verified");
                StringAssert.Contains(pr.StatusDetails, "could not be verified");
            }
            else if (expected.Value)
            {
                Assert.IsNull(pr.MergeBlockReason);
            }
            if (checks)
            {
                Assert.AreEqual("success", pr.CiStatus);
                Assert.AreEqual("1/1 checks passed", pr.ChecksSummary);
                Assert.AreEqual("https://dev.azure.com/org/check", pr.ChecksUrl);
            }
        }

        [TestMethod]
        [DataRow("active", 1)]
        [DataRow("pending", 1)]
        [DataRow("fixed", 0)]
        [DataRow("unknown", null)]
        public async Task ReviewThreads_UnknownStatesCannotEstablishResolvedDiscussionsAsync(string status, int? expected)
        {
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/pullrequests"))
                    return Values(PullRequest(1, draft: true));
                if (uri.AbsolutePath.EndsWith("/threads"))
                    return Json("{\"value\":[{\"status\":\"" + status + "\"},{\"status\":\"active\",\"isDeleted\":true}]}");
                return DefaultResponse(uri);
            });

            var results = await Provider(client).GetUserPullRequestsAsync(new DevHubActivityOptions(), CancellationToken.None);

            Assert.AreEqual(expected, results.Single().UnresolvedThreadCount);
            Assert.IsNull(results.Single().CanMerge);
        }

        [TestMethod]
        public async Task MalformedOptionalBuildData_DoesNotDiscardRequiredPullRequestsAsync()
        {
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/pullrequests"))
                    return Values(PullRequest(1, draft: true));
                if (uri.AbsolutePath.EndsWith("/builds"))
                    return Values("{\"definition\":{\"name\":\"Malformed build\"}}");
                return DefaultResponse(uri);
            });

            var detail = await Provider(client).GetRepoDetailAsync(_repo, new DevHubActivityOptions(), CancellationToken.None);

            Assert.IsNotNull(detail);
            Assert.HasCount(1, detail.PullRequests);
            Assert.IsEmpty(detail.CiRuns);
            Assert.IsNull(detail.PullRequests.Single().CanMerge);
            Assert.AreEqual("Build activity could not be loaded. Check Build read access and refresh to retry.", detail.ActivityWarning);
        }

        [TestMethod]
        public async Task OptionalFailures_DoNotDiscardPullRequestsOrReviewOwnershipAsync()
        {
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/pullrequests"))
                    return Values(PullRequest(1, reviewer: true));
                if (uri.AbsolutePath.Contains("/pullRequests/") || uri.AbsolutePath.EndsWith("/builds"))
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                return DefaultResponse(uri);
            });

            var detail = await Provider(client).GetRepoDetailAsync(_repo, new DevHubActivityOptions(), CancellationToken.None);

            Assert.IsNotNull(detail);
            var pr = detail.PullRequests.Single();
            Assert.IsEmpty(detail.CiRuns);
            Assert.Contains("Build activity could not be loaded. Check Build read access and refresh to retry.", detail.ActivityWarning);
            Assert.Contains("HTTP 403", detail.ActivityWarning);
            Assert.IsTrue(pr.IsAuthoredByCurrentUser);
            Assert.IsTrue(pr.IsReviewRequestedForCurrentUser);
            Assert.IsNull(pr.UnresolvedThreadCount);
            Assert.IsNull(pr.CanMerge);
            StringAssert.Contains(pr.MergeBlockReason, "could not be verified");
            StringAssert.Contains(pr.NextActionText, "could not be verified");
            StringAssert.Contains(pr.StatusDetails, "could not be verified");
        }

        [TestMethod]
        public async Task CancellationDuringOptionalEnrichment_PropagatesAsync()
        {
            using var cancellation = new CancellationTokenSource();
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/pullrequests"))
                    return Values(PullRequest(1, draft: true));
                if (uri.AbsolutePath.EndsWith("/threads"))
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                return DefaultResponse(uri);
            });

            await Assert.ThrowsAsync<OperationCanceledException>(() => Provider(client)
                .GetUserPullRequestsAsync(new DevHubActivityOptions(), cancellation.Token));
        }

        [TestMethod]
        [DataRow("null", false)]
        [DataRow("null", true)]
        [DataRow("{}", false)]
        [DataRow("{}", true)]
        [DataRow(null, false)]
        [DataRow(null, true)]
        public async Task BuildFailureTimeline_AbsentLogDoesNotMarkLoadedBuildsPartialAsync(string? logJson, bool repositoryScoped)
        {
            var timelineRequests = 0;
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/builds"))
                    return Values(Build(1, "me", "failed"));
                if (uri.AbsolutePath.EndsWith("/timeline"))
                {
                    timelineRequests++;
                    return Json("{\"records\":[{\"name\":\"Compile\",\"result\":\"failed\",\"issues\":[{\"type\":\"error\",\"message\":\"Compilation failed\"}]"
                        + (logJson == null ? string.Empty : ",\"log\":" + logJson) + "}]}");
                }
                return DefaultResponse(uri);
            });

            var result = await Provider(client).FetchActivityAsync(repositoryScoped ? DevHubActivityKind.Repository : DevHubActivityKind.CiRuns,
                new DevHubActivityOptions(), repositoryScoped ? _repo : null!, CancellationToken.None);

            Assert.AreEqual(1, timelineRequests);
            var run = result.CiRuns.Single();
            Assert.IsTrue(run.IsTriggeredByCurrentUser);
            Assert.AreEqual("failure", run.Status);
            Assert.AreEqual("Compilation failed", run.FailureSummary);
            Assert.AreEqual(run.WebUrl, run.FailureUrl, "A missing task log should retain the build page as the failure link.");
            Assert.AreEqual(DevHubActivityStatus.Success, result.Status, "Optional null log metadata must not invalidate the loaded inventory.");
            Assert.IsNull(result.Message);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task BuildFailures_EnrichOnlyTwoNewestPerRepositoryWithoutDroppingOlderRunsAsync(bool scoped)
        {
            var timelineIds = new List<int>();
            var now = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
            var failedCount = scoped ? 6 : 12;
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/builds"))
                    return Values(Enumerable.Range(1, failedCount + 1).Select(id => Build(id, "me",
                        id > failedCount ? "succeeded" : "failed", now.AddMinutes(id), id <= 6 ? "repo" : "other")).ToArray());
                if (uri.AbsolutePath.EndsWith("/timeline"))
                {
                    timelineIds.Add(int.Parse(uri.AbsolutePath.Split('/').AsEnumerable().Reverse().Skip(1).First()));
                    return Json("{\"records\":[{\"name\":\"Compile\",\"result\":\"failed\"}]}");
                }
                return DefaultResponse(uri);
            });

            var result = await Provider(client).FetchActivityAsync(scoped ? DevHubActivityKind.Repository : DevHubActivityKind.CiRuns,
                new DevHubActivityOptions(), scoped ? _repo : null!, CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Success, result.Status);
            Assert.HasCount(failedCount + 1, result.CiRuns);
            CollectionAssert.AreEquivalent(scoped ? new[] { 5, 6 } : new[] { 5, 6, 11, 12 }, timelineIds);
            foreach (var group in result.CiRuns.Where(run => run.Status == "failure").GroupBy(run => run.RepoIdentifier))
            {
                var ordered = group.OrderByDescending(run => run.Timestamp).ToArray();
                Assert.IsTrue(ordered.Take(2).All(run => run.FailureSummary == "Compile"));
                Assert.IsTrue(ordered.Skip(2).All(run => run.FailureSummary == null && run.FailureUrl == null));
            }
            Assert.IsTrue(result.CiRuns.All(run => !string.IsNullOrEmpty(run.WebUrl)));
        }

        [TestMethod]
        public async Task BuildOwnershipAndFailureTimeline_AreMappedAsync()
        {
            using var client = Client(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/builds"))
                {
                    Assert.Contains("requestedFor=me", uri.Query);
                    return Values(Build(1, "me", "failed"), Build(2, "other"));
                }
                if (uri.AbsolutePath.EndsWith("/timeline"))
                    return Json("{\"records\":[{\"name\":\"Compile\",\"result\":\"failed\",\"issues\":[{\"type\":\"error\",\"message\":\"Compilation failed\"}],\"log\":{\"id\":8}}]}");
                return DefaultResponse(uri);
            });

            var results = await Provider(client).GetUserCiRunsAsync(new DevHubActivityOptions(), CancellationToken.None);

            var run = results.Single();
            Assert.IsTrue(run.IsTriggeredByCurrentUser);
            Assert.AreEqual("Compilation failed", run.FailureSummary);
            Assert.AreEqual("https://dev.azure.com/org/project/_apis/build/builds/1/logs/8?api-version=7.1", run.FailureUrl);
        }

        private static HttpResponseMessage DefaultResponse(Uri uri)
        {
            var path = uri.AbsolutePath;
            if (path.Contains("connectionData"))
            {
                Assert.AreEqual("?api-version=3.0-preview", uri.Query);
                return Json(Identity);
            }
            if (path.EndsWith("/repositories/repo"))
                return Json("{\"id\":\"" + RepositoryId + "\"}");
            if (path.EndsWith("/wiql"))
                return Json("{\"workItems\":[]}");
            if (path.EndsWith("/workitemtypes"))
                return Json("{\"value\":[{\"name\":\"Bug\"}]}");
            if (path.EndsWith("/states"))
                return Json("{\"value\":[{\"name\":\"Active\",\"category\":\"InProgress\"},{\"name\":\"Shipped\",\"category\":\"Completed\"}]}");
            return Values();
        }

        private static string PullRequest(int id, string author = "me", bool reviewer = false, bool draft = false,
            string repository = "repo", string status = "active", DateTime? closed = null)
        {
            return JsonSerializer.Serialize(new
            {
                pullRequestId = id,
                title = "Change " + id,
                status,
                isDraft = draft,
                createdBy = new { displayName = "Author", id = author },
                reviewers = reviewer ? new[] { new { id = "me", vote = 0 } } : new[] { new { id = "approver", vote = 10 } },
                repository = new { id = RepositoryId, name = repository, project = new { id = ProjectId } },
                sourceRefName = "refs/heads/feature",
                targetRefName = "refs/heads/main",
                mergeStatus = "succeeded",
                lastMergeSourceCommit = new { commitId = "current" },
                creationDate = DateTime.UtcNow.AddDays(-30),
                closedDate = closed,
            });
        }

        private static string WorkItem(int id, string author = "me", string assigned = "me", string state = "Active",
            string repositoryId = RepositoryId, bool historicalClosedDate = false, DateTime? closedDate = null)
        {
            var fields = new Dictionary<string, object>
            {
                ["System.Title"] = "Work " + id,
                ["System.State"] = state,
                ["System.WorkItemType"] = "Bug",
                ["System.CreatedBy"] = new { id = author, displayName = "Reporter" },
                ["System.AssignedTo"] = new { id = assigned, displayName = "Assigned person" },
                ["System.CreatedDate"] = DateTime.UtcNow.AddDays(-30),
                ["System.ChangedDate"] = DateTime.UtcNow.AddDays(-1),
                ["System.IterationPath"] = "project\\Sprint 4",
                ["Microsoft.VSTS.Common.Priority"] = 2,
                ["System.Tags"] = "triaged; Blocked",
            };
            if (historicalClosedDate)
                fields["Microsoft.VSTS.Common.ClosedDate"] = DateTime.UtcNow.AddDays(-20);
            if (closedDate.HasValue)
                fields["Microsoft.VSTS.Common.ClosedDate"] = closedDate.Value;
            return JsonSerializer.Serialize(new
            {
                id,
                fields,
                relations = new[] { new { rel = "ArtifactLink", url = $"vstfs:///Git/PullRequestId/{ProjectId}%2F{repositoryId}%2F99" } },
            });
        }

        private static string Build(int id, string requestedFor = "other", string result = "succeeded",
            DateTime? finishedAt = null, string repositoryName = "repo") => JsonSerializer.Serialize(new
        {
            id,
            status = "completed",
            result,
            definition = new { name = "CI", id = 1 },
            repository = new { name = repositoryName },
            sourceBranch = "refs/heads/main",
            finishTime = finishedAt ?? DateTime.UtcNow,
            requestedFor = new { id = requestedFor },
            _links = new { web = new { href = $"https://dev.azure.com/org/project/_build/results?buildId={id}" } },
        });

        private static int QueryInt(Uri uri, string name) => int.Parse(QueryValue(uri, name));

        private static async Task<string> ReadQueryAsync(HttpRequestMessage request)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return doc.RootElement.GetProperty("query").GetString()!;
        }

        private static string QueryValue(Uri uri, string name) => uri.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('='))
            .Where(pair => Uri.UnescapeDataString(pair[0]) == name)
            .Select(pair => Uri.UnescapeDataString(pair[1])).Single();

        private static AzureDevOpsDevHubProvider Provider(HttpClient client) => new(client,
            (_, _, _) => Task.FromResult(new DevHubCredential("user", "test-token")), (_, _) => { },
            _ => Task.FromResult<IReadOnlyList<string>>(new[] { "https://dev.azure.com/org/project/_git/repo" }));

        private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(request => Task.FromResult(respond(request))));

        private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => new(new Handler(respond));

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Values(params string[] values) => Json("{\"value\":[" + string.Join(",", values) + "]}");

        private sealed class InlineProgress(Action<DevHubDashboard> report) : IProgress<DevHubDashboard>
        {
            public void Report(DevHubDashboard value) => report(value);
        }

        private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return respond(request);
            }
        }
    }
}
