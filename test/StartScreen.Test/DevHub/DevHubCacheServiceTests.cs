using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Services.DevHub;
using System;
using System.IO;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;
using System.Collections.Generic;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubCacheServiceTests
    {
        private string _testCacheFolder;
        private DevHubCacheService _cache;

        [TestInitialize]
        public void Setup()
        {
            _testCacheFolder = Path.Combine(Path.GetTempPath(), "StartScreenTest_" + Guid.NewGuid().ToString("N"));
            _cache = new DevHubCacheService(_testCacheFolder, TimeSpan.FromMinutes(15));
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_testCacheFolder))
                    Directory.Delete(_testCacheFolder, recursive: true);
            }
            catch { }
        }

        [TestMethod]
        public void IsCacheStale_NoCacheFile_ReturnsTrue()
        {
            Assert.IsTrue(_cache.IsCacheStale());
        }

        [TestMethod]
        public async Task ReadDashboard_NoCacheFile_ReturnsNull()
        {
            var result = await _cache.ReadDashboardAsync();

            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task WriteThenRead_RoundTrips()
        {
            var dashboard = new DevHubDashboard
            {
                FetchedAt = DateTime.UtcNow,
                Users = new List<DevHubUser>
                {
                    new DevHubUser { Username = "testuser", ProviderName = "GitHub" }
                },
                PullRequests = new List<DevHubPullRequest>
                {
                    new DevHubPullRequest
                    {
                        Title = "Test PR",
                        Number = "#1",
                        NumericId = 1,
                        ProviderName = "GitHub",
                        UpdatedAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow,
                    }
                }
            };

            _cache.WriteDashboard(dashboard);
            var result = await _cache.ReadDashboardAsync();

            Assert.IsNotNull(result);
            Assert.HasCount(1, result.Users);
            Assert.AreEqual("testuser", result.Users[0].Username);
            Assert.HasCount(1, result.PullRequests);
            Assert.AreEqual("Test PR", result.PullRequests[0].Title);
        }

        [TestMethod]
        public void IsCacheStale_AfterWrite_ReturnsFalse()
        {
            var dashboard = new DevHubDashboard { FetchedAt = DateTime.UtcNow };
            _cache.WriteDashboard(dashboard);

            Assert.IsFalse(_cache.IsCacheStale());
        }

        [TestMethod]
        public void GetCacheTimestamp_NoCacheFile_ReturnsNull()
        {
            Assert.IsNull(_cache.GetCacheTimestamp());
        }

        [TestMethod]
        public void GetCacheTimestamp_AfterWrite_ReturnsTimestamp()
        {
            var dashboard = new DevHubDashboard { FetchedAt = DateTime.UtcNow };
            _cache.WriteDashboard(dashboard);

            var timestamp = _cache.GetCacheTimestamp();

            Assert.IsNotNull(timestamp);
            Assert.IsLessThan(5, (DateTime.UtcNow - timestamp.Value).TotalSeconds);
        }

        [TestMethod]
        public void ClearCache_RemovesFolder()
        {
            var dashboard = new DevHubDashboard { FetchedAt = DateTime.UtcNow };
            _cache.WriteDashboard(dashboard);

            _cache.ClearCache();

            Assert.IsFalse(Directory.Exists(_testCacheFolder));
            Assert.IsTrue(_cache.IsCacheStale());
        }

        [TestMethod]
        public void ClearCache_NoFolder_DoesNotThrow()
        {
            _cache.ClearCache();
            // Should not throw
        }

        [TestMethod]
        public void WriteDashboard_Null_DoesNothing()
        {
            _cache.WriteDashboard(null);

            Assert.IsFalse(Directory.Exists(_testCacheFolder));
        }

        [TestMethod]
        public void IsCacheStale_WithShortThreshold_ReturnsTrueAfterExpiry()
        {
            var shortCache = new DevHubCacheService(_testCacheFolder, TimeSpan.Zero);
            var dashboard = new DevHubDashboard { FetchedAt = DateTime.UtcNow };
            shortCache.WriteDashboard(dashboard);

            // With zero threshold, it's immediately stale
            Assert.IsTrue(shortCache.IsCacheStale());
        }
    }
}
