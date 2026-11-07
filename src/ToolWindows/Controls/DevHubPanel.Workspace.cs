using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
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
        private readonly DevHubActivityViewModel _activity = new();
        private DevHubActivityList _myWorkPresentation;
        private DevHubActivityList _incomingPresentation;
        private int _activityLoadCount;
        private bool _isApplyingMyWorkFilter;
        private DevHubDefaultTab _activeWorkView = DevHubDefaultTab.MyWork;

        internal DevHubActivityOptions ActivityOptions { get; } = new DevHubActivityOptions();

        internal void SetActivityLoading(bool loading)
        {
            _activityLoadCount = Math.Max(0, _activityLoadCount + (loading ? 1 : -1));
            if (_activityLoadCount == 0)
                _isConnectionLoading = false;
            UpdateBusyState();
            if (!loading && _activityLoadCount == 0)
                SelectInitialMyWorkTab();
        }

        private void InitializeActivityPresentation()
        {
            _myWorkPresentation = new DevHubActivityList(MyWorkList);
            _incomingPresentation = new DevHubActivityList(IncomingList);
            UpdateActivityPresentation();
            ((INotifyCollectionChanged)_activity.MyWorkItems).CollectionChanged += ActivityItemsChanged;
            ((INotifyCollectionChanged)_activity.IncomingItems).CollectionChanged += ActivityItemsChanged;
            MyWorkAttentionFilter.SetBinding(ContentControl.ContentProperty,
                new Binding(nameof(DevHubActivityViewModel.AttentionFilterText)) { Source = _activity });
            MyWorkCount.SetBinding(TextBlock.TextProperty,
                new Binding(nameof(DevHubActivityViewModel.MyWorkCountText)) { Source = _activity });
            IncomingCount.SetBinding(TextBlock.TextProperty,
                new Binding(nameof(DevHubActivityViewModel.IncomingCountText)) { Source = _activity });
            MyWorkEmpty.SetBinding(TextBlock.TextProperty,
                new Binding(nameof(DevHubActivityViewModel.MyWorkEmptyText)) { Source = _activity });
            IncomingEmpty.SetBinding(TextBlock.TextProperty,
                new Binding(nameof(DevHubActivityViewModel.IncomingEmptyText)) { Source = _activity });
            var visibilityConverter = new BooleanToVisibilityConverter();
            MyWorkEmpty.SetBinding(VisibilityProperty, new Binding(nameof(DevHubActivityViewModel.IsMyWorkEmpty))
                { Source = _activity, Converter = visibilityConverter });
            IncomingEmpty.SetBinding(VisibilityProperty, new Binding(nameof(DevHubActivityViewModel.IsIncomingEmpty))
                { Source = _activity, Converter = visibilityConverter });
        }

        private void ActivityItemsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            InvalidateBorderCache();
        }

        private void BeginWorkspaceVisit(object sender, RoutedEventArgs e)
        {
            _activity.BeginVisit();
            ApplyMyWorkTabSelection();
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
            _activity.UpdateRepositoryActivity(prs, issues, builds, GetVisibleCopilotSessions(),
                Options.Instance.DevHubSortOrder, ActivityOptions.IncludeCompleted);
            SelectInitialMyWorkTab();
            UpdateActivityPresentation();
        }

        private void RefreshWorkspaceActivity()
        {
            _activity.Refresh(GetVisibleCopilotSessions(), Options.Instance.DevHubSortOrder, ActivityOptions.IncludeCompleted);
            SelectInitialMyWorkTab();
            UpdateActivityPresentation();
        }

        private void SelectInitialMyWorkTab()
        {
            if (_activity.TrySelectInitialFilter())
                ApplyMyWorkTabSelection();
        }

        private void ApplyMyWorkTabSelection()
        {
            RadioButton selected = _activity.MyWorkKind switch
            {
                DevHubWorkItemKind.Copilot => MyWorkCopilotFilter,
                DevHubWorkItemKind.PullRequest => MyWorkPrFilter,
                DevHubWorkItemKind.Issue => MyWorkIssueFilter,
                DevHubWorkItemKind.Build => MyWorkBuildFilter,
                _ => MyWorkAttentionFilter,
            };
            _isApplyingMyWorkFilter = true;
            try
            {
                selected.IsChecked = true;
            }
            finally
            {
                _isApplyingMyWorkFilter = false;
            }
            UpdateActivityPresentation();
        }

        private void ActivityFilter_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is RadioButton filter && filter.GroupName == "MyWorkKind")
                _activity.CancelInitialFilterSelection();
        }

        private void ActivityFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (MyWorkList != null && IncomingList != null)
            {
                var group = (sender as RadioButton)?.GroupName;
                if (group == "MyWorkKind")
                {
                    if (_isApplyingMyWorkFilter)
                        return;
                    var kind = MyWorkPrFilter.IsChecked == true ? DevHubWorkItemKind.PullRequest
                        : MyWorkIssueFilter.IsChecked == true ? DevHubWorkItemKind.Issue
                        : MyWorkCopilotFilter.IsChecked == true ? DevHubWorkItemKind.Copilot
                        : MyWorkBuildFilter.IsChecked == true ? DevHubWorkItemKind.Build : DevHubWorkItemKind.All;
                    _activity.SetMyWorkFilter(kind, MyWorkAttentionFilter.IsChecked == true);
                }
                else if (group == "IncomingKind")
                {
                    var kind = IncomingPrFilter.IsChecked == true ? DevHubWorkItemKind.PullRequest
                        : IncomingIssueFilter.IsChecked == true ? DevHubWorkItemKind.Issue
                        : IncomingBuildFilter.IsChecked == true ? DevHubWorkItemKind.Build : DevHubWorkItemKind.All;
                    _activity.SetIncomingFilter(kind);
                }
                UpdateActivityPresentation();
            }
        }

        private void WorkItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !(sender is FrameworkElement element)
                || !(element.DataContext is DevHubWorkItem item))
                return;

            if (item.CopilotSessionId != null)
                OpenCopilotSessionAsync(item.CopilotSession, item.CopilotSession.Origin).FileAndForget(nameof(DevHubPanel));
            else if (item.IsPullRequest)
                StartScreenPackage.BackgroundTaskFactory.RunAsync(() => Services.PullRequestNavigationService.OpenAsync(item.WebUrl))
                    .FileAndForget(nameof(DevHubPanel));
            else
                OpenUrl(item.WebUrl);
        }

        private void UpdateActivityPresentation()
        {
            if (_myWorkPresentation == null || _incomingPresentation == null)
                return;
            var myWork = _myWorkPresentation.Show(_activity.MyWorkKind, _activity.MyWorkAttention, _activity.MyWorkItems);
            var incoming = _incomingPresentation.Show(_activity.IncomingKind, false, _activity.IncomingItems);
            if (!ReferenceEquals(MyWorkList, myWork) || !ReferenceEquals(IncomingList, incoming))
                InvalidateBorderCache();
            MyWorkList = myWork;
            IncomingList = incoming;
        }

        private void ClearInactiveActivityItems()
        {
            _myWorkPresentation?.ClearInactiveItems();
            _incomingPresentation?.ClearInactiveItems();
        }
    }

    internal sealed class DevHubActivityList
    {
        private readonly ItemsControl _original;
        private readonly Dictionary<(DevHubWorkItemKind Kind, bool Attention), ItemsControl> _lists = new();
        private ItemsControl _active;

        public DevHubActivityList(ItemsControl original)
        {
            _original = original;
        }

        internal void ClearInactiveItems()
        {
            foreach (var list in _lists.Values)
            {
                if (!ReferenceEquals(list, _active))
                    ((ObservableCollection<DevHubWorkItem>)list.ItemsSource).Clear();
            }
        }

        public ItemsControl Show(DevHubWorkItemKind kind, bool attention, IReadOnlyList<DevHubWorkItem> items)
        {
            var key = (kind, attention);
            if (!_lists.TryGetValue(key, out var list))
            {
                list = _lists.Count == 0 ? _original : new ItemsControl
                {
                    ItemTemplate = _original.ItemTemplate,
                    ItemsPanel = _original.ItemsPanel,
                    ItemContainerStyle = _original.ItemContainerStyle,
                    AlternationCount = _original.AlternationCount,
                    Tag = _original.Tag,
                    Margin = _original.Margin,
                    Focusable = _original.Focusable,
                    Visibility = Visibility.Collapsed,
                };
                list.ItemsSource = new ObservableCollection<DevHubWorkItem>();
                if (!ReferenceEquals(list, _original))
                {
                    var parent = (Panel)_original.Parent;
                    parent.Children.Insert(parent.Children.IndexOf(_original) + _lists.Count, list);
                }
                _lists.Add(key, list);
            }

            // Refresh even a previously hidden list before it becomes visible: its scope or data may have changed.
            DevHubActivityViewModel.SynchronizeItems((ObservableCollection<DevHubWorkItem>)list.ItemsSource, items);
            if (!ReferenceEquals(_active, list))
            {
                if (_active != null)
                    _active.Visibility = Visibility.Collapsed;
                list.Visibility = Visibility.Visible;
                _active = list;
            }
            return list;
        }
    }

    public sealed class ActivitySeparatorVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values[2] is string tag && tag == "Team")
                return Visibility.Visible;
            if (values[0] is int index && values[1] is int count)
                return index < count - 1 ? Visibility.Visible : Visibility.Collapsed;
            return DependencyProperty.UnsetValue;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}