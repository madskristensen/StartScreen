using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StartScreen.Models.DevHub;

namespace StartScreen.ToolWindows.Controls
{
    public partial class DevHubPanel
    {
        private DateTime? _overviewBaseline;
        private string _workspaceActivitySignature;
        private bool _overviewSeenForVisit;
        private IReadOnlyList<DevHubPullRequest> _activityPullRequests = Array.Empty<DevHubPullRequest>();
        private IReadOnlyList<DevHubIssue> _activityIssues = Array.Empty<DevHubIssue>();
        private IReadOnlyList<DevHubCiRun> _activityBuilds = Array.Empty<DevHubCiRun>();

        private void BeginWorkspaceVisit(object sender, RoutedEventArgs e)
        {
            _overviewBaseline = Options.Instance.LastOverviewSeen;
            _workspaceActivitySignature = null;
            _overviewSeenForVisit = false;
            _copilotLoaded = true;
            if (Options.Instance.ApplyOverviewDefault())
                SaveOptionsAsync().FileAndForget(nameof(DevHubPanel));
            _defaultTabApplied = false;
            ApplyDefaultTab();
            StartCopilotActivity();
        }

        private void MarkOverviewAsSeen()
        {
            if (_overviewSeenForVisit)
                return;
            _overviewBaseline ??= Options.Instance.LastOverviewSeen;
            _overviewSeenForVisit = true;
            Options.Instance.LastOverviewSeen = DateTime.UtcNow;
            SaveOptionsAsync().FileAndForget(nameof(DevHubPanel));
        }

        private void SelectDefaultTab(DevHubDefaultTab tab)
        {
            DevHubSubTabs.SelectedItem = tab switch
            {
                DevHubDefaultTab.Issues => IssuesTab,
                DevHubDefaultTab.PullRequests => PrsTab,
                DevHubDefaultTab.Builds => CiTab,
                DevHubDefaultTab.NeedsYou => NeedsYouTab,
                _ => OverviewTab,
            };
        }

        private void UpdateWorkspaceActivity(IReadOnlyList<DevHubPullRequest> prs,
            IReadOnlyList<DevHubIssue> issues, IReadOnlyList<DevHubCiRun> builds)
        {
            _activityPullRequests = prs;
            _activityIssues = issues;
            _activityBuilds = builds;
            _overviewBaseline ??= Options.Instance.LastOverviewSeen;
            var sessions = GetVisibleCopilotSessions();
            var needsYou = DevHubWorkItem.GetCopilotItems(sessions, true).Concat(DevHubWorkItem.GetNeedsYou(prs)).ToArray();
            var ownPullRequests = DevHubWorkItem.GetOwnPullRequests(prs);
            var changes = DevHubWorkItem.GetCopilotItems(sessions, false)
                .Concat(DevHubWorkItem.GetChanges(prs, issues, builds, _overviewBaseline.Value)).ToArray();
            string signature = string.Join("\n", needsYou.Concat(ownPullRequests).Concat(changes)
                .Select(item => $"{item.CopilotSessionId}|{item.WebUrl}|{item.Title}|{item.Detail}|{item.StatusText}|{item.CopilotStatus}|{item.CopilotStatusDetails}|{item.UpdatedAt.Ticks}"));
            if (_workspaceActivitySignature != signature)
            {
                _workspaceActivitySignature = signature;
                InvalidateBorderCache();
                NeedsYouList.ItemsSource = needsYou;
                OwnPullRequestsList.ItemsSource = ownPullRequests;
                OwnPullRequestsSection.Visibility = ownPullRequests.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                ChangesList.ItemsSource = changes;
                NeedsYouEmpty.Visibility = needsYou.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                ChangesEmpty.Visibility = changes.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                NeedsYouTab.Header = needsYou.Length == 0 ? "Needs you" : $"Needs you {needsYou.Length}";
            }
            if (ReferenceEquals(DevHubSubTabs.SelectedItem, OverviewTab))
            {
                MarkOverviewAsSeen();
            }
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