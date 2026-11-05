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
        private string _serviceHealthText = "Service status not checked";
        private string _serviceHealthDetails = "Refresh to check Visual Studio service health.";
        private bool _isServiceHealthy;
        private bool _hasServiceHealthStatus;
        private RemoteRepoIdentifier _repositoryScopeOverride;
        private readonly DevHubRepoSummaryService _repositorySummaries;
        private bool _isRefreshingWorkspaceSignals;
        private int _workspaceSignalsGeneration;
        private int _repositorySummaryGeneration;
        private System.Windows.Threading.DispatcherTimer _serviceHealthExpiryTimer;

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
            if (_isRefreshingWorkspaceSignals)
                return;
            int generation = ++_workspaceSignalsGeneration;
            _isRefreshingWorkspaceSignals = true;
            OnPropertyChanged(nameof(IsRefreshingAny));
            OnPropertyChanged(nameof(UnifiedRefreshStatus));
            try
            {
                if (force)
                {
                    _repositorySummaryGeneration++;
                    _repositorySummaries.InvalidateAll();
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
                    OnPropertyChanged(nameof(IsRefreshingAny));
                    OnPropertyChanged(nameof(UnifiedRefreshStatus));
                }
            }
        }

        private async Task RefreshServiceHealthAsync(CancellationToken cancellationToken)
        {
            _serviceHealthExpiryTimer?.Stop();
            SetServiceHealth("Checking service status...", "Checking the public Visual Studio service status.", false, isChecking: true);
            var status = await ServiceHealthService.GetStatusAsync(cancellationToken);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            SetServiceHealth(status.StatusText, $"{status.Details}\nChecked {status.CheckedAtUtc.ToLocalTime():g}.",
                status.State == ServiceHealthState.Healthy && !status.IsStale);
            if (status.State != ServiceHealthState.Unknown && !status.IsStale)
            {
                if (_serviceHealthExpiryTimer == null)
                {
                    _serviceHealthExpiryTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMinutes(5),
                    };
                    _serviceHealthExpiryTimer.Tick += (sender, args) => ExpireServiceHealth();
                }
                _serviceHealthExpiryTimer.Start();
            }
        }

        internal void ExpireServiceHealth()
        {
            _serviceHealthExpiryTimer?.Stop();
            SetServiceHealth("Service status out of date", "Refresh to check the current public Visual Studio service status.", false);
        }

        private async Task RefreshRepositorySummariesAsync(IEnumerable<MruItem> items, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (!Options.Instance.DevHubEnabled)
                return;
            int generation = _repositorySummaryGeneration;
            var visible = new HashSet<MruItem>(VisibleMruItems);
            var groups = items.Where(item => visible.Contains(item) && item.HasRemoteUrl)
                .OrderByDescending(item => ReferenceEquals(item, SelectedMruItem))
                .ThenByDescending(item => item.IsPinned)
                .Take(20)
                .GroupBy(item => RemoteRepoIdentifier.TryParse(item.RemoteUrl))
                .ToArray();
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
                if (generation != _repositorySummaryGeneration)
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
            }));
        }

        internal async Task RefreshSelectedRepositorySummaryAsync(MruItem item, CancellationToken cancellationToken)
        {
            if (item == null)
                return;
            try
            {
                await RefreshRepositorySummariesAsync([item], cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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
            _serviceHealthText = text;
            _serviceHealthDetails = details;
            _isServiceHealthy = healthy;
            _hasServiceHealthStatus = !isChecking;
            OnPropertyChanged(nameof(ServiceHealthText));
            OnPropertyChanged(nameof(ServiceHealthDetails));
            OnPropertyChanged(nameof(IsServiceHealthy));
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