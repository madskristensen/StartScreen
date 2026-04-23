using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Services.DevHub
{
    /// <summary>
    /// Resolves the appropriate <see cref="IDevHubProvider"/> for a given remote URL.
    /// Maintains a static list of all known providers.
    /// </summary>
    internal static class DevHubProviderRegistry
    {
        private static readonly IDevHubProvider[] Providers =
        [
            new GitHubDevHubProvider(),
            new AzureDevOpsDevHubProvider(),
            new BitbucketDevHubProvider(),
        ];

        /// <summary>
        /// Returns the first provider that can handle the given remote URL, or null.
        /// </summary>
        public static IDevHubProvider GetProvider(string remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
                return null;

            return Providers.FirstOrDefault(p => p.CanHandle(remoteUrl));
        }

        /// <summary>
        /// Returns all registered providers, regardless of authentication state.
        /// </summary>
        public static IReadOnlyList<IDevHubProvider> GetAllProviders() => Providers;

        /// <summary>
        /// Returns all providers that currently have valid credentials.
        /// </summary>
        public static async Task<IReadOnlyList<IDevHubProvider>> GetAuthenticatedProvidersAsync(CancellationToken cancellationToken)
        {
            var results = new List<IDevHubProvider>();

            foreach (var provider in Providers)
            {
                var user = await provider.GetAuthenticatedUserAsync(cancellationToken);
                if (user != null)
                {
                    results.Add(provider);
                }
            }

            return results;
        }
    }
}
