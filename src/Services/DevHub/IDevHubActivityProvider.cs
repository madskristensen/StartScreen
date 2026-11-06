using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub
{
    internal enum DevHubActivityKind
    {
        Issues,
        PullRequests,
        CiRuns,
        Repository,
    }

    /// <summary>The application-facing activity contract. Empty success and failed sources are distinct.</summary>
    internal interface IDevHubActivitySource
    {
        Task<DevHubActivitySnapshot> FetchActivityAsync(DevHubActivityKind kind, DevHubActivityOptions options,
            RemoteRepoIdentifier repo, CancellationToken cancellationToken);
    }

    /// <summary>Optional authentication contract for providers that discover organization endpoints from local repositories.</summary>
    internal interface IDevHubRefreshProvider
    {
        Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(DevHubRefreshContext context, CancellationToken cancellationToken);
    }

    /// <summary>An immutable workspace discovery result shared only within one dashboard operation.</summary>
    internal sealed class DevHubRefreshContext
    {
        public DevHubRefreshContext(IEnumerable<string> remoteUrls, DevHubActivityStatus status = DevHubActivityStatus.Success)
        {
            RemoteUrls = Array.AsReadOnly((remoteUrls ?? Array.Empty<string>()).ToArray());
            Status = status;
        }

        public IReadOnlyList<string> RemoteUrls { get; }
        public DevHubActivityStatus Status { get; }
    }

    internal sealed class DevHubActivityOptions
    {
        public const int DefaultLimit = 30;
        public const int MaximumLimit = 300;

        public DevHubActivityOptions(int maxItems = DefaultLimit, bool includeCompleted = false, DevHubRefreshContext refreshContext = null)
        {
            if (maxItems < 1 || maxItems > MaximumLimit)
                throw new ArgumentOutOfRangeException(nameof(maxItems));

            MaxItems = maxItems;
            IncludeCompleted = includeCompleted;
            RefreshContext = refreshContext;
        }

        public int MaxItems { get; }
        public bool IncludeCompleted { get; }
        public DevHubRefreshContext RefreshContext { get; }
    }

    internal interface IDevHubActivityProvider
    {
        Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(DevHubActivityOptions options, CancellationToken cancellationToken);
        Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(DevHubActivityOptions options, CancellationToken cancellationToken);
        Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(DevHubActivityOptions options, CancellationToken cancellationToken);
        Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, DevHubActivityOptions options, CancellationToken cancellationToken);
    }
}