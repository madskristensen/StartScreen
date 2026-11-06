using System.Globalization;
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
            var service = Service(client, () => now);

            var first = await service.GetTicketsAsync(false, CancellationToken.None);
            Assert.AreSame(first, await service.GetTicketsAsync(false, CancellationToken.None));
            Assert.AreEqual(2, metadataRequests);
            Assert.AreEqual(1, activityRequests);

            now = now.AddMinutes(59);
            Assert.AreSame(first, await service.GetTicketsAsync(false, CancellationToken.None));
            Assert.AreEqual(1, activityRequests);

            var forced = await service.GetTicketsAsync(true, CancellationToken.None);
            Assert.AreNotSame(first, forced);
            Assert.AreEqual(now, forced.UpdatedUtc);
            Assert.AreEqual(2, activityRequests);
            now = now.AddMinutes(59).AddSeconds(59);
            Assert.AreSame(forced, await service.GetTicketsAsync(false, CancellationToken.None));
            Assert.AreEqual(2, activityRequests);

            now = now.AddSeconds(1);
            var expired = await service.GetTicketsAsync(false, CancellationToken.None);
            Assert.AreNotSame(forced, expired);
            Assert.AreEqual(now, expired.UpdatedUtc);
            Assert.AreEqual(3, activityRequests);

            principal = 8;
            var switched = await service.GetTicketsAsync(false, CancellationToken.None);
            Assert.AreEqual(8L, switched.PrincipalId);
            Assert.AreEqual(8L, switched.Tickets.Single().Id);
            Assert.AreEqual(4, activityRequests);
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
            var service = Service(client);
            await service.GetTicketsAsync(false, CancellationToken.None);
            fail = true;

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetTicketsAsync(false, CancellationToken.None));
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
            var service = Service(client);

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

        private static FeedbackService Service(HttpClient client, Func<DateTime>? utcNow = null) => new FeedbackService(client,
            _ => Task.FromResult(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1))), utcNow);

        private static object Action(string activity, long rootPostId) => new { activity, rootPostId };

        private static object Ticket(long id, long author, bool voted, string? url = null) => new
        {
            answerHubId = id,
            createdByPrincipalId = author.ToString(CultureInfo.InvariantCulture),
            hasVoted = voted,
            title = $"<p>Ticket &amp; {id}</p>",
            communityUrl = url ?? $"https://developercommunity.visualstudio.com/t/test/{id}",
            state = "closed_fixed",
            totalVotes = 20,
            lastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(id),
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
            internal Func<JsonElement?, CancellationToken, Task<HttpResponseMessage>>? HydrateAsync { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requests);
                Assert.AreEqual("https://sendvsfeedback2.azurewebsites.net", request.RequestUri!.GetLeftPart(UriPartial.Authority));
                Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
                Assert.AreEqual("test-token", request.Headers.Authorization?.Parameter);
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