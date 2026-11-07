using System.Net;
using System.Net.Http;
using System.Text.Json;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class ServiceHealthServiceTests
    {
        [TestMethod]
        public void ParseStatus_ResolvedServicesWithoutSeverity_AreHealthy()
        {
            var now = DateTimeOffset.UtcNow;
            var json = Feed("{\"name\":\"NuGet\",\"state\":\"Resolved\",\"geographies\":[{\"geography\":\"Americas\",\"state\":\"Resolved\"}]}", now);

            var result = ServiceHealthService.ParseStatus(json, now);

            Assert.AreEqual(ServiceHealthState.Healthy, result.State);
            Assert.AreEqual("Service operational", result.StatusText);
            Assert.AreEqual(now, result.CheckedAtUtc);
            Assert.AreEqual(now, result.LastUpdatedUtc);
            Assert.IsFalse(result.IsStale);
            StringAssert.Contains(result.Details, "does not report GitHub public API health");
            Assert.AreEqual("https://vsstatus.visualstudio.com/", result.StatusPageUrl);
        }

        [TestMethod]
        [DataRow("{\"name\":\"NuGet\",\"state\":\"Active\"}")]
        [DataRow("{\"name\":\"NuGet\",\"state\":\"Resolved\",\"severity\":2}")]
        [DataRow("{\"name\":\"NuGet\",\"state\":\"Resolved\",\"severity\":\"Warning\"}")]
        [DataRow("{\"name\":\"NuGet\",\"state\":\"Resolved\",\"geographies\":[{\"state\":\"Investigating\"}]}")]
        public void ParseStatus_ServiceOrRegionalIncident_IsIncident(string service)
        {
            var result = ServiceHealthService.ParseStatus(Feed(service, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

            Assert.AreEqual(ServiceHealthState.Incident, result.State);
            Assert.AreEqual("Visual Studio service incident", result.StatusText);
            StringAssert.Contains(result.Details, "NuGet");
        }

        [TestMethod]
        [DataRow("{\"name\":\"NuGet\",\"state\":\"Resolved\",\"severity\":null}")]
        [DataRow("{\"name\":\"NuGet\",\"state\":\"Resolved\",\"severity\":0}")]
        [DataRow("{\"name\":\"NuGet\",\"state\":\"Resolved\",\"severity\":\"None\"}")]
        public void ParseStatus_HealthySeverities_AreHealthy(string service)
        {
            var result = ServiceHealthService.ParseStatus(Feed(service, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

            Assert.AreEqual(ServiceHealthState.Healthy, result.State);
        }

        [TestMethod]
        [DataRow("{}")]
        [DataRow("[]")]
        [DataRow("{\"services\":[]}")]
        [DataRow("{\"services\":[{\"name\":\"NuGet\",\"state\":\"Resolved\"}]}")]
        public void ParseStatus_MissingFeedStructure_Throws(string json)
        {
            Assert.ThrowsExactly<FormatException>(() => ServiceHealthService.ParseStatus(json, DateTimeOffset.UtcNow));
        }

        [TestMethod]
        [DataRow("{}")]
        [DataRow("{\"name\":\"NuGet\"}")]
        [DataRow("{\"name\":\"NuGet\",\"state\":\"Unrecognized\"}")]
        [DataRow("{\"name\":\"NuGet\",\"state\":true}")]
        [DataRow("{\"name\":\"NuGet\",\"state\":\"Resolved\",\"severity\":{}}")]
        [DataRow("{\"name\":\"NuGet\",\"state\":\"Resolved\",\"severity\":\"Unrecognized\"}")]
        [DataRow("{\"name\":\"NuGet\",\"state\":\"Resolved\",\"geographies\":{}}")]
        public void ParseStatus_MalformedService_DoesNotClaimHealthy(string service)
        {
            var json = Feed(service, DateTimeOffset.UtcNow);

            Assert.ThrowsExactly<FormatException>(() => ServiceHealthService.ParseStatus(json, DateTimeOffset.UtcNow));
        }

        [TestMethod]
        public void ParseStatus_InvalidJson_Throws()
        {
            Assert.Throws<JsonException>(() => ServiceHealthService.ParseStatus("not json", DateTimeOffset.UtcNow));
        }

        [TestMethod]
        public void ParseStatus_OldFeed_ExplicitlyLabelsStale()
        {
            var now = DateTimeOffset.UtcNow;
            var result = ServiceHealthService.ParseStatus(
                Feed("{\"name\":\"NuGet\",\"state\":\"Resolved\"}", now.AddHours(-1)), now);

            Assert.IsTrue(result.IsStale);
            StringAssert.StartsWith(result.StatusText, "Stale - ");
            StringAssert.StartsWith(result.Details, "Stale status - ");
        }

        [TestMethod]
        public void ParseStatus_FutureTimestamp_Throws()
        {
            var now = DateTimeOffset.UtcNow;
            var json = Feed("{\"name\":\"NuGet\",\"state\":\"Resolved\"}", now.AddHours(1));

            Assert.ThrowsExactly<FormatException>(() => ServiceHealthService.ParseStatus(json, now));
        }

        [TestMethod]
        public async Task GetStatusAsync_FailedRequest_ReturnsExplicitlyUnknownAsync()
        {
            using var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            using var client = new HttpClient(handler);

            var result = await ServiceHealthService.GetStatusAsync(client, CancellationToken.None);

            Assert.AreEqual(ServiceHealthState.Unknown, result.State);
            Assert.AreEqual("Visual Studio service status unknown", result.StatusText);
            Assert.IsTrue(result.IsStale);
            Assert.IsNull(result.LastUpdatedUtc);
            StringAssert.Contains(result.Details, "previously displayed status is stale");
        }

        [TestMethod]
        public async Task GetStatusAsync_MalformedFeed_ReturnsUnknownAsync()
        {
            using var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
            using var client = new HttpClient(handler);

            var result = await ServiceHealthService.GetStatusAsync(client, CancellationToken.None);

            Assert.AreEqual(ServiceHealthState.Unknown, result.State);
        }

        [TestMethod]
        public async Task GetStatusAsync_ValidResponse_UsesVisualStudioFeedAsync()
        {
            var json = Feed("{\"name\":\"NuGet\",\"state\":\"Resolved\"}", DateTimeOffset.UtcNow);
            using var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
            using var client = new HttpClient(handler);

            var result = await ServiceHealthService.GetStatusAsync(client, CancellationToken.None);

            Assert.AreEqual(ServiceHealthState.Healthy, result.State);
            Assert.AreEqual(ServiceHealthService.StatusApiUrl, handler.LastRequest!.RequestUri!.AbsoluteUri);
            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        public async Task GetStatusAsync_Cancelled_DoesNotSendRequestAsync()
        {
            using var handler = new TestHttpMessageHandler((_, _) => throw new AssertFailedException("Unexpected request."));
            using var client = new HttpClient(handler);
            using var source = new CancellationTokenSource();
            source.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ServiceHealthService.GetStatusAsync(client, source.Token));
            Assert.AreEqual(0, handler.RequestCount);
        }

        private static string Feed(string service, DateTimeOffset timestamp)
        {
            return "{\"queriedTimestamp\":\"" + timestamp.ToString("O") + "\",\"services\":[" + service + "]}";
        }
    }
}