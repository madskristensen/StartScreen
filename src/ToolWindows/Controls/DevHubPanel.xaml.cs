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
        private bool _isLoading;

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
            _currentDashboard = dashboard;

            if (dashboard == null)
            {
                ShowNotConnected();
                return;
            }

            if (!dashboard.HasAuthentication)
            {
                ShowNotConnected();
                return;
            }

            if (filterRepo != null)
            {
                var detail = dashboard.FilterByRepo(filterRepo);
                RepoContextName.Text = filterRepo.DisplayName;
                RepoContextHeader.Visibility = Visibility.Visible;
                UpdatePullRequests(detail.PullRequests);
                UpdateIssues(detail.Issues);
                UpdateCiRuns(detail.CiRuns);
            }
            else
            {
                RepoContextHeader.Visibility = Visibility.Collapsed;
                UpdatePullRequests(dashboard.PullRequests);
                UpdateIssues(dashboard.Issues);
                UpdateCiRuns(dashboard.CiRuns);
            }

            UpdateLastRefresh(dashboard.FetchedAt);
            ShowDashboard();
        }

        /// <summary>
        /// Shows the loading state with an optional message.
        /// </summary>
        public void ShowLoading(string message = null)
        {
            _isLoading = true;
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
            _isLoading = false;
            NotConnectedPanel.Visibility = Visibility.Collapsed;
            DashboardPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorMessageText.Text = message;
        }

        private void ShowNotConnected()
        {
            _isLoading = false;
            DashboardPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            NotConnectedPanel.Visibility = Visibility.Visible;
        }

        private void ShowDashboard()
        {
            _isLoading = false;
            NotConnectedPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            DashboardPanel.Visibility = Visibility.Visible;
        }

        private void UpdatePullRequests(IReadOnlyList<DevHubPullRequest> pullRequests)
        {
            if (pullRequests != null && pullRequests.Count > 0)
            {
                PullRequestsList.ItemsSource = pullRequests.OrderByDescending(pr => pr.UpdatedAt).ToList();
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
            if (issues != null && issues.Count > 0)
            {
                IssuesList.ItemsSource = issues.OrderByDescending(i => i.CreatedAt).ToList();
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
            if (ciRuns != null && ciRuns.Count > 0)
            {
                CiRunsList.ItemsSource = ciRuns.OrderByDescending(r => r.Timestamp).ToList();
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

        private void ConnectGitHub_Click(object sender, RoutedEventArgs e)
        {
            ConnectAccountRequested?.Invoke(this, "github.com");
        }

        private void ConnectAdo_Click(object sender, RoutedEventArgs e)
        {
            ConnectAccountRequested?.Invoke(this, "dev.azure.com");
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

            list.UpdateLayout();

            var borders = CollectFocusableBorders(list);
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
                var borders = CollectFocusableBorders(list);
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
                    results.Add(b);
                }
                else
                {
                    CollectFocusableBordersRecursive(child, results);
                }
            }
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
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hex && hex.Length >= 6)
            {
                hex = hex.TrimStart('#');
                if (byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                    byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                    byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    // Semi-transparent background like GitHub dark mode labels
                    return new SolidColorBrush(Color.FromArgb(50, r, g, b));
                }
            }

            return new SolidColorBrush(Color.FromArgb(30, 128, 128, 128));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a hex color string to a foreground brush (the label's own color at higher opacity).
    /// </summary>
    public class HexColorToForegroundConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hex && hex.Length >= 6)
            {
                hex = hex.TrimStart('#');
                if (byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                    byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                    byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    // Lighten the color for readability on dark backgrounds
                    r = (byte)Math.Min(255, r + 80);
                    g = (byte)Math.Min(255, g + 80);
                    b = (byte)Math.Min(255, b + 80);
                    return new SolidColorBrush(Color.FromRgb(r, g, b));
                }
            }

            return SystemColors.ControlTextBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
