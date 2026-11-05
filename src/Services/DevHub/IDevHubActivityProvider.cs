using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub
{
    internal sealed class DevHubActivityOptions
    {
        public const int DefaultLimit = 30;
        public const int MaximumLimit = 300;

        public DevHubActivityOptions(int maxItems = DefaultLimit, bool includeCompleted = false)
        {
            if (maxItems < 1 || maxItems > MaximumLimit)
                throw new ArgumentOutOfRangeException(nameof(maxItems));

            MaxItems = maxItems;
            IncludeCompleted = includeCompleted;
        }

        public int MaxItems { get; }
        public bool IncludeCompleted { get; }
    }

    internal interface IDevHubActivityProvider
    {
        Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(DevHubActivityOptions options, CancellationToken cancellationToken);
        Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(DevHubActivityOptions options, CancellationToken cancellationToken);
        Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(DevHubActivityOptions options, CancellationToken cancellationToken);
        Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, DevHubActivityOptions options, CancellationToken cancellationToken);
    }
}