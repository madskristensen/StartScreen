using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using StartScreen.Models.DevHub;

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

        private void BeginWorkspaceVisit(object sender, RoutedEventArgs e)
        {
            _workspaceActivitySignature = null;
            _copilotLoaded = true;
            if (Options.Instance.ApplyWorkViewsDefault())
                SaveOptionsAsync().FileAndForget(nameof(DevHubPanel));
            _defaultTabApplied = false;
            ApplyDefaultTab();
            StartCopilotActivity();
        }

        private void SelectDefaultTab(DevHubDefaultTab tab)
        {
            DevHubSubTabs.SelectedItem = tab == DevHubDefaultTab.Incoming ? IncomingTab : MyWorkTab;
        }

        private void UpdateWorkspaceActivity(IReadOnlyList<DevHubPullRequest> prs,
            IReadOnlyList<DevHubIssue> issues, IReadOnlyList<DevHubCiRun> builds)
        {
            _activityPullRequests = prs;
            _activityIssues = issues;
            _activityBuilds = builds;
            var sessions = GetVisibleCopilotSessions();
            var myWork = DevHubWorkItem.GetMyWork(prs, issues, sessions, Options.Instance.DevHubSortOrder);
            var incoming = DevHubWorkItem.GetIncoming(prs, issues, builds, Options.Instance.DevHubSortOrder);
            string signature = string.Join("\n", myWork.Concat(incoming)
                .Select(item => $"{item.Section}|{item.CopilotSessionId}|{item.WebUrl}|{item.Title}|{item.Detail}|{item.StatusText}|{item.AttentionReason}|{item.CopilotStatus}|{item.CopilotStatusDetails}|{item.UpdatedAt.Ticks}|{item.BranchDisplayText}|{string.Join(",", item.Labels.Select(label => label.Name + ":" + label.Color))}"));
            if (_workspaceActivitySignature != signature)
            {
                _workspaceActivitySignature = signature;
                _myWorkItems = myWork;
                _incomingItems = incoming;
                RenderWorkViews();
            }
        }

        private void ActivityFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (MyWorkList != null && IncomingList != null)
                RenderWorkViews();
        }

        private void RenderWorkViews()
        {
            InvalidateBorderCache();
            var myWorkKind = MyWorkPrFilter.IsChecked == true ? DevHubWorkItemKind.PullRequest
                : MyWorkIssueFilter.IsChecked == true ? DevHubWorkItemKind.Issue
                : MyWorkCopilotFilter.IsChecked == true ? DevHubWorkItemKind.Copilot : DevHubWorkItemKind.All;
            var incomingKind = IncomingPrFilter.IsChecked == true ? DevHubWorkItemKind.PullRequest
                : IncomingIssueFilter.IsChecked == true ? DevHubWorkItemKind.Issue
                : IncomingBuildFilter.IsChecked == true ? DevHubWorkItemKind.Build : DevHubWorkItemKind.All;
            var myWork = DevHubWorkItem.Filter(_myWorkItems, myWorkKind, false);
            var incoming = DevHubWorkItem.Filter(_incomingItems, incomingKind, false);
            MyWorkNeedsAttention.Content = $"Needs attention ({myWork.Count(item => item.NeedsAttention)})";
            IncomingNeedsAttention.Content = $"Needs attention ({incoming.Count(item => item.NeedsAttention)})";
            myWork = DevHubWorkItem.Filter(myWork, DevHubWorkItemKind.All, MyWorkNeedsAttention.IsChecked == true);
            incoming = DevHubWorkItem.Filter(incoming, DevHubWorkItemKind.All, IncomingNeedsAttention.IsChecked == true);
            MyWorkList.ItemsSource = CreateWorkItemView(myWork);
            IncomingList.ItemsSource = CreateWorkItemView(incoming);
            MyWorkEmpty.Visibility = myWork.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            IncomingEmpty.Visibility = incoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MyWorkEmpty.Text = MyWorkNeedsAttention.IsChecked == true
                ? "No items need your attention in this view." : "No matching personal work in the loaded activity.";
            IncomingEmpty.Text = IncomingNeedsAttention.IsChecked == true
                ? "No items need your attention in this view." : "No matching incoming activity.";
            MyWorkTab.Header = _myWorkItems.Count == 0 ? "My work" : $"My work {_myWorkItems.Count}";
            IncomingTab.Header = _incomingItems.Count == 0 ? "Incoming" : $"Incoming {_incomingItems.Count}";
        }

        internal static ListCollectionView CreateWorkItemView(IReadOnlyList<DevHubWorkItem> items)
        {
            var view = new ListCollectionView(items.ToArray());
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DevHubWorkItem.Section)));
            return view;
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