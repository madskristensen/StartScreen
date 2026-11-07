using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Azure.Core;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class FeedbackServiceTests
    {
        private readonly string _cacheFolder = Path.Combine(Path.GetTempPath(), "StartScreen-Feedback-" + Guid.NewGuid().ToString("N"));

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_cacheFolder))
                Directory.Delete(_cacheFolder, true);
        }

        [TestMethod]
        public async Task Load_PagesAllActivityDeduplicatesAndChecksCurrentOwnershipAndVotesAsync()
        {
            var actions = Enumerable.Range(0, 1502).Select(_ => Action("commented", 999)).ToArray();
            actions[0] = Action("posted", 10);
            actions[1] = Action("voted", 11);
            actions[2] = Action("voted", 12);
            actions[3] = Action("voted", 13);
            actions[4] = Action("posted", 14);
            actions[5] = Action("posted", 16);
            actions[6] = Action("posted", 17);
            actions[500] = Action("voted", 11);
            actions[1501] = Action("posted", 18);
            var pages = new List<int>();
            var hydrated = new List<long>();
            using var handler = new Handler((path, body) =>
            {
                if (path == "metadata")
                    return Json(new { Id = "7" });
                if (path == "getActivity")
                {
                    var page = body!.Value.GetProperty("page").GetInt32();
                    Assert.AreEqual(500, body.Value.GetProperty("pageSize").GetInt32());
                    pages.Add(page);
                    return Json(new { totalCount = actions.Length, activities = actions.Skip((page - 1) * 500).Take(500) });
                }

                Assert.AreEqual("item", path);
                Assert.AreEqual("_item", body!.Value.GetProperty("filter").GetString());
                Assert.AreEqual(3, body.Value.GetProperty("typesFilterFlags").GetInt32());
                Assert.AreEqual(20220110, body.Value.GetProperty("searchFor").GetProperty("version").GetInt32());
                Assert.IsFalse(body.Value.TryGetProperty("spaceIds", out _), "Tickets from other product spaces must not be excluded.");
                var id = RequestedId(body);
                lock (hydrated)
                    hydrated.Add(id);
                return id == 14 ? Json(new { results = Array.Empty<object>() })
                    : Json(new { results = new[] { Ticket(id, id == 10 || id == 13 || id == 17 || id == 18 ? 7 : 8, id == 11 || id == 13) } });
            });
            using var client = new HttpClient(handler);
            var service = Service(client);

            FeedbackSnapshot result = await service.GetTicketsAsync(false, CancellationToken.None);

            Assert.AreEqual(7L, result.PrincipalId);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, pages);
            CollectionAssert.AreEquivalent(new long[] { 10, 11, 12, 13, 14, 16, 17, 18 }, hydrated);
            CollectionAssert.AreEqual(new long[] { 18, 17, 13, 11, 10 }, result.Tickets.Select(ticket => ticket.Id).ToArray());
            var both = result.Tickets.Single(ticket => ticket.Id == 13);
            Assert.IsTrue(both.IsCreatedByUser);
            Assert.IsTrue(both.IsUpvotedByUser);
            Assert.AreEqual("Created and upvoted by you", both.Relationship);
            Assert.AreEqual("Ticket & 13", both.Title);
            Assert.AreEqual("closed fixed", both.State);
            Assert.AreEqual(20, both.Votes);
            Assert.AreEqual("https://developercommunity.visualstudio.com/t/test/13", both.Url);
            Assert.AreEqual(new DateTime(2026, 1, 14, 0, 0, 0, DateTimeKind.Utc), both.UpdatedUtc);
        }

        [TestMethod]
        public async Task Load_KeepsOnlyTwentyMostRecentlyUpdatedEligibleTicketsInMemoryAndOnDiskAsync()
        {
            var actions = Enumerable.Range(1, 30).Select(id => Action("posted", id)).ToArray();
            using var handler = new Handler((path, body) => path == "metadata" ? Json(new { Id = 7 })
                : path == "getActivity" ? Json(new { totalCount = actions.Length, activities = actions })
                : Json(new { results = new[] { Ticket(RequestedId(body), RequestedId(body) <= 28 ? 7 : 8,
                    RequestedId(body) == 29, updatedUtc: RequestedId(body) == 1 ? DateTime.UtcNow : null) } }));
            using var client = new HttpClient(handler);
            var diskCache = new FeedbackCache(_cacheFolder);
            var service = Service(client, diskCache: diskCache);
            var expected = new long[] { 1 }.Concat(Enumerable.Range(11, 19).Reverse().Select(id => (long)id)).ToArray();

            FeedbackSnapshot result = await service.GetTicketsAsync(false, CancellationToken.None);

            Assert.HasCount(20, result.Tickets);
            CollectionAssert.AreEqual(expected, result.Tickets.Select(ticket => ticket.Id).ToArray());
            Assert.AreSame(result, await service.GetTicketsAsync(false, CancellationToken.None));
            var requests = handler.RequestCount;
            FeedbackSnapshot restored = await Service(client, diskCache: diskCache).GetCachedTicketsAsync(CancellationToken.None);
            CollectionAssert.AreEqual(expected, restored.Tickets.Select(ticket => ticket.Id).ToArray());
            Assert.AreEqual(requests + 1, handler.RequestCount, "Restoring the cache should only verify account identity.");
        }

        [TestMethod]
        public async Task Cache_RevalidatesIdentityExpiresAndSupportsForcedRefreshAsync()
        {
            var principal = 7L;
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var metadataRequests = 0;
            var activityRequests = 0;
            using var handler = new Handler((path, _) =>
            {
                if (path == "metadata")
                {
                    metadataRequests++;
                    return Json(new { Id = principal });
                }
                if (path == "getActivity")
                {
                    activityRequests++;
                    return Json(new { totalCount = 1, activities = new[] { Action("posted", principal) } });
                }
                return Json(new { results = new[] { Ticket(principal, principal, false) } });
            });
            using var client = new HttpClient(handler);
            var service = Service(client, () => now, () => handler.ExpectedToken);

            var first = await service.GetTicketsAsync(false, CancellationToken.None);
            Assert.AreSame(first, await service.GetTicketsAsync(false, CancellationToken.None));
            Assert.AreEqual(1, metadataRequests);
            Assert.AreEqual(1, activityRequests);
            Assert.AreEqual(3, handler.RequestCount);

            now = now.AddDays(6).AddHours(23).AddMinutes(59);
            Assert.AreSame(first, await service.GetTicketsAsync(false, CancellationToken.None));
            Assert.AreEqual(1, metadataRequests);
            Assert.AreEqual(1, activityRequests);

            var forced = await service.GetTicketsAsync(true, CancellationToken.None);
            Assert.AreNotSame(first, forced);
            Assert.AreEqual(now, forced.UpdatedUtc);
            Assert.AreEqual(2, activityRequests);
            now = now.AddDays(7).AddSeconds(-1);
            Assert.AreSame(forced, await service.GetTicketsAsync(false, CancellationToken.None));
            Assert.AreEqual(2, activityRequests);

            now = now.AddSeconds(1);
            var expired = await service.GetTicketsAsync(false, CancellationToken.None);
            Assert.AreNotSame(forced, expired);
            Assert.AreEqual(now, expired.UpdatedUtc);
            Assert.AreEqual(3, activityRequests);

            principal = 8;
            handler.ExpectedToken = "other-account-token";
            var switched = await service.GetTicketsAsync(false, CancellationToken.None);
            Assert.AreEqual(8L, switched.PrincipalId);
            Assert.AreEqual(8L, switched.Tickets.Single().Id);
            Assert.AreEqual(4, activityRequests);
            Assert.AreEqual(4, metadataRequests);

            handler.ExpectedToken = "renewed-account-token";
            Assert.AreSame(switched, await service.GetTicketsAsync(false, CancellationToken.None));
            Assert.AreEqual(5, metadataRequests);
            Assert.AreEqual(4, activityRequests, "Token renewal for the same account must not reload tickets.");
            Assert.AreSame(switched, await service.GetTicketsAsync(false, CancellationToken.None));
            Assert.AreEqual(5, metadataRequests);
        }

        [TestMethod]
        [DataRow(401)]
        [DataRow(403)]
        public async Task AuthorizationFailure_DoesNotReturnCachedAccountDataAsync(int status)
        {
            var fail = false;
            using var handler = new Handler((path, _) => path == "metadata"
                ? (fail ? new HttpResponseMessage((HttpStatusCode)status) : Json(new { Id = 7 }))
                : Json(new { totalCount = 0, activities = Array.Empty<object>() }));
            using var client = new HttpClient(handler);
            var service = Service(client, getToken: () => handler.ExpectedToken);
            await service.GetTicketsAsync(false, CancellationToken.None);
            fail = true;
            handler.ExpectedToken = "other-account-token";

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetTicketsAsync(false, CancellationToken.None));
        }

        [TestMethod]
        public async Task ForcedRefreshFailure_DoesNotLeaveAReusableCachedSuccessAsync()
        {
            var fail = false;
            using var handler = new Handler((path, _) => fail ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : path == "metadata" ? Json(new { Id = 7 })
                : Json(new { totalCount = 0, activities = Array.Empty<object>() }));
            using var client = new HttpClient(handler);
            var service = Service(client);
            await service.GetTicketsAsync(false, CancellationToken.None);
            fail = true;

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetTicketsAsync(true, CancellationToken.None));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetTicketsAsync(false, CancellationToken.None));
            Assert.AreEqual(4, handler.RequestCount);
        }

        [TestMethod]
        public async Task DiskCache_NewServiceReusesAllTicketFieldsWithoutDownloadingActivityAsync()
        {
            using var handler = TicketHandler(Ticket(10, 7, true));
            using var client = new HttpClient(handler);
            var diskCache = new FeedbackCache(_cacheFolder);
            var first = await Service(client, diskCache: diskCache).GetTicketsAsync(false, CancellationToken.None);

            var restored = await Service(client, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);

            Assert.AreEqual(4, handler.RequestCount, "A new session must verify identity but not fetch activity or hydrate tickets.");
            Assert.AreEqual(first.PrincipalId, restored.PrincipalId);
            Assert.AreEqual(first.UpdatedUtc, restored.UpdatedUtc, "Reading the cache must not extend its freshness.");
            var expected = first.Tickets.Single();
            var actual = restored.Tickets.Single();
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Title, actual.Title);
            Assert.AreEqual(expected.Url, actual.Url);
            Assert.AreEqual(expected.State, actual.State);
            Assert.AreEqual(expected.Type, actual.Type);
            Assert.AreEqual(expected.Votes, actual.Votes);
            Assert.AreEqual(expected.IsCreatedByUser, actual.IsCreatedByUser);
            Assert.AreEqual(expected.IsUpvotedByUser, actual.IsUpvotedByUser);
            Assert.AreEqual(expected.UpdatedUtc, actual.UpdatedUtc);
            var stored = File.ReadAllText(Path.Combine(_cacheFolder, "7.json"));
            Assert.DoesNotContain(expected.Title, stored);
            Assert.DoesNotContain("test-token", stored);
            Assert.DoesNotContain("PrincipalId", stored);
        }

        [TestMethod]
        public async Task Load_IncludesBothCreatedProblemReportsAndUpvotedSuggestionsInTheSameSnapshotAsync()
        {
            using var handler = new Handler((path, body) => path == "metadata" ? Json(new { Id = 7 })
                : path == "getActivity" ? Json(new { totalCount = 2, activities = new[] { Action("posted", 10), Action("voted", 11) } })
                : Json(new
                {
                    results = new[] { RequestedId(body) == 10
                        ? Ticket(10, 7, false, type: 0, state: "consideration")
                        : Ticket(11, 8, true, type: 3, state: "consideration") },
                }));
            using var client = new HttpClient(handler);
            var downloaded = await Service(client, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);
            var restored = await Service(client, diskCache: new FeedbackCache(_cacheFolder)).GetCachedTicketsAsync(CancellationToken.None);

            Assert.IsNotNull(restored);
            foreach (var snapshot in new[] { downloaded, restored })
            {
                Assert.HasCount(2, snapshot.Tickets);
                var problem = snapshot.Tickets.Single(ticket => ticket.Id == 10);
                Assert.AreEqual(FeedbackTicketType.Problem, problem.Type);
                Assert.IsTrue(problem.IsCreatedByUser);
                Assert.AreEqual("Under Consideration", problem.StateText);
                var suggestion = snapshot.Tickets.Single(ticket => ticket.Id == 11);
                Assert.AreEqual(FeedbackTicketType.Suggestion, suggestion.Type);
                Assert.IsTrue(suggestion.IsUpvotedByUser);
                Assert.AreEqual("Under Review", suggestion.StateText);
            }
            Assert.AreEqual(5, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(FeedbackTicketType.Problem, "consideration", "Under Consideration", "2E6FC2")]
        [DataRow(FeedbackTicketType.Suggestion, "consideration", "Under Review", "2E6FC2")]
        [DataRow(FeedbackTicketType.Problem, "pending", "Fixed - Pending Release", "2E7D1C")]
        [DataRow(FeedbackTicketType.Suggestion, "pending", "Completed - Preview", "2E7D1C")]
        [DataRow(FeedbackTicketType.Problem, "closed_fixed", "Closed - Fixed", "2E7D1C")]
        [DataRow(FeedbackTicketType.Suggestion, "fixed", "Completed - Release", "2E7D1C")]
        public async Task TicketType_PreservesWebsiteLabelsAndColorsAcrossHydrationAndDiskCacheAsync(
            FeedbackTicketType type, string state, string expectedText, string expectedColor)
        {
            using var handler = TicketHandler(Ticket(10, 7, false, type: (int)type, state: state));
            using var client = new HttpClient(handler);
            var downloaded = await Service(client, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);
            var restored = await Service(client, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);

            foreach (var snapshot in new[] { downloaded, restored })
            {
                var ticket = snapshot.Tickets.Single();
                Assert.AreEqual(type, ticket.Type);
                Assert.AreEqual(state.Replace('_', ' '), ticket.State);
                Assert.AreEqual(expectedText, ticket.StateText);
                Assert.AreEqual(expectedColor, ticket.StateColor);
            }
            Assert.AreEqual(4, handler.RequestCount, "A typed cache must retain labels without rehydrating tickets.");
        }

        [TestMethod]
        [DataRow(-1)]
        [DataRow(2)]
        public async Task DiskCache_UnknownTicketTypeIsRefetchedBeforeDisplayAsync(int type)
        {
            var cache = new FeedbackCache(_cacheFolder);
            var legacy = JsonSerializer.Deserialize<FeedbackTicket>("{\"Id\":10,\"State\":\"consideration\"}")!;
            Assert.AreEqual(FeedbackTicketType.Unknown, legacy.Type, "Old snapshots must not silently default suggestions to problems.");
            legacy.Type = (FeedbackTicketType)type;
            await cache.WriteAsync(new FeedbackSnapshot(7, [legacy], DateTime.UtcNow), CancellationToken.None);
            using var handler = TicketHandler(Ticket(10, 7, false, type: 3, state: "consideration"));
            using var client = new HttpClient(handler);
            var service = Service(client, diskCache: cache);

            Assert.IsNull(await service.GetCachedTicketsAsync(CancellationToken.None));
            var refreshed = await service.GetTicketsAsync(false, CancellationToken.None);

            Assert.AreEqual(FeedbackTicketType.Suggestion, refreshed.Tickets.Single().Type);
            Assert.AreEqual("Under Review", refreshed.Tickets.Single().StateText);
            Assert.AreEqual(4, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(42)]
        public async Task Hydration_UnsupportedSearchTicketTypesAreRejectedAsync(int type)
        {
            using var handler = TicketHandler(Ticket(10, 7, false, type: type));
            using var client = new HttpClient(handler);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Service(client).GetTicketsAsync(false, CancellationToken.None));
        }

        [TestMethod]
        public async Task DiskCache_EmptySnapshotSurvivesANewServiceInstanceAsync()
        {
            using var handler = new Handler((path, _) => path == "metadata" ? Json(new { Id = 7 })
                : Json(new { totalCount = 0, activities = Array.Empty<object>() }));
            using var client = new HttpClient(handler);
            await Service(client, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);

            var restored = await Service(client, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);

            Assert.IsEmpty(restored.Tickets);
            Assert.AreEqual(3, handler.RequestCount);
        }

        [TestMethod]
        public async Task DiskCache_ExpiresAfterSevenDaysAcrossServiceInstancesAsync()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            using var handler = TicketHandler(Ticket(10, 7, false));
            using var client = new HttpClient(handler);
            await Service(client, () => now, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);
            now = now.AddDays(7);

            var refreshed = await Service(client, () => now, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);

            Assert.AreEqual(now, refreshed.UpdatedUtc);
            Assert.AreEqual(6, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DiskCache_DoesNotReuseAnotherAccountsSnapshotAsync(bool copyWrongAccountFile)
        {
            var principal = 7L;
            using var handler = new Handler((path, _) => path == "metadata" ? Json(new { Id = principal })
                : path == "getActivity" ? Json(new { totalCount = 1, activities = new[] { Action("posted", principal) } })
                : Json(new { results = new[] { Ticket(principal, principal, false) } }));
            using var client = new HttpClient(handler);
            await Service(client, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);
            if (copyWrongAccountFile)
                File.Copy(Path.Combine(_cacheFolder, "7.json"), Path.Combine(_cacheFolder, "8.json"));
            principal = 8;

            var current = await Service(client, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);

            Assert.AreEqual(8L, current.PrincipalId);
            Assert.AreEqual(8L, current.Tickets.Single().Id);
            Assert.AreEqual(6, handler.RequestCount);
            Assert.HasCount(2, Directory.GetFiles(_cacheFolder, "*.json"));
        }

        [TestMethod]
        public async Task DiskCache_CorruptEncryptedDataIsReplacedByACompleteOnlineSnapshotAsync()
        {
            await AtomicJsonCache.WriteAsync(Path.Combine(_cacheFolder, "7.json"), new byte[] { 1, 2, 3 });
            using var handler = TicketHandler(Ticket(10, 7, false));
            using var client = new HttpClient(handler);
            var cache = new FeedbackCache(_cacheFolder);

            var result = await Service(client, diskCache: cache).GetTicketsAsync(false, CancellationToken.None);

            Assert.AreEqual(10L, result.Tickets.Single().Id);
            Assert.AreEqual(3, handler.RequestCount);
            Assert.AreEqual(10L, (await cache.ReadAsync(7, CancellationToken.None))!.Tickets.Single().Id);
        }

        [TestMethod]
        public async Task DiskCache_ForcedRefreshPreservesLastSnapshotWithoutSavingFailedHydrationAsync()
        {
            var fail = false;
            using var handler = new Handler((path, _) => path == "metadata" ? Json(new { Id = 7 })
                : path == "getActivity" ? Json(new { totalCount = 1, activities = new[] { Action("posted", 10) } })
                : fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json(new { results = new[] { Ticket(10, 7, false) } }));
            using var client = new HttpClient(handler);
            await Service(client, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);
            fail = true;

            await Assert.ThrowsAsync<HttpRequestException>(() => Service(client, diskCache: new FeedbackCache(_cacheFolder))
                .GetTicketsAsync(true, CancellationToken.None));

            var retained = await new FeedbackCache(_cacheFolder).ReadAsync(7, CancellationToken.None);
            Assert.IsNotNull(retained);
            Assert.AreEqual(10L, retained.Tickets.Single().Id);
            fail = false;
            var recovered = await Service(client, diskCache: new FeedbackCache(_cacheFolder)).GetTicketsAsync(false, CancellationToken.None);
            Assert.HasCount(1, recovered.Tickets);
            Assert.AreEqual(7, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(604799, true)]
        [DataRow(604800, false)]
        [DataRow(-1, false)]
        public async Task CacheOnly_EnforcesSevenDayBoundaryWithoutDownloadingActivityAsync(int ageSeconds, bool usable)
        {
            var now = DateTime.UtcNow;
            var cache = new FeedbackCache(_cacheFolder);
            await cache.WriteAsync(new FeedbackSnapshot(7,
                [new FeedbackTicket { Id = 10, Title = "Cached ticket", Type = FeedbackTicketType.Problem }], now.AddSeconds(-ageSeconds)), CancellationToken.None);
            using var handler = new Handler((path, _) =>
            {
                Assert.AreEqual("metadata", path, "A cache-only read must never download activity or hydrate tickets.");
                return Json(new { Id = 7 });
            });
            using var client = new HttpClient(handler);

            var restored = await Service(client, () => now, diskCache: cache).GetCachedTicketsAsync(CancellationToken.None);

            Assert.AreEqual(usable, restored != null);
            if (usable)
            {
                Assert.AreEqual(10L, restored!.Tickets.Single().Id);
                Assert.AreEqual(now.AddSeconds(-ageSeconds), restored.UpdatedUtc);
            }
            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        public async Task StartupRefresh_ReplacesRetainedCacheWithLatestTicketsAndTimestampAsync()
        {
            var now = DateTime.UtcNow;
            var cache = new FeedbackCache(_cacheFolder);
            await cache.WriteAsync(new FeedbackSnapshot(7,
                [new FeedbackTicket { Id = 1, Title = "Old ticket", Type = FeedbackTicketType.Problem }], now.AddDays(-3)), CancellationToken.None);
            using var handler = TicketHandler(Ticket(10, 7, false));
            using var client = new HttpClient(handler);
            var service = Service(client, () => now, diskCache: cache);

            var cached = await service.GetCachedTicketsAsync(CancellationToken.None);
            Assert.IsNotNull(cached);
            Assert.AreEqual(1L, cached.Tickets.Single().Id);
            Assert.AreEqual(1, handler.RequestCount);

            var latest = await service.GetTicketsAsync(true, CancellationToken.None);
            var persisted = await cache.ReadAsync(7, CancellationToken.None);

            Assert.AreEqual(10L, latest.Tickets.Single().Id);
            Assert.AreEqual(now, latest.UpdatedUtc);
            Assert.IsNotNull(persisted);
            Assert.AreEqual(10L, persisted.Tickets.Single().Id);
            Assert.AreEqual(now, persisted.UpdatedUtc);
            Assert.AreEqual(4, handler.RequestCount);
        }

        [TestMethod]
        public async Task DiskCache_WriteFailureDoesNotDiscardValidDownloadedTicketsAsync()
        {
            Directory.CreateDirectory(_cacheFolder);
            var blockedFolder = Path.Combine(_cacheFolder, "not-a-folder");
            File.WriteAllText(blockedFolder, "Test path obstruction");
            using var handler = TicketHandler(Ticket(10, 7, false));
            using var client = new HttpClient(handler);

            var result = await Service(client, diskCache: new FeedbackCache(blockedFolder)).GetTicketsAsync(false, CancellationToken.None);

            Assert.AreEqual(10L, result.Tickets.Single().Id);
            Assert.AreEqual(3, handler.RequestCount);
            Assert.AreEqual("Test path obstruction", File.ReadAllText(blockedFolder));
        }

        [TestMethod]
        public async Task ConcurrentCacheReaders_ReuseTheCompleteSnapshotWithoutMoreRequestsAsync()
        {
            using var handler = TicketHandler(Ticket(10, 7, false));
            using var client = new HttpClient(handler);
            var service = Service(client);

            var snapshots = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => service.GetTicketsAsync(false, CancellationToken.None)));

            Assert.HasCount(1, snapshots[0].Tickets);
            Assert.IsTrue(snapshots.All(snapshot => ReferenceEquals(snapshots[0], snapshot)));
            Assert.AreEqual(3, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(429)]
        [DataRow(503)]
        public async Task FailedHydration_IsNotPublishedOrCachedAsAnEmptySuccessAsync(int status)
        {
            var fail = true;
            var activityRequests = 0;
            using var handler = new Handler((path, _) =>
            {
                if (path == "metadata")
                    return Json(new { Id = 7 });
                if (path == "getActivity")
                {
                    activityRequests++;
                    return Json(new { totalCount = 1, activities = new[] { Action("posted", 10) } });
                }
                return fail ? new HttpResponseMessage((HttpStatusCode)status) : Json(new { results = new[] { Ticket(10, 7, false) } });
            });
            using var client = new HttpClient(handler);
            var service = Service(client);

            await Assert.ThrowsAsync<HttpRequestException>(() => service.GetTicketsAsync(false, CancellationToken.None));
            fail = false;
            Assert.HasCount(1, (await service.GetTicketsAsync(false, CancellationToken.None)).Tickets);
            Assert.AreEqual(2, activityRequests);
        }

        [TestMethod]
        public async Task EmptyAccount_ReturnsAnEmptySnapshotWithoutHydrationAsync()
        {
            using var handler = new Handler((path, _) => path == "metadata" ? Json(new { Id = 7 })
                : Json(new { totalCount = 0, activities = Array.Empty<object>() }));
            using var client = new HttpClient(handler);

            var result = await Service(client).GetTicketsAsync(false, CancellationToken.None);

            Assert.IsEmpty(result.Tickets);
            Assert.AreEqual(2, handler.RequestCount);
        }

        [TestMethod]
        public async Task IncompleteActivityPage_FailsExplicitlyAsync()
        {
            using var handler = new Handler((path, _) => path == "metadata" ? Json(new { Id = 7 })
                : Json(new { totalCount = 1, activities = Array.Empty<object>() }));
            using var client = new HttpClient(handler);

            await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => Service(client).GetTicketsAsync(false, CancellationToken.None));
        }

        [TestMethod]
        [DataRow("http://developercommunity.visualstudio.com/t/test/10")]
        [DataRow("https://example.com/t/test/10")]
        [DataRow("file:///C:/test")]
        [DataRow("https://developercommunity.visualstudio.com:444/t/test/10")]
        public async Task UnsafeBrowserLink_IsRejectedAsync(string url)
        {
            using var handler = TicketHandler(Ticket(10, 7, false, url));
            using var client = new HttpClient(handler);

            await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => Service(client).GetTicketsAsync(false, CancellationToken.None));
        }

        [TestMethod]
        public async Task MismatchedTicketId_IsRejectedAsync()
        {
            using var handler = TicketHandler(Ticket(11, 7, false));
            using var client = new HttpClient(handler);

            await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => Service(client).GetTicketsAsync(false, CancellationToken.None));
        }

        [TestMethod]
        public async Task Cancellation_StopsBeforeCredentialAcquisitionAsync()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var service = new FeedbackService(getTokenAsync: _ => throw new AssertFailedException("Must not acquire a token."));

            await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetTicketsAsync(false, cancellation.Token));
        }

        [TestMethod]
        public async Task ExpiredCredential_IsRejectedBeforeSendingRequestsAsync()
        {
            using var handler = new Handler((_, _) => throw new AssertFailedException("Must not send an expired credential."));
            using var client = new HttpClient(handler);
            var service = new FeedbackService(client, _ => Task.FromResult(new AccessToken("test-token", DateTimeOffset.MinValue)));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetTicketsAsync(false, CancellationToken.None));
            Assert.AreEqual(0, handler.RequestCount);
        }

        [TestMethod]
        public async Task CancellationDuringHydration_DoesNotCacheTheResultAsync()
        {
            using var cancellation = new CancellationTokenSource();
            var cancel = true;
            using var handler = new Handler((path, _) =>
            {
                if (path == "metadata")
                    return Json(new { Id = 7 });
                if (path == "getActivity")
                    return Json(new { totalCount = 1, activities = new[] { Action("posted", 10) } });
                if (cancel)
                    cancellation.Cancel();
                return Json(new { results = new[] { Ticket(10, 7, false) } });
            });
            using var client = new HttpClient(handler);
            var service = Service(client, diskCache: new FeedbackCache(_cacheFolder));

            await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetTicketsAsync(false, cancellation.Token));
            cancel = false;
            Assert.HasCount(1, (await service.GetTicketsAsync(false, CancellationToken.None)).Tickets);
            Assert.AreEqual(6, handler.RequestCount);
        }

        [TestMethod]
        public async Task Hydration_IsBoundedToFourConcurrentRequestsAsync()
        {
            var active = 0;
            var peak = 0;
            using var handler = new Handler((path, _) => path == "metadata" ? Json(new { Id = 7 })
                : path == "getActivity" ? Json(new { totalCount = 12, activities = Enumerable.Range(1, 12).Select(id => Action("posted", id)) })
                : throw new AssertFailedException("The async hydration handler must be used."));
            handler.HydrateAsync = async (body, token) =>
            {
                var current = Interlocked.Increment(ref active);
                int observed;
                do
                {
                    observed = Volatile.Read(ref peak);
                }
                while (current > observed && Interlocked.CompareExchange(ref peak, current, observed) != observed);
                try
                {
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    return Json(new { results = new[] { Ticket(RequestedId(body), 7, false) } });
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            };
            using var client = new HttpClient(handler);

            var result = await Service(client).GetTicketsAsync(false, CancellationToken.None);

            Assert.HasCount(12, result.Tickets);
            Assert.IsTrue(peak > 0 && peak <= 4, $"Observed {peak} simultaneous requests.");
            Assert.AreEqual(0, active);
        }

        private static FeedbackService Service(HttpClient client, Func<DateTime>? utcNow = null, Func<string>? getToken = null,
            FeedbackCache? diskCache = null) => new FeedbackService(client,
            _ => Task.FromResult(new AccessToken(getToken?.Invoke() ?? "test-token", new DateTimeOffset(utcNow?.Invoke() ?? DateTime.UtcNow).AddHours(1))), utcNow, diskCache);

        private static object Action(string activity, long rootPostId) => new { activity, rootPostId };

        private static object Ticket(long id, long author, bool voted, string? url = null, int type = 0,
            string state = "closed_fixed", DateTime? updatedUtc = null) => new
        {
            answerHubId = id,
            createdByPrincipalId = author.ToString(CultureInfo.InvariantCulture),
            hasVoted = voted,
            title = $"<p>Ticket &amp; {id}</p>",
            communityUrl = url ?? $"https://developercommunity.visualstudio.com/t/test/{id}",
            state,
            type,
            totalVotes = 20,
            lastModifiedUtc = updatedUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(id),
        };

        private static long RequestedId(JsonElement? body) => long.Parse(body!.Value.GetProperty("searchFor").GetProperty("title").GetString()!, CultureInfo.InvariantCulture);

        private static HttpResponseMessage Json(object value) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

        private static Handler TicketHandler(object ticket) => new Handler((path, _) => path == "metadata" ? Json(new { Id = 7 })
            : path == "getActivity" ? Json(new { totalCount = 1, activities = new[] { Action("posted", 10) } })
            : Json(new { results = new[] { ticket } }));

        private sealed class Handler(Func<string, JsonElement?, HttpResponseMessage> respond) : HttpMessageHandler
        {
            private int _requests;
            internal int RequestCount => Volatile.Read(ref _requests);
            internal string ExpectedToken { get; set; } = "test-token";
            internal Func<JsonElement?, CancellationToken, Task<HttpResponseMessage>>? HydrateAsync { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requests);
                Assert.AreEqual("https://sendvsfeedback2.azurewebsites.net", request.RequestUri!.GetLeftPart(UriPartial.Authority));
                Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
                Assert.AreEqual(ExpectedToken, request.Headers.Authorization?.Parameter);
                var path = request.RequestUri.Segments.Last();
                Assert.AreEqual(path == "metadata" ? HttpMethod.Get : HttpMethod.Post, request.Method);
                using var body = request.Content == null ? null : JsonDocument.Parse(await request.Content.ReadAsStringAsync());
                return path == "item" && HydrateAsync != null
                    ? await HydrateAsync(body?.RootElement, cancellationToken)
                    : respond(path, body?.RootElement);
            }
        }
    }
}