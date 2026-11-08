using System.Net;
using System.Net.Http;
using System.Text.Json;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class GitHubActivityDetailsTests
    {
        private static readonly RemoteRepoIdentifier _repo = new RemoteRepoIdentifier("github.com", "owner", "repo");

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task PullRequests_CustomQueryConstrainsReviewAndMentionLanesButNotAuthoredOrAssignedWorkAsync(bool scoped)
        {
            var queries = new List<string>();
            using var client = Client(request =>
            {
                var query = Query(request);
                queries.Add(query);
                if (scoped)
                    Assert.Contains("AND repo:owner/repo", query);
                if (query.Contains("assignee:me"))
                {
                    Assert.DoesNotContain("org:ligershark", query);
                    var merged = query.Contains("is:merged");
                    return Search(new[] { Item(merged ? 5 : 4, assigned: true, closed: merged, merged: merged) });
                }
                if (query.Contains("author:me"))
                {
                    Assert.DoesNotContain("org:ligershark", query);
                    return query.Contains("is:merged")
                        ? Search(new[] { Item(3, "me", closed: true, merged: true) })
                        : Search(new[] { Item(2, "me") });
                }
                return query.Contains("(user:me org:ligershark)")
                    ? Search(new[] { Item(1) }) : Search(new[] { Item(99) });
            });

            var results = await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me",
                new DevHubActivityOptions(includeCompleted: true), CancellationToken.None, scoped ? _repo : null,
                "user:{login} org:ligershark", populateMetadata: false);

            CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5 }, results.Select(pr => pr.NumericId).ToArray());
            var incoming = results.Single(pr => pr.NumericId == 1);
            Assert.IsTrue(incoming.IsReviewRequestedForCurrentUser);
            Assert.IsTrue(incoming.IsMentioned);
            Assert.IsTrue(incoming.NeedsAttention);
            Assert.IsTrue(results.Single(pr => pr.NumericId == 2).IsAuthoredByCurrentUser);
            Assert.IsTrue(results.Single(pr => pr.NumericId == 3).IsMerged);
            Assert.IsTrue(results.Single(pr => pr.NumericId == 4).IsAssignedToCurrentUser);
            Assert.IsTrue(results.Single(pr => pr.NumericId == 5).IsAssignedToCurrentUser);
            Assert.IsTrue(results.Single(pr => pr.NumericId == 5).IsMerged);
            Assert.HasCount(7, queries);
            Assert.IsTrue(queries.Where(query => !query.Contains("author:me") && !query.Contains("assignee:me"))
                .All(query => query.Contains("is:pr AND (user:me org:ligershark)")));
            Assert.IsTrue(queries.Any(query => query.Contains("AND (user:me org:ligershark) AND is:open AND review-requested:me")));
            Assert.IsTrue(queries.Any(query => query.Contains("AND (user:me org:ligershark) AND is:open AND mentions:me")));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Issues_CustomQueryConstrainsMentionsButNotAuthoredAssignedOrCompletedWorkAsync(bool scoped)
        {
            var queries = new List<string>();
            using var client = Client(request =>
            {
                var query = Query(request);
                queries.Add(query);
                if (scoped)
                    Assert.Contains("AND repo:owner/repo", query);
                if (query.Contains("author:me") || query.Contains("assignee:me"))
                {
                    Assert.DoesNotContain("org:ligershark", query);
                    var assigned = query.Contains("assignee:me");
                    var closed = query.Contains("is:closed");
                    return Search(new[] { Item((assigned ? 3 : 2) + (closed ? 2 : 0),
                        assigned ? "other" : "me", assigned: assigned, closed: closed) });
                }
                return query.Contains("(user:me org:ligershark)")
                    ? Search(new[] { Item(1) }) : Search(new[] { Item(99) });
            });

            var results = await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me",
                new DevHubActivityOptions(includeCompleted: true), CancellationToken.None, scoped ? _repo : null,
                "user:{login} org:ligershark");

            CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5 }, results.Select(issue => issue.NumericId).ToArray());
            Assert.IsTrue(results.Single(issue => issue.NumericId == 1).IsMentioned);
            Assert.IsTrue(results.Single(issue => issue.NumericId == 2).IsAuthoredByCurrentUser);
            Assert.IsTrue(results.Single(issue => issue.NumericId == 3).IsAssignedToCurrentUser);
            Assert.IsTrue(results.Single(issue => issue.NumericId == 4).IsClosed);
            Assert.IsTrue(results.Single(issue => issue.NumericId == 5).IsClosed);
            Assert.HasCount(6, queries);
            Assert.IsTrue(queries.Any(query => query.Contains("is:issue AND (user:me org:ligershark) AND is:open AND mentions:me")));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task PersonalActivity_DefaultRequestsAndResultsAreLimitedToTwentyPerFeedAsync(bool issues)
        {
            var requests = 0;
            using var client = Client(request =>
            {
                requests++;
                StringAssert.Contains(Query(request), "per_page=20");
                Assert.AreEqual(1, PageNumber(request));
                return Search(Enumerable.Range(1, 40).Select(id => Item(id, "me", assigned: true)));
            });

            var count = issues
                ? (await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me",
                    new DevHubActivityOptions(), CancellationToken.None)).Count
                : (await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me",
                    new DevHubActivityOptions(), CancellationToken.None, populateMetadata: false)).Count;

            Assert.AreEqual(20, count);
            Assert.AreEqual(issues ? 4 : 5, requests);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PullRequestParsing_PreservesAssignmentFromSearchAndRepositoryPayloads(bool repositoryPayload)
        {
            var json = Item(1, assigned: true, repoPayload: repositoryPayload);
            var result = repositoryPayload
                ? GitHubDevHubProvider.ParseRepoPullRequests("[" + json + "]", _repo, "me")
                : GitHubDevHubProvider.ParsePullRequests("{\"items\":[" + json + "]}", "me");

            Assert.IsTrue(result.Single().IsAssignedToCurrentUser);
            Assert.IsFalse(result.Single().IsAuthoredByCurrentUser);
        }

        [TestMethod]
        public async Task AssignedPullRequests_PaginateIndependentlyAndRemainPersonalWhenLanesOverlapAsync()
        {
            var assignedPages = new List<int>();
            using var client = Client(request =>
            {
                var query = Query(request);
                if (!query.Contains("assignee:me"))
                    return query.Contains("org:incoming") ? Search(new[] { Item(1) }) : Search(Array.Empty<string>());
                Assert.DoesNotContain("org:incoming", query);
                var page = PageNumber(request);
                assignedPages.Add(page);
                return Search(Enumerable.Range((page - 1) * 100 + 1, 100).Select(id => Item(id, assigned: true)));
            });

            var results = await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me",
                new DevHubActivityOptions(125), CancellationToken.None, customQuery: "org:incoming", populateMetadata: false);

            Assert.HasCount(125, results);
            Assert.IsTrue(results.All(pr => pr.IsAssignedToCurrentUser));
            Assert.HasCount(125, DevHubWorkItem.GetMyWork(results, [], []));
            Assert.IsEmpty(DevHubWorkItem.GetIncoming(results, [], []));
            CollectionAssert.AreEqual(new[] { 1, 2 }, assignedPages);
        }

        [TestMethod]
        [DataRow("me")]
        [DataRow(null)]
        public async Task RepoDetails_CustomQueryUsesSearchInsteadOfUnfilteredRestListsAsync(string? login)
        {
            using var client = Client(request =>
            {
                if (request.Method == HttpMethod.Post)
                    return Json("{\"data\":{}}");
                if (request.RequestUri!.AbsolutePath.EndsWith("/actions/runs", StringComparison.Ordinal))
                    return Json("{\"workflow_runs\":[]}");
                Assert.AreEqual("/search/issues", request.RequestUri.AbsolutePath);
                var query = Query(request);
                Assert.Contains("AND repo:owner/repo", query);
                if (query.Contains("author:me") || query.Contains("assignee:me"))
                {
                    Assert.DoesNotContain("org:ligershark", query);
                    return Search(new[] { Item(2, "me", assigned: true) });
                }
                Assert.Contains("AND (org:ligershark OR org:other)", query);
                return Search(new[] { Item(1) });
            });

            var detail = await GitHubDevHubProvider.GetRepoDetailAsync(client, Credential(), login!, _repo,
                new DevHubActivityOptions(), CancellationToken.None, "org:ligershark OR org:other");

            CollectionAssert.AreEquivalent(login == null ? new[] { 1 } : new[] { 1, 2 },
                detail.PullRequests.Select(pr => pr.NumericId).ToArray());
            CollectionAssert.AreEquivalent(login == null ? new[] { 1 } : new[] { 1, 2 },
                detail.Issues.Select(issue => issue.NumericId).ToArray());
            if (login != null)
            {
                Assert.IsTrue(detail.PullRequests.Single(pr => pr.NumericId == 1).IsReviewRequestedForCurrentUser);
                Assert.IsTrue(detail.PullRequests.Single(pr => pr.NumericId == 1).IsMentioned);
                Assert.IsTrue(detail.Issues.Single(issue => issue.NumericId == 1).IsMentioned);
            }
        }

        [TestMethod]
        [DataRow(125, 2)]
        [DataRow(300, 3)]
        public async Task PullRequests_PaginatesIndependentAuthoredLaneBeyondOneHundredAsync(int limit, int pages)
        {
            var authoredUrls = new List<string>();
            using var client = Client(request =>
            {
                var query = Query(request);
                if (!query.Contains("author:me"))
                    return Json("{\"items\":[]}");
                Assert.DoesNotContain("org:incoming", query);
                authoredUrls.Add(query);
                var page = PageNumber(request);
                return Search(Enumerable.Range((page - 1) * 100 + 1, 100).Select(id => Item(id, "me")));
            });

            var results = await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me",
                new DevHubActivityOptions(limit), CancellationToken.None, customQuery: "is:open org:incoming", populateMetadata: false);

            Assert.HasCount(limit, results);
            Assert.IsTrue(results.All(pr => pr.IsAuthoredByCurrentUser));
            Assert.AreEqual(limit, results.Max(pr => pr.NumericId));
            Assert.HasCount(pages, authoredUrls);
            Assert.IsTrue(authoredUrls.All(url => url.Contains("per_page=100")));
        }

        [TestMethod]
        public async Task Issues_AssigneeQueryStillRunsAfterAuthorFailureAndPaginatesAsync()
        {
            var assignedPages = new List<int>();
            using var client = Client(request =>
            {
                var query = Query(request);
                if (query.Contains("author:me"))
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (!query.Contains("assignee:me"))
                    return Search(Array.Empty<string>());
                var page = PageNumber(request);
                assignedPages.Add(page);
                return Search(Enumerable.Range((page - 1) * 100 + 1, 100).Select(id => Item(id, assigned: true)));
            });

            var results = await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me",
                new DevHubActivityOptions(125), CancellationToken.None);

            Assert.HasCount(125, results);
            Assert.IsTrue(results.All(issue => issue.IsAssignedToCurrentUser));
            CollectionAssert.AreEqual(new[] { 1, 2 }, assignedPages);
        }

        [TestMethod]
        public async Task Issues_OptionalLaterPageFailurePreservesEarlierPagesAndOtherLanesAsync()
        {
            using var client = Client(request =>
            {
                var query = Query(request);
                if (query.Contains("author:me"))
                    return PageNumber(request) == 1
                        ? Search(Enumerable.Range(1, 100).Select(id => Item(id, "me")))
                        : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return query.Contains("assignee:me") ? Search(new[] { Item(101, assigned: true) }) : Search(Array.Empty<string>());
            });

            var results = await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me",
                new DevHubActivityOptions(125), CancellationToken.None);

            Assert.HasCount(101, results);
            Assert.IsTrue(results.Single(issue => issue.NumericId == 101).IsAssignedToCurrentUser);
            Assert.AreEqual(100, results.Count(issue => issue.IsAuthoredByCurrentUser));
        }

        [TestMethod]
        public async Task Pagination_HonorsNextLinkOnShortPageAsync()
        {
            using var client = Client(request =>
            {
                if (!Query(request).Contains("assignee:me"))
                    return Search(Array.Empty<string>());
                var page = PageNumber(request);
                var response = Search(new[] { Item(page, assigned: true) });
                if (page == 1)
                    response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/search/issues?page=2>; rel=\"next\"");
                return response;
            });

            var results = await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me",
                new DevHubActivityOptions(125), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { 1, 2 }, results.Select(issue => issue.NumericId).ToArray());
        }

        [TestMethod]
        public async Task Mentions_AreInformationalAndMetadataFailureKeepsPullRequestsAsync()
        {
            using var client = Client(request => request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : Query(request).Contains("mentions:me") ? Search(new[] { Item(1) }) : Search(Array.Empty<string>()));

            var results = await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me",
                new DevHubActivityOptions(), CancellationToken.None);

            var pr = results.Single();
            Assert.IsTrue(pr.IsMentioned);
            Assert.IsFalse(pr.IsReviewRequestedForCurrentUser);
            Assert.IsFalse(pr.NeedsAttention);
            Assert.IsNull(pr.CanMerge);
            Assert.IsNull(pr.UnresolvedThreadCount);
        }

        [TestMethod]
        public async Task IssueMentions_DoNotInventAssignmentOrAttentionAsync()
        {
            using var client = Client(request => Query(request).Contains("mentions:me")
                ? Search(new[] { Item(1) }) : Search(Array.Empty<string>()));

            var issue = (await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me",
                new DevHubActivityOptions(), CancellationToken.None)).Single();

            Assert.IsTrue(issue.IsMentioned);
            Assert.IsFalse(issue.IsAssignedToCurrentUser);
            Assert.IsFalse(issue.NeedsAttention);
        }

        [TestMethod]
        public async Task CompletedPullRequests_KeepActiveAndExcludeOldMergeEvenIfRecentlyUpdatedAsync()
        {
            var queries = new List<string>();
            using var client = Client(request =>
            {
                var query = Query(request);
                queries.Add(query);
                if (query.Contains("is:merged"))
                    return Search(new[] { Item(2, "me", closed: true, merged: true),
                        Item(3, "me", closed: true, merged: true, oldOutcome: true) });
                return query.Contains("author:me") ? Search(new[] { Item(1, "me") }) : Search(Array.Empty<string>());
            });

            var results = await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me",
                new DevHubActivityOptions(includeCompleted: true), CancellationToken.None, populateMetadata: false);

            CollectionAssert.AreEqual(new[] { 1, 2 }, results.Select(pr => pr.NumericId).ToArray());
            Assert.IsTrue(results[0].IsOpen);
            Assert.IsTrue(results[1].IsMerged);
            Assert.AreEqual("merged", results[1].Status);
            Assert.IsFalse(results[1].IsOpen);
            Assert.IsTrue(queries.Any(query => query.Contains("is:merged author:me merged:>=")));
            Assert.IsTrue(queries.Any(query => query.Contains("is:open author:me")));
        }

        [TestMethod]
        public async Task CompletedIssues_KeepClosedAssignmentsAndExcludeOldClosuresAsync()
        {
            var completedQueries = new List<string>();
            using var client = Client(request =>
            {
                var query = Query(request);
                if (query.Contains("is:closed"))
                {
                    completedQueries.Add(query);
                    return query.Contains("assignee:me")
                        ? Search(new[] { Item(2, assigned: true, closed: true), Item(3, assigned: true, closed: true, oldOutcome: true) })
                        : Search(new[] { Item(4, "me", closed: true) });
                }
                return query.Contains("author:me") ? Search(new[] { Item(1, "me") }) : Search(Array.Empty<string>());
            });

            var results = await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me",
                new DevHubActivityOptions(includeCompleted: true), CancellationToken.None);

            Assert.HasCount(3, results);
            Assert.IsFalse(results.Single(issue => issue.NumericId == 1).IsClosed);
            var assigned = results.Single(issue => issue.NumericId == 2);
            Assert.IsTrue(assigned.IsClosed);
            Assert.IsTrue(assigned.IsAssignedToCurrentUser);
            Assert.IsFalse(assigned.NeedsAttention);
            Assert.IsFalse(results.Any(issue => issue.NumericId == 3));
            Assert.HasCount(2, completedQueries);
            Assert.IsTrue(completedQueries.All(query => query.Contains("closed:>=")));
        }

        [TestMethod]
        public async Task DefaultOptions_DoNotQueryCompletedActivityAsync()
        {
            var queries = new List<string>();
            using var client = Client(request =>
            {
                queries.Add(Query(request));
                return Search(Array.Empty<string>());
            });

            await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me", new DevHubActivityOptions(), CancellationToken.None);
            await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me", new DevHubActivityOptions(), CancellationToken.None);

            Assert.HasCount(9, queries);
            Assert.IsFalse(queries.Any(query => query.Contains("is:closed") || query.Contains("is:merged")));
        }

        [TestMethod]
        [DataRow("UNKNOWN")]
        [DataRow("HAS_HOOKS")]
        public void MergeReadiness_ApprovalAndSuccessCannotReplaceVerifiedCleanState(string mergeState)
        {
            var pr = OwnPullRequest();
            Apply(pr, Metadata(mergeState: mergeState));

            Assert.IsTrue(pr.IsApproved);
            Assert.AreEqual("success", pr.CiStatus);
            Assert.IsNull(pr.CanMerge);
            Assert.IsFalse(string.IsNullOrEmpty(pr.MergeBlockReason));
        }

        [TestMethod]
        public void MergeReadiness_RequiresCompleteDiscussionsAndChecks()
        {
            var pr = OwnPullRequest();
            Apply(pr, Metadata());
            Assert.IsTrue(pr.CanMerge);
            Assert.AreEqual(0, pr.UnresolvedThreadCount);
            Assert.IsNull(pr.MergeBlockReason);

            Apply(pr, Metadata(threadNextPage: true));
            Assert.IsNull(pr.CanMerge);
            Assert.IsNull(pr.UnresolvedThreadCount);

            Apply(pr, Metadata(checkNextPage: true));
            Assert.IsNull(pr.CanMerge);
            StringAssert.Contains(pr.ChecksSummary, "partial");

            Apply(pr, "{\"state\":\"OPEN\",\"isDraft\":false,\"mergeStateStatus\":\"CLEAN\",\"mergeable\":\"MERGEABLE\",\"reviewDecision\":\"APPROVED\"}");
            Assert.IsNull(pr.CanMerge);
            Assert.IsNull(pr.UnresolvedThreadCount);
        }

        [TestMethod]
        public void DiscussionsAndChecks_ExposeUnresolvedCountNamesAndDirectFailingLink()
        {
            var pr = OwnPullRequest();
            Apply(pr, Metadata(unresolved: true, checkState: "FAILURE"));

            Assert.AreEqual(1, pr.UnresolvedThreadCount);
            Assert.IsFalse(pr.CanMerge);
            Assert.IsTrue(pr.NeedsAttention);
            StringAssert.Contains(pr.ChecksSummary, "Unit tests: FAILURE");
            StringAssert.Contains(pr.ChecksSummary, "Legacy build: SUCCESS");
            Assert.AreEqual("https://github.com/owner/repo/actions/runs/10/job/20", pr.ChecksUrl);
        }

        [TestMethod]
        [DataRow("CHANGES_REQUESTED", "SUCCESS", "CLEAN")]
        [DataRow("REVIEW_REQUIRED", "SUCCESS", "CLEAN")]
        [DataRow("APPROVED", "FAILURE", "CLEAN")]
        [DataRow("APPROVED", "PENDING", "CLEAN")]
        [DataRow("APPROVED", "SUCCESS", "BLOCKED")]
        public void MergeReadiness_ConfirmedBlockersAreNotReady(string review, string checks, string state)
        {
            var pr = OwnPullRequest();
            Apply(pr, Metadata(mergeState: state, review: review, checkState: checks));

            Assert.IsFalse(pr.CanMerge);
            Assert.IsFalse(string.IsNullOrWhiteSpace(pr.MergeBlockReason));
        }

        [TestMethod]
        [DataRow(401, false, "Reconnect the GitHub account")]
        [DataRow(403, false, "Refresh cannot grant missing permissions")]
        [DataRow(403, true, "rate limit")]
        [DataRow(429, false, "rate limit")]
        [DataRow(503, false, "Try again later")]
        public async Task OptionalMetadataHttpFailure_ExplainsCauseAndRecoveryWithoutStalingInventoryAsync(
            int status, bool rateLimited, string expectedGuidance)
        {
            var fail = true;
            using var client = Client(request =>
            {
                if (request.Method != HttpMethod.Post)
                    return Query(request).Contains("author:me") ? Search([Item(1, "me")]) : Search([]);
                if (!fail)
                    return Json("{\"data\":{\"r0\":{\"pullRequest\":" + Metadata() + "}}}");
                var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") };
                if (rateLimited)
                    response.Headers.Add("X-RateLimit-Remaining", "0");
                return response;
            });
            Task<DevHubActivityResult<List<DevHubPullRequest>>> FetchAsync() => DevHubActivityRequest.RunAsync(
                () => GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me", new DevHubActivityOptions(), CancellationToken.None),
                prs => prs.Count > 0, CancellationToken.None);

            var failed = await FetchAsync();

            Assert.AreEqual(DevHubActivityStatus.Success, failed.Status);
            Assert.HasCount(1, failed.Value);
            Assert.IsNull(failed.Value[0].CanMerge);
            StringAssert.Contains(failed.Message, $"HTTP {status}");
            StringAssert.Contains(failed.Message, expectedGuidance);
            Assert.DoesNotContain("Refresh to retry", failed.Message);

            fail = false;
            var recovered = await FetchAsync();
            Assert.AreEqual(DevHubActivityStatus.Success, recovered.Status);
            Assert.IsNull(recovered.Message);
            Assert.IsTrue(recovered.Value[0].CanMerge);
        }

        [TestMethod]
        public async Task OptionalGraphQlError_ReportsReasonAndOnlyInvalidatesAffectedPullRequestAsync()
        {
            using var client = Client(request => request.Method == HttpMethod.Post
                ? Json("{\"errors\":[{\"message\":\"Checks permission denied not-a-real-token\",\"path\":[\"r1\",\"pullRequest\",\"reviewDecision\"]}],"
                    + "\"data\":{\"r0\":{\"pullRequest\":" + Metadata() + "},\"r1\":{\"pullRequest\":" + Metadata() + "}}}")
                : Query(request).Contains("author:me") ? Search([Item(1, "me"), Item(2, "me")]) : Search([]));

            var result = await DevHubActivityRequest.RunAsync(() => GitHubDevHubProvider.GetUserPullRequestsAsync(
                client, Credential(), "me", new DevHubActivityOptions(), CancellationToken.None), prs => prs.Count > 0, CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Success, result.Status);
            Assert.IsTrue(result.Value.Single(pr => pr.NumericId == 1).CanMerge);
            Assert.IsNull(result.Value.Single(pr => pr.NumericId == 2).CanMerge);
            StringAssert.Contains(result.Message, "Checks permission denied");
            Assert.DoesNotContain("not-a-real-token", result.Message);
            StringAssert.Contains(result.Message, "[redacted]");
        }

        [TestMethod]
        public async Task OptionalSamlRestriction_PreservesOtherOrganizationsWithoutAPageWarningAsync()
        {
            using var client = Client(request => request.Method == HttpMethod.Post
                ? Json("{\"errors\":[{\"message\":\"Resource protected by organization SAML enforcement. You must grant your OAuth token access to this organization.\",\"path\":[\"r0\"]}],\"data\":{\"r0\":null,\"r1\":{\"pullRequest\":" + Metadata() + "}}}")
                : Query(request).Contains("author:me") ? Search([
                    Item(1, "me").Replace("owner/repo", "restricted-org/repo"),
                    Item(2, "me").Replace("owner/repo", "permitted-org/repo")]) : Search([]));

            var result = await DevHubActivityRequest.RunAsync(() => GitHubDevHubProvider.GetUserPullRequestsAsync(
                client, Credential(), "me", new DevHubActivityOptions(), CancellationToken.None), prs => prs.Count > 0, CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Success, result.Status);
            Assert.HasCount(2, result.Value);
            var restricted = result.Value.Single(pr => pr.NumericId == 1);
            var permitted = result.Value.Single(pr => pr.NumericId == 2);
            Assert.AreEqual("restricted-org", restricted.RepoIdentifier.Owner);
            Assert.AreEqual("permitted-org", permitted.RepoIdentifier.Owner);
            Assert.IsNull(restricted.CanMerge);
            Assert.IsTrue(permitted.CanMerge);
            Assert.IsNull(result.Message);
        }

        [TestMethod]
        public async Task OptionalSamlRestriction_DoesNotHideOtherMetadataFailuresAsync()
        {
            using var client = Client(request => request.Method == HttpMethod.Post
                ? Json("{\"errors\":[{\"message\":\"Organization SAML enforcement\",\"path\":[\"r0\"]},"
                    + "{\"message\":\"Checks permission denied\",\"path\":[\"r1\"]}],"
                    + "\"data\":{\"r0\":null,\"r1\":null,\"r2\":{\"pullRequest\":" + Metadata() + "}}}")
                : Query(request).Contains("author:me") ? Search([Item(1, "me"), Item(2, "me"), Item(3, "me")]) : Search([]));

            var result = await DevHubActivityRequest.RunAsync(() => GitHubDevHubProvider.GetUserPullRequestsAsync(
                client, Credential(), "me", new DevHubActivityOptions(), CancellationToken.None), prs => prs.Count > 0, CancellationToken.None);

            Assert.HasCount(3, result.Value);
            Assert.IsNull(result.Value.Single(pr => pr.NumericId == 1).CanMerge);
            Assert.IsNull(result.Value.Single(pr => pr.NumericId == 2).CanMerge);
            Assert.IsTrue(result.Value.Single(pr => pr.NumericId == 3).CanMerge);
            StringAssert.Contains(result.Message, "Checks permission denied");
            Assert.DoesNotContain("SAML", result.Message);
        }

        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task CachedOptionalSamlWarning_IsRemovedWithoutLosingDataOrUnrelatedFailuresAsync(bool otherFailure, bool samlFirst)
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StartScreen-Saml-" + Guid.NewGuid().ToString("N"));
            var cache = new DevHubCacheService(folder);
            const string saml = "Review/check metadata is restricted by organization SAML SSO for org/repo#1. Authorize the existing OAuth app under GitHub Settings > Applications > Authorized OAuth Apps for that organization. Loaded pull requests remain available.";
            const string failure = "Checks permission denied";
            var message = !otherFailure ? saml : samlFirst ? saml + " " + failure : failure + " " + saml;
            try
            {
                var dashboard = new DevHubDashboard();
                dashboard.PullRequests.Add(new DevHubPullRequest { NumericId = 1 });
                dashboard.Sources["GitHub/pulls"] = new DevHubActivitySnapshot { Message = message };
                dashboard.ActivityWarnings["GitHub/pulls"] = message;
                dashboard.ActivityWarnings["Other"] = "Network unavailable";
                await cache.WriteDashboardAsync(dashboard);

                var restored = await cache.ReadDashboardAsync();

                Assert.IsNotNull(restored);
                Assert.HasCount(1, restored.PullRequests);
                Assert.AreEqual(1, restored.PullRequests[0].NumericId);
                Assert.AreEqual(otherFailure ? failure : null, restored.Sources["GitHub/pulls"].Message);
                Assert.AreEqual(otherFailure, restored.ActivityWarnings.ContainsKey("GitHub/pulls"));
                if (otherFailure)
                    Assert.AreEqual(failure, restored.ActivityWarnings["GitHub/pulls"]);
                Assert.AreEqual("Network unavailable", restored.ActivityWarnings["Other"]);
            }
            finally
            {
                if (System.IO.Directory.Exists(folder))
                    System.IO.Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public async Task CachedSamlWarning_IsMovedToOutputWithoutHidingOtherWarningsAsync()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StartScreen-Saml-" + Guid.NewGuid().ToString("N"));
            var cache = new DevHubCacheService(folder);
            const string legacy = "Pull requests loaded. GitHub is connected. Some organizations restrict review/check details with SAML SSO.";
            try
            {
                var dashboard = new DevHubDashboard();
                dashboard.Sources["GitHub/pulls"] = new DevHubActivitySnapshot { Message = legacy };
                dashboard.ActivityWarnings["GitHub/pulls"] = legacy;
                dashboard.ActivityWarnings["Other"] = "Checks permission denied";
                await cache.WriteDashboardAsync(dashboard);

                var restored = await cache.ReadDashboardAsync();

                Assert.IsNotNull(restored);
                Assert.IsNull(restored.Sources["GitHub/pulls"].Message);
                Assert.IsFalse(restored.ActivityWarnings.ContainsKey("GitHub/pulls"));
                Assert.AreEqual("Checks permission denied", restored.ActivityWarnings["Other"]);
            }
            finally
            {
                if (System.IO.Directory.Exists(folder))
                    System.IO.Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public async Task OptionalGraphQlErrors_PreserveUsableMetadataButNeverClaimReadyAsync()
        {
            using var client = Client(request => request.Method == HttpMethod.Post
                ? Json("{\"errors\":[{\"message\":\"One field unavailable\"}],\"data\":{\"r0\":{\"pullRequest\":" + Metadata() + "}}}")
                : Query(request).Contains("author:me") ? Search(new[] { Item(1, "me") }) : Search(Array.Empty<string>()));

            var pr = (await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me",
                new DevHubActivityOptions(), CancellationToken.None)).Single();

            Assert.IsTrue(pr.IsApproved);
            Assert.AreEqual("success", pr.CiStatus);
            Assert.IsNull(pr.CanMerge);
        }

        [TestMethod]
        public async Task RepoDetails_PersonalReviewAndMentionFlagsSurviveRestMergeAsync()
        {
            using var client = Client(request =>
            {
                if (request.Method == HttpMethod.Post)
                    return Json("{\"data\":{}}");
                if (request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal))
                    return Json("[" + Item(1, "me", repoPayload: true) + "]");
                if (request.RequestUri.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal)
                    && request.RequestUri.AbsolutePath != "/search/issues")
                    return Json("[]");
                if (request.RequestUri.AbsolutePath.EndsWith("/actions/runs", StringComparison.Ordinal))
                    return Json("{\"workflow_runs\":[]}");
                var query = Query(request);
                Assert.Contains("repo:owner/repo", query);
                return query.Contains("is:pr") && (query.Contains("review-requested:me") || query.Contains("mentions:me"))
                    ? Search(new[] { Item(1, "me") }) : Search(Array.Empty<string>());
            });

            var detail = await GitHubDevHubProvider.GetRepoDetailAsync(client, Credential(), "me", _repo,
                new DevHubActivityOptions(), CancellationToken.None);

            var pr = detail.PullRequests.Single();
            Assert.IsTrue(pr.IsAuthoredByCurrentUser);
            Assert.IsTrue(pr.IsReviewRequestedForCurrentUser);
            Assert.IsTrue(pr.IsMentioned);
            Assert.AreEqual("main", pr.TargetBranch);
        }

        [TestMethod]
        public async Task RepoDetails_ExtendedOptionsIncludeCompletedWorkAndCiActorAsync()
        {
            using var client = Client(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/actions/runs", StringComparison.Ordinal))
                    return Json("{\"workflow_runs\":[" + Run(1, "me", null, "success") + "]}");
                if (request.RequestUri.AbsolutePath != "/search/issues")
                    return Json("[]");
                var query = Query(request);
                if (query.Contains("is:merged"))
                    return Search(new[] { Item(2, "me", closed: true, merged: true) });
                return query.Contains("is:closed") && query.Contains("assignee:me")
                    ? Search(new[] { Item(3, assigned: true, closed: true) }) : Search(Array.Empty<string>());
            });

            var detail = await GitHubDevHubProvider.GetRepoDetailAsync(client, Credential(), "me", _repo,
                new DevHubActivityOptions(includeCompleted: true), CancellationToken.None);

            Assert.IsTrue(detail.PullRequests.Single().IsMerged);
            Assert.IsFalse(detail.PullRequests.Single().IsOpen);
            Assert.IsTrue(detail.Issues.Single().IsClosed);
            Assert.IsTrue(detail.Issues.Single().IsAssignedToCurrentUser);
            Assert.IsTrue(detail.CiRuns.Single().IsTriggeredByCurrentUser);
        }

        [TestMethod]
        public void IssueMetadata_UsesExplicitTypeAssignmentMilestoneAndUnambiguousLabels()
        {
            var json = "{\"items\":[" + Item(1, extra: ",\"type\":{\"name\":\"Bug\"},\"milestone\":{\"title\":\"November\"},"
                + "\"labels\":[{\"name\":\"blocked\"},{\"name\":\"priority:high\"}]") + "]}";
            var issue = GitHubDevHubProvider.ParseIssues(json, "me").Single();

            Assert.AreEqual("Bug", issue.WorkItemType);
            Assert.AreEqual("November", issue.Iteration);
            Assert.IsTrue(issue.IsBlocked);
            Assert.AreEqual("priority:high", issue.Priority);
            Assert.IsFalse(issue.IsAssignedToCurrentUser);

            json = "{\"items\":[" + Item(2, assigned: true, extra: ",\"labels\":[{\"name\":\"P1\"},{\"name\":\"P2\"},{\"name\":\"not-blocked\"}]") + "]}";
            issue = GitHubDevHubProvider.ParseIssues(json, "me").Single();
            Assert.AreEqual("me", issue.AssignedTo);
            Assert.IsNull(issue.Priority);
            Assert.IsFalse(issue.IsBlocked);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task CiRuns_UseActualActorAndBoundedOptionalFailedJobDetailsAsync(bool jobsUnavailable)
        {
            var jobRequests = 0;
            using var client = Client(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/user/repos")
                    return Json("[{\"full_name\":\"owner/repo\"}]");
                if (request.RequestUri.AbsolutePath.EndsWith("/jobs", StringComparison.Ordinal))
                {
                    jobRequests++;
                    StringAssert.Contains(Query(request), "per_page=100");
                    return jobsUnavailable ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                        : Json("{\"jobs\":[{\"name\":\"Unit tests\",\"conclusion\":\"failure\",\"html_url\":\"https://github.com/owner/repo/actions/runs/1/job/2\"}]}");
                }
                return Json("{\"workflow_runs\":[" + Run(1, "me", "other", "failure") + ","
                    + Run(2, "other", "ME", "success") + "," + Run(3, "me", null, "success") + "]}");
            });

            var runs = await GitHubDevHubProvider.GetUserCiRunsAsync(client, Credential(), "me",
                new DevHubActivityOptions(), CancellationToken.None);

            Assert.HasCount(3, runs);
            var failed = runs.Single(run => run.Status == "failure");
            Assert.IsFalse(failed.IsTriggeredByCurrentUser);
            Assert.IsTrue(runs.Where(run => run.Status == "success").All(run => run.IsTriggeredByCurrentUser));
            Assert.AreEqual(1, jobRequests);
            if (jobsUnavailable)
                Assert.IsNull(failed.FailureUrl);
            else
            {
                Assert.AreEqual("Unit tests", failed.FailureSummary);
                Assert.AreEqual("https://github.com/owner/repo/actions/runs/1/job/2", failed.FailureUrl);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task CiRuns_EnrichOnlyTwoNewestFailuresWithoutDroppingOlderRunsAsync(bool scoped)
        {
            var jobIds = new List<int>();
            var now = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
            using var client = Client(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/user/repos")
                    return Json("[{\"full_name\":\"owner/repo\"}]");
                if (path.EndsWith("/actions/runs", StringComparison.Ordinal))
                    return Json("{\"workflow_runs\":[" + string.Join(",", Enumerable.Range(1, 7)
                        .Select(id => Run(id, "me", null, id == 7 ? "success" : "failure", now.AddMinutes(id)))) + "]}");
                if (path.EndsWith("/jobs", StringComparison.Ordinal))
                {
                    jobIds.Add(int.Parse(path.Split('/').AsEnumerable().Reverse().Skip(1).First()));
                    return Json("{\"jobs\":[{\"name\":\"Unit tests\",\"conclusion\":\"failure\",\"html_url\":\"https://github.com/owner/repo/actions\"}]}");
                }
                return request.Method == HttpMethod.Post ? Json("{\"data\":{}}") : Json("[]");
            });

            var runs = scoped
                ? (await GitHubDevHubProvider.GetRepoDetailAsync(client, Credential(), "me", _repo,
                    new DevHubActivityOptions(), CancellationToken.None)).CiRuns
                : await GitHubDevHubProvider.GetUserCiRunsAsync(client, Credential(), "me",
                    new DevHubActivityOptions(), CancellationToken.None);

            Assert.HasCount(7, runs);
            CollectionAssert.AreEqual(new[] { 6, 5 }, jobIds);
            Assert.HasCount(2, runs.Where(run => run.FailureSummary == "Unit tests").ToArray());
            Assert.IsTrue(runs.Where(run => run.Timestamp < now.AddMinutes(5)).All(run => run.FailureSummary == null && run.FailureUrl == null));
            Assert.IsTrue(runs.All(run => !string.IsNullOrEmpty(run.WebUrl)));
        }

        [TestMethod]
        public async Task CiRuns_PaginatesBeyondOneHundredAsync()
        {
            var pages = new List<int>();
            using var client = Client(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/user/repos")
                    return Json("[{\"full_name\":\"owner/repo\"}]");
                var page = PageNumber(request);
                pages.Add(page);
                return Json("{\"workflow_runs\":[" + string.Join(",", Enumerable.Range((page - 1) * 100 + 1, 100)
                    .Select(id => Run(id, "me", null, "success"))) + "]}");
            });

            var results = await GitHubDevHubProvider.GetUserCiRunsAsync(client, Credential(), "me",
                new DevHubActivityOptions(125), CancellationToken.None);

            Assert.HasCount(125, results);
            CollectionAssert.AreEqual(new[] { 1, 2 }, pages);
            Assert.IsTrue(results.All(run => run.IsTriggeredByCurrentUser));
        }

        [TestMethod]
        public async Task Pagination_RequestedCancellationPropagatesRatherThanReturningPartialSuccessAsync()
        {
            using var source = new CancellationTokenSource();
            using var client = Client(request =>
            {
                if (!Query(request).Contains("author:me"))
                    return Search(Array.Empty<string>());
                if (PageNumber(request) == 2)
                    source.Cancel();
                return Search(Enumerable.Range(1, 100).Select(id => Item(id, "me")));
            });

            await Assert.ThrowsAsync<OperationCanceledException>(() => GitHubDevHubProvider.GetUserPullRequestsAsync(
                client, Credential(), "me", new DevHubActivityOptions(125), source.Token, populateMetadata: false));
        }

        [TestMethod]
        public async Task OptionalMetadata_RequestedCancellationPropagatesAsync()
        {
            using var source = new CancellationTokenSource();
            using var client = Client(request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    source.Cancel();
                    return Json("{\"data\":{}}");
                }
                return Query(request).Contains("author:me") ? Search(new[] { Item(1, "me") }) : Search(Array.Empty<string>());
            });

            await Assert.ThrowsAsync<OperationCanceledException>(() => GitHubDevHubProvider.GetUserPullRequestsAsync(
                client, Credential(), "me", new DevHubActivityOptions(), source.Token));
        }

        private static string Item(int id, string author = "other", bool assigned = false, bool closed = false,
            bool merged = false, bool oldOutcome = false, bool repoPayload = false, string extra = "")
        {
            var now = DateTime.UtcNow;
            var outcome = now.AddDays(oldOutcome ? -30 : -1).ToString("O");
            return "{\"repository_url\":\"https://api.github.com/repos/owner/repo\",\"number\":" + id
                + ",\"title\":\"Activity\",\"user\":{\"login\":\"" + author + "\"},\"state\":\"" + (closed ? "closed" : "open")
                + "\",\"assignees\":" + (assigned ? "[{\"login\":\"me\"}]" : "[]")
                + ",\"created_at\":\"" + now.AddDays(-40).ToString("O") + "\",\"updated_at\":\"" + now.ToString("O")
                + "\",\"closed_at\":" + (closed ? "\"" + outcome + "\"" : "null")
                + ",\"html_url\":\"https://github.com/owner/repo/issues/" + id + "\""
                + (merged ? ",\"pull_request\":{\"merged_at\":\"" + outcome + "\"}" : "")
                + (repoPayload ? ",\"base\":{\"ref\":\"main\"},\"head\":{\"ref\":\"feature\"}" : "") + extra + "}";
        }

        private static string Run(int id, string actor, string? triggeringActor, string conclusion, DateTime? updatedAt = null)
        {
            return "{\"id\":" + id + ",\"name\":\"Build " + id + "\",\"status\":\"completed\",\"conclusion\":\"" + conclusion
                + "\",\"actor\":{\"login\":\"" + actor + "\"}" + (triggeringActor == null ? "" : ",\"triggering_actor\":{\"login\":\"" + triggeringActor + "\"}")
                + ",\"updated_at\":\"" + (updatedAt ?? new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc)).ToString("O")
                + "\",\"html_url\":\"https://github.com/owner/repo/actions/runs/" + id + "\"}";
        }

        private static string Metadata(string mergeState = "CLEAN", bool unresolved = false, bool threadNextPage = false,
            bool checkNextPage = false, string review = "APPROVED", string checkState = "SUCCESS")
        {
            return JsonSerializer.Serialize(new
            {
                state = "OPEN",
                isDraft = false,
                mergeable = "MERGEABLE",
                mergeStateStatus = mergeState,
                reviewDecision = review,
                reviewThreads = new { nodes = new[] { new { isResolved = !unresolved } }, pageInfo = new { hasNextPage = threadNextPage } },
                commits = new
                {
                    nodes = new[] { new { commit = new { statusCheckRollup = new
                    {
                        state = checkState,
                        contexts = new
                        {
                            nodes = new object[]
                            {
                                new { context = "Legacy build", state = "SUCCESS", targetUrl = "https://example.com/build" },
                                new { name = "Unit tests", conclusion = checkState, detailsUrl = "https://github.com/owner/repo/actions/runs/10/job/20" },
                            },
                            pageInfo = new { hasNextPage = checkNextPage },
                        },
                    } } } },
                },
            });
        }

        private static DevHubPullRequest OwnPullRequest() => new DevHubPullRequest
        {
            RepoIdentifier = _repo,
            NumericId = 1,
            Status = "open",
            IsAuthoredByCurrentUser = true,
            UpdatedAt = DateTime.UtcNow,
        };

        private static void Apply(DevHubPullRequest pr, string json)
        {
            using var doc = JsonDocument.Parse(json);
            GitHubDevHubProvider.ApplyActionMetadata(pr, doc.RootElement);
        }

        private static DevHubCredential Credential() => new DevHubCredential("test", "not-a-real-token");
        private static string Query(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.Query);
        private static int PageNumber(HttpRequestMessage request) => int.Parse(request.RequestUri!.Query.Split('&').Single(part => part.StartsWith("page=", StringComparison.Ordinal)).Substring(5));
        private static HttpResponseMessage Search(IEnumerable<string> items) => Json("{\"items\":[" + string.Join(",", items) + "]}");
        private static HttpResponseMessage Json(string json) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new HttpClient(new Handler(respond));

        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            private readonly object _sync = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                    return Task.FromResult(respond(request));
            }
        }
    }
}
