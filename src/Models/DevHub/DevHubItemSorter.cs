using System;
using System.Collections.Generic;
using System.Linq;

namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// Orders Dev Hub issues and pull requests according to the user's
    /// <see cref="DevHubSortOrder"/> preference. Provider grouping uses the
    /// item's <see cref="RemoteRepoIdentifier"/>; within each group items are
    /// ordered most recently updated first.
    /// </summary>
    public static class DevHubItemSorter
    {
        /// <summary>
        /// Returns a new list ordered according to <paramref name="order"/>.
        /// </summary>
        /// <typeparam name="T">The item type (issue or pull request).</typeparam>
        /// <param name="items">The items to order. A null value yields an empty list.</param>
        /// <param name="order">The desired sort order.</param>
        /// <param name="repoSelector">Selects the repository identifier used for provider grouping.</param>
        /// <param name="updatedSelector">Selects the timestamp used for "most recent" ordering.</param>
        public static List<T> Sort<T>(
            IEnumerable<T> items,
            DevHubSortOrder order,
            Func<T, RemoteRepoIdentifier> repoSelector,
            Func<T, DateTime> updatedSelector)
        {
            if (items == null)
            {
                return new List<T>();
            }

            if (repoSelector == null)
            {
                throw new ArgumentNullException(nameof(repoSelector));
            }

            if (updatedSelector == null)
            {
                throw new ArgumentNullException(nameof(updatedSelector));
            }

            switch (order)
            {
                case DevHubSortOrder.GitHubFirst:
                    return items
                        .OrderBy(item => IsGitHub(repoSelector(item)) ? 0 : 1)
                        .ThenByDescending(updatedSelector)
                        .ToList();

                case DevHubSortOrder.AzureDevOpsFirst:
                    return items
                        .OrderBy(item => IsAzureDevOps(repoSelector(item)) ? 0 : 1)
                        .ThenByDescending(updatedSelector)
                        .ToList();

                default:
                    return items
                        .OrderByDescending(updatedSelector)
                        .ToList();
            }
        }

        private static bool IsGitHub(RemoteRepoIdentifier repo)
        {
            return repo != null
                && repo.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAzureDevOps(RemoteRepoIdentifier repo)
        {
            return repo != null
                && (repo.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
                    || repo.IsAzureDevOpsServer);
        }
    }
}
