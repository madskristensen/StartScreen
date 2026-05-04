using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub
{
    /// <summary>
    /// Helpers for enumerating known Azure DevOps hosts (cloud + on-premises Server),
    /// and for auto-discovering on-premises hosts from the user's git remotes.
    /// </summary>
    internal static class AzureDevOpsServerHelper
    {
        /// <summary>
        /// The canonical Azure DevOps cloud host.
        /// </summary>
        public const string CloudHost = "dev.azure.com";

        /// <summary>
        /// Returns the list of on-premises Azure DevOps Server hosts the user has
        /// manually added through the settings panel. Cloud is not included.
        /// </summary>
        public static IReadOnlyList<string> GetConfiguredServerHosts()
        {
            var raw = Options.Instance.DevHubAdoServers ?? string.Empty;
            return raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim())
                      .Where(s => s.Length > 0)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToList();
        }

        /// <summary>
        /// Persists the configured Azure DevOps Server hosts back to settings.
        /// </summary>
        public static void SaveConfiguredServerHosts(IEnumerable<string> hosts)
        {
            var clean = (hosts ?? Array.Empty<string>())
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.Trim())
                .Where(h => !h.Equals(CloudHost, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            Options.Instance.DevHubAdoServers = string.Join(";", clean);
            Options.Instance.SaveAsync().FireAndForget();
        }

        /// <summary>
        /// Adds an on-premises Azure DevOps Server host to the configured list (no-op if already present).
        /// </summary>
        public static void AddConfiguredServerHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;

            host = host.Trim();
            if (host.Equals(CloudHost, StringComparison.OrdinalIgnoreCase))
                return;

            var existing = GetConfiguredServerHosts();
            if (existing.Any(h => h.Equals(host, StringComparison.OrdinalIgnoreCase)))
                return;

            var updated = existing.Concat(new[] { host });
            SaveConfiguredServerHosts(updated);
        }

        /// <summary>
        /// Removes an on-premises Azure DevOps Server host from the configured list.
        /// </summary>
        public static void RemoveConfiguredServerHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;

            var remaining = GetConfiguredServerHosts()
                .Where(h => !h.Equals(host, StringComparison.OrdinalIgnoreCase));
            SaveConfiguredServerHosts(remaining);
        }

        /// <summary>
        /// Returns the union of cloud + configured on-premises hosts. Cloud always first.
        /// </summary>
        public static IReadOnlyList<string> GetAllKnownHosts()
        {
            var hosts = new List<string> { CloudHost };
            foreach (var h in GetConfiguredServerHosts())
            {
                if (!hosts.Any(x => x.Equals(h, StringComparison.OrdinalIgnoreCase)))
                    hosts.Add(h);
            }
            return hosts;
        }

        /// <summary>
        /// Scans the provided git remote URLs for on-premises Azure DevOps Server hosts.
        /// A remote is considered an on-prem ADO Server if its URL parses to a
        /// <see cref="RemoteRepoIdentifier"/> with <see cref="RemoteRepoIdentifier.IsAzureDevOpsServer"/> set.
        /// Returns distinct hosts, excluding any already present in <paramref name="excludeHosts"/>.
        /// </summary>
        public static IReadOnlyList<DiscoveredServer> DiscoverServers(
            IEnumerable<string> remoteUrls,
            IEnumerable<string> excludeHosts = null)
        {
            var exclude = new HashSet<string>(
                excludeHosts ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            var byHost = new Dictionary<string, DiscoveredServer>(StringComparer.OrdinalIgnoreCase);

            foreach (var url in remoteUrls ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                RemoteRepoIdentifier id;
                try
                {
                    id = RemoteRepoIdentifier.TryParse(url);
                }
                catch
                {
                    continue;
                }

                if (id == null || !id.IsAzureDevOpsServer)
                    continue;

                if (exclude.Contains(id.Host))
                    continue;

                if (!byHost.TryGetValue(id.Host, out var existing))
                {
                    byHost[id.Host] = new DiscoveredServer(id.Host, id.BaseUrl);
                }
                else if (string.IsNullOrEmpty(existing.SampleBaseUrl) && !string.IsNullOrEmpty(id.BaseUrl))
                {
                    byHost[id.Host] = new DiscoveredServer(id.Host, id.BaseUrl);
                }
            }

            return byHost.Values.ToList();
        }

        /// <summary>
        /// Auto-discovers on-premises Azure DevOps Server hosts from the current MRU items.
        /// Reads <see cref="Models.MruItem.RemoteUrl"/> from each item that has been populated.
        /// Excludes hosts already in the configured server list and the cloud host.
        /// Safe to call from any thread.
        /// </summary>
        public static async Task<IReadOnlyList<DiscoveredServer>> DiscoverFromMruAsync()
        {
            try
            {
                var items = await StartScreen.Services.MruService.GetMruItemsAsync();
                var remotes = items
                    .Select(i => i.RemoteUrl)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .ToList();

                var exclude = new List<string> { CloudHost };
                exclude.AddRange(GetConfiguredServerHosts());

                return DiscoverServers(remotes, exclude);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return Array.Empty<DiscoveredServer>();
            }
        }
    }

    /// <summary>
    /// Represents an auto-discovered on-premises Azure DevOps Server.
    /// </summary>
    internal sealed class DiscoveredServer
    {
        public string Host { get; }

        /// <summary>
        /// A representative collection-level base URL observed in one of the user's remotes,
        /// e.g. "https://tfs.contoso.com/tfs/DefaultCollection". May be null if unknown.
        /// </summary>
        public string SampleBaseUrl { get; }

        public DiscoveredServer(string host, string sampleBaseUrl)
        {
            Host = host;
            SampleBaseUrl = sampleBaseUrl;
        }
    }
}
