using System.Text.Json;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubCredentialCacheTests
    {
        [TestMethod]
        [DataRow("token")]
        [DataRow("username")]
        [DataRow("bearer")]
        [DataRow("endpoint")]
        [DataRow("generation")]
        public void CacheSeparatesCredentialsEndpointsAndGenerations(string change)
        {
            var generation = 0;
            var cache = new DevHubCredentialCache<string>(TimeSpan.FromMinutes(5), getGeneration: () => generation);
            var credential = new DevHubCredential(change == "bearer" ? null! : "me", "test-token");
            var endpoint = "https://example.com/profile";
            Assert.IsTrue(cache.Store(credential, endpoint, "verified", generation));
            Assert.IsTrue(cache.TryGetValue(credential, endpoint, out var original));
            Assert.AreEqual("verified", original);
            switch (change)
            {
                case "token": credential = new DevHubCredential("me", "different-token"); break;
                case "username": credential = new DevHubCredential("other", "test-token"); break;
                case "bearer": credential = new DevHubCredential("test-token", DateTimeOffset.MaxValue); break;
                case "endpoint": endpoint = "https://example.com/other/profile"; break;
                case "generation": generation++; break;
            }

            Assert.IsFalse(cache.TryGetValue(credential, endpoint, out _));
        }

        [TestMethod]
        [DataRow(299, true)]
        [DataRow(300, false)]
        [DataRow(-1, false)]
        public void CacheHasStrictLifetimeAndRejectsFutureEntries(int seconds, bool expected)
        {
            var now = DateTimeOffset.UtcNow;
            var cache = new DevHubCredentialCache<string>(TimeSpan.FromMinutes(5), () => now, () => 0);
            var credential = new DevHubCredential("me", "test-token");
            Assert.IsTrue(cache.Store(credential, "profile", "verified", 0));
            now = now.AddSeconds(seconds);

            Assert.AreEqual(expected, cache.TryGetValue(credential, "profile", out _));
        }

        [TestMethod]
        public void CacheHitsDoNotExtendFreshness()
        {
            var now = DateTimeOffset.UtcNow;
            var cache = new DevHubCredentialCache<string>(TimeSpan.FromMinutes(5), () => now, () => 0);
            var credential = new DevHubCredential("me", "test-token");
            Assert.IsTrue(cache.Store(credential, "profile", "verified", 0));
            now = now.AddMinutes(4);
            Assert.IsTrue(cache.TryGetValue(credential, "profile", out _));
            now = now.AddMinutes(1);

            Assert.IsFalse(cache.TryGetValue(credential, "profile", out _));
        }

        [TestMethod]
        public void CacheCannotOutliveCredentialOrAcceptInvalidatedWork()
        {
            var now = DateTimeOffset.UtcNow;
            var generation = 0;
            var cache = new DevHubCredentialCache<string>(TimeSpan.FromHours(1), () => now, () => generation);
            var credential = new DevHubCredential("test-token", now.AddSeconds(10));
            Assert.IsTrue(cache.Store(credential, "profile", "verified", generation));
            now = now.AddSeconds(10);
            Assert.IsFalse(cache.TryGetValue(credential, "profile", out _));
            Assert.IsFalse(cache.Store(credential, "profile", "expired", generation));
            generation++;

            Assert.IsFalse(cache.Store(new DevHubCredential("me", "new-token"), "profile", "old work", generation - 1));
        }

        [TestMethod]
        public void CapacityEvictsOldestCompletedValue()
        {
            var now = DateTimeOffset.UtcNow;
            var cache = new DevHubCredentialCache<string>(TimeSpan.FromHours(1), () => now, () => 0);
            var credential = new DevHubCredential("me", "test-token");
            for (var index = 0; index <= DevHubCredentialCache<string>.Capacity; index++)
            {
                now = now.AddSeconds(1);
                Assert.IsTrue(cache.Store(credential, "profile" + index, "user" + index, 0));
            }

            Assert.IsFalse(cache.TryGetValue(credential, "profile0", out _));
            Assert.IsTrue(cache.TryGetValue(credential, "profile" + DevHubCredentialCache<string>.Capacity, out var newest));
            Assert.AreEqual("user" + DevHubCredentialCache<string>.Capacity, newest);
        }

        [TestMethod]
        public void CredentialContextIsNeverPersistedWithUserProfiles()
        {
            var json = JsonSerializer.Serialize(new DevHubUser { Username = "me", CredentialContext = "private-cache-key" });

            Assert.DoesNotContain("CredentialContext", json);
            Assert.DoesNotContain("private-cache-key", json);
        }
    }
}
