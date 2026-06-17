using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.ToolWindows.Controls
{
    public partial class DevHubPanel : UserControl
    {
        private DevHubDashboard _currentDashboard;
        private RemoteRepoIdentifier _currentFilterRepo;

        // Repo the user scoped to via the item context menu ("Scope to <repo>").
        // Intentionally not persisted, so the scope resets when the Start Screen is reopened.
        private RemoteRepoIdentifier _scopedRepo;
        private IReadOnlyList<DevHubPullRequest> _lastBoundPullRequests;
        private IReadOnlyList<DevHubIssue> _lastBoundIssues;
        private IReadOnlyList<DevHubCiRun> _lastBoundCiRuns;
        private List<Border> _cachedBorders;
        private ItemsControl _cachedBordersList;
        private bool _suppressSaveOnLostFocus;
        private bool _showGitHubCredentialHelp;
        private bool _suppressGitHubAccountSelectionChanged;
        private bool _defaultTabApplied;
        private bool _suppressSettingsComboChanged;

        // Host that the inline PAT entry box is currently targeting (cloud or on-prem).
        // Set when the user clicks "Sign in" / "Change" on a server row; consumed by AdoPatBox_KeyDown.
        private string _pendingAdoHost;

        public DevHubPanel()
        {
            InitializeComponent();
        }

        private void DevHubPanel_GotFocus(object sender, RoutedEventArgs e)
        {
            // When the UserControl itself gets focus (via Tab), delegate to first item
            if (e.OriginalSource == this)
            {
                FocusFirstItem();
            }
        }

        /// <summary>
        /// Raised when Left arrow is pressed to navigate back to MRU list.
        /// </summary>
        public event EventHandler FocusMruRequested;

        /// <summary>
        /// Raised when Right arrow is pressed to navigate to the News section.
        /// </summary>
        public event EventHandler FocusNewsRequested;

        /// <summary>
        /// Updates the panel to show a filtered view for the given repo, or global dashboard if null.
        /// </summary>
        public void UpdateView(DevHubDashboard dashboard, RemoteRepoIdentifier filterRepo = null)
        {
            // Skip the entire update when nothing has changed. RefreshAsync reports
            // progress several times (after auth, then after each of issues / PRs / CI),
            // and re-binding three ItemsControls each time causes a visible UI hitch
            // even though most of the data is unchanged between calls.
            // A context-menu scope takes precedence over any externally supplied filter.
            RemoteRepoIdentifier effectiveFilter = _scopedRepo ?? filterRepo;

            bool dashboardChanged = !ReferenceEquals(_currentDashboard, dashboard);
            bool filterChanged = !Equals(_currentFilterRepo, effectiveFilter);

            _currentDashboard = dashboard;
            _currentFilterRepo = effectiveFilter;

            if (dashboard == null)
            {
                if (dashboardChanged || filterChanged)
                {
                    ShowNotConnected(hasGitHub: false, hasAdo: false);
                }
                return;
            }

            bool hasGitHub = dashboard.HasProvider("github.com");
            bool hasAdo = HasAnyAdoConnection(dashboard);

            if (!dashboard.HasAuthentication)
            {
                ShowNotConnected(hasGitHub, hasAdo);
                return;
            }

            IReadOnlyList<DevHubPullRequest> prs;
            IReadOnlyList<DevHubIssue> issues;
            IReadOnlyList<DevHubCiRun> ciRuns;

            if (effectiveFilter != null)
            {
                var detail = dashboard.FilterByRepo(effectiveFilter);
                prs = detail.PullRequests;
                issues = detail.Issues;
                ciRuns = detail.CiRuns;
            }
            else
            {
                prs = dashboard.PullRequests;
                issues = dashboard.Issues;
                ciRuns = dashboard.CiRuns;
            }

            // Only re-bind sections whose data changed. Each Update* method
            // resets ItemsSource and forces WPF to rebuild the visual tree, which is
            // the primary cause of the UI thread hitch when DevHub data arrives.
            if (filterChanged || !DevHubItemComparer.SamePullRequests(_lastBoundPullRequests, prs))
            {
                IReadOnlyList<DevHubPullRequest> previousPrs = _lastBoundPullRequests;
                _lastBoundPullRequests = Snapshot(prs);
                UpdatePullRequests(prs, previousPrs);
            }
            if (filterChanged || !DevHubItemComparer.SameIssues(_lastBoundIssues, issues))
            {
                IReadOnlyList<DevHubIssue> previousIssues = _lastBoundIssues;
                _lastBoundIssues = Snapshot(issues);
                UpdateIssues(issues, previousIssues);
            }
            if (filterChanged || !DevHubItemComparer.SameCiRuns(_lastBoundCiRuns, ciRuns))
            {
                IReadOnlyList<DevHubCiRun> previousCi = _lastBoundCiRuns;
                _lastBoundCiRuns = Snapshot(ciRuns);
                UpdateCiRuns(ciRuns, previousCi);
            }

            UpdateSettingsAccountStatus(hasGitHub, hasAdo);
            UpdateLastRefresh(dashboard.FetchedAt);
            ShowDashboard();
        }

        private static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T> items)
        {
            return items?.ToList();
        }

        /// <summary>
        /// Shows the loading state with an optional message.
        /// </summary>
        public void ShowLoading(string message = null)
        {
            LoadingText.Text = message ?? "Loading your activity...";
            NotConnectedPanel.Visibility = Visibility.Collapsed;
            DashboardPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Shows an error state with a message.
        /// </summary>
        public void ShowError(string message)
        {
            NotConnectedPanel.Visibility = Visibility.Collapsed;
            DashboardPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorMessageText.Text = message;
        }

        private void ShowNotConnected(bool hasGitHub, bool hasAdo)
        {
            DashboardPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            NotConnectedPanel.Visibility = Visibility.Visible;

            ConnectGitHubTextBlock.Visibility = hasGitHub ? Visibility.Collapsed : Visibility.Visible;
            GitHubCredentialHelpTextBlock.Visibility = _showGitHubCredentialHelp && !hasGitHub ? Visibility.Visible : Visibility.Collapsed;
            ConnectAdoTextBlock.Visibility = hasAdo ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateSettingsAccountStatus(bool hasGitHub, bool hasAdo)
        {
            ConnectedGitHubStatus.Visibility = hasGitHub ? Visibility.Visible : Visibility.Collapsed;
            SettingsConnectGitHubTextBlock.Visibility = hasGitHub ? Visibility.Collapsed : Visibility.Visible;
            GitHubConnectedStatusText.Text = hasGitHub
                ? $"GitHub: {GetConnectedGitHubAccountDisplayText()}"
                : "GitHub: connected";
            if (hasGitHub)
            {
                GitHubCredentialSettingsHelpTextBlock.Visibility = Visibility.Collapsed;
                GitHubPatEntryPanel.Visibility = Visibility.Collapsed;
                RefreshGitHubAccountsAsync().FireAndForget();
            }
            else
            {
                GitHubAccountSelectionPanel.Visibility = Visibility.Collapsed;
            }

            // Azure DevOps state is reflected by the AdoServersList; nothing else to do here.
            // hasAdo is preserved as a parameter so callers can decide initial visibility of the
            // top-level not-connected prompts via ShowNotConnected.
            _ = hasAdo;
        }

        public void ShowGitHubCredentialManagerUnavailable()
        {
            _showGitHubCredentialHelp = true;

            var hasGitHub = _currentDashboard?.HasProvider("github.com") == true;
            var hasAdo = HasAnyAdoConnection(_currentDashboard) || HasAnyAdoCredential();

            GitHubCredentialSettingsHelpTextBlock.Visibility = Visibility.Visible;

            if (_currentDashboard?.HasAuthentication == true)
            {
                UpdateView(_currentDashboard, _currentFilterRepo);
                SettingsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                ShowNotConnected(hasGitHub, hasAdo);
            }
        }

        private void ShowDashboard()
        {
            NotConnectedPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            DashboardPanel.Visibility = Visibility.Visible;

            ApplyDefaultTab();
        }

        // Selects the user's preferred default sub-tab the first time the dashboard
        // is shown. Subsequent refreshes leave the user's current tab untouched.
        private void ApplyDefaultTab()
        {
            if (_defaultTabApplied)
            {
                return;
            }

            _defaultTabApplied = true;

            int index = (int)Options.Instance.DevHubDefaultTab;
            if (index >= 0 && index < DevHubSubTabs.Items.Count)
            {
                DevHubSubTabs.SelectedIndex = index;
            }
        }

        private void UpdatePullRequests(IReadOnlyList<DevHubPullRequest> pullRequests, IReadOnlyList<DevHubPullRequest> previousPullRequests)
        {
            InvalidateBorderCache();
            if (pullRequests != null && pullRequests.Count > 0)
            {
                DevHubNewFlagCalculator.Apply(
                    pullRequests,
                    previousPullRequests,
                    Options.Instance.LastDevHubPrsSeen,
                    item => item.UpdatedAt,
                    item => item.WebUrl,
                    item => item.IsNew,
                    (item, isNew) => item.IsNew = isNew);
                PullRequestsList.ItemsSource = pullRequests;
                NoPrsText.Visibility = Visibility.Collapsed;
            }
            else
            {
                PullRequestsList.ItemsSource = null;
                NoPrsText.Visibility = Visibility.Visible;
            }
            UpdatePrNewIndicator();
        }

        private void UpdateIssues(IReadOnlyList<DevHubIssue> issues, IReadOnlyList<DevHubIssue> previousIssues)
        {
            InvalidateBorderCache();
            if (issues != null && issues.Count > 0)
            {
                DevHubNewFlagCalculator.Apply(
                    issues,
                    previousIssues,
                    Options.Instance.LastDevHubIssuesSeen,
                    item => item.UpdatedAt,
                    item => item.WebUrl,
                    item => item.IsNew,
                    (item, isNew) => item.IsNew = isNew);
                IssuesList.ItemsSource = issues;
                NoIssuesText.Visibility = Visibility.Collapsed;
            }
            else
            {
                IssuesList.ItemsSource = null;
                NoIssuesText.Visibility = Visibility.Visible;
            }
            UpdateIssuesNewIndicator();
        }

        private void UpdateCiRuns(IReadOnlyList<DevHubCiRun> ciRuns, IReadOnlyList<DevHubCiRun> previousCiRuns)
        {
            InvalidateBorderCache();
            if (ciRuns != null && ciRuns.Count > 0)
            {
                DevHubNewFlagCalculator.Apply(
                    ciRuns,
                    previousCiRuns,
                    Options.Instance.LastDevHubCiSeen,
                    item => item.Timestamp,
                    item => item.WebUrl,
                    item => item.IsNew,
                    (item, isNew) => item.IsNew = isNew);
                CiRunsList.ItemsSource = ciRuns;
                NoCiText.Visibility = Visibility.Collapsed;
            }
            else
            {
                CiRunsList.ItemsSource = null;
                NoCiText.Visibility = Visibility.Visible;
            }
            UpdateCiNewIndicator();
        }

        private void UpdateIssuesNewIndicator()
        {
            bool hasNew = IssuesTab != null
                && !IssuesTab.IsSelected
                && _lastBoundIssues != null
                && _lastBoundIssues.Any(i => i.IsNew);
            IssueNewDot.Visibility = hasNew ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdatePrNewIndicator()
        {
            bool hasNew = PrsTab != null
                && !PrsTab.IsSelected
                && _lastBoundPullRequests != null
                && _lastBoundPullRequests.Any(p => p.IsNew);
            PrNewDot.Visibility = hasNew ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateCiNewIndicator()
        {
            bool hasNew = CiTab != null
                && !CiTab.IsSelected
                && _lastBoundCiRuns != null
                && _lastBoundCiRuns.Any(c => c.IsNew);
            CiNewDot.Visibility = hasNew ? Visibility.Visible : Visibility.Collapsed;

            if (hasNew)
            {
                bool hasNewFailure = _lastBoundCiRuns.Any(c => c.IsNew && string.Equals(c.Status, "failure", StringComparison.OrdinalIgnoreCase));
                CiNewDot.Fill = hasNewFailure ? _ciDotFailureBrush : _ciDotDefaultBrush;
                CiNewDot.ToolTip = hasNewFailure
                    ? "A build has failed since you last viewed this tab"
                    : "New activity since you last viewed this tab";
            }
        }

        private static readonly System.Windows.Media.SolidColorBrush _ciDotDefaultBrush = CreateFrozenBrush(0xFF, 0x4C, 0xAF, 0x50);
        private static readonly System.Windows.Media.SolidColorBrush _ciDotFailureBrush = CreateFrozenBrush(0xFF, 0xE5, 0x39, 0x35);

        private static System.Windows.Media.SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
        {
            System.Windows.Media.SolidColorBrush brush = new(System.Windows.Media.Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private void DevHubSubTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Only react to selection changes that originated from the TabControl itself,
            // not from inner ListBox / ItemsControl bubbling up.
            if (!ReferenceEquals(e.OriginalSource, DevHubSubTabs))
            {
                return;
            }

            MarkSelectedTabAsSeen();
        }

        private void MarkSelectedTabAsSeen()
        {
            object selected = DevHubSubTabs?.SelectedItem;
            DateTime now = DateTime.UtcNow;

            if (ReferenceEquals(selected, IssuesTab))
            {
                Options.Instance.LastDevHubIssuesSeen = now;
                ClearIsNewFlags(_lastBoundIssues, item => item.IsNew = false);
                RefreshItemsControl(IssuesList);
                UpdateIssuesNewIndicator();
            }
            else if (ReferenceEquals(selected, PrsTab))
            {
                Options.Instance.LastDevHubPrsSeen = now;
                ClearIsNewFlags(_lastBoundPullRequests, item => item.IsNew = false);
                RefreshItemsControl(PullRequestsList);
                UpdatePrNewIndicator();
            }
            else if (ReferenceEquals(selected, CiTab))
            {
                Options.Instance.LastDevHubCiSeen = now;
                ClearIsNewFlags(_lastBoundCiRuns, item => item.IsNew = false);
                RefreshItemsControl(CiRunsList);
                UpdateCiNewIndicator();
            }
            else
            {
                return;
            }

            _ = SaveOptionsAsync();
        }

        private static void ClearIsNewFlags<T>(IReadOnlyList<T> items, Action<T> clear)
        {
            if (items == null)
            {
                return;
            }

            foreach (T item in items)
            {
                clear(item);
            }
        }

        private static void RefreshItemsControl(ItemsControl list)
        {
            // Models do not raise INotifyPropertyChanged for IsNew, so re-bind to
            // force the data templates to re-evaluate the visibility of the NEW badge.
            System.Collections.IEnumerable current = list?.ItemsSource;
            if (current == null)
            {
                return;
            }

            list.ItemsSource = null;
            list.ItemsSource = current;
        }

        private static async Task SaveOptionsAsync()
        {
            try
            {
                await Options.Instance.SaveAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void UpdateLastRefresh(DateTime fetchedAt)
        {
            string text;
            if (fetchedAt != default)
            {
                var ago = DateTime.UtcNow - fetchedAt;
                if (ago.TotalMinutes < 1)
                    text = "Updated just now";
                else if (ago.TotalMinutes < 60)
                    text = $"Updated {(int)ago.TotalMinutes}m ago";
                else
                    text = $"Updated {(int)ago.TotalHours}h ago";
            }
            else
            {
                text = "";
            }

            LastRefreshChanged?.Invoke(this, text);
        }

        private void PrItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left &&
                sender is FrameworkElement fe &&
                fe.DataContext is DevHubPullRequest pr &&
                !string.IsNullOrEmpty(pr.WebUrl))
            {
                OpenUrl(pr.WebUrl);
            }
        }

        private void IssueItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left &&
                sender is FrameworkElement fe &&
                fe.DataContext is DevHubIssue issue &&
                !string.IsNullOrEmpty(issue.WebUrl))
            {
                OpenUrl(issue.WebUrl);
            }
        }

        private void CiItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left &&
                sender is FrameworkElement fe &&
                fe.DataContext is DevHubCiRun ci &&
                !string.IsNullOrEmpty(ci.WebUrl))
            {
                OpenUrl(ci.WebUrl);
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ClearFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_scopedRepo == null)
            {
                return;
            }

            _scopedRepo = null;

            if (_currentDashboard != null)
            {
                UpdateView(_currentDashboard);
            }
        }

        private void ScopeToRepoMenuItem_Click(object sender, RoutedEventArgs e)
        {
            RemoteRepoIdentifier repo = GetRepoIdentifierFromDataContext(GetContextMenuItemDataContext(sender));
            if (repo == null)
            {
                return;
            }

            _scopedRepo = repo;

            if (_currentDashboard != null)
            {
                UpdateView(_currentDashboard);
            }
        }

        // Shows "Scope to <repo>" when unscoped and "Show all repositories" when scoped.
        private void DevHubContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu && menu.Items.Count >= 6
                && menu.Items[4] is MenuItem scopeTo
                && menu.Items[5] is MenuItem showAll)
            {
                bool scoped = _scopedRepo != null;
                scopeTo.Visibility = scoped ? Visibility.Collapsed : Visibility.Visible;
                showAll.Visibility = scoped ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // Themes each item's context menu as soon as the item is realized, so a plain
        // right-click shows the VS-themed menu even before any keyboard navigation.
        private void DevHubItemBorder_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border border)
            {
                ApplyContextMenuTheme(border);
            }
        }

        private static RemoteRepoIdentifier GetRepoIdentifierFromDataContext(object dataContext)
        {
            switch (dataContext)
            {
                case DevHubPullRequest pr: return pr.RepoIdentifier;
                case DevHubIssue issue: return issue.RepoIdentifier;
                case DevHubCiRun ci: return ci.RepoIdentifier;
                default: return null;
            }
        }

        public void ToggleSettings()
        {
            Settings_Click(this, null);
        }

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsPanel.Visibility == Visibility.Visible)
            {
                SettingsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                OpenSettingsPanel();
            }
        }

        private void OpenSettingsPanel()
        {
            SearchQueryTextBox.Text = Options.Instance.DevHubSearchQuery ?? "";
            SearchQueryPlaceholder.Visibility = string.IsNullOrEmpty(SearchQueryTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            bool hasGitHub = _currentDashboard?.HasProvider("github.com") == true;
            bool hasAdo = HasAnyAdoConnection(_currentDashboard) || HasAnyAdoCredential();
            UpdateSettingsAccountStatus(hasGitHub, hasAdo);
            AdoPatBox.Clear();
            AdoPatEntryPanel.Visibility = Visibility.Collapsed;
            AddAdoServerPanel.Visibility = Visibility.Collapsed;
            AddAdoServerLink.Visibility = Visibility.Visible;
            _pendingAdoHost = null;
            RefreshAdoServersList();

            PopulateDisplaySettings();

            SettingsPanel.Visibility = Visibility.Visible;
        }

        private void PopulateDisplaySettings()
        {
            _suppressSettingsComboChanged = true;
            try
            {
                DefaultTabComboBox.SelectedIndex = (int)Options.Instance.DevHubDefaultTab;
                SortOrderComboBox.SelectedIndex = (int)Options.Instance.DevHubSortOrder;
            }
            finally
            {
                _suppressSettingsComboChanged = false;
            }
        }

        private void DefaultTabComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressSettingsComboChanged)
            {
                return;
            }

            var selected = (DevHubDefaultTab)DefaultTabComboBox.SelectedIndex;
            if (selected == Options.Instance.DevHubDefaultTab)
            {
                return;
            }

            Options.Instance.DevHubDefaultTab = selected;
            Options.Instance.SaveAsync().FireAndForget();

            // Reflect the new default immediately so the user sees the effect.
            int index = (int)selected;
            if (index >= 0 && index < DevHubSubTabs.Items.Count)
            {
                DevHubSubTabs.SelectedIndex = index;
            }
        }

        private void SortOrderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressSettingsComboChanged)
            {
                return;
            }

            var selected = (DevHubSortOrder)SortOrderComboBox.SelectedIndex;
            if (selected == Options.Instance.DevHubSortOrder)
            {
                return;
            }

            Options.Instance.DevHubSortOrder = selected;
            Options.Instance.SaveAsync().FireAndForget();

            // Re-fetch so the lists come back ordered by the new preference.
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        private void SearchQueryTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressSaveOnLostFocus)
            {
                _suppressSaveOnLostFocus = false;
                return;
            }

            SaveSearchQuery();
        }

        private void SearchQueryTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchQueryPlaceholder.Visibility = string.IsNullOrEmpty(SearchQueryTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SearchQueryTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SaveSearchQuery();
                SettingsPanel.Visibility = Visibility.Collapsed;
                RefreshRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _suppressSaveOnLostFocus = true;
                SearchQueryTextBox.Text = Options.Instance.DevHubSearchQuery ?? "";
                SettingsPanel.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
        }

        private void SaveSearchQuery()
        {
            var newQuery = SearchQueryTextBox.Text?.Trim() ?? "";
            if (newQuery != (Options.Instance.DevHubSearchQuery ?? ""))
            {
                Options.Instance.DevHubSearchQuery = newQuery;
                Options.Instance.SaveAsync().FireAndForget();
            }
        }

        private string GetConnectedGitHubAccountDisplayText()
        {
            if (_currentDashboard?.Users != null)
            {
                for (int i = 0; i < _currentDashboard.Users.Count; i++)
                {
                    var user = _currentDashboard.Users[i];
                    if (string.Equals(user?.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(user.Username))
                    {
                        return user.Username;
                    }
                }
            }

            return "connected";
        }

        private async Task RefreshGitHubAccountsAsync()
        {
            try
            {
                var accounts = await Services.DevHub.DevHubCredentialHelper.GetGitHubAccountsAsync(System.Threading.CancellationToken.None);
                var preferred = Options.Instance.DevHubGitHubAccount?.Trim() ?? string.Empty;

                _suppressGitHubAccountSelectionChanged = true;
                GitHubAccountsComboBox.ItemsSource = accounts;

                if (accounts.Count > 0)
                {
                    var selected = accounts.FirstOrDefault(account =>
                        string.Equals(account, preferred, StringComparison.OrdinalIgnoreCase));

                    if (selected == null)
                    {
                        var connected = GetConnectedGitHubAccountDisplayText();
                        selected = accounts.FirstOrDefault(account =>
                            string.Equals(account, connected, StringComparison.OrdinalIgnoreCase));
                    }

                    if (selected != null)
                    {
                        GitHubAccountsComboBox.SelectedItem = selected;
                    }
                    else
                    {
                        GitHubAccountsComboBox.SelectedItem = null;
                    }

                    GitHubAccountSelectionPanel.Visibility = Visibility.Visible;
                }
                else
                {
                    GitHubAccountsComboBox.SelectedItem = null;
                    GitHubAccountSelectionPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                GitHubAccountSelectionPanel.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _suppressGitHubAccountSelectionChanged = false;
            }
        }

        private void GitHubAccountsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressGitHubAccountSelectionChanged)
                return;

            var selectedAccount = GitHubAccountsComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selectedAccount))
                return;

            var current = Options.Instance.DevHubGitHubAccount ?? string.Empty;
            if (string.Equals(current, selectedAccount, StringComparison.OrdinalIgnoreCase))
                return;

            Options.Instance.DevHubGitHubAccount = selectedAccount;
            Options.Instance.SaveAsync().FireAndForget();
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        private async void GitHubSignOut_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedAccount = GitHubAccountsComboBox.SelectedItem as string;
                if (!string.IsNullOrWhiteSpace(selectedAccount))
                {
                    await Services.DevHub.DevHubCredentialHelper.RemoveGitHubAccountAsync(
                        selectedAccount, System.Threading.CancellationToken.None);
                }

                Services.DevHub.DevHubCredentialHelper.RemoveCredential("github.com");
                Services.DevHub.DevHubCredentialHelper.ClearCachedCredentials();
                Options.Instance.DevHubGitHubAccount = string.Empty;
                Options.Instance.SaveAsync().FireAndForget();
                await RefreshGitHubAccountsAsync();
                RefreshRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void ConnectGitHub_Click(object sender, RoutedEventArgs e)
        {
            ConnectAccountRequested?.Invoke(this, "github.com");
        }

        private void ShowGitHubPatEntry_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Visible;
            GitHubPatEntryPanel.Visibility = Visibility.Visible;
            GitHubCredentialSettingsHelpTextBlock.Visibility = Visibility.Collapsed;
            GitHubPatBox.Clear();
            GitHubPatBox.Focus();
        }

        private void GitHubPatBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var pat = GitHubPatBox.Password?.Trim();
                if (!string.IsNullOrEmpty(pat))
                {
                    var selectedAccount = GitHubAccountsComboBox.SelectedItem as string;
                    Services.DevHub.DevHubCredentialHelper.StoreCredential("github.com", selectedAccount ?? string.Empty, pat);
                    Options.Instance.DevHubGitHubAccount = selectedAccount ?? string.Empty;
                    Options.Instance.SaveAsync().FireAndForget();
                    Services.DevHub.DevHubCredentialHelper.ClearCachedCredentials();
                    _showGitHubCredentialHelp = false;
                    GitHubCredentialSettingsHelpTextBlock.Visibility = Visibility.Collapsed;
                    GitHubPatBox.Clear();
                    GitHubPatEntryPanel.Visibility = Visibility.Collapsed;
                    SettingsPanel.Visibility = Visibility.Collapsed;
                    RefreshRequested?.Invoke(this, EventArgs.Empty);
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                GitHubPatBox.Clear();
                GitHubPatEntryPanel.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
        }

        private void GitHubPatBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            GitHubPatPlaceholder.Visibility = string.IsNullOrEmpty(GitHubPatBox.Password)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ConnectAdo_Click(object sender, RoutedEventArgs e)
        {
            // Azure DevOps cannot be connected through Git Credential Manager, so prompt
            // for a personal access token directly instead of firing the (always-failing)
            // GCM-based connect flow used for GitHub.
            ShowAdoPatEntry(AzureDevOpsServerHelper.CloudHost);
        }

        private void AdoPatBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var pat = AdoPatBox.Password?.Trim();
                var host = string.IsNullOrEmpty(_pendingAdoHost) ? AzureDevOpsServerHelper.CloudHost : _pendingAdoHost;
                if (!string.IsNullOrEmpty(pat))
                {
                    Services.DevHub.DevHubCredentialHelper.StoreCredential(host, string.Empty, pat);
                    Services.DevHub.DevHubCredentialHelper.ClearCachedCredentials();
                    AdoPatBox.Clear();
                    AdoPatEntryPanel.Visibility = Visibility.Collapsed;
                    _pendingAdoHost = null;
                    RefreshAdoServersList();
                    RefreshRequested?.Invoke(this, EventArgs.Empty);
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                AdoPatBox.Clear();
                AdoPatEntryPanel.Visibility = Visibility.Collapsed;
                _pendingAdoHost = null;
                e.Handled = true;
            }
        }

        private void AdoPatBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            AdoPatPlaceholder.Visibility = string.IsNullOrEmpty(AdoPatBox.Password)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OpenAdoPatPage_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://learn.microsoft.com/en-us/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate");
        }

        private void OpenGitHubPatPage_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/settings/tokens");
        }

        // ---- Azure DevOps server list (cloud + on-prem) ----

        private static bool HasAnyAdoConnection(DevHubDashboard dashboard)
        {
            if (dashboard == null)
                return false;

            for (int i = 0; i < dashboard.Users.Count; i++)
            {
                if (string.Equals(dashboard.Users[i].ProviderName, "Azure DevOps", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool HasAnyAdoCredential()
        {
            foreach (var host in AzureDevOpsServerHelper.GetAllKnownHosts())
            {
                if (Services.DevHub.DevHubCredentialHelper.HasCredential(host))
                    return true;
            }
            return false;
        }

        private void RefreshAdoServersList()
        {
            var rows = new List<AdoServerRow>();

            // Cloud is always shown first.
            AddAdoServerRow(rows, AzureDevOpsServerHelper.CloudHost, isCloud: true, isConfigured: false);

            foreach (var host in AzureDevOpsServerHelper.GetConfiguredServerHosts())
            {
                AddAdoServerRow(rows, host, isCloud: false, isConfigured: true);
            }

            AdoServersList.ItemsSource = rows;

            // Auto-discover additional on-prem servers from MRU in the background and merge them in.
            DiscoverAndMergeServersAsync().FireAndForget();
        }

        private static void AddAdoServerRow(List<AdoServerRow> rows, string host, bool isCloud, bool isConfigured)
        {
            bool connected = Services.DevHub.DevHubCredentialHelper.HasCredential(host);
            rows.Add(new AdoServerRow
            {
                Host = host,
                DisplayText = isCloud ? "Azure DevOps (cloud)" : host,
                StatusMoniker = connected ? KnownMonikers.StatusOK : KnownMonikers.StatusOffline,
                SignInVisibility = connected ? Visibility.Collapsed : Visibility.Visible,
                ChangeVisibility = connected ? Visibility.Visible : Visibility.Collapsed,
                DisconnectVisibility = connected ? Visibility.Visible : Visibility.Collapsed,
                // Allow removing user-configured (or auto-detected) on-prem hosts when no credential is stored.
                RemoveVisibility = (!isCloud && !connected) ? Visibility.Visible : Visibility.Collapsed,
            });
        }

        private async Task DiscoverAndMergeServersAsync()
        {
            try
            {
                var discovered = await AzureDevOpsServerHelper.DiscoverFromMruAsync();
                if (discovered.Count == 0)
                    return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (SettingsPanel.Visibility != Visibility.Visible)
                    return;

                var current = (AdoServersList.ItemsSource as IEnumerable<AdoServerRow>)?.ToList() ?? new List<AdoServerRow>();
                var existing = new HashSet<string>(current.Select(r => r.Host), StringComparer.OrdinalIgnoreCase);

                bool changed = false;
                foreach (var srv in discovered)
                {
                    if (existing.Contains(srv.Host))
                        continue;

                    AddAdoServerRow(current, srv.Host, isCloud: false, isConfigured: false);
                    changed = true;
                }

                if (changed)
                {
                    AdoServersList.ItemsSource = current;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void AdoServer_SignIn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Documents.Hyperlink hl && hl.Tag is string host && !string.IsNullOrWhiteSpace(host))
            {
                ShowAdoPatEntry(host);
            }
        }

        private void ShowAdoPatEntry(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;

            // The PAT entry box lives inside the settings panel, so make sure it is open
            // (e.g. when invoked from the empty "Connect Azure DevOps account..." prompt).
            if (SettingsPanel.Visibility != Visibility.Visible)
            {
                OpenSettingsPanel();
            }

            _pendingAdoHost = host;
            AdoPatHostLabel.Text = host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase)
                ? "Azure DevOps (cloud) personal access token"
                : $"Personal access token for {host}";
            AdoPatBox.Clear();
            AdoPatEntryPanel.Visibility = Visibility.Visible;
            AddAdoServerPanel.Visibility = Visibility.Collapsed;
            AddAdoServerLink.Visibility = Visibility.Visible;
            AdoPatBox.Focus();
        }

        private void AdoServer_Disconnect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Documents.Hyperlink hl && hl.Tag is string host && !string.IsNullOrWhiteSpace(host))
            {
                Services.DevHub.DevHubCredentialHelper.RemoveCredential(host);
                Services.DevHub.DevHubCredentialHelper.ClearCachedCredentials();

                if (string.Equals(_pendingAdoHost, host, StringComparison.OrdinalIgnoreCase))
                    _pendingAdoHost = null;

                AdoPatEntryPanel.Visibility = Visibility.Collapsed;
                RefreshAdoServersList();
                RefreshRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void AdoServer_Remove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Documents.Hyperlink hl && hl.Tag is string host && !string.IsNullOrWhiteSpace(host))
            {
                AzureDevOpsServerHelper.RemoveConfiguredServerHost(host);
                Services.DevHub.DevHubCredentialHelper.RemoveCredential(host);
                Services.DevHub.DevHubCredentialHelper.ClearCachedCredentials();

                if (string.Equals(_pendingAdoHost, host, StringComparison.OrdinalIgnoreCase))
                    _pendingAdoHost = null;

                RefreshAdoServersList();
                RefreshRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void ShowAddAdoServer_Click(object sender, RoutedEventArgs e)
        {
            AddAdoServerLink.Visibility = Visibility.Collapsed;
            AddAdoServerPanel.Visibility = Visibility.Visible;
            AdoPatEntryPanel.Visibility = Visibility.Collapsed;
            AddAdoServerUrlBox.Clear();
            AddAdoServerPatBox.Clear();
            AddAdoServerUrlBox.Focus();
        }

        private void CancelAddAdoServer_Click(object sender, RoutedEventArgs e)
        {
            AddAdoServerPanel.Visibility = Visibility.Collapsed;
            AddAdoServerLink.Visibility = Visibility.Visible;
            AddAdoServerUrlBox.Clear();
            AddAdoServerPatBox.Clear();
        }

        private void AddAdoServer_Click(object sender, RoutedEventArgs e)
        {
            var host = AzureDevOpsServerHelper.NormalizeServerInput(AddAdoServerUrlBox.Text);
            var pat = AddAdoServerPatBox.Password?.Trim();
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pat))
                return;

            // Cloud is implicit; redirect to the cloud sign-in flow if the user typed dev.azure.com.
            AzureDevOpsServerHelper.AddConfiguredServerHost(host);
            Services.DevHub.DevHubCredentialHelper.StoreCredential(host, string.Empty, pat);
            Services.DevHub.DevHubCredentialHelper.ClearCachedCredentials();

            AddAdoServerPanel.Visibility = Visibility.Collapsed;
            AddAdoServerLink.Visibility = Visibility.Visible;
            AddAdoServerUrlBox.Clear();
            AddAdoServerPatBox.Clear();

            RefreshAdoServersList();
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        private void AddAdoServerUrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            AddAdoServerUrlPlaceholder.Visibility = string.IsNullOrEmpty(AddAdoServerUrlBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void AddAdoServerUrlBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddAdoServerPatBox.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelAddAdoServer_Click(sender, e);
                e.Handled = true;
            }
        }

        private void AddAdoServerPatBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddAdoServer_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelAddAdoServer_Click(sender, e);
                e.Handled = true;
            }
        }

        private void AddAdoServerPatBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            AddAdoServerPatPlaceholder.Visibility = string.IsNullOrEmpty(AddAdoServerPatBox.Password)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// Backing data for an Azure DevOps server row in the settings list.
        /// </summary>
        private sealed class AdoServerRow
        {
            public string Host { get; set; }
            public string DisplayText { get; set; }
            public ImageMoniker StatusMoniker { get; set; }
            public Visibility SignInVisibility { get; set; }
            public Visibility ChangeVisibility { get; set; }
            public Visibility DisconnectVisibility { get; set; }
            public Visibility RemoveVisibility { get; set; }
        }

        /// <summary>
        /// Raised when the user requests a refresh of the dashboard data.
        /// </summary>
        public event EventHandler RefreshRequested;

        /// <summary>
        /// Raised when the "last updated" display text changes.
        /// </summary>
        public event EventHandler<string> LastRefreshChanged;

        /// <summary>
        /// Raised when the user wants to connect a new account.
        /// The string argument is the host (e.g., "github.com" or "dev.azure.com").
        /// </summary>
        public event EventHandler<string> ConnectAccountRequested;

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        private static string GetWebUrlFromDataContext(object dataContext)
        {
            switch (dataContext)
            {
                case DevHubPullRequest pr: return pr.WebUrl;
                case DevHubIssue issue: return issue.WebUrl;
                case DevHubCiRun ci: return ci.WebUrl;
                default: return null;
            }
        }

        private void OpenInBrowserMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var url = GetWebUrlFromDataContext(GetContextMenuItemDataContext(sender));
            if (!string.IsNullOrEmpty(url))
            {
                OpenUrl(url);
            }
        }

        private void CopyUrlMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var url = GetWebUrlFromDataContext(GetContextMenuItemDataContext(sender));
            if (!string.IsNullOrEmpty(url))
            {
                Clipboard.SetText(url);
            }
        }

        private static object GetContextMenuItemDataContext(object sender)
        {
            if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe)
                return fe.DataContext;
            return null;
        }

        /// <summary>
        /// Focuses the first item in the currently visible tab.
        /// Returns true if focus was set successfully.
        /// </summary>
        private void FocusSelectedTab()
        {
            if (DevHubSubTabs.SelectedItem is TabItem tab)
            {
                tab.Focus();
            }
        }

        internal bool FocusFirstItem()
        {
            if (DashboardPanel.Visibility != Visibility.Visible)
                return false;

            var list = GetActiveItemsList();
            if (list == null)
            {
                FocusSelectedTab();
                return true;
            }

            var borders = GetCachedBorders(list);
            if (borders.Count > 0)
            {
                borders[0].Focus();
                return true;
            }

            FocusSelectedTab();
            return true;
        }

        private ItemsControl GetActiveItemsList()
        {
            switch (DevHubSubTabs.SelectedIndex)
            {
                case 0: return IssuesList;
                case 1: return PullRequestsList;
                case 2: return CiRunsList;
                default: return IssuesList;
            }
        }

        private void DevHubPanel_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (DashboardPanel.Visibility != Visibility.Visible)
                return;

            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null)
                return;

            // Check if the focused element is an item border in the active list
            var list = GetActiveItemsList();
            if (list != null)
            {
                var borders = GetCachedBorders(list);
                var focusedBorder = focused as Border;
                int index = focusedBorder != null ? borders.IndexOf(focusedBorder) : -1;

                if (index >= 0)
                {
                    if (e.Key == Key.Down)
                    {
                        if (index < borders.Count - 1)
                        {
                            borders[index + 1].Focus();
                        }
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Up)
                    {
                        if (index > 0)
                        {
                            borders[index - 1].Focus();
                        }
                        else
                        {
                            // Focus the selected tab header
                            if (DevHubSubTabs.SelectedItem is TabItem tab)
                            {
                                tab.Focus();
                            }
                        }
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Left)
                    {
                        FocusMruRequested?.Invoke(this, EventArgs.Empty);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Right)
                    {
                        FocusNewsRequested?.Invoke(this, EventArgs.Empty);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Enter)
                    {
                        focusedBorder.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        {
                            RoutedEvent = Border.MouseUpEvent,
                        });
                        e.Handled = true;
                    }
                    else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        var url = GetWebUrlFromDataContext(focusedBorder.DataContext);
                        if (!string.IsNullOrEmpty(url))
                        {
                            Clipboard.SetText(url);
                        }
                        e.Handled = true;
                    }
                    return;
                }
            }

            // Check if focused on a tab header
            if (IsInsideTabHeader(focused))
            {
                if (e.Key == Key.Down)
                {
                    FocusFirstItem();
                    e.Handled = true;
                }
                else if (e.Key == Key.Left && DevHubSubTabs.SelectedIndex == 0)
                {
                    FocusMruRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right && DevHubSubTabs.SelectedIndex == DevHubSubTabs.Items.Count - 1)
                {
                    FocusNewsRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                }
            }
        }

        private bool IsInsideTabHeader(DependencyObject element)
        {
            while (element != null && element != DevHubSubTabs)
            {
                if (element is TabItem)
                    return true;
                element = VisualTreeHelper.GetParent(element);
            }

            return element == DevHubSubTabs;
        }

        private List<Border> GetCachedBorders(ItemsControl list)
        {
            if (_cachedBorders != null && _cachedBordersList == list)
                return _cachedBorders;

            _cachedBordersList = list;
            _cachedBorders = CollectFocusableBorders(list);
            return _cachedBorders;
        }

        private void InvalidateBorderCache()
        {
            _cachedBorders = null;
            _cachedBordersList = null;
        }

        private static List<Border> CollectFocusableBorders(ItemsControl list)
        {
            var result = new List<Border>();
            if (list != null)
            {
                CollectFocusableBordersRecursive(list, result);
            }
            return result;
        }

        private static void CollectFocusableBordersRecursive(DependencyObject parent, List<Border> results)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is Border b && b.Focusable)
                {
                    ApplyContextMenuTheme(b);
                    results.Add(b);
                }
                else
                {
                    CollectFocusableBordersRecursive(child, results);
                }
            }
        }

        private static void ApplyContextMenuTheme(Border border)
        {
            ContextMenu menu = border.ContextMenu;
            if (menu == null || menu.Tag as string == "themed")
                return;

            ThemedContextMenuHelper.ApplyVsTheme(menu);
            if (menu.Items.Count >= 3)
            {
                ((MenuItem)menu.Items[0]).Icon = ThemedContextMenuHelper.CreateMenuIcon(Microsoft.VisualStudio.Imaging.KnownMonikers.BrowserLink);
                ((MenuItem)menu.Items[2]).Icon = ThemedContextMenuHelper.CreateMenuIcon(Microsoft.VisualStudio.Imaging.KnownMonikers.Copy);
            }
            if (menu.Items.Count >= 6)
            {
                ((MenuItem)menu.Items[4]).Icon = ThemedContextMenuHelper.CreateMenuIcon(Microsoft.VisualStudio.Imaging.KnownMonikers.Filter);
                ((MenuItem)menu.Items[5]).Icon = ThemedContextMenuHelper.CreateMenuIcon(Microsoft.VisualStudio.Imaging.KnownMonikers.DeleteFilter);
            }
            menu.Tag = "themed";
        }
    }

    /// <summary>
    /// Converts a CI status string (e.g., "success", "failure") to the appropriate VS KnownMoniker.
    /// </summary>
    public class CiStatusToMonikerConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = value as string;
            return status switch
            {
                "success" => Microsoft.VisualStudio.Imaging.KnownMonikers.StatusOK,
                "failure" => Microsoft.VisualStudio.Imaging.KnownMonikers.StatusError,
                "pending" => Microsoft.VisualStudio.Imaging.KnownMonikers.StatusRunning,
                "cancelled" => Microsoft.VisualStudio.Imaging.KnownMonikers.StatusStopped,
                "skipped" => Microsoft.VisualStudio.Imaging.KnownMonikers.StatusExcluded,
                _ => Microsoft.VisualStudio.Imaging.KnownMonikers.StatusInformation,
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a hex color string (e.g., "d73a4a") to a SolidColorBrush for label backgrounds.
    /// </summary>
    public class HexColorToBrushConverter : System.Windows.Data.IValueConverter
    {
        private static readonly Dictionary<string, SolidColorBrush> _cache = new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hex && hex.Length >= 6)
            {
                hex = hex.TrimStart('#');
                if (_cache.TryGetValue(hex, out var cached))
                    return cached;

                if (byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                    byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                    byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                    brush.Freeze();
                    _cache[hex] = brush;
                    return brush;
                }
            }

            return new SolidColorBrush(Color.FromRgb(128, 128, 128));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a hex color string to a contrasting foreground brush (black or white)
    /// based on the perceived luminance of the color. Works in both light and dark themes.
    /// </summary>
    public class HexColorToForegroundConverter : System.Windows.Data.IValueConverter
    {
        private static readonly SolidColorBrush DarkBrush = CreateFrozen(Color.FromRgb(30, 30, 30));
        private static readonly SolidColorBrush LightBrush = CreateFrozen(Color.FromRgb(255, 255, 255));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hex && hex.Length >= 6)
            {
                hex = hex.TrimStart('#');
                if (byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                    byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                    byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    double luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                    return luminance > 0.5 ? DarkBrush : LightBrush;
                }
            }

            return SystemColors.ControlTextBrush;
        }

        private static SolidColorBrush CreateFrozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
