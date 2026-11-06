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
        private List<Border> _cachedBorders;
        private DependencyObject _cachedBordersList;
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

        public void SetWorkspaceFilter(RemoteRepoIdentifier repo)
        {
            if (!Equals(_scopedRepo, repo))
            {
                _loadMoreStartingItemCount = null;
                ActivityLoadStatus.Visibility = Visibility.Collapsed;
            }
            _scopedRepo = repo;
        }

        public void ShowPullRequests()
        {
            _defaultTabApplied = true;
            _initialMyWorkTabPending = false;
            MyWorkPrFilter.IsChecked = true;
            IncomingPrFilter.IsChecked = true;
        }

        public void ShowRequestedReviews()
        {
            _defaultTabApplied = true;
            _initialMyWorkTabPending = false;
            MyWorkAttentionFilter.IsChecked = true;
            SelectDefaultTab(DevHubDefaultTab.MyWork);
        }

        /// <summary>
        /// Updates the panel to show a filtered view for the given repo, or global dashboard if null.
        /// </summary>
        public void UpdateView(DevHubDashboard dashboard, RemoteRepoIdentifier filterRepo = null)
        {
            // A context-menu scope takes precedence over any externally supplied filter.
            RemoteRepoIdentifier effectiveFilter = _scopedRepo ?? filterRepo;

            bool dashboardChanged = !ReferenceEquals(_currentDashboard, dashboard);
            bool filterChanged = !Equals(_currentFilterRepo, effectiveFilter);

            _currentDashboard = dashboard;
            _currentFilterRepo = effectiveFilter;

            if (dashboard == null)
            {
                LastRefreshChanged?.Invoke(this, string.Empty);
                if (dashboardChanged || filterChanged || ErrorPanel.Visibility == Visibility.Visible || LoadingPanel.Visibility == Visibility.Visible)
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
                if (dashboard.ActivityWarnings.Count > 0)
                    ShowActivityError(string.Join("\n", dashboard.ActivityWarnings.Values.Distinct()));
                LastRefreshChanged?.Invoke(this, GetActivityRefreshStatus(dashboard) ?? string.Empty);
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
                ActivityWarningText.Text = detail.ActivityWarning;
            }
            else
            {
                prs = dashboard.PullRequests;
                issues = dashboard.Issues;
                ciRuns = dashboard.CiRuns;
                ActivityWarningText.Text = string.Join("\n", dashboard.ActivityWarnings.Values.Distinct());
            }

            ActivityWarningText.Visibility = string.IsNullOrEmpty(ActivityWarningText.Text) ? Visibility.Collapsed : Visibility.Visible;
            UpdateActivityCoverage(dashboard);
            UpdateSettingsAccountStatus(hasGitHub, hasAdo);
            UpdateWorkspaceActivity(prs, issues, ciRuns);
            UpdateLastRefresh(dashboard.FetchedAt);
            var refreshStatus = GetActivityRefreshStatus(dashboard);
            if (refreshStatus != null)
                LastRefreshChanged?.Invoke(this, refreshStatus);
            ShowDashboard();
        }

        internal static string GetActivityRefreshStatus(DevHubDashboard dashboard)
        {
            return dashboard.IsRefreshing ? "Refreshing activity..." : dashboard.Status == DevHubActivityStatus.Success ? null
                : $"Activity {dashboard.Status.ToString().ToLowerInvariant()} - some sources may be stale";
        }

        /// <summary>
        /// Shows the loading state with an optional message.
        /// </summary>
        public void ShowLoading(string message = null)
        {
            LoadingText.Text = message ?? "Loading your activity...";
            ResetStatusPanels(NotConnectedPanel, LoadingPanel, ErrorPanel);
            DashboardPanel.Visibility = Visibility.Visible;
            LoadingPanel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Shows an error state with a message.
        /// </summary>
        public void ShowError(string message)
        {
            ResetStatusPanels(NotConnectedPanel, LoadingPanel, ErrorPanel);
            DashboardPanel.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorMessageText.Text = message;
            ClearRepositoryActivity();
        }

        internal void ShowActivityError(string message)
        {
            ActivityWarningText.Text = message;
            ActivityWarningText.Visibility = Visibility.Visible;
        }

        public void ShowLocalWorkspace()
        {
            _scopedRepo = null;
            _currentFilterRepo = null;
            _currentDashboard = null;
            ClearRepositoryActivity();
            ShowDashboard();
        }

        private void ShowNotConnected(bool hasGitHub, bool hasAdo)
        {
            ResetStatusPanels(NotConnectedPanel, LoadingPanel, ErrorPanel);
            DashboardPanel.Visibility = Visibility.Visible;
            NotConnectedPanel.Visibility = Visibility.Visible;

            ConnectGitHubTextBlock.Visibility = hasGitHub ? Visibility.Collapsed : Visibility.Visible;
            GitHubCredentialHelpTextBlock.Visibility = _showGitHubCredentialHelp && !hasGitHub ? Visibility.Visible : Visibility.Collapsed;
            ConnectAdoTextBlock.Visibility = hasAdo ? Visibility.Collapsed : Visibility.Visible;
            ClearRepositoryActivity();
        }

        private void ClearRepositoryActivity()
        {
            ActivityWarningText.Text = null;
            ActivityWarningText.Visibility = Visibility.Collapsed;
            ActivityCoveragePanel.Visibility = Visibility.Collapsed;
            UpdateWorkspaceActivity(Array.Empty<DevHubPullRequest>(), Array.Empty<DevHubIssue>(), Array.Empty<DevHubCiRun>());
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
            if (SettingsPanel.Visibility == Visibility.Visible)
                RefreshAdoServersList();
        }

        public void ShowAzureDevOpsSignInRequired()
        {
            if (_currentDashboard?.HasAuthentication == true)
                UpdateView(_currentDashboard, _currentFilterRepo);
            else
                ShowNotConnected(hasGitHub: false, hasAdo: false);

            OpenSettingsPanel();
            AdoAuthenticationHelpText.Text = AzureDevOpsCredentialProvider.SignInMessage;
            AdoAuthenticationHelpText.Visibility = Visibility.Visible;
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
            ResetStatusPanels(NotConnectedPanel, LoadingPanel, ErrorPanel);
            DashboardPanel.Visibility = Visibility.Visible;

            ApplyDefaultTab();
        }

        internal static void ResetStatusPanels(UIElement notConnected, UIElement loading, UIElement error)
        {
            notConnected.Visibility = Visibility.Collapsed;
            loading.Visibility = Visibility.Collapsed;
            error.Visibility = Visibility.Collapsed;
        }

        // Keeps the stored preference as the initial keyboard focus destination.
        private void ApplyDefaultTab()
        {
            if (_defaultTabApplied)
            {
                return;
            }

            _defaultTabApplied = true;

            SelectDefaultTab(Options.Instance.DevHubDefaultTab);
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
                    text = $"Updated {(int)ago.TotalMinutes} minute{((int)ago.TotalMinutes == 1 ? "" : "s")} ago";
                else
                    text = $"Updated {(int)ago.TotalHours} hour{((int)ago.TotalHours == 1 ? "" : "s")} ago";
            }
            else
            {
                text = "";
            }

            LastRefreshChanged?.Invoke(this, text);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ClearFilter_Click(object sender, RoutedEventArgs e)
        {
            _scopedRepo = null;
            ScopeToRepoRequested?.Invoke(this, null);

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
                // Show the already-loaded items for this repo immediately, then let the host
                // fetch a fuller set in the background.
                UpdateView(_currentDashboard);
            }

            ScopeToRepoRequested?.Invoke(this, repo);
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
                case DevHubWorkItem work: return work.RepoIdentifier;
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
                DefaultTabComboBox.SelectedIndex = Options.Instance.DevHubDefaultTab == DevHubDefaultTab.Incoming ? 1 : 0;
                SortOrderComboBox.SelectedIndex = (int)Options.Instance.DevHubSortOrder;
            }
            finally
            {
                _suppressSettingsComboChanged = false;
            }
        }

        private void DefaultTabComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressSettingsComboChanged || DefaultTabComboBox.SelectedIndex < 0)
            {
                return;
            }

            var selected = DefaultTabComboBox.SelectedIndex == 1 ? DevHubDefaultTab.Incoming : DevHubDefaultTab.MyWork;
            if (selected == Options.Instance.DevHubDefaultTab)
            {
                return;
            }

            Options.Instance.DevHubDefaultTab = selected;
            Options.Instance.SaveAsync().FireAndForget();

            // Reflect the new default immediately so the user sees the effect.
            SelectDefaultTab(selected);
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
            Services.DevHub.DevHubCredentialHelper.ClearCachedCredentials();
            Options.Instance.SaveAsync().FireAndForget();
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        private void GitHubSignOut_Click(object sender, RoutedEventArgs e)
        {
            SignOutFromGitHubAsync().FileAndForget(nameof(DevHubPanel));
        }

        private async Task SignOutFromGitHubAsync()
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
            AdoAuthenticationHelpText.Visibility = Visibility.Collapsed;
            ConnectAccountRequested?.Invoke(this, AzureDevOpsServerHelper.CloudHost);
        }

        private void ShowCloudAdoPatEntry_Click(object sender, RoutedEventArgs e)
        {
            ShowAdoPatEntry(AzureDevOpsServerHelper.CloudHost);
        }

        private void AdoPatBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitAdoPat();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelAdoPatEntry();
                e.Handled = true;
            }
        }

        private void SaveAdoPat_Click(object sender, RoutedEventArgs e)
        {
            CommitAdoPat();
        }

        private void CancelAdoPat_Click(object sender, RoutedEventArgs e)
        {
            CancelAdoPatEntry();
        }

        private void CommitAdoPat()
        {
            var pat = AdoPatBox.Password?.Trim();
            var host = string.IsNullOrEmpty(_pendingAdoHost) ? AzureDevOpsServerHelper.CloudHost : _pendingAdoHost;
            if (!string.IsNullOrEmpty(pat))
            {
                Services.DevHub.DevHubCredentialHelper.StoreCredential(host, string.Empty, pat);
                if (host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase))
                {
                    Options.Instance.DevHubAdoAuthentication = AzureDevOpsAuthenticationMode.PersonalAccessToken;
                    Options.Instance.SaveAsync().FireAndForget();
                }
                Services.DevHub.DevHubCredentialHelper.ClearCachedCredentials();
                AdoAuthenticationHelpText.Visibility = Visibility.Collapsed;
                AdoPatBox.Clear();
                AdoPatEntryPanel.Visibility = Visibility.Collapsed;
                _pendingAdoHost = null;
                RefreshAdoServersList();
                RefreshRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void CancelAdoPatEntry()
        {
            AdoPatBox.Clear();
            AdoPatEntryPanel.Visibility = Visibility.Collapsed;
            _pendingAdoHost = null;
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
                if (host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase)
                    && Options.Instance.DevHubAdoAuthentication == AzureDevOpsAuthenticationMode.Disconnected)
                    continue;
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

        private void AddAdoServerRow(List<AdoServerRow> rows, string host, bool isCloud, bool isConfigured)
        {
            bool connected = isCloud
                ? Options.Instance.DevHubAdoAuthentication != AzureDevOpsAuthenticationMode.Disconnected
                    && _currentDashboard?.HasProvider(AzureDevOpsServerHelper.CloudHost) == true
                : Services.DevHub.DevHubCredentialHelper.HasCredential(host);
            var authentication = DevHubCredentialHelper.UsesVisualStudio(Options.Instance.DevHubAdoAuthentication,
                isCloud && DevHubCredentialHelper.HasCredential(host)) ? "Visual Studio / Entra ID" : "PAT";
            rows.Add(new AdoServerRow
            {
                Host = host,
                DisplayText = isCloud ? $"Azure DevOps (cloud) - {(connected ? authentication : "not connected")}" : host,
                StatusMoniker = connected ? KnownMonikers.StatusOK : KnownMonikers.StatusOffline,
                SignInText = isCloud ? "Use VS account" : "Sign in",
                SignInVisibility = connected ? Visibility.Collapsed : Visibility.Visible,
                ChangeVisibility = connected && !isCloud ? Visibility.Visible : Visibility.Collapsed,
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
                if (host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase))
                    ConnectAdo_Click(sender, e);
                else
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
            AdoConnectionOptions.IsExpanded = true;
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
                if (host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase))
                {
                    Options.Instance.DevHubAdoAuthentication = AzureDevOpsAuthenticationMode.Disconnected;
                    Options.Instance.SaveAsync().FireAndForget();
                }
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
            if (host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase))
            {
                Options.Instance.DevHubAdoAuthentication = AzureDevOpsAuthenticationMode.PersonalAccessToken;
                Options.Instance.SaveAsync().FireAndForget();
            }
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
            public string SignInText { get; set; }
            public Visibility SignInVisibility { get; set; }
            public Visibility ChangeVisibility { get; set; }
            public Visibility DisconnectVisibility { get; set; }
            public Visibility RemoveVisibility { get; set; }
        }

        /// <summary>
        /// Raised when the user requests a refresh of the dashboard data.
        /// </summary>
        public event EventHandler RefreshRequested;

        public event EventHandler ActivityOptionsChanged;

        /// <summary>
        /// Raised when the "last updated" display text changes.
        /// </summary>
        public event EventHandler<string> LastRefreshChanged;

        /// <summary>
        /// Raised when the user wants to connect a new account.
        /// The string argument is the host (e.g., "github.com" or "dev.azure.com").
        /// </summary>
        public event EventHandler<string> ConnectAccountRequested;

        /// <summary>
        /// Raised when the user scopes the dashboard to a single repository via the item
        /// context menu, so the host can fetch a fuller set of data for just that repo.
        /// </summary>
        public event EventHandler<RemoteRepoIdentifier> ScopeToRepoRequested;

        private static void OpenUrl(string url)
        {
            OpenUrlAsync(url).FileAndForget(nameof(DevHubPanel));
        }

        private static async Task OpenUrlAsync(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not open this activity in your browser.");
            }
        }

        private static string GetWebUrlFromDataContext(object dataContext)
        {
            switch (dataContext)
            {
                case DevHubPullRequest pr: return pr.WebUrl;
                case DevHubIssue issue: return issue.WebUrl;
                case DevHubCiRun ci: return ci.WebUrl;
                case DevHubWorkItem work: return work.WebUrl;
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
        /// Focuses the first item in the active work blade.
        /// Returns true if focus was set successfully.
        /// </summary>
        private void FocusActiveBladeHeader()
        {
            (_activeWorkView == DevHubDefaultTab.Incoming ? IncomingHeading : WorkHeadingLine).Focus();
        }

        internal bool FocusFirstItem()
        {
            if (DashboardPanel.Visibility != Visibility.Visible)
                return false;

            GetActiveItemsList();
            FocusFirstItemInActiveBlade();
            return true;
        }

        private DependencyObject GetActiveItemsList()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (IsInsideBlade(focused, IncomingBlade))
                _activeWorkView = DevHubDefaultTab.Incoming;
            else if (IsInsideBlade(focused, MyWorkBlade))
                _activeWorkView = DevHubDefaultTab.MyWork;
            return _activeWorkView == DevHubDefaultTab.Incoming ? IncomingList : MyWorkList;
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
                            FocusActiveBladeHeader();
                        }
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Left)
                    {
                        MoveBetweenBlades(false);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Right)
                    {
                        MoveBetweenBlades(true);
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

            if (ReferenceEquals(focused, WorkHeadingLine) || ReferenceEquals(focused, IncomingHeading))
            {
                if (e.Key == Key.Down)
                {
                    FocusFirstItem();
                    e.Handled = true;
                }
                else if (e.Key == Key.Left)
                {
                    MoveBetweenBlades(false);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right)
                {
                    MoveBetweenBlades(true);
                    e.Handled = true;
                }
            }
        }

        private void MoveBetweenBlades(bool right)
        {
            if (right && _activeWorkView == DevHubDefaultTab.MyWork)
            {
                SelectDefaultTab(DevHubDefaultTab.Incoming);
                FocusFirstItemInActiveBlade();
            }
            else if (!right && _activeWorkView == DevHubDefaultTab.Incoming)
            {
                SelectDefaultTab(DevHubDefaultTab.MyWork);
                FocusFirstItemInActiveBlade();
            }
            else if (right)
                FocusNewsRequested?.Invoke(this, EventArgs.Empty);
            else
                FocusMruRequested?.Invoke(this, EventArgs.Empty);
        }

        private void FocusFirstItemInActiveBlade()
        {
            var list = _activeWorkView == DevHubDefaultTab.Incoming ? IncomingList : MyWorkList;
            var borders = GetCachedBorders(list);
            if (borders.Count > 0)
                borders[0].Focus();
            else
                FocusActiveBladeHeader();
        }

        private static bool IsInsideBlade(DependencyObject element, DependencyObject blade)
        {
            while (element != null)
            {
                if (ReferenceEquals(element, blade))
                    return true;
                element = element is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(element);
            }

            return false;
        }

        private List<Border> GetCachedBorders(DependencyObject list)
        {
            if (_cachedBorders != null && _cachedBordersList == list)
                return _cachedBorders;

            _cachedBordersList = list;
            _cachedBorders = CollectFocusableBorders(list);
            foreach (var border in _cachedBorders)
                ApplyContextMenuTheme(border);
            return _cachedBorders;
        }

        private void InvalidateBorderCache()
        {
            _cachedBorders = null;
            _cachedBordersList = null;
        }

        internal static List<Border> CollectFocusableBorders(DependencyObject list)
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
        private static readonly SolidColorBrush _darkBrush = CreateFrozen(Color.FromRgb(30, 30, 30));
        private static readonly SolidColorBrush _lightBrush = CreateFrozen(Color.FromRgb(255, 255, 255));

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
                    return luminance > 0.5 ? _darkBrush : _lightBrush;
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
