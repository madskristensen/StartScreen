using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;
using StartScreen.Services.DevHub;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenViewModel
    {
        private bool _showSolutionsOnly;
        private string _updateAvailableText = "VS update available";
        private string _serviceHealthText = "Service operational";
        private string _serviceHealthDetails = "Assuming normal service while the initial background health check completes.";
        private bool _isServiceHealthy = true;
        private bool _hasServiceHealthStatus = true;
        private RemoteRepoIdentifier _repositoryScopeOverride;
        private readonly DevHubRepoSummaryService _repositorySummaries;
        private HashSet<MruItem> _realizedMruItems;

        internal void EnableMruViewportTracking()
        {
            _realizedMruItems = new HashSet<MruItem>();
        }

        internal void SetMruItemRealized(MruItem item, bool realized)
        {
            if (item == null || _realizedMruItems == null)
                return;
            if (realized)
                _realizedMruItems.Add(item);
            else
                _realizedMruItems.Remove(item);
        }
        private bool _isRefreshingWorkspaceSignals;
        private int _workspaceSignalsGeneration;
        private int _repositorySummaryGeneration;
        private readonly Func<CancellationToken, Task<ServiceHealthStatus>> _getServiceHealthStatusAsync;
        private readonly Func<JoinableTaskFactory> _getServiceHealthTaskFactory;
        private CancellationTokenSource _serviceHealthRefreshCancellation;
        private System.Windows.Threading.DispatcherTimer _serviceHealthRefreshTimer;
        private IReadOnlyList<DevHubPullRequest> _workspacePullRequests = Array.Empty<DevHubPullRequest>();
        private bool _workspaceActivityIncomplete;
        private IReadOnlyList<DevHubIssue> _workspaceIssues = Array.Empty<DevHubIssue>();
        private IReadOnlyList<WorkspaceVisitHistory.Snapshot> _workspaceVisits = Array.Empty<WorkspaceVisitHistory.Snapshot>();
        private bool _workspaceVisitsLoaded;
        private string _workspaceActivityContext;
        private DateTime _workspaceActivityFetchedAtUtc;
        private string _workspaceVisitError;

        internal void UpdateWorkspaceActivity(DevHubDashboard dashboard, DevHubDashboard scoped = null, bool cached = false)
        {
            var snapshots = new[] { scoped, dashboard }.Where(snapshot => snapshot?.HasAuthentication == true).ToArray();
            _workspacePullRequests = snapshots.SelectMany(snapshot => snapshot.PullRequests)
                .GroupBy(pr => pr.WebUrl, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            _workspaceIssues = snapshots.SelectMany(snapshot => snapshot.Issues)
                .GroupBy(issue => issue.WebUrl, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            _workspaceActivityContext = snapshots.Length == 0 ? null : string.Join(";", snapshots.SelectMany(snapshot => snapshot.Users)
                .Select(user => user.Host + ":" + user.IdentityId + ":" + user.Username).Distinct().OrderBy(identity => identity))
                + "|" + string.Join(";", snapshots.Select(snapshot => (snapshot.QueryContext ?? string.Empty)
                    + ":" + snapshot.ActivityLimit + ":" + snapshot.IncludesCompleted).Distinct().OrderBy(context => context));
            _workspaceActivityFetchedAtUtc = snapshots.Length == 0 ? default : snapshots.Min(snapshot => snapshot.FetchedAt);
            _workspaceActivityIncomplete = cached || snapshots.Any(snapshot => snapshot.IsRefreshing
                || snapshot.Status != DevHubActivityStatus.Success || snapshot.ActivityWarnings.Count > 0);
            if (_mruMetadataCacheLoaded)
                _mruMetadataCache.Apply(_allMruItems, _workspaceActivityContext, includeLocal: false);
            RefreshWorkspaceActivity(_allMruItems?.AsEnumerable() ?? Enumerable.Empty<MruItem>());
            if (_mruMetadataCacheLoaded && dashboard?.HasAuthentication == true && !dashboard.IsRefreshing)
                StartScreenPackage.BackgroundTaskFactory.RunAsync(() =>
                    RefreshRepositorySummariesAsync(VisibleMruItems.ToArray(), GetBackgroundCancellationToken()))
                    .FileAndForget(nameof(StartScreenViewModel));
        }

        private void RefreshWorkspaceActivity(IEnumerable<MruItem> items)
        {
            foreach (var item in items)
            {
                item.ApplyBranchActivity(WorkspaceActivity.Format(item, _workspacePullRequests, _workspaceActivityIncomplete));
                var previous = _workspaceVisits.FirstOrDefault(visit => string.Equals(visit.Path, item.Path?.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
                if (_workspaceVisitsLoaded || _workspaceVisitError != null)
                    item.ApplyVisitChanges(_workspaceVisitError ?? (previous == null ? null : WorkspaceVisitHistory.Compare(previous,
                        WorkspaceVisitHistory.Capture(item, _workspacePullRequests, _workspaceIssues, _workspaceActivityContext, DateTime.UtcNow),
                        _workspaceActivityIncomplete)));
            }
        }

        private async Task LoadWorkspaceVisitsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var visits = await WorkspaceVisitHistory.ReadAsync(WorkspaceVisitHistory.DefaultFile, cancellationToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                _workspaceVisits = visits;
                _workspaceVisitsLoaded = true;
                _workspaceVisitError = null;
                RefreshWorkspaceActivity(_allMruItems?.AsEnumerable() ?? Enumerable.Empty<MruItem>());
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                _workspaceVisitError = "Workspace visit history unavailable. See the Start Screen Output pane.";
                OutputLog.Info("Workspace", _workspaceVisitError);
                RefreshWorkspaceActivity(_allMruItems?.AsEnumerable() ?? Enumerable.Empty<MruItem>());
            }
        }

        internal async Task RecordWorkspaceVisitAsync(string path)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                var item = _allMruItems?.FirstOrDefault(candidate => string.Equals(candidate.Path?.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                    path?.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
                if (_isDisposed || item == null || !item.HasGitBranch || item.IsDetachedHead || !item.HasRemoteUrl
                    || string.IsNullOrEmpty(_workspaceActivityContext) || _workspaceActivityIncomplete
                    || _workspaceActivityFetchedAtUtc < DateTime.UtcNow.AddMinutes(-15))
                    return;
                var snapshot = WorkspaceVisitHistory.Capture(item, _workspacePullRequests, _workspaceIssues, _workspaceActivityContext, DateTime.UtcNow);
                await WorkspaceVisitHistory.SaveAsync(WorkspaceVisitHistory.DefaultFile, snapshot, StartScreenPackage.ShutdownToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(StartScreenPackage.ShutdownToken);
                if (!_isDisposed)
                {
                    _workspaceVisits = _workspaceVisits.Where(visit => !string.Equals(visit.Path, snapshot.Path, StringComparison.OrdinalIgnoreCase))
                        .Append(snapshot).ToArray();
                    _workspaceVisitError = null;
                    RefreshWorkspaceActivity([item]);
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                OutputLog.Info("Workspace", "Could not save the workspace visit checkpoint. The previous checkpoint is retained.");
            }
        }

        public string WorkSectionTitle => _repositoryScopeOverride?.Repo ?? SelectedMruItem?.Name ?? "Your work";

        public string SelectedWorkspaceMessage => string.Empty;

        public RemoteRepoIdentifier SelectedRepository => _repositoryScopeOverride ?? RemoteRepoIdentifier.TryParse(SelectedMruItem?.RemoteUrl);

        internal IReadOnlyList<string> GetCopilotWorkspaceRoots()
        {
            var repository = SelectedRepository;
            var items = SelectedMruItem != null ? new[] { SelectedMruItem }
                : _allMruItems.Where(item => repository != null && Equals(RemoteRepoIdentifier.TryParse(item.RemoteUrl), repository));
            return items.Where(item => !string.IsNullOrEmpty(item.Path))
                .Select(item => item.Type == MruItemType.Folder ? item.Path : System.IO.Path.GetDirectoryName(item.Path))
                .Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal void SetRepositoryScope(RemoteRepoIdentifier repo)
        {
            _repositoryScopeOverride = repo;
            OnPropertyChanged(nameof(WorkSectionTitle));
            OnPropertyChanged(nameof(HasSelectedMruItem));
            OnPropertyChanged(nameof(SelectedRepository));
        }

        public bool ShowSolutionsOnly
        {
            get => _showSolutionsOnly;
            set
            {
                if (_showSolutionsOnly != value)
                {
                    _showSolutionsOnly = value;
                    OnPropertyChanged();
                    RefreshMruCollections();
                }
            }
        }

        public string UpdateAvailableText
        {
            get => _updateAvailableText;
            private set
            {
                _updateAvailableText = value;
                OnPropertyChanged();
            }
        }

        public string ServiceHealthText => _serviceHealthText;

        public string ServiceHealthDetails => _serviceHealthDetails;

        public bool IsServiceHealthy => _isServiceHealthy;

        public bool HasServiceHealthStatus => _hasServiceHealthStatus;

        internal async Task RefreshWorkspaceSignalsAsync(bool force, CancellationToken cancellationToken)
        {
            if (GetBackgroundCancellationToken().IsCancellationRequested || _isRefreshingWorkspaceSignals)
                return;
            int generation = ++_workspaceSignalsGeneration;
            _isRefreshingWorkspaceSignals = true;
            WorkspaceRefreshState.IsLoading = true;
            OnPropertyChanged(nameof(IsRefreshingAny));
            NotifyRefreshStatusChanged();
            try
            {
                if (force)
                {
                    _repositorySummaryGeneration++;
                    _repositorySummaries.InvalidateAll();
                    _mruMetadataCache.InvalidateSummaries();
                    await RefreshMruAsync(cancellationToken, force: true);
                }
                await Task.WhenAll(RefreshServiceHealthAsync(cancellationToken),
                    RefreshRepositorySummariesAsync(VisibleMruItems.ToArray(), cancellationToken),
                    CheckForUpdateAsync(cancellationToken));
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (generation == _workspaceSignalsGeneration)
                {
                    _isRefreshingWorkspaceSignals = false;
                    WorkspaceRefreshState.IsLoading = false;
                    OnPropertyChanged(nameof(IsRefreshingAny));
                    NotifyRefreshStatusChanged();
                }
            }
        }

        internal async Task RefreshServiceHealthAsync(CancellationToken cancellationToken)
        {
            var taskFactory = _getServiceHealthTaskFactory();
            await taskFactory.SwitchToMainThreadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (GetBackgroundCancellationToken().IsCancellationRequested)
                return;
            CancellationTokenSource refreshCancellation =
                ReplaceRefreshCancellation(ref _serviceHealthRefreshCancellation, cancellationToken);
            _serviceHealthRefreshTimer?.Stop();
            WorkspaceRefreshState.IsLoading = true;
            OnPropertyChanged(nameof(IsRefreshingAny));
            NotifyRefreshStatusChanged();
            try
            {
                SetServiceHealth("Checking service status...", "Checking the public Visual Studio service status.", false, isChecking: true);
                var status = await _getServiceHealthStatusAsync(refreshCancellation.Token);
                await taskFactory.SwitchToMainThreadAsync(refreshCancellation.Token);
                refreshCancellation.Token.ThrowIfCancellationRequested();
                SetServiceHealth(status.StatusText, $"{status.Details}\nChecked {status.CheckedAtUtc.ToLocalTime():g}.",
                    status.State == ServiceHealthState.Healthy && !status.IsStale);
                SetRefreshState(WorkspaceRefreshState, status.StatusText, cached: status.IsStale,
                    stale: status.IsStale, unavailable: status.State == ServiceHealthState.Unknown,
                    successfulUtc: !status.IsStale && status.State != ServiceHealthState.Unknown ? status.CheckedAtUtc : null);
            }
            catch (OperationCanceledException ex) when (refreshCancellation.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await taskFactory.SwitchToMainThreadAsync();
                if (!refreshCancellation.IsCancellationRequested)
                {
                    SetServiceHealth("Service status unavailable", "Unable to refresh Visual Studio service status. Retrying automatically in five minutes. Open the status page for current information.", false);
                    SetRefreshState(WorkspaceRefreshState, ServiceHealthText, unavailable: true);
                }
            }
            finally
            {
                await taskFactory.SwitchToMainThreadAsync();
                CompleteRefresh(ref _serviceHealthRefreshCancellation, refreshCancellation, () =>
                {
                    WorkspaceRefreshState.IsLoading = false;
                    OnPropertyChanged(nameof(IsRefreshingAny));
                    NotifyRefreshStatusChanged();
                    if (cancellationToken.IsCancellationRequested || GetBackgroundCancellationToken().IsCancellationRequested)
                        return;

                    if (_serviceHealthRefreshTimer == null)
                    {
                        _serviceHealthRefreshTimer = new System.Windows.Threading.DispatcherTimer();
                        _serviceHealthRefreshTimer.Tick += ServiceHealthRefreshTimer_Tick;
                    }
                    _serviceHealthRefreshTimer.Interval = TimeSpan.FromMinutes(5);
                    _serviceHealthRefreshTimer.Start();
                });
            }
        }

        private void ServiceHealthRefreshTimer_Tick(object sender, EventArgs e)
        {
            _serviceHealthRefreshTimer.Stop();
            CancellationToken cancellationToken = GetBackgroundCancellationToken();
            if (cancellationToken.IsCancellationRequested)
                return;

            var taskFactory = _getServiceHealthTaskFactory();
            taskFactory.RunAsync(() => RefreshServiceHealthAsync(cancellationToken))
                .Task.Forget();
        }

        private async Task RefreshRepositorySummariesAsync(IEnumerable<MruItem> items, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (!Options.Instance.DevHubEnabled || !_mruMetadataCacheLoaded || _workspaceActivityContext == null)
                return;
            int generation = _repositorySummaryGeneration;
            string activityContext = _workspaceActivityContext;
            var groups = GetRepositorySummaryGroups(items.Where(item => _mruMetadataCache.IsSummaryStale(item, activityContext)));
            foreach (var group in groups.Where(group => group.Key == null))
            {
                foreach (var item in group)
                    item.ApplyRepositorySummary("Unsupported repository", "Activity is unavailable for this remote repository.");
            }
            await Task.WhenAll(groups.Where(group => group.Key != null).Select(async group =>
            {
                await TaskScheduler.Default;
                var summary = await _repositorySummaries.GetSummaryAsync(group.Key, cancellationToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (generation != _repositorySummaryGeneration || activityContext != _workspaceActivityContext)
                    return;
                string text = FormatRepositorySummary(summary);
                string details = summary.Status == DevHubRepoSummaryStatus.Ready
                    ? $"{group.Key.DisplayName}. Repository-wide open PRs; direct personal review requests only. Updated {summary.FetchedAt.ToLocalTime():g}. {summary.StatusMessage}"
                    : summary.StatusMessage;
                foreach (var item in group)
                {
                    if (Equals(RemoteRepoIdentifier.TryParse(item.RemoteUrl), group.Key))
                        item.ApplyRepositorySummary(text, details, summary.OpenPullRequests, summary.RequestedReviews);
                }
                if (summary.Status == DevHubRepoSummaryStatus.Ready)
                {
                    _mruMetadataCache.Update(group, local: false, activityContext);
                    await _mruMetadataCache.SaveAsync(_allMruItems.ToArray(), cancellationToken);
                }
            }));
        }

        internal IGrouping<RemoteRepoIdentifier, MruItem>[] GetRepositorySummaryGroups(IEnumerable<MruItem> items)
        {
            var visible = new HashSet<MruItem>(VisibleMruItems);
            return items.Where(item => visible.Contains(item) && item.HasRemoteUrl
                    && (_realizedMruItems == null || _realizedMruItems.Contains(item)
                        || item.IsPinned || ReferenceEquals(item, SelectedMruItem)))
                .OrderByDescending(item => ReferenceEquals(item, SelectedMruItem))
                .ThenByDescending(item => item.IsPinned)
                .GroupBy(item => RemoteRepoIdentifier.TryParse(item.RemoteUrl))
                .ToArray();
        }

        internal async Task RefreshSelectedRepositorySummaryAsync(MruItem item, CancellationToken cancellationToken)
        {
            if (item == null)
                return;
            try
            {
                await RefreshRepositorySummariesAsync([item], cancellationToken);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!cancellationToken.IsCancellationRequested)
                    item.ApplyRepositorySummary("Repository activity unavailable", "Refresh to try loading repository activity again.");
            }
        }

        internal static string FormatRepositorySummary(DevHubRepoSummary summary)
        {
            if (summary.Status != DevHubRepoSummaryStatus.Ready)
            {
                return summary.Status switch
                {
                    DevHubRepoSummaryStatus.Unauthorized => "Sign in to see repository activity",
                    DevHubRepoSummaryStatus.Unsupported => "Repository activity not supported",
                    _ => "Repository activity unavailable",
                };
            }
            string prs = summary.OpenPullRequests == 0 ? "No open PRs"
                : $"{summary.OpenPullRequests} open PR{(summary.OpenPullRequests == 1 ? "" : "s")}";
            return summary.RequestedReviews.HasValue
                ? $"{prs} / {summary.RequestedReviews} review{(summary.RequestedReviews == 1 ? "" : "s")} requested"
                : $"{prs} / Reviews unavailable";
        }

        internal void SetServiceHealth(string text, string details, bool healthy, bool isChecking = false)
        {
            if (isChecking && _hasServiceHealthStatus)
                return;

            bool textChanged = _serviceHealthText != text;
            bool detailsChanged = _serviceHealthDetails != details;
            bool healthyChanged = _isServiceHealthy != healthy;
            bool visibilityChanged = _hasServiceHealthStatus != !isChecking;
            _serviceHealthText = text;
            _serviceHealthDetails = details;
            _isServiceHealthy = healthy;
            _hasServiceHealthStatus = !isChecking;
            if (textChanged)
                OnPropertyChanged(nameof(ServiceHealthText));
            if (detailsChanged)
                OnPropertyChanged(nameof(ServiceHealthDetails));
            if (healthyChanged)
                OnPropertyChanged(nameof(IsServiceHealthy));
            if (visibilityChanged)
                OnPropertyChanged(nameof(HasServiceHealthStatus));
        }

        private void SelectedMruItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MruItem.RemoteUrl))
            {
                OnPropertyChanged(nameof(SelectedRepository));
            }
            else if (e.PropertyName == nameof(MruItem.Name))
            {
                OnPropertyChanged(nameof(WorkSectionTitle));
            }
        }

        internal static bool MatchesMruFilter(MruItem item, string query, bool solutionsOnly)
        {
            return item != null
                && (!solutionsOnly || item.IsPinned || item.Type == MruItemType.Solution)
                && (string.IsNullOrWhiteSpace(query)
                    || (item.Name?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (item.Path?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
        }
    }
}