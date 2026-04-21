using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub
{
    /// <summary>
    /// Defines the contract for a remote hosting provider (GitHub, Azure DevOps, etc.).
    /// Each provider handles a specific set of remote URL hosts and knows how to fetch
    /// user activity data from that service.
    /// </summary>
    internal interface IDevHubProvider
    {
        /// <summary>
        /// Display name shown in the UI (e.g., "GitHub", "Azure DevOps").
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Returns true if this provider can handle the given remote URL.
        /// </summary>
        bool CanHandle(string remoteUrl);

        /// <summary>
        /// Parses a remote URL into a structured identifier.
        /// Returns null if the URL cannot be parsed by this provider.
        /// </summary>
        RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl);

        /// <summary>
        /// Gets the authenticated user for this provider.
        /// Returns null if no credentials are available.
        /// </summary>
        Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Fetches all open pull requests authored by or assigned to the authenticated user.
        /// Returns an empty list if the user is not authenticated or on error.
        /// </summary>
        Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Fetches issues or work items assigned to the authenticated user.
        /// Returns an empty list if the user is not authenticated or on error.
        /// </summary>
        Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Fetches recent CI/build failures for the authenticated user's repositories.
        /// Returns an empty list if the user is not authenticated or on error.
        /// </summary>
        Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Fetches detailed PR, issue, and CI data for a specific repository.
        /// Returns null if the user is not authenticated or on error.
        /// </summary>
        Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken);

        /// <summary>
        /// Builds the web URL for a repository's home page.
        /// </summary>
        string GetRepoWebUrl(RemoteRepoIdentifier repo);

        /// <summary>
        /// Builds the web URL for a repository's pull request list.
        /// </summary>
        string GetPullRequestsWebUrl(RemoteRepoIdentifier repo);

        /// <summary>
        /// Builds the web URL for a repository's issues list.
        /// </summary>
        string GetIssuesWebUrl(RemoteRepoIdentifier repo);
    }
}
