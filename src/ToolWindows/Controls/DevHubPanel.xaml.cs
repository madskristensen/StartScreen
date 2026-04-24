using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using StartScreen.Models.DevHub;

namespace StartScreen.ToolWindows.Controls
{
    public partial class DevHubPanel : UserControl
    {
        private DevHubDashboard _currentDashboard;
        private RemoteRepoIdentifier _currentFilterRepo;
        private IReadOnlyList<DevHubPullRequest> _lastBoundPullRequests;
        private IReadOnlyList<DevHubIssue> _lastBoundIssues;
        private IReadOnlyList<DevHubCiRun> _lastBoundCiRuns;
        private List<Border> _cachedBorders;
        private ItemsControl _cachedBordersList;
        private bool _suppressSaveOnLostFocus;

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
        /// Raised when the user wants to clear the MRU selection filter.
        /// </summary>
        public event EventHandler ClearFilterRequested;

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
            bool dashboardChanged = !ReferenceEquals(_currentDashboard, dashboard);
            bool filterChanged = !Equals(_currentFilterRepo, filterRepo);

            _currentDashboard = dashboard;
            _currentFilterRepo = filterRepo;

            if (dashboard == null)
            {
                if (dashboardChanged || filterChanged)
                {
                    ShowNotConnected(hasGitHub: false, hasAdo: false);
                }
                return;
            }

            bool hasGitHub = dashboard.HasProvider("github.com");
            bool hasAdo = dashboard.HasProvider("dev.azure.com");

            if (!dashboard.HasAuthentication)
            {
                ShowNotConnected(hasGitHub, hasAdo);
                return;
            }

            IReadOnlyList<DevHubPullRequest> prs;
            IReadOnlyList<DevHubIssue> issues;
            IReadOnlyList<DevHubCiRun> ciRuns;

            if (filterRepo != null)
            {
                var detail = dashboard.FilterByRepo(filterRepo);
                RepoContextName.Text = filterRepo.DisplayName;
                RepoContextHeader.Visibility = Visibility.Visible;
                prs = detail.PullRequests;
                issues = detail.Issues;
                ciRuns = detail.CiRuns;
            }
            else
            {
                RepoContextHeader.Visibility = Visibility.Collapsed;
                prs = dashboard.PullRequests;
                issues = dashboard.Issues;
                ciRuns = dashboard.CiRuns;
            }

            // Only re-bind sections whose data changed. Each Update* method
            // resets ItemsSource and forces WPF to rebuild the visual tree, which is
            // the primary cause of the UI thread hitch when DevHub data arrives.
            if (filterChanged || !DevHubItemComparer.SamePullRequests(_lastBoundPullRequests, prs))
            {
                _lastBoundPullRequests = prs;
                UpdatePullRequests(prs);
            }
            if (filterChanged || !DevHubItemComparer.SameIssues(_lastBoundIssues, issues))
            {
                _lastBoundIssues = issues;
                UpdateIssues(issues);
            }
            if (filterChanged || !DevHubItemComparer.SameCiRuns(_lastBoundCiRuns, ciRuns))
            {
                _lastBoundCiRuns = ciRuns;
                UpdateCiRuns(ciRuns);
            }

            UpdateSettingsAccountStatus(hasGitHub, hasAdo);
            UpdateLastRefresh(dashboard.FetchedAt);
            ShowDashboard();
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
            ConnectAdoTextBlock.Visibility = hasAdo ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateSettingsAccountStatus(bool hasGitHub, bool hasAdo)
        {
            ConnectedGitHubStatus.Visibility = hasGitHub ? Visibility.Visible : Visibility.Collapsed;
            SettingsConnectGitHubTextBlock.Visibility = hasGitHub ? Visibility.Collapsed : Visibility.Visible;
            ConnectedAdoStatus.Visibility = hasAdo ? Visibility.Visible : Visibility.Collapsed;
            AdoPatEntryPanel.Visibility = hasAdo ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowDashboard()
        {
            NotConnectedPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            DashboardPanel.Visibility = Visibility.Visible;
        }

        private void UpdatePullRequests(IReadOnlyList<DevHubPullRequest> pullRequests)
        {
            InvalidateBorderCache();
            if (pullRequests != null && pullRequests.Count > 0)
            {
                PullRequestsList.ItemsSource = pullRequests;
                PrCountBadge.Text = $"({pullRequests.Count})";
                NoPrsText.Visibility = Visibility.Collapsed;
            }
            else
            {
                PullRequestsList.ItemsSource = null;
                PrCountBadge.Text = "";
                NoPrsText.Visibility = Visibility.Visible;
            }
        }

        private void UpdateIssues(IReadOnlyList<DevHubIssue> issues)
        {
            InvalidateBorderCache();
            if (issues != null && issues.Count > 0)
            {
                IssuesList.ItemsSource = issues;
                IssueCountBadge.Text = $"({issues.Count})";
                NoIssuesText.Visibility = Visibility.Collapsed;
            }
            else
            {
                IssuesList.ItemsSource = null;
                IssueCountBadge.Text = "";
                NoIssuesText.Visibility = Visibility.Visible;
            }
        }

        private void UpdateCiRuns(IReadOnlyList<DevHubCiRun> ciRuns)
        {
            InvalidateBorderCache();
            if (ciRuns != null && ciRuns.Count > 0)
            {
                CiRunsList.ItemsSource = ciRuns;
                CiCountBadge.Text = $"({ciRuns.Count})";
                NoCiText.Visibility = Visibility.Collapsed;
            }
            else
            {
                CiRunsList.ItemsSource = null;
                CiCountBadge.Text = "";
                NoCiText.Visibility = Visibility.Visible;
            }
        }

        private void UpdateLastRefresh(DateTime fetchedAt)
        {
            if (fetchedAt != default)
            {
                var ago = DateTime.UtcNow - fetchedAt;
                if (ago.TotalMinutes < 1)
                    LastRefreshText.Text = "Updated just now";
                else if (ago.TotalMinutes < 60)
                    LastRefreshText.Text = $"Updated {(int)ago.TotalMinutes}m ago";
                else
                    LastRefreshText.Text = $"Updated {(int)ago.TotalHours}h ago";
            }
            else
            {
                LastRefreshText.Text = "";
            }
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
            ClearFilterRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsPanel.Visibility == Visibility.Visible)
            {
                SettingsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                SearchQueryTextBox.Text = Options.Instance.DevHubSearchQuery ?? "";
                SearchQueryPlaceholder.Visibility = string.IsNullOrEmpty(SearchQueryTextBox.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                bool hasGitHub = _currentDashboard?.HasProvider("github.com") == true;
                bool hasAdo = _currentDashboard?.HasProvider("dev.azure.com") == true
                    || Services.DevHub.DevHubCredentialHelper.HasCredential("dev.azure.com");
                UpdateSettingsAccountStatus(hasGitHub, hasAdo);
                AdoPatBox.Clear();

                SettingsPanel.Visibility = Visibility.Visible;
            }
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

        private void ConnectGitHub_Click(object sender, RoutedEventArgs e)
        {
            ConnectAccountRequested?.Invoke(this, "github.com");
        }

        private void ConnectAdo_Click(object sender, RoutedEventArgs e)
        {
            ConnectAccountRequested?.Invoke(this, "dev.azure.com");
        }

        private void AdoPatBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var pat = AdoPatBox.Password?.Trim();
                if (!string.IsNullOrEmpty(pat))
                {
                    Services.DevHub.DevHubCredentialHelper.StoreCredential("dev.azure.com", string.Empty, pat);
                    Services.DevHub.DevHubCredentialHelper.ClearCachedCredentials();
                    AdoPatBox.Clear();
                    SettingsPanel.Visibility = Visibility.Collapsed;
                    RefreshRequested?.Invoke(this, EventArgs.Empty);
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                AdoPatBox.Clear();
                SettingsPanel.Visibility = Visibility.Collapsed;
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

        private void ChangeAdoPat_Click(object sender, RoutedEventArgs e)
        {
            // Show the PAT entry panel so the user can enter a new token
            ConnectedAdoStatus.Visibility = Visibility.Collapsed;
            AdoPatEntryPanel.Visibility = Visibility.Visible;
            AdoPatBox.Clear();
            AdoPatBox.Focus();
        }

        private void DisconnectAdo_Click(object sender, RoutedEventArgs e)
        {
            Services.DevHub.DevHubCredentialHelper.RemoveCredential("dev.azure.com");
            Services.DevHub.DevHubCredentialHelper.ClearCachedCredentials();
            ConnectedAdoStatus.Visibility = Visibility.Collapsed;
            AdoPatEntryPanel.Visibility = Visibility.Visible;
            AdoPatBox.Clear();
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Raised when the user requests a refresh of the dashboard data.
        /// </summary>
        public event EventHandler RefreshRequested;

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
