using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub
{
    internal sealed class DevHubActivityResult<T>
    {
        internal T Value { get; set; }
        internal DevHubActivityStatus Status { get; set; }
        internal string Message { get; set; }
    }

    /// <summary>
    /// Collects failures across concurrent feeds and optional enrichment inside one provider operation.
    /// The async-local scope prevents simultaneous providers and scoped requests sharing outcomes.
    /// </summary>
    internal sealed class DevHubActivityRequest
    {
        private static readonly AsyncLocal<DevHubActivityRequest> _current = new AsyncLocal<DevHubActivityRequest>();
        private int _failed;
        private int _unauthorized;
        private readonly ConcurrentDictionary<string, byte> _messages = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        internal static bool HasFailure => _current.Value?._failed > 0;

        internal static void Warning(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                OutputLog.Info("Activity", message);
                _current.Value?._messages.TryAdd(message, 0);
            }
        }

        internal static void Failed(bool unauthorized = false)
        {
            var current = _current.Value;
            if (current == null)
                return;
            Interlocked.Increment(ref current._failed);
            if (unauthorized)
                Interlocked.Increment(ref current._unauthorized);
        }

        internal static HttpResponseMessage Observe(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                Failed(response.StatusCode == HttpStatusCode.Unauthorized);
                var host = response.RequestMessage?.RequestUri?.Host;
                Warning($"{host ?? "The activity service"} returned HTTP {(int)response.StatusCode}. Refresh to retry.");
            }
            return response;
        }

        internal static async Task<DevHubActivitySnapshot> FetchAsync(IDevHubProvider provider, DevHubActivityKind kind,
            DevHubActivityOptions options, RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            var activity = provider as IDevHubActivityProvider;
            switch (kind)
            {
                case DevHubActivityKind.Issues:
                    var issues = await RunAsync(() => activity != null ? activity.GetUserIssuesAsync(options, cancellationToken)
                        : provider.GetUserIssuesAsync(cancellationToken), items => items.Count > 0, cancellationToken);
                    return new DevHubActivitySnapshot { Status = issues.Status, Message = issues.Message, Issues = issues.Value?.ToList() ?? new List<DevHubIssue>() };
                case DevHubActivityKind.PullRequests:
                    var prs = await RunAsync(() => activity != null ? activity.GetUserPullRequestsAsync(options, cancellationToken)
                        : provider.GetUserPullRequestsAsync(cancellationToken), items => items.Count > 0, cancellationToken);
                    return new DevHubActivitySnapshot { Status = prs.Status, Message = prs.Message, PullRequests = prs.Value?.ToList() ?? new List<DevHubPullRequest>() };
                case DevHubActivityKind.CiRuns:
                    var builds = await RunAsync(() => activity != null ? activity.GetUserCiRunsAsync(options, cancellationToken)
                        : provider.GetUserCiRunsAsync(cancellationToken), items => items.Count > 0, cancellationToken);
                    return new DevHubActivitySnapshot { Status = builds.Status, Message = builds.Message, CiRuns = builds.Value?.ToList() ?? new List<DevHubCiRun>() };
                case DevHubActivityKind.Repository:
                    var result = await RunAsync(async () =>
                    {
                        var detail = activity != null ? await activity.GetRepoDetailAsync(repo, options, cancellationToken)
                            : await provider.GetRepoDetailAsync(repo, options.MaxItems, cancellationToken);
                        if (!string.IsNullOrEmpty(detail?.ActivityWarning) && !detail.HasCategoryOutcomes)
                            Failed();
                        return detail;
                    }, detail => detail.HasData, cancellationToken);
                    return new DevHubActivitySnapshot
                    {
                        Status = result.Value?.HasCategoryOutcomes == true && result.Status == DevHubActivityStatus.Success
                            ? AggregateStatus(result.Value.PullRequestsStatus, result.Value.IssuesStatus, result.Value.CiRunsStatus, result.Value.HasData)
                            : result.Status,
                        RepoIdentifier = repo,
                        HasCategoryOutcomes = result.Value?.HasCategoryOutcomes == true && result.Status == DevHubActivityStatus.Success,
                        PullRequestsStatus = result.Value?.PullRequestsStatus ?? result.Status,
                        IssuesStatus = result.Value?.IssuesStatus ?? result.Status,
                        CiRunsStatus = result.Value?.CiRunsStatus ?? result.Status,
                        FetchedAt = result.Value?.FetchedAt ?? default,
                        PullRequests = result.Value?.PullRequests ?? new List<DevHubPullRequest>(),
                        Issues = result.Value?.Issues ?? new List<DevHubIssue>(),
                        CiRuns = result.Value?.CiRuns ?? new List<DevHubCiRun>(),
                        Message = string.IsNullOrEmpty(result.Value?.ActivityWarning) ? result.Message : result.Value.ActivityWarning,
                    };
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        internal static DevHubActivityStatus AggregateStatus(DevHubActivityStatus prs, DevHubActivityStatus issues,
            DevHubActivityStatus builds, bool hasData)
        {
            var statuses = new[] { prs, issues, builds };
            return statuses.All(status => status == DevHubActivityStatus.Success) ? DevHubActivityStatus.Success
                : hasData || statuses.Any(status => status == DevHubActivityStatus.Success || status == DevHubActivityStatus.Partial) ? DevHubActivityStatus.Partial
                : statuses.Any(status => status == DevHubActivityStatus.Unauthorized) ? DevHubActivityStatus.Unauthorized : DevHubActivityStatus.Unavailable;
        }

        internal static async Task<List<T>> ReadCategoryAsync<T>(DevHubRepoDetail detail, DevHubActivityKind kind,
            Func<Task<List<T>>> fetch, CancellationToken cancellationToken)
        {
            var result = await RunAsync(fetch, items => items.Count > 0, cancellationToken);
            detail.HasCategoryOutcomes = true;
            if (!string.IsNullOrEmpty(result.Message))
                detail.ActivityWarning = string.Join(" ", new[] { detail.ActivityWarning, result.Message }.Where(message => !string.IsNullOrWhiteSpace(message)));
            switch (kind)
            {
                case DevHubActivityKind.PullRequests: detail.PullRequestsStatus = result.Status; break;
                case DevHubActivityKind.Issues: detail.IssuesStatus = result.Status; break;
                case DevHubActivityKind.CiRuns: detail.CiRunsStatus = result.Status; break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
            detail.Status = AggregateStatus(detail.PullRequestsStatus, detail.IssuesStatus, detail.CiRunsStatus, detail.HasData || result.Value?.Count > 0);
            return result.Value ?? new List<T>();
        }

        internal static async Task<DevHubActivityResult<T>> RunAsync<T>(Func<Task<T>> fetch,
            Func<T, bool> hasData, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = _current.Value;
            var request = new DevHubActivityRequest();
            _current.Value = request;
            T value = default;
            try
            {
                value = await fetch();
                cancellationToken.ThrowIfCancellationRequested();
                if (value == null)
                    Failed();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                Failed();
            }
            finally
            {
                _current.Value = previous;
            }

            return new DevHubActivityResult<T>
            {
                Value = value,
                Status = request._failed == 0 ? DevHubActivityStatus.Success
                    : value != null && hasData(value) ? DevHubActivityStatus.Partial
                    : request._unauthorized > 0 ? DevHubActivityStatus.Unauthorized : DevHubActivityStatus.Unavailable,
                Message = request._messages.Count == 0 ? null : string.Join(" ", request._messages.Keys.OrderBy(message => message, StringComparer.Ordinal)),
            };
        }
    }
}