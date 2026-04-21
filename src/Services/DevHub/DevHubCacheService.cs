using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub
{
    /// <summary>
    /// Manages disk-based caching for Dev Hub data.
    /// Cache-first design: loads from disk instantly, refreshes in background if stale.
    /// </summary>
    internal class DevHubCacheService
    {
        private readonly string _cacheFolder;
        private readonly TimeSpan _staleThreshold;

        private const string DashboardFileName = "dashboard.json";

        public DevHubCacheService(string cacheFolder, TimeSpan? staleThreshold = null)
        {
            _cacheFolder = cacheFolder ?? throw new ArgumentNullException(nameof(cacheFolder));
            _staleThreshold = staleThreshold ?? TimeSpan.FromMinutes(15);
        }

        /// <summary>
        /// Creates a cache service with the default cache location.
        /// </summary>
        public DevHubCacheService()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StartScreen", "DevHubCache"))
        {
        }

        /// <summary>
        /// Returns true if the dashboard cache is stale or does not exist.
        /// </summary>
        public bool IsCacheStale()
        {
            var filePath = Path.Combine(_cacheFolder, DashboardFileName);
            if (!File.Exists(filePath))
                return true;

            var lastModified = File.GetLastWriteTimeUtc(filePath);
            return DateTime.UtcNow - lastModified >= _staleThreshold;
        }

        /// <summary>
        /// Reads the cached dashboard from disk.
        /// Returns null if no cache exists or on error.
        /// </summary>
        public async Task<DevHubDashboard> ReadDashboardAsync()
        {
            var filePath = Path.Combine(_cacheFolder, DashboardFileName);
            if (!File.Exists(filePath))
                return null;

            try
            {
                byte[] fileBytes;
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                {
                    fileBytes = new byte[stream.Length];
                    await stream.ReadAsync(fileBytes, 0, fileBytes.Length);
                }

                return JsonSerializer.Deserialize<DevHubDashboard>(fileBytes, JsonOptions);
            }
            catch (Exception ex)
            {
                ex.Log();
                return null;
            }
        }

        /// <summary>
        /// Writes the dashboard data to disk cache.
        /// </summary>
        public void WriteDashboard(DevHubDashboard dashboard)
        {
            if (dashboard == null)
                return;

            try
            {
                Directory.CreateDirectory(_cacheFolder);

                var filePath = Path.Combine(_cacheFolder, DashboardFileName);
                var json = JsonSerializer.SerializeToUtf8Bytes(dashboard, JsonOptions);
                File.WriteAllBytes(filePath, json);
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        /// <summary>
        /// Deletes all cached data.
        /// </summary>
        public void ClearCache()
        {
            try
            {
                if (Directory.Exists(_cacheFolder))
                {
                    Directory.Delete(_cacheFolder, recursive: true);
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        /// <summary>
        /// Returns the last modified time of the cache file, or null if it doesn't exist.
        /// </summary>
        public DateTime? GetCacheTimestamp()
        {
            var filePath = Path.Combine(_cacheFolder, DashboardFileName);
            if (!File.Exists(filePath))
                return null;

            return File.GetLastWriteTimeUtc(filePath);
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
    }
}
