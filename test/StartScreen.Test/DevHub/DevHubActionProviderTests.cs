using System.Net;
using System.Net.Http;
using System.Text.Json;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubActionProviderTests
    {
        private static readonly RemoteRepoIdentifier _gitHub = new RemoteRepoIdentifier("github.com", "owner", "repo");
        private static readonly RemoteRepoIdentifier _ado = new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project");
        private const string RepositoryId = "e2d789d1-67ab-405a-9704-c72759720867";

        [TestMethod]
        [DataRow("open", true, false, false, null, null)]
        [DataRow("open", true, true, false, "pending", null)]
        [DataRow("open", true, false, true, "failure", "Changes requested")]
        [DataRow("conflicts", true, false, true, "failure", "Conflicts")]
        [DataRow("open", true, false, false, "failure", "Checks failing")]
        [DataRow("open", false, true, true, "failure", "Review requested")]
        [DataRow("open", false, false, true, "failure", null)]
        [DataRow("conflicts", false, false, false, "failure", null)]
        [DataRow("draft", true, true, true, "failure", null)]
        [DataRow("draft", false, true, true, "failure", null)]
        [DataRow("closed", true, true, true, "failure", null)]
        [DataRow("merged", false, true, true, "failure", null)]
        [DataRow("unknown", true, true, true, "failure", null)]
        [DataRow(null, false, true, true, "failure", null)]
        public void PullRequestAttention_RequiresKnownCurrentAction(string? status, bool authored,
            bool requested, bool changes, string? ci, string? expected)
        {
            var pr = new DevHubPullRequest
            {
                Status = status,
                IsAuthoredByCurrentUser = authored,
                IsReviewRequestedForCurrentUser = requested,
                HasChangesRequested = changes,
                CiStatus = ci,
                UpdatedAt = DateTime.UtcNow,
            };

            Assert.AreEqual(expected, pr.AttentionReason);
            Assert.AreEqual(expected != null, pr.NeedsAttention);
        }

        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task AzurePersonalActivity_QueriesAllKnownProjectsAndIsolatesFailuresAsync(bool issues, bool denied)
        {
            var urls = Enumerable.Range(1, 6).SelectMany(project => new[]
            {
                $"https://dev.azure.com/org/project{project}/_git/first",
                $"https://dev.azure.com/org/project{project}/_git/second",
            }).ToArray();
            var queries = 0;
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                var uri = request.RequestUri!;
                var path = uri.AbsolutePath;
                if (path.EndsWith("/connectionData"))
                    return Json("{\"authenticatedUser\":{\"id\":\"me\"}}");
                if (path.EndsWith(issues ? "/wiql" : "/git/pullrequests"))
                {
                    Interlocked.Increment(ref queries);
                    Assert.DoesNotContain("/repositories/", path);
                    StringAssert.Contains(uri.Query, "$top=30");
                    if (!issues)
                    {
                        Assert.IsTrue(uri.Query.Contains("searchCriteria.creatorId=me") || uri.Query.Contains("searchCriteria.reviewerId=me"));
                        StringAssert.Contains(uri.Query, "searchCriteria.status=active");
                    }
                    if (denied && path.Contains("/project1/"))
                        return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    if (!issues && uri.Query.Contains("searchCriteria.reviewerId=me"))
                        return Json("{\"value\":[]}");
                    return Json(issues ? "{\"workItems\":[{\"id\":1}]}"
                        : AdoPrJson("active", false, 0).Replace("\"pullRequestId\":9", "\"repository\":{\"name\":\"not-in-mru\"},\"pullRequestId\":9"));
                }
                Assert.IsTrue(issues && path.EndsWith("/workitems"));
                return Json(AdoIssueJson("Active", "me", "me", false));
            }));
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential()), (_, _) => { },
                _ => Task.FromResult<IReadOnlyList<string>>(urls));

            var prs = issues ? Array.Empty<DevHubPullRequest>() : await provider.GetUserPullRequestsAsync(CancellationToken.None);
            var tasks = issues ? await provider.GetUserIssuesAsync(CancellationToken.None) : Array.Empty<DevHubIssue>();
            var work = DevHubWorkItem.GetMyWork(prs, tasks, []);

            Assert.AreEqual(issues ? 6 : 12, queries);
            Assert.HasCount(denied ? 5 : 6, work);
            Assert.IsTrue(work.All(item => !string.IsNullOrEmpty(item.WebUrl)));
            Assert.IsTrue(prs.All(pr => pr.RepoIdentifier.Repo == "not-in-mru" && pr.IsAuthoredByCurrentUser));
            Assert.IsTrue(tasks.All(issue => issue.IsAssignedToCurrentUser && issue.RepoIdentifier == null));
        }

        [TestMethod]
        public async Task AzureScopedPersonalPullRequests_AreNotLimitedByTheRepositoryFeedAsync()
        {
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/connectionData"))
                    return Json("{\"authenticatedUser\":{\"id\":\"me\"}}");
                if (path.EndsWith("/pullrequests"))
                {
                    return Json(request.RequestUri.Query.Contains("searchCriteria.creatorId=me")
                        ? AdoPrJson("active", false, -10)
                        : AdoPrJson("active", false, 0).Replace("\"pullRequestId\":9", "\"pullRequestId\":10")
                            .Replace("\"id\":\"me\"", "\"id\":\"other\""));
                }
                if (path.EndsWith("/repositories/repo"))
                    return Json("{\"id\":\"" + RepositoryId + "\"}");
                return Json(path.EndsWith("/wiql") ? "{\"workItems\":[]}" : "{\"value\":[]}");
            }));
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential()), (_, _) => { });

            var detail = await provider.GetRepoDetailAsync(_ado, 1, CancellationToken.None);

            Assert.IsNotNull(detail);
            Assert.IsNull(detail.ActivityWarning);
            Assert.HasCount(2, detail.PullRequests);
            var own = DevHubWorkItem.GetMyWork(detail.PullRequests, [], []).Single();
            Assert.AreEqual("Changes requested", own.StatusText);
            StringAssert.EndsWith(own.WebUrl, "/pullrequest/9");
            Assert.IsNull(detail.PullRequests.Single(pr => pr.NumericId == 9).CiStatus);
            Assert.HasCount(1, DevHubWorkItem.GetIncoming(detail.PullRequests, [], []));
        }

        [TestMethod]
        [DataRow("open", true)]
        [DataRow("conflicts", true)]
        [DataRow("draft", true)]
        [DataRow("OPEN", true)]
        [DataRow("closed", false)]
        [DataRow("merged", false)]
        [DataRow("unknown", false)]
        [DataRow(null, false)]
        public void PullRequestIsOpen_ExcludesClosedAndUnknown(string? status, bool expected)
        {
            Assert.AreEqual(expected, new DevHubPullRequest { Status = status }.IsOpen);
        }

        [TestMethod]
        public void CurrentReviewDecision_ReplacesSupersededChangesAndDismissals()
        {
            var pr = OwnPr();
            ApplyGitHub(pr, "\"reviewDecision\":\"CHANGES_REQUESTED\"");
            Assert.IsTrue(pr.HasChangesRequested);
            Assert.IsFalse(pr.IsApproved);
            Assert.AreEqual("Changes requested", pr.StatusText);

            ApplyGitHub(pr, "\"reviewDecision\":\"APPROVED\"");
            Assert.IsFalse(pr.HasChangesRequested);
            Assert.IsTrue(pr.IsApproved);
            Assert.AreEqual(0, pr.ApprovalCount);
            Assert.AreEqual("Approved", pr.StatusText);
            Assert.IsFalse(pr.NeedsAttention);

            ApplyGitHub(pr, "\"reviewDecision\":\"CHANGES_REQUESTED\"");
            Assert.IsFalse(pr.IsApproved);
            ApplyGitHub(pr, "\"reviewDecision\":\"APPROVED\"");
            ApplyGitHub(pr, "\"reviewDecision\":null");
            Assert.IsFalse(pr.HasChangesRequested);
            Assert.IsFalse(pr.IsApproved);
            Assert.AreEqual("Waiting for review", pr.StatusText);
            Assert.IsFalse(pr.NeedsAttention);
        }

        [TestMethod]
        [DataRow("open", null, 0, "Approved")]
        [DataRow("open", "pending", 0, "Checks pending")]
        [DataRow("open", "failure", 0, "Checks failing")]
        [DataRow("open", null, 1, "1 approval")]
        [DataRow("open", null, 2, "2 approvals")]
        [DataRow("draft", null, 0, "Draft")]
        [DataRow("closed", null, 0, "Closed")]
        [DataRow("unknown", null, 0, "Waiting for review")]
        public void ApprovalStatus_DoesNotInventCountsOrOverrideChecksDraftsAndRealCounts(string status, string? ci, int count, string expected)
        {
            var pr = new DevHubPullRequest { Status = status, CiStatus = ci, ApprovalCount = count, IsApproved = true };

            Assert.AreEqual(expected, pr.StatusText);
        }

        [TestMethod]
        [DataRow("OPEN", false, true)]
        [DataRow("OPEN", true, false)]
        [DataRow("CLOSED", false, false)]
        [DataRow("MERGED", false, false)]
        [DataRow("UNKNOWN", false, false)]
        public void GitHubApproval_RequiresActiveNonDraftDecision(string state, bool draft, bool expected)
        {
            var pr = OwnPr();
            pr.IsApproved = true;
            using var metadata = JsonDocument.Parse("{\"state\":\"" + state + "\",\"isDraft\":"
                + (draft ? "true" : "false") + ",\"reviewDecision\":\"APPROVED\"}");

            GitHubDevHubProvider.ApplyActionMetadata(pr, metadata.RootElement);

            Assert.AreEqual(expected, pr.IsApproved);
            Assert.AreEqual(0, pr.ApprovalCount);
        }

        [TestMethod]
        [DataRow(false, 1)]
        [DataRow(false, 2)]
        [DataRow(true, 1)]
        [DataRow(true, 2)]
        public async Task GitHubSupplementalIssueFailures_PreserveBaseAndAlreadyFetchedResultsAsync(bool scoped, int failAt)
        {
            var original = new DevHubIssue { NumericId = 1, RepoIdentifier = _gitHub, Title = "Existing REST issue" };
            var results = new List<DevHubIssue> { original };
            var handler = new TestHttpMessageHandler((request, index) =>
            {
                var query = Uri.UnescapeDataString(request.RequestUri!.Query);
                Assert.DoesNotContain("org:", query);
                StringAssert.Contains(query, "advanced_search=true");
                if (scoped)
                    StringAssert.Contains(query, "AND repo:owner/repo");
                return index == failAt ? new HttpResponseMessage((HttpStatusCode)429)
                    : Json("{\"items\":[" + GitHubSearchItem(2, "me") + "]}");
            });
            using var client = new HttpClient(handler);

            var warning = await GitHubDevHubProvider.PopulateOwnIssuesAsync(client, Credential(), results,
                "me", 20, CancellationToken.None, scoped ? _gitHub : null);

            Assert.AreEqual("Some issue activity could not be loaded. Refresh to retry.", warning);
            Assert.AreSame(original, results[0]);
            Assert.HasCount(failAt, results);
            Assert.AreEqual(failAt, handler.RequestCount);
            if (failAt == 2)
                Assert.AreEqual(2, results[1].NumericId);
        }

        [TestMethod]
        public async Task GitHubSupplementalIssues_RequestedCancellationPropagatesAsync()
        {
            using var source = new CancellationTokenSource();
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) =>
            {
                source.Cancel();
                return new HttpResponseMessage((HttpStatusCode)429);
            }));

            await Assert.ThrowsAsync<OperationCanceledException>(() => GitHubDevHubProvider.PopulateOwnIssuesAsync(
                client, Credential(), [], "me", 20, source.Token));
        }

        [TestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task GitHubAuthoredLookup_IsIndependentOfIncomingQueryAndKeepsReviewRequestsAsync(bool fails, bool scoped)
        {
            var own = OwnPr();
            own.IsReviewRequestedForCurrentUser = true;
            var incoming = new DevHubPullRequest { NumericId = 3, RepoIdentifier = _gitHub, Status = "open", IsReviewRequestedForCurrentUser = true };
            var results = new List<DevHubPullRequest> { own, incoming };
            var handler = new TestHttpMessageHandler((request, _) =>
            {
                var query = Uri.UnescapeDataString(request.RequestUri!.Query);
                StringAssert.Contains(query, "is:pr is:open author:me");
                Assert.DoesNotContain("org:", query);
                Assert.AreEqual(scoped, query.Contains("AND repo:owner/repo"));
                StringAssert.Contains(query, "per_page=30");
                StringAssert.Contains(query, "advanced_search=true");
                return fails ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                    : Json("{\"items\":[" + GitHubSearchItem(1, "me") + "," + GitHubSearchItem(2, "me") + "," + GitHubSearchItem(4, "other") + "]}");
            });
            using var client = new HttpClient(handler);

            await GitHubDevHubProvider.PopulateOwnPullRequestsAsync(client, Credential(), results, "me", CancellationToken.None, scoped ? _gitHub : null);

            Assert.AreEqual(1, handler.RequestCount);
            Assert.HasCount(fails ? 2 : 3, results);
            Assert.AreSame(own, results.Single(pr => pr.NumericId == 1));
            Assert.IsTrue(own.IsReviewRequestedForCurrentUser);
            Assert.IsTrue(incoming.IsReviewRequestedForCurrentUser);
            Assert.HasCount(fails ? 1 : 2, results.Where(pr => pr.IsAuthoredByCurrentUser).ToArray());
        }

        [TestMethod]
        [DataRow("FAILURE", "failure", "Checks failing")]
        [DataRow("ERROR", "failure", "Checks failing")]
        [DataRow("PENDING", "pending", null)]
        [DataRow("SUCCESS", "success", null)]
        [DataRow("UNKNOWN", null, null)]
        public void GitHubRollup_MapsActualCheckState(string state, string? expectedCi, string? reason)
        {
            var pr = OwnPr();
            ApplyGitHub(pr, "\"commits\":{\"nodes\":[{\"commit\":{\"statusCheckRollup\":{\"state\":\"" + state + "\"}}}]}");

            Assert.AreEqual(expectedCi, pr.CiStatus);
            Assert.AreEqual(reason, pr.AttentionReason);
        }

        [TestMethod]
        public void GitHubMetadata_PreservesExplicitReviewRequestsButExcludesClosedAndDraftActions()
        {
            var pr = OwnPr();
            pr.IsAuthoredByCurrentUser = false;
            pr.IsReviewRequestedForCurrentUser = true;
            ApplyGitHub(pr, "\"mergeable\":\"CONFLICTING\"");
            Assert.AreEqual("conflicts", pr.Status);
            Assert.AreEqual("Review requested", pr.AttentionReason);

            using (var doc = JsonDocument.Parse("{\"state\":\"OPEN\",\"isDraft\":true,\"mergeable\":\"CONFLICTING\"}"))
                GitHubDevHubProvider.ApplyActionMetadata(pr, doc.RootElement);
            Assert.AreEqual("draft", pr.Status);
            Assert.IsFalse(pr.NeedsAttention);

            using (var doc = JsonDocument.Parse("{\"state\":\"CLOSED\",\"reviewDecision\":\"CHANGES_REQUESTED\"}"))
                GitHubDevHubProvider.ApplyActionMetadata(pr, doc.RootElement);
            Assert.IsFalse(pr.IsOpen);
            Assert.IsFalse(pr.HasChangesRequested);
            Assert.IsFalse(pr.IsReviewRequestedForCurrentUser);
        }

        [TestMethod]
        public async Task GitHubMetadata_UsesOneBoundedBatchForAuthoredActiveNonDraftPrsAsync()
        {
            var prs = Enumerable.Range(1, 150).Select(id => OwnPr(id)).ToList();
            prs.Insert(0, new DevHubPullRequest { Status = "open", NumericId = 999, RepoIdentifier = _gitHub });
            prs.Insert(0, new DevHubPullRequest { Status = "draft", IsAuthoredByCurrentUser = true, NumericId = 998, RepoIdentifier = _gitHub });
            Task<string>? bodyTask = null;
            var handler = new TestHttpMessageHandler((request, _) =>
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("https://api.github.com/graphql", request.RequestUri!.AbsoluteUri);
                Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
                bodyTask = request.Content!.ReadAsStringAsync();
                return Json("{\"data\":{\"r0\":{\"pullRequest\":{\"state\":\"OPEN\",\"reviewDecision\":\"CHANGES_REQUESTED\"}}}}");
            });
            using var client = new HttpClient(handler);

            await GitHubDevHubProvider.PopulateActionMetadataAsync(client, Credential(), prs, CancellationToken.None);

            Assert.IsNotNull(bodyTask);
            var body = await bodyTask;
            StringAssert.Contains(body, "reviewDecision");
            StringAssert.Contains(body, "statusCheckRollup");
            StringAssert.Contains(body, "number:100");
            Assert.DoesNotContain("number:101", body);
            Assert.DoesNotContain("number:999", body);
            Assert.DoesNotContain("number:998", body);
            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsTrue(prs.Single(pr => pr.NumericId == 1).HasChangesRequested);
            Assert.IsFalse(prs.Single(pr => pr.NumericId == 101).NeedsAttention);
        }

        [TestMethod]
        public async Task GitHubMetadata_ApiErrorsAreNotEmptySuccessAsync()
        {
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) => Json("{\"errors\":[{\"message\":\"Unavailable\"}],\"data\":null}")));

            await Assert.ThrowsExactlyAsync<JsonException>(() => GitHubDevHubProvider.PopulateActionMetadataAsync(
                client, Credential(), [OwnPr()], CancellationToken.None));
        }

        [TestMethod]
        [DataRow("open", "ME", "me", true, true, false)]
        [DataRow("closed", "me", "me", true, true, true)]
        [DataRow("open", "other", "me", false, true, false)]
        [DataRow("open", "me", "other", true, false, false)]
        [DataRow("open", "other", "other", false, false, false)]
        [DataRow("unknown", "me", "me", true, false, false)]
        public void GitHubIssues_MapIdentityAndStateInSearchAndRepoPaths(string state, string author,
            string assignee, bool authored, bool assigned, bool closed)
        {
            var item = "{\"repository_url\":\"https://api.github.com/repos/owner/repo\",\"number\":1,\"title\":\"Issue\","
                + "\"user\":{\"login\":\"" + author + "\"},\"assignees\":[{\"login\":\"" + assignee + "\"}],"
                + "\"state\":\"" + state + "\",\"created_at\":\"2026-11-01T10:00:00Z\",\"updated_at\":\"2026-11-02T10:00:00Z\",\"html_url\":\"https://github.com/owner/repo/issues/1\"}";
            var search = GitHubDevHubProvider.ParseIssues("{\"items\":[" + item + "]}", "me").Single();
            var scoped = GitHubDevHubProvider.ParseRepoIssues("[" + item + "]", _gitHub, "me").Single();

            foreach (var issue in new[] { search, scoped })
            {
                Assert.AreEqual(authored, issue.IsAuthoredByCurrentUser);
                Assert.AreEqual(assigned, issue.IsAssignedToCurrentUser);
                Assert.AreEqual(closed, issue.IsClosed);
                Assert.AreEqual(assigned && !closed ? "Assigned to you" : null, issue.AttentionReason);
                Assert.AreEqual(state, issue.StatusText);
            }
        }

        [TestMethod]
        [DataRow(-10, true)]
        [DataRow(-5, false)]
        [DataRow(0, false)]
        [DataRow(5, false)]
        [DataRow(10, false)]
        public void AzureVotes_DistinguishRejectionFromWaitingOrApproval(int vote, bool changes)
        {
            var json = AdoPrJson("active", false, vote);
            var pr = AzureDevOpsDevHubProvider.ParseAdoPullRequests(json, _ado, "me").Single();

            Assert.IsTrue(pr.IsAuthoredByCurrentUser);
            Assert.AreEqual(changes, pr.HasChangesRequested);
            Assert.AreEqual(changes ? "Changes requested" : null, pr.AttentionReason);
        }

        [TestMethod]
        public void AzurePullRequests_ExcludeClosedDraftAndDisplayNameOwnership()
        {
            var closed = AzureDevOpsDevHubProvider.ParseAdoPullRequests(AdoPrJson("completed", true, -10), _ado, "me").Single();
            Assert.IsFalse(closed.HasChangesRequested);
            Assert.IsFalse(closed.NeedsAttention);
            var draft = AzureDevOpsDevHubProvider.ParseAdoPullRequests(AdoPrJson("active", true, -10), _ado, "me").Single();
            Assert.IsFalse(draft.NeedsAttention);
            var other = AzureDevOpsDevHubProvider.ParseAdoPullRequests(AdoPrJson("active", false, -10), _ado, "display-name").Single();
            Assert.IsFalse(other.IsAuthoredByCurrentUser);
            Assert.IsFalse(other.NeedsAttention);
        }

        [TestMethod]
        public void AzureConflicts_OnlyKnownActiveNonDraftMergeConflicts()
        {
            var json = AdoPrJson("active", false, 0).Replace("\"status\":\"active\"", "\"status\":\"active\",\"mergeStatus\":\"conflicts\"");
            var pr = AzureDevOpsDevHubProvider.ParseAdoPullRequests(json, _ado, "me").Single();
            Assert.AreEqual("conflicts", pr.Status);
            Assert.AreEqual("Conflicts", pr.AttentionReason);
        }

        [TestMethod]
        public void AzureChecks_IgnoreOldIterationsUnrelatedBranchesAndSupersededFailures()
        {
            var json = AdoPrJson("active", false, 0);
            var prs = AzureDevOpsDevHubProvider.ParseAdoPullRequests(json, _ado, "me");
            var builds = "{\"value\":[" + Build(1, "old", "refs/pull/9/merge", "failed") + ","
                + Build(2, "current", "refs/heads/main", "failed") + ","
                + Build(3, "current", "refs/pull/9/merge", "failed") + ","
                + Build(4, "current", "refs/pull/9/merge", "succeeded") + "]}";

            AzureDevOpsDevHubProvider.ApplyBuildStatuses(prs, json, builds);

            Assert.AreEqual("success", prs.Single().CiStatus);
            Assert.IsFalse(prs.Single().NeedsAttention);
            AzureDevOpsDevHubProvider.ApplyBuildStatuses(prs, json, "{\"value\":[" + Build(5, "current", "refs/pull/9/merge", "failed") + "]}");
            Assert.AreEqual("Checks failing", prs.Single().AttentionReason);
            prs.Single().IsAuthoredByCurrentUser = false;
            Assert.IsFalse(prs.Single().NeedsAttention);
        }

        [TestMethod]
        public void AzureIssues_VerifyIdsAndRequireExplicitRepoLinksWhenScoped()
        {
            var json = AdoIssueJson("Active", "me", "ME", true);
            var issue = AzureDevOpsDevHubProvider.ParseAdoIssues(json, _ado, "me", RepositoryId).Single();
            Assert.IsTrue(issue.IsAuthoredByCurrentUser);
            Assert.IsTrue(issue.IsAssignedToCurrentUser);
            Assert.AreEqual("Active", issue.StatusText);
            Assert.AreEqual(_ado, issue.RepoIdentifier);
            Assert.IsEmpty(AzureDevOpsDevHubProvider.ParseAdoIssues(json, _ado, "display-name", RepositoryId));
            Assert.IsEmpty(AzureDevOpsDevHubProvider.ParseAdoIssues(AdoIssueJson("Active", "me", "me", false), _ado, "me", RepositoryId));
            var aggregate = AzureDevOpsDevHubProvider.ParseAdoIssues(json, _ado, "me").Single();
            Assert.IsNull(aggregate.RepoIdentifier);
            Assert.AreEqual("org/project", aggregate.RepoDisplayName);
        }

        [TestMethod]
        public void AzureIssues_ClosedAssignmentCannotCreateAction()
        {
            var issue = AzureDevOpsDevHubProvider.ParseAdoIssues(AdoIssueJson("Closed", "other", "me", true), _ado, "me").Single();
            Assert.IsTrue(issue.IsClosed);
            Assert.IsTrue(issue.IsAssignedToCurrentUser);
            Assert.IsFalse(issue.NeedsAttention);
            Assert.AreEqual("Closed", issue.StatusText);
            var own = AzureDevOpsDevHubProvider.ParseAdoIssues(AdoIssueJson("Active", "me", "other", true), _ado, "me").Single();
            Assert.IsTrue(own.IsAuthoredByCurrentUser);
            Assert.IsFalse(own.NeedsAttention);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AzureRepoDetail_QueriesOwnIssuesInBoundedBulkAndPreservesCloudServerAuthAsync(bool server)
        {
            var repo = server ? RemoteRepoIdentifier.TryParse("https://tfs.contoso.com/tfs/Collection/project/_git/repo")! : _ado;
            var queryTasks = new List<Task<string>>();
            var handler = new TestHttpMessageHandler((request, _) =>
            {
                Assert.AreEqual("Basic", request.Headers.Authorization!.Scheme);
                var uri = request.RequestUri!;
                var path = uri.AbsolutePath;
                if (path.EndsWith("/connectionData"))
                    return Json("{\"authenticatedUser\":{\"id\":\"me\"}}");
                if (path.EndsWith("/wiql"))
                {
                    Assert.AreEqual(HttpMethod.Post, request.Method);
                    queryTasks.Add(request.Content!.ReadAsStringAsync());
                    StringAssert.Contains(uri.Query, "$top=10");
                    StringAssert.Contains(uri.Query, server ? "api-version=5.0" : "api-version=7.1");
                    return Json("{\"workItems\":[{\"id\":1}]}");
                }
                if (path.EndsWith("/workitems"))
                {
                    StringAssert.Contains(uri.Query, "ids=1");
                    return Json(AdoIssueJson("Active", "me", "me", true));
                }
                return Json(path.EndsWith("/repositories/repo") ? "{\"id\":\"" + RepositoryId + "\"}" : "{\"value\":[]}");
            });
            using var client = new HttpClient(handler);
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential()), (_, _) => { }, getCredentialHosts: () => []);

            var detail = await provider.GetRepoDetailAsync(repo, 10, CancellationToken.None);

            var queryBodies = await Task.WhenAll(queryTasks);
            Assert.HasCount(2, queryBodies);
            var query = queryBodies.Single(body => body.Contains("System.CreatedBy"));
            StringAssert.Contains(query, "System.CreatedBy");
            StringAssert.Contains(query, "System.AssignedTo");
            StringAssert.Contains(query, "@Me");
            Assert.HasCount(1, queryBodies.Where(body => !body.Contains("System.CreatedBy")));
            Assert.IsNotNull(detail);
            Assert.HasCount(1, detail.Issues);
            Assert.IsTrue(detail.Issues.Single().NeedsAttention);
            Assert.AreEqual(11, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AzureIssueQuery_RetriesUnauthorizedPostButReportsActualFailuresAsync(bool forbidden)
        {
            var attempts = 0;
            var invalidations = 0;
            var bodies = new List<Task<string>>();
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/connectionData"))
                    return Json("{\"authenticatedUser\":{\"id\":\"me\"}}");
                if (path.EndsWith("/wiql"))
                {
                    attempts++;
                    bodies.Add(request.Content!.ReadAsStringAsync());
                    if (forbidden || attempts == 1)
                        return new HttpResponseMessage(forbidden ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized);
                    return Json("{\"workItems\":[{\"id\":1}]}");
                }
                if (path.EndsWith("/workitems"))
                    return Json(AdoIssueJson("Active", "me", "me", true));
                if (path.EndsWith("/pullrequests"))
                    return Json(AdoPrJson("active", false, 0));
                if (path.EndsWith("/build/builds"))
                    return Json("{\"value\":[" + Build(1, "current", "refs/pull/9/merge", "failed") + "]}");
                return Json(path.EndsWith("/repositories/repo") ? "{\"id\":\"" + RepositoryId + "\"}" : "{\"value\":[]}");
            }));
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential()), (_, _) => invalidations++);

            var detail = await provider.GetRepoDetailAsync(_ado, 10, CancellationToken.None);

            Assert.AreEqual(forbidden ? 2 : 3, attempts);
            Assert.AreEqual(forbidden ? 0 : 1, invalidations);
            Assert.IsNotNull(detail);
            Assert.HasCount(1, detail.PullRequests);
            Assert.HasCount(1, detail.CiRuns);
            Assert.AreEqual("failure", detail.PullRequests.Single().CiStatus);
            Assert.AreEqual("failure", detail.CiRuns.Single().Status);
            if (forbidden)
            {
                Assert.IsEmpty(detail.Issues);
                Assert.Contains("Issue activity could not be loaded. Check Work Items read access and refresh to retry.", detail.ActivityWarning);
                Assert.Contains("HTTP 403", detail.ActivityWarning);
            }
            else
            {
                Assert.HasCount(1, detail.Issues);
                Assert.IsNull(detail.ActivityWarning);
                var contents = await Task.WhenAll(bodies);
                Assert.AreEqual(contents[0], contents[1]);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AzureIssueQuery_RequestedCancellationPropagatesWithoutReturningPartialSuccessAsync(bool httpFailure)
        {
            using var source = new CancellationTokenSource();
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/connectionData"))
                    return Json("{\"authenticatedUser\":{\"id\":\"me\"}}");
                if (path.EndsWith("/wiql"))
                {
                    source.Cancel();
                    if (httpFailure)
                        return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    throw new OperationCanceledException(source.Token);
                }
                return Json(path.EndsWith("/repositories/repo") ? "{\"id\":\"" + RepositoryId + "\"}" : "{\"value\":[]}");
            }));
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential()), (_, _) => { });

            await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetRepoDetailAsync(_ado, 10, source.Token));
        }

        [TestMethod]
        public async Task GitHubMetadata_CancellationDoesNotSendRequestsAsync()
        {
            using var source = new CancellationTokenSource();
            source.Cancel();
            var handler = new TestHttpMessageHandler((_, _) => throw new AssertFailedException("Unexpected request."));
            using var client = new HttpClient(handler);

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => GitHubDevHubProvider.PopulateActionMetadataAsync(
                client, Credential(), [OwnPr()], source.Token));

            Assert.AreEqual(0, handler.RequestCount);
        }

        [TestMethod]
        public void NewActionMetadata_IsPersistedButComputedFieldsAreNot()
        {
            var pr = OwnPr();
            pr.HasChangesRequested = true;
            pr.IsApproved = true;
            var prJson = JsonSerializer.Serialize(pr);
            StringAssert.Contains(prJson, "\"HasChangesRequested\":true");
            StringAssert.Contains(prJson, "\"IsApproved\":true");
            Assert.IsTrue(JsonSerializer.Deserialize<DevHubPullRequest>(prJson)!.IsApproved);
            Assert.DoesNotContain("AttentionReason", prJson);
            Assert.DoesNotContain("NeedsAttention", prJson);
            Assert.DoesNotContain("IsOpen", prJson);
            var issue = new DevHubIssue { IsAuthoredByCurrentUser = true, IsAssignedToCurrentUser = true, IsClosed = true, State = "Closed" };
            var issueJson = JsonSerializer.Serialize(issue);
            StringAssert.Contains(issueJson, "\"IsAssignedToCurrentUser\":true");
            StringAssert.Contains(issueJson, "\"IsAuthoredByCurrentUser\":true");
            StringAssert.Contains(issueJson, "\"IsClosed\":true");
            Assert.DoesNotContain("AttentionReason", issueJson);
            Assert.DoesNotContain("NeedsAttention", issueJson);
            Assert.DoesNotContain("StatusText", issueJson);
            Assert.IsFalse(JsonSerializer.Deserialize<DevHubIssue>(issueJson)!.NeedsAttention);
        }

        [TestMethod]
        public void Comparer_ObservesEachNewActionField()
        {
            var left = OwnPr();
            var right = OwnPr();
            Assert.IsTrue(DevHubItemComparer.SamePullRequests([left], [right]));
            right.HasChangesRequested = true;
            Assert.IsFalse(DevHubItemComparer.SamePullRequests([left], [right]));
            right.HasChangesRequested = false;
            Assert.IsTrue(DevHubItemComparer.SamePullRequests([left], [right]));
            right.IsApproved = true;
            Assert.IsFalse(DevHubItemComparer.SamePullRequests([left], [right]));
            foreach (var mutate in new Action<DevHubIssue>[]
            {
                issue => issue.IsAuthoredByCurrentUser = true,
                issue => issue.IsAssignedToCurrentUser = true,
                issue => issue.IsClosed = true,
            })
            {
                var first = new DevHubIssue();
                var second = new DevHubIssue();
                Assert.IsTrue(DevHubItemComparer.SameIssues([first], [second]));
                mutate(second);
                Assert.IsFalse(DevHubItemComparer.SameIssues([first], [second]));
            }
        }

        private static void ApplyGitHub(DevHubPullRequest pr, string fields)
        {
            using var doc = JsonDocument.Parse("{\"state\":\"OPEN\"," + fields + "}");
            GitHubDevHubProvider.ApplyActionMetadata(pr, doc.RootElement);
        }

        private static DevHubPullRequest OwnPr(int id = 1) => new DevHubPullRequest
        {
            NumericId = id,
            RepoIdentifier = _gitHub,
            Status = "open",
            IsAuthoredByCurrentUser = true,
            UpdatedAt = DateTime.UtcNow.Date,
        };

        private static string AdoPrJson(string status, bool draft, int vote) =>
            "{\"value\":[{\"pullRequestId\":9,\"title\":\"Change\",\"status\":\"" + status + "\","
            + "\"isDraft\":" + (draft ? "true" : "false") + ",\"createdBy\":{\"id\":\"me\",\"displayName\":\"display-name\"},"
            + "\"reviewers\":[{\"id\":\"reviewer\",\"vote\":" + vote + "}],\"lastMergeCommit\":{\"commitId\":\"current\"},"
            + "\"sourceRefName\":\"refs/heads/feature\",\"targetRefName\":\"refs/heads/main\",\"creationDate\":\"2026-11-01T10:00:00Z\"}]}";

        private static string Build(int id, string commit, string branch, string result) =>
            "{\"id\":" + id + ",\"definition\":{\"id\":1},\"sourceVersion\":\"" + commit + "\",\"sourceBranch\":\"" + branch
            + "\",\"status\":\"completed\",\"result\":\"" + result + "\",\"queueTime\":\"2026-11-02T10:00:00Z\"}";

        private static string GitHubSearchItem(int id, string author) =>
            "{\"repository_url\":\"https://api.github.com/repos/owner/repo\",\"number\":" + id + ",\"title\":\"Activity\","
            + "\"user\":{\"login\":\"" + author + "\"},\"state\":\"open\",\"draft\":false,"
            + "\"created_at\":\"2026-11-01T10:00:00Z\",\"updated_at\":\"2026-11-02T10:00:00Z\",\"html_url\":\"https://github.com/owner/repo/issues/" + id + "\"}";

        private static string AdoIssueJson(string state, string author, string assigned, bool linked) =>
            "{\"value\":[{\"id\":1,\"fields\":{\"System.Title\":\"Task\",\"System.State\":\"" + state + "\","
            + "\"System.CreatedBy\":{\"id\":\"" + author + "\",\"displayName\":\"display-name\"},\"System.AssignedTo\":{\"id\":\"" + assigned + "\"},"
            + "\"System.CreatedDate\":\"2026-11-01T10:00:00Z\",\"System.ChangedDate\":\"2026-11-02T10:00:00Z\"},\"relations\":"
            + (linked ? "[{\"rel\":\"ArtifactLink\",\"url\":\"vstfs:///Git/PullRequestId/project-id%2F" + RepositoryId + "%2F9\"}]" : "[]") + "}]}";

        private static DevHubCredential Credential() => new DevHubCredential("unverified", "synthetic");

        private static HttpResponseMessage Json(string json) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}