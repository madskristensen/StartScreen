using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
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

        private const string _dashboardFileName = "dashboard.json";

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
            var filePath = Path.Combine(_cacheFolder, _dashboardFileName);
            if (!File.Exists(filePath))
                return true;

            var lastModified = File.GetLastWriteTimeUtc(filePath);
            return DateTime.UtcNow - lastModified >= _staleThreshold;
        }

        /// <summary>
        /// Reads the cached dashboard from disk.
        /// Returns null if no cache exists or on error.
        /// </summary>
        public async Task<DevHubDashboard> ReadDashboardAsync(CancellationToken cancellationToken = default)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = Path.Combine(_cacheFolder, _dashboardFileName);
            if (!File.Exists(filePath))
                return null;

            try
            {
                var dashboard = await AtomicJsonCache.ReadAsync<DevHubDashboard>(filePath, cancellationToken, _jsonOptions).ConfigureAwait(false);
                if (dashboard != null)
                {
                    foreach (var source in dashboard.Sources.Values)
                        source.Message = RemoveOptionalSamlWarnings(source.Message);
                    foreach (var warning in dashboard.ActivityWarnings.ToArray())
                    {
                        var remaining = RemoveOptionalSamlWarnings(warning.Value);
                        if (remaining == warning.Value)
                            continue;
                        OutputLog.Info("GitHub", warning.Value);
                        if (remaining == null)
                            dashboard.ActivityWarnings.Remove(warning.Key);
                        else
                            dashboard.ActivityWarnings[warning.Key] = remaining;
                    }
                }
                return dashboard;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return null;
            }
        }

        private static string RemoveOptionalSamlWarnings(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;
            var remaining = message.Replace(
                "Pull requests loaded. GitHub is connected. Some organizations restrict review/check details with SAML SSO.", string.Empty);
            const string prefix = "Review/check metadata is restricted by organization SAML SSO for ";
            const string suffix = ". Loaded pull requests remain available.";
            while (true)
            {
                var start = remaining.IndexOf(prefix, StringComparison.Ordinal);
                var end = start < 0 ? -1 : remaining.IndexOf(suffix, start, StringComparison.Ordinal);
                if (end < 0)
                    break;
                remaining = remaining.Remove(start, end + suffix.Length - start);
            }
            return remaining == message ? message : string.IsNullOrWhiteSpace(remaining) ? null : remaining.Trim();
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
                var filePath = Path.Combine(_cacheFolder, _dashboardFileName);
                AtomicJsonCache.Write(filePath, dashboard, _jsonOptions);
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
            }
        }

        /// <summary>Persists a dashboard without blocking the caller's synchronization context.</summary>
        public async Task WriteDashboardAsync(DevHubDashboard dashboard, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dashboard == null)
                return;

            try
            {
                await AtomicJsonCache.WriteAsync(Path.Combine(_cacheFolder, _dashboardFileName), dashboard,
                    cancellationToken, _jsonOptions).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
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
                ex.LogAsync().FireAndForget();
            }
        }

        /// <summary>
        /// Returns the last modified time of the cache file, or null if it doesn't exist.
        /// </summary>
        public DateTime? GetCacheTimestamp()
        {
            var filePath = Path.Combine(_cacheFolder, _dashboardFileName);
            if (!File.Exists(filePath))
                return null;

            return File.GetLastWriteTimeUtc(filePath);
        }

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
    }
}
