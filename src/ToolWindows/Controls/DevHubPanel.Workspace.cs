using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.ToolWindows.Controls
{
    public partial class DevHubPanel
    {
        private string _workspaceActivitySignature;
        private IReadOnlyList<DevHubWorkItem> _myWorkItems = Array.Empty<DevHubWorkItem>();
        private IReadOnlyList<DevHubWorkItem> _incomingItems = Array.Empty<DevHubWorkItem>();
        private IReadOnlyList<DevHubPullRequest> _activityPullRequests = Array.Empty<DevHubPullRequest>();
        private IReadOnlyList<DevHubIssue> _activityIssues = Array.Empty<DevHubIssue>();
        private IReadOnlyList<DevHubCiRun> _activityBuilds = Array.Empty<DevHubCiRun>();
        private int _activityLoadCount;
        private int? _loadMoreStartingItemCount;
        private bool _initialMyWorkTabPending = true;
        private DevHubDefaultTab _activeWorkView = DevHubDefaultTab.MyWork;

        internal DevHubActivityOptions ActivityOptions { get; private set; } = new DevHubActivityOptions();

        internal void SetActivityLoading(bool loading)
        {
            _activityLoadCount = Math.Max(0, _activityLoadCount + (loading ? 1 : -1));
            if (_activityLoadCount == 0 && _loadMoreStartingItemCount.HasValue)
            {
                ActivityLoadStatus.Text = (_currentDashboard?.ActivityLimit ?? 0) < ActivityOptions.MaxItems
                    ? "Could not load more. Refresh to retry."
                    : GetLoadedActivityCount() > _loadMoreStartingItemCount.Value ? "More activity loaded." : "No additional items found.";
                ActivityLoadStatus.Visibility = Visibility.Visible;
                _loadMoreStartingItemCount = null;
            }
            UpdateActivityLoadingState();
            if (!loading && _activityLoadCount == 0)
                SelectInitialMyWorkTab(loadingCompleted: true);
        }

        private int GetLoadedActivityCount() => _activityPullRequests.Count + _activityIssues.Count + _activityBuilds.Count;

        private void UpdateActivityLoadingState()
        {
            var loading = _activityLoadCount > 0 || _loadMoreStartingItemCount.HasValue;
            LoadMoreLink.IsEnabled = !loading;
            IncludeCompletedItems.IsEnabled = !loading;
            LoadMoreLabel.Text = loading ? (_loadMoreStartingItemCount.HasValue ? "Loading..." : "Updating...") : "Load more";
            LoadMoreText.Visibility = loading || Math.Max(ActivityOptions.MaxItems, _currentDashboard?.ActivityLimit ?? 0) < DevHubActivityOptions.MaximumLimit
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LoadMoreActivity_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var loadedLimit = Math.Max(ActivityOptions.MaxItems, _currentDashboard?.ActivityLimit ?? 0);
            if (_activityLoadCount > 0 || _loadMoreStartingItemCount.HasValue || loadedLimit >= DevHubActivityOptions.MaximumLimit)
                return;
            _loadMoreStartingItemCount = GetLoadedActivityCount();
            ActivityLoadStatus.Visibility = Visibility.Collapsed;
            ActivityOptions = new DevHubActivityOptions(Math.Min(DevHubActivityOptions.MaximumLimit,
                loadedLimit + DevHubActivityOptions.DefaultLimit), ActivityOptions.IncludeCompleted);
            UpdateActivityLoadingState();
            ActivityOptionsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void IncludeCompletedItems_Changed(object sender, RoutedEventArgs e)
        {
            if (CoverageText == null || MyWorkList == null || IncomingList == null)
                return;
            ActivityOptions = new DevHubActivityOptions(ActivityOptions.MaxItems, IncludeCompletedItems.IsChecked == true);
            ActivityLoadStatus.Visibility = Visibility.Collapsed;
            UpdateWorkspaceActivity(_activityPullRequests, _activityIssues, _activityBuilds);
            ActivityOptionsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateActivityCoverage(DevHubDashboard dashboard)
        {
            ActivityCoveragePanel.Visibility = Visibility.Visible;
            CoverageText.Text = $"Recent activity - up to {dashboard.ActivityLimit} per query";
            CoverageText.ToolTip = "Lists are bounded, not complete inventories. Load more expands each query up to 300 items. "
                + "Azure DevOps covers projects discovered from recent workspaces; repository scopes require explicit work-item links. "
                + "Permissions and provider availability can limit results. Recent completion history covers the last 14 days.";
            UpdateActivityLoadingState();
        }

        private void BeginWorkspaceVisit(object sender, RoutedEventArgs e)
        {
            _workspaceActivitySignature = null;
            _initialMyWorkTabPending = true;
            _copilotLoaded = true;
            if (Options.Instance.ApplyWorkViewsDefault())
                SaveOptionsAsync().FileAndForget(nameof(DevHubPanel));
            _defaultTabApplied = false;
            ApplyDefaultTab();
            SelectInitialMyWorkTab();
            StartCopilotActivity();
        }

        private void SelectDefaultTab(DevHubDefaultTab tab)
        {
            _activeWorkView = tab == DevHubDefaultTab.Incoming ? DevHubDefaultTab.Incoming : DevHubDefaultTab.MyWork;
        }

        private void UpdateWorkspaceActivity(IReadOnlyList<DevHubPullRequest> prs,
            IReadOnlyList<DevHubIssue> issues, IReadOnlyList<DevHubCiRun> builds)
        {
            _activityPullRequests = prs;
            _activityIssues = issues;
            _activityBuilds = builds;
            var sessions = GetVisibleCopilotSessions();
            var myWork = DevHubWorkItem.GetMyWork(prs, issues, sessions, Options.Instance.DevHubSortOrder,
                ActivityOptions.IncludeCompleted, builds);
            var incoming = DevHubWorkItem.GetIncoming(prs, issues, builds, Options.Instance.DevHubSortOrder);
            string signature = string.Join("\n", myWork.Concat(incoming)
                .Select(item => $"{item.Section}|{item.CopilotSessionId}|{item.WebUrl}|{item.Title}|{item.Detail}|{item.StatusText}|{item.DurationText}|{item.AttentionReason}|{item.NextActionText}|{item.AuthorText}|{item.IsAssignedToCurrentUser}|{item.StatusDetails}|{item.CopilotStatus}|{item.CopilotStatusDetails}|{item.UpdatedAt.Ticks}|{item.BranchDisplayText}|{string.Join(",", item.Labels.Select(label => label.Name + ":" + label.Color))}"));
            if (_workspaceActivitySignature != signature)
            {
                _workspaceActivitySignature = signature;
                _myWorkItems = myWork;
                _incomingItems = incoming;
                RenderWorkViews();
            }
            SelectInitialMyWorkTab();
        }

        private void SelectInitialMyWorkTab(bool loadingCompleted = false)
        {
            if (!_initialMyWorkTabPending || _activityLoadCount > 0
                || (_myWorkItems.Count == 0 && !loadingCompleted))
                return;

            _initialMyWorkTabPending = false;
            if (MyWorkAttentionFilter.IsChecked != true)
                return;

            RadioButton selected = GetInitialMyWorkKind(_myWorkItems) switch
            {
                DevHubWorkItemKind.Copilot => MyWorkCopilotFilter,
                DevHubWorkItemKind.PullRequest => MyWorkPrFilter,
                DevHubWorkItemKind.Issue => MyWorkIssueFilter,
                DevHubWorkItemKind.Build => MyWorkBuildFilter,
                _ => MyWorkAttentionFilter,
            };
            selected.IsChecked = true;
        }

        internal static DevHubWorkItemKind GetInitialMyWorkKind(IReadOnlyList<DevHubWorkItem> items)
        {
            if (items.Any(item => item.NeedsAttention))
                return DevHubWorkItemKind.All;

            return new[] { DevHubWorkItemKind.Copilot, DevHubWorkItemKind.PullRequest, DevHubWorkItemKind.Issue, DevHubWorkItemKind.Build }
                .FirstOrDefault(kind => items.Any(item => item.Kind == kind));
        }

        private void ActivityFilter_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is RadioButton filter && filter.GroupName == "MyWorkKind")
                _initialMyWorkTabPending = false;
        }

        private void ActivityFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (MyWorkList != null && IncomingList != null)
            {
                var group = (sender as RadioButton)?.GroupName;
                if (group == "MyWorkKind")
                    _initialMyWorkTabPending = false;
                RenderWorkViews(refreshMyWork: group != "IncomingKind", refreshIncoming: group != "MyWorkKind");
            }
        }

        private void RenderWorkViews(bool refreshMyWork = true, bool refreshIncoming = true)
        {
            InvalidateBorderCache();
            var myWorkKind = MyWorkPrFilter.IsChecked == true ? DevHubWorkItemKind.PullRequest
                : MyWorkIssueFilter.IsChecked == true ? DevHubWorkItemKind.Issue
                : MyWorkCopilotFilter.IsChecked == true ? DevHubWorkItemKind.Copilot
                : MyWorkBuildFilter.IsChecked == true ? DevHubWorkItemKind.Build : DevHubWorkItemKind.All;
            var incomingKind = IncomingPrFilter.IsChecked == true ? DevHubWorkItemKind.PullRequest
                : IncomingIssueFilter.IsChecked == true ? DevHubWorkItemKind.Issue
                : IncomingBuildFilter.IsChecked == true ? DevHubWorkItemKind.Build : DevHubWorkItemKind.All;
            var myWorkAttention = MyWorkAttentionFilter.IsChecked == true;
            var myWork = DevHubWorkItem.Filter(_myWorkItems, myWorkKind, myWorkAttention);
            var incoming = DevHubWorkItem.Filter(_incomingItems, incomingKind, false);
            MyWorkAttentionFilter.Content = $"Attention ({_myWorkItems.Count(item => item.NeedsAttention)})";
            if (refreshMyWork)
                MyWorkList.ItemsSource = CreateWorkItemView(myWork);
            if (refreshIncoming)
                IncomingList.ItemsSource = CreateWorkItemView(incoming);
            MyWorkEmpty.Visibility = myWork.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            IncomingEmpty.Visibility = incoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MyWorkEmpty.Text = myWorkAttention
                ? "No items need your attention in this view." : "No matching personal work in the loaded activity.";
            IncomingEmpty.Text = "No matching team activity.";
            MyWorkCount.Text = _myWorkItems.Count == 0 ? string.Empty : $" {_myWorkItems.Count}";
            IncomingCount.Text = _incomingItems.Count == 0 ? string.Empty : $" {_incomingItems.Count}";
        }

        internal static ListCollectionView CreateWorkItemView(IReadOnlyList<DevHubWorkItem> items)
        {
            return new ListCollectionView(items.ToArray());
        }

        private void WorkItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !(sender is FrameworkElement element)
                || !(element.DataContext is DevHubWorkItem item))
                return;

            if (item.CopilotSessionId != null)
                OpenCopilotSessionAsync(item.CopilotSessionId).FileAndForget(nameof(DevHubPanel));
            else if (item.IsPullRequest)
                StartScreenPackage.BackgroundTaskFactory.RunAsync(() => Services.PullRequestNavigationService.OpenAsync(item.WebUrl))
                    .FileAndForget(nameof(DevHubPanel));
            else
                OpenUrl(item.WebUrl);
        }

        private void OpenPullRequestInBrowser_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement element)
                OpenUrl(GetWebUrlFromDataContext(element.DataContext));
        }

        private void OpenPullRequestInVisualStudio_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement element)
            {
                var url = GetWebUrlFromDataContext(element.DataContext);
                StartScreenPackage.BackgroundTaskFactory.RunAsync(() => Services.PullRequestNavigationService.OpenAsync(url))
                    .FileAndForget(nameof(DevHubPanel));
            }
        }

    }
}