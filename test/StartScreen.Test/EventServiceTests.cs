using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class EventServiceTests
    {
        private static readonly DateTimeOffset _now = new DateTimeOffset(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Local)).ToUniversalTime();

        [TestMethod]
        [DataRow(167.999, 1)]
        [DataRow(168d, 2)]
        [DataRow(169d, 2)]
        [DataRow(-1d, 2)]
        public async Task Schedule_UsesTheRawHttpsFeedAndPersistentSevenDayCacheAsync(double hours, int requests)
        {
            using var directory = new TemporaryDirectory();
            var clock = _now;
            using var handler = new TestHttpMessageHandler((request, _) =>
            {
                Assert.AreEqual(EventService.FeedUrl, request.RequestUri!.AbsoluteUri);
                Assert.AreEqual("https", request.RequestUri.Scheme);
                return Json(Event("Scheduled", new DateTime(2026, 11, 30)));
            });
            using var client = new HttpClient(handler);
            var first = new EventService(client, directory.Path, () => clock);
            Assert.IsFalse((await first.GetAsync(false, CancellationToken.None)).IsCached);
            clock = _now.AddHours(hours);
            var reopened = new EventService(client, directory.Path, () => clock);
            var result = await reopened.GetAsync(false, CancellationToken.None);
            Assert.AreEqual(requests, handler.RequestCount);
            Assert.AreEqual(requests == 1, result.IsCached);
            Assert.AreEqual("Scheduled", result.Events.Single().Name);
            Assert.IsNull(result.Message, "Successful fresh and cached schedules must not show routine update messages.");
        }

        [TestMethod]
        [DataRow(false, true, true)]
        [DataRow(false, true, false)]
        [DataRow(false, false, true)]
        [DataRow(true, true, true)]
        public async Task ConditionalRefresh_ReusesValidatedCatalogAndRestartsSevenDayCacheAsync(bool force, bool useETag, bool useLastModified)
        {
            using var directory = new TemporaryDirectory();
            var clock = _now;
            const string originalETag = "W/\"original\"";
            const string updatedETag = "W/\"validated\"";
            var originalModified = _now.AddDays(-2);
            var updatedModified = _now.AddDays(-1);
            using var handler = new TestHttpMessageHandler((request, count) =>
            {
                Assert.AreEqual(count > 1 && useETag ? 1 : 0, request.Headers.IfNoneMatch.Count);
                if (count > 1 && useETag)
                    Assert.AreEqual(count == 2 ? originalETag : updatedETag, request.Headers.IfNoneMatch.Single().ToString());
                Assert.AreEqual(count > 1 && useLastModified ? count == 2 ? originalModified : updatedModified
                    : (DateTimeOffset?)null, request.Headers.IfModifiedSince);
                var response = count == 1
                    ? Json(Event("Today", _now.LocalDateTime.Date), Event("Future", new DateTime(2026, 11, 30)))
                    : new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new StringContent("not JSON; a 304 body must not be read") };
                if (count <= 2)
                {
                    if (useETag)
                        response.Headers.ETag = EntityTagHeaderValue.Parse(count == 1 ? originalETag : updatedETag);
                    if (useLastModified)
                        response.Content.Headers.LastModified = count == 1 ? originalModified : updatedModified;
                }
                return response;
            });
            using var client = new HttpClient(handler);
            Assert.HasCount(2, (await new EventService(client, directory.Path, () => clock).GetAsync(false, CancellationToken.None)).Events);
            clock = _now.AddDays(force ? 1 : 7);
            var validatedAt = clock;
            var result = await new EventService(client, directory.Path, () => clock).GetAsync(force, CancellationToken.None);
            Assert.AreEqual("Future", result.Events.Single().Name, "A 304 must still remove events that have ended.");
            Assert.IsFalse(result.IsCached, "Server validation counts as a successful refresh, like the blog feeds.");
            Assert.IsFalse(result.IsStale);
            Assert.IsFalse(result.IsUnavailable);
            Assert.IsNull(result.Message);
            Assert.AreEqual(validatedAt, result.LastSuccessfulRefreshUtc);
            var snapshot = await AtomicJsonCache.ReadAsync<EventService.Snapshot>(directory.GetPath("events.json"),
                options: new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.AreEqual(validatedAt, snapshot!.FetchedAtUtc);
            Assert.HasCount(2, snapshot.Catalog.Events);
            Assert.AreEqual(useETag ? updatedETag : null, snapshot.ETag);
            Assert.AreEqual(useLastModified ? updatedModified : (DateTimeOffset?)null, snapshot.LastModified);
            clock = validatedAt.Add(EventService.CacheLifetime).AddMilliseconds(-1);
            Assert.IsTrue((await new EventService(client, directory.Path, () => clock).GetAsync(false, CancellationToken.None)).IsCached);
            Assert.AreEqual(2, handler.RequestCount);
            clock = clock.AddMilliseconds(1);
            await new EventService(client, directory.Path, () => clock).GetAsync(false, CancellationToken.None);
            Assert.AreEqual(3, handler.RequestCount);
            snapshot = await AtomicJsonCache.ReadAsync<EventService.Snapshot>(directory.GetPath("events.json"),
                options: new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.AreEqual(clock, snapshot!.FetchedAtUtc);
            Assert.AreEqual(useETag ? updatedETag : null, snapshot.ETag, "Missing 304 headers must retain the saved validators.");
            Assert.AreEqual(useLastModified ? updatedModified : (DateTimeOffset?)null, snapshot.LastModified);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ChangedResponse_ReplacesScheduleAndDoesNotReuseObsoleteValidatorsAsync(bool includeNewValidators)
        {
            using var directory = new TemporaryDirectory();
            var clock = _now;
            using var handler = new TestHttpMessageHandler((request, count) =>
            {
                Assert.AreEqual(count == 2 ? "\"original\"" : count == 3 && includeNewValidators ? "\"changed\"" : null,
                    request.Headers.IfNoneMatch.SingleOrDefault()?.ToString());
                Assert.AreEqual(count == 2 ? _now.AddDays(-2) : count == 3 && includeNewValidators ? _now.AddDays(-1)
                    : (DateTimeOffset?)null, request.Headers.IfModifiedSince);
                var response = Json(Event(count == 1 ? "Original" : "Changed", new DateTime(2026, 11, 30)));
                if (count == 1 || includeNewValidators)
                {
                    response.Headers.ETag = new EntityTagHeaderValue(count == 1 ? "\"original\"" : "\"changed\"");
                    response.Content.Headers.LastModified = _now.AddDays(count == 1 ? -2 : -1);
                }
                return response;
            });
            using var client = new HttpClient(handler);
            var service = new EventService(client, directory.Path, () => clock);
            await service.GetAsync(false, CancellationToken.None);
            clock = _now.AddDays(7);
            var changed = await service.GetAsync(false, CancellationToken.None);
            Assert.AreEqual("Changed", changed.Events.Single().Name);
            Assert.IsFalse(changed.IsStale);
            Assert.AreEqual(clock, changed.LastSuccessfulRefreshUtc);
            var snapshot = await AtomicJsonCache.ReadAsync<EventService.Snapshot>(directory.GetPath("events.json"),
                options: new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.AreEqual(includeNewValidators ? "\"changed\"" : null, snapshot!.ETag);
            Assert.AreEqual(includeNewValidators ? _now.AddDays(-1) : (DateTimeOffset?)null, snapshot.LastModified);
            clock = clock.AddDays(7);
            await new EventService(client, directory.Path, () => clock).GetAsync(false, CancellationToken.None);
            Assert.AreEqual(3, handler.RequestCount);
        }

        [TestMethod]
        public async Task LegacyCacheWithoutValidators_RemainsUsableAndUpgradesOnTheNextDownloadAsync()
        {
            using var directory = new TemporaryDirectory();
            var clock = _now;
            await AtomicJsonCache.WriteAsync(directory.GetPath("events.json"), new
            {
                FetchedAtUtc = clock,
                Catalog = new { Version = 1, Events = new[] { Event("Legacy", new DateTime(2026, 11, 30)) } },
            });
            using var handler = new TestHttpMessageHandler((request, _) =>
            {
                Assert.IsEmpty(request.Headers.IfNoneMatch);
                Assert.IsNull(request.Headers.IfModifiedSince);
                var response = Json(Event("Updated", new DateTime(2026, 11, 30)));
                response.Headers.ETag = new EntityTagHeaderValue("\"upgraded\"");
                return response;
            });
            using var client = new HttpClient(handler);
            var service = new EventService(client, directory.Path, () => clock);
            Assert.AreEqual("Legacy", (await service.GetAsync(false, CancellationToken.None)).Events.Single().Name);
            Assert.AreEqual(0, handler.RequestCount);
            clock = clock.AddDays(7);
            Assert.AreEqual("Updated", (await service.GetAsync(false, CancellationToken.None)).Events.Single().Name);
            var snapshot = await AtomicJsonCache.ReadAsync<EventService.Snapshot>(directory.GetPath("events.json"),
                options: new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.AreEqual("\"upgraded\"", snapshot!.ETag);
        }

        [TestMethod]
        [DataRow("missing")]
        [DataRow("invalid catalog")]
        [DataRow("no validators")]
        [DataRow("malformed ETag")]
        public async Task NotModified_WithoutAUsableValidatedCacheDoesNotClaimSuccessAsync(string cacheState)
        {
            using var directory = new TemporaryDirectory();
            var usable = cacheState == "no validators" || cacheState == "malformed ETag";
            if (cacheState != "missing")
            {
                var catalog = EventService.Parse(Serialize(Event("Retained", new DateTime(2026, 11, 30))));
                if (!usable)
                    catalog.Version = 0;
                await AtomicJsonCache.WriteAsync(directory.GetPath("events.json"), new EventService.Snapshot
                {
                    Catalog = catalog, FetchedAtUtc = _now,
                    ETag = cacheState == "malformed ETag" ? "not a valid entity tag" : !usable ? "\"unusable\"" : null,
                });
            }
            using var handler = new TestHttpMessageHandler((request, _) =>
            {
                Assert.IsEmpty(request.Headers.IfNoneMatch);
                Assert.IsNull(request.Headers.IfModifiedSince);
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });
            using var client = new HttpClient(handler);
            var result = await new EventService(client, directory.Path, () => _now.AddDays(7)).GetAsync(true, CancellationToken.None);
            Assert.IsTrue(result.IsStale);
            Assert.AreEqual(!usable, result.IsUnavailable);
            if (usable)
                Assert.AreEqual("Retained", result.Events.Single().Name);
            else
                Assert.IsEmpty(result.Events);
            if (cacheState == "missing")
                Assert.IsFalse(File.Exists(directory.GetPath("events.json")));
            else
            {
                var snapshot = await AtomicJsonCache.ReadAsync<EventService.Snapshot>(directory.GetPath("events.json"),
                    options: new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.AreEqual(_now, snapshot!.FetchedAtUtc);
            }
        }

        [TestMethod]
        public async Task CanceledRevalidation_DoesNotAdvanceTheCacheOrReplaceItsValidatorsAsync()
        {
            using var directory = new TemporaryDirectory();
            using var cancellation = new CancellationTokenSource();
            var clock = _now;
            using var handler = new TestHttpMessageHandler((_, count) =>
            {
                if (count == 1)
                {
                    var response = Json(Event("Retained", new DateTime(2026, 11, 30)));
                    response.Headers.ETag = new EntityTagHeaderValue("\"original\"");
                    return response;
                }
                cancellation.Cancel();
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });
            using var client = new HttpClient(handler);
            var service = new EventService(client, directory.Path, () => clock);
            await service.GetAsync(false, CancellationToken.None);
            clock = clock.AddDays(7);
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetAsync(false, cancellation.Token));
            var snapshot = await AtomicJsonCache.ReadAsync<EventService.Snapshot>(directory.GetPath("events.json"),
                options: new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.AreEqual(_now, snapshot!.FetchedAtUtc);
            Assert.AreEqual("\"original\"", snapshot.ETag);
        }

        [TestMethod]
        public async Task Schedule_SortsConfirmedDatesAndRetainsOngoingEventsButNotExpiredOrUndatedAsync()
        {
            using var directory = new TemporaryDirectory();
            var expired = Event("Expired", new DateTime(2026, 11, 7));
            expired.EndDate = new DateTime(2026, 11, 8);
            var ongoing = Event("Ongoing", new DateTime(2026, 11, 8));
            ongoing.EndDate = new DateTime(2026, 11, 10);
            using var handler = new TestHttpMessageHandler((_, _) => Json(
                Event("Later", new DateTime(2027, 8, 2)), expired,
                new ConferenceEvent { Name = "Not announced" },
                Event("Next", new DateTime(2026, 11, 10)), ongoing));
            using var client = new HttpClient(handler);
            var service = new EventService(client, directory.Path, () => _now);
            var result = await service.GetAsync(false, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "Ongoing", "Next", "Later" }, result.Events.Select(item => item.Name).ToArray());
            Assert.IsFalse(result.IsUnavailable);
        }

        [TestMethod]
        public async Task FreshCache_StillRemovesEventsThatEndedSinceItsLastActivationAsync()
        {
            using var directory = new TemporaryDirectory();
            var clock = _now;
            using var handler = new TestHttpMessageHandler((_, _) => Json(Event("Today", new DateTime(2026, 11, 9))));
            using var client = new HttpClient(handler);
            var service = new EventService(client, directory.Path, () => clock);
            Assert.HasCount(1, (await service.GetAsync(false, CancellationToken.None)).Events);
            clock = clock.AddDays(1);
            var result = await service.GetAsync(false, CancellationToken.None);
            Assert.IsEmpty(result.Events);
            Assert.IsTrue(result.IsCached);
            Assert.AreEqual(1, handler.RequestCount);
            StringAssert.Contains(result.Message, "No upcoming events");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FailedRefresh_PreservesTheLastGoodSnapshotAndDoesNotRestartItsSevenDayLifetimeAsync(bool invalidJson)
        {
            using var directory = new TemporaryDirectory();
            var clock = _now;
            using var handler = new TestHttpMessageHandler((request, count) =>
            {
                if (count == 1)
                {
                    var response = Json(Event("Retained", new DateTime(2026, 11, 30)));
                    response.Headers.ETag = new EntityTagHeaderValue("\"retained\"");
                    response.Content.Headers.LastModified = _now.AddDays(-1);
                    return response;
                }
                Assert.AreEqual("\"retained\"", request.Headers.IfNoneMatch.Single().ToString());
                Assert.AreEqual(_now.AddDays(-1), request.Headers.IfModifiedSince);
                return invalidJson ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{broken") }
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            });
            using var client = new HttpClient(handler);
            var service = new EventService(client, directory.Path, () => clock);
            await service.GetAsync(false, CancellationToken.None);
            clock = clock.AddDays(8);
            var result = await service.GetAsync(false, CancellationToken.None);
            Assert.AreEqual("Retained", result.Events.Single().Name);
            Assert.IsTrue(result.IsCached);
            Assert.IsTrue(result.IsStale);
            Assert.IsFalse(result.IsUnavailable);
            StringAssert.Contains(result.Message, "Could not update events");
            var snapshot = await AtomicJsonCache.ReadAsync<EventService.Snapshot>(directory.GetPath("events.json"),
                options: new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.AreEqual(_now, snapshot!.FetchedAtUtc);
            Assert.AreEqual("\"retained\"", snapshot.ETag);
            Assert.AreEqual(_now.AddDays(-1), snapshot.LastModified);
            await service.GetAsync(false, CancellationToken.None);
            Assert.AreEqual(3, handler.RequestCount, "A failed refresh must remain eligible for retry.");
        }

        [TestMethod]
        public async Task FirstLoadFailure_IsVisibleAndNeverCreatesAFreshEmptyCacheAsync()
        {
            using var directory = new TemporaryDirectory();
            using var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
            using var client = new HttpClient(handler);
            var result = await new EventService(client, directory.Path, () => _now).GetAsync(false, CancellationToken.None);
            Assert.IsTrue(result.IsUnavailable);
            Assert.IsEmpty(result.Events);
            StringAssert.Contains(result.Message, "unavailable");
            Assert.IsFalse(File.Exists(directory.GetPath("events.json")));
        }

        [TestMethod]
        public async Task ForceRefresh_BypassesTheFreshCacheAsync()
        {
            using var directory = new TemporaryDirectory();
            using var handler = new TestHttpMessageHandler((_, count) => Json(Event("Event " + count, new DateTime(2026, 11, 30))));
            using var client = new HttpClient(handler);
            var service = new EventService(client, directory.Path, () => _now);
            await service.GetAsync(false, CancellationToken.None);
            Assert.AreEqual("Event 2", (await service.GetAsync(true, CancellationToken.None)).Events.Single().Name);
            Assert.AreEqual(2, handler.RequestCount);
        }

        [TestMethod]
        public async Task Cancellation_NeverPublishesAnIncompleteSnapshotAsync()
        {
            using var directory = new TemporaryDirectory();
            using var cancellation = new CancellationTokenSource();
            using var handler = new TestHttpMessageHandler((_, _) =>
            {
                cancellation.Cancel();
                return Json(Event("Canceled", new DateTime(2026, 11, 30)));
            });
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<OperationCanceledException>(() => new EventService(client, directory.Path, () => _now)
                .GetAsync(false, cancellation.Token));
            Assert.IsFalse(File.Exists(directory.GetPath("events.json")));
        }

        [TestMethod]
        [DataRow("http://example.com/image.png", false)]
        [DataRow("https://example.com/image.png", true)]
        public void Catalog_RequiresHttpsAndValidDateRanges(string thumbnail, bool invalidRange)
        {
            var item = Event("Invalid", new DateTime(2027, 1, 2));
            item.ThumbnailUrl = thumbnail;
            if (invalidRange)
                item.EndDate = new DateTime(2027, 1, 1);
            Assert.ThrowsExactly<InvalidDataException>(() => EventService.Parse(Serialize(item)));
        }

        [TestMethod]
        public void CuratedCatalog_HasSourceLinksAndMarksSubscriberBenefits()
        {
            using var stream = typeof(EventServiceTests).Assembly.GetManifestResourceStream("events.json");
            using var reader = new StreamReader(stream!);
            var catalog = EventService.Parse(reader.ReadToEnd());
            var dated = catalog.Events.Where(item => item.StartDate.HasValue).ToArray();
            Assert.IsGreaterThanOrEqualTo(12, dated.Length);
            foreach (var item in dated)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(item.Location));
                if (item.Location != "Virtual")
                    Assert.AreEqual(2, item.Location.Split(',').Length, "Event locations use City, Country without venue or state details.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(item.Description));
                Assert.IsNotNull(item.Sources);
                Assert.IsNotEmpty(item.Sources);
                Assert.AreEqual(item.Name.StartsWith("Visual Studio Live!", StringComparison.Ordinal), item.HasSubscriberDiscount);
            }
            foreach (var name in new[] { "Microsoft Build 2027", "GitHub Universe 2027", "DEVintersection" })
                Assert.IsTrue(catalog.Events.Any(item => item.Name == name));
            Assert.IsTrue(dated.Any(item => item.Name == ".NET Conf 2026"));
            Assert.IsGreaterThanOrEqualTo(5, dated.Count(item => item.Name.StartsWith("NDC", StringComparison.Ordinal)));
        }

        [TestMethod]
        public async Task Thumbnails_AreDeduplicatedAndCachedForSevenDaysAcrossServiceInstancesAsync()
        {
            using var directory = new TemporaryDirectory();
            var clock = _now;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4)));
            using var image = new MemoryStream();
            encoder.Save(image);
            using var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(image.ToArray()) });
            using var client = new HttpClient(handler);
            ConferenceEvent[] Items() => [Event("First", new DateTime(2027, 1, 1)), Event("Second", new DateTime(2027, 2, 1))];
            var first = Items();
            Assert.AreEqual(0, await new EventService(client, directory.Path, () => clock).LoadThumbnailsAsync(first, CancellationToken.None));
            Assert.AreSame(first[0].Thumbnail, first[1].Thumbnail);
            Assert.IsTrue(first[0].Thumbnail!.IsFrozen);
            Assert.AreEqual(200, first[0].Thumbnail!.DecodePixelWidth);
            clock = _now.AddDays(6);
            Assert.AreEqual(0, await new EventService(client, directory.Path, () => clock).LoadThumbnailsAsync(Items(), CancellationToken.None));
            Assert.AreEqual(1, handler.RequestCount);
            clock = _now.AddDays(7);
            Assert.AreEqual(0, await new EventService(client, directory.Path, () => clock).LoadThumbnailsAsync(Items(), CancellationToken.None));
            Assert.AreEqual(2, handler.RequestCount);
        }

        [TestMethod]
        public async Task ThumbnailFailure_ReportsPartialContentWithoutDiscardingTheEventAsync()
        {
            using var directory = new TemporaryDirectory();
            using var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
            using var client = new HttpClient(handler);
            var item = Event("Retained", new DateTime(2027, 1, 1));
            Assert.AreEqual(1, await new EventService(client, directory.Path, () => _now).LoadThumbnailsAsync([item], CancellationToken.None));
            Assert.IsNull(item.Thumbnail);
            Assert.AreEqual("Retained", item.Name);
        }

        private static ConferenceEvent Event(string name, DateTime date) => new ConferenceEvent
        {
            Name = name, Description = "Developer conference", Location = "Seattle, USA", StartDate = date,
            Url = "https://example.com/" + Uri.EscapeDataString(name), ThumbnailUrl = "https://example.com/thumbnail.png",
        };

        private static string Serialize(params ConferenceEvent[] items) => JsonSerializer.Serialize(new { version = 1, events = items });
        private static HttpResponseMessage Json(params ConferenceEvent[] items) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Serialize(items)),
        };
    }
}
