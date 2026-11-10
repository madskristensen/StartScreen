using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using StartScreen.Commands;
using StartScreen.Models.DevHub;

namespace StartScreen.ToolWindows.Controls
{
    public partial class DevHubPanel
    {
        public static readonly DependencyProperty ShowSnoozedItemsProperty = DependencyProperty.Register(
            nameof(ShowSnoozedItems), typeof(bool), typeof(DevHubPanel),
            new PropertyMetadata(false, (sender, _) => ((DevHubPanel)sender).RefreshSnoozedVisibility()));

        public bool ShowSnoozedItems
        {
            get => (bool)GetValue(ShowSnoozedItemsProperty);
            set => SetValue(ShowSnoozedItemsProperty, value);
        }

        private DevHubItemPreferences _activityPreferences = new();
        private string _activityPreferencesJson;
        private DispatcherTimer _snoozeExpiryTimer;
        private bool _isSavingActivityPreference;

        private void ActivityOptions_Click(object sender, RoutedEventArgs e)
        {
            ShowActivityOptionsAsync().FileAndForget(nameof(DevHubPanel));
        }

        private async Task ShowActivityOptionsAsync()
        {
            try
            {
                var shell = await VS.GetServiceAsync<SVsUIShell, IVsUIShell>()
                    ?? throw new InvalidOperationException("Visual Studio context menus are unavailable.");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_isDisposed || !IsVisible)
                    return;
                DevHubViewMenu.Current = new DevHubViewMenu
                {
                    GetShowSnoozedItems = () => ShowSnoozedItems,
                    ToggleSnoozedItemsAsync = ToggleSnoozedItemsAsync,
                };
                try
                {
                    ErrorHandler.ThrowOnFailure(shell.UpdateCommandUI(1));
                    var point = ActivityOptionsButton.PointToScreen(new Point(0, ActivityOptionsButton.ActualHeight));
                    var guid = PackageGuids.StartScreen;
                    var points = new[] { new POINTS { x = (short)point.X, y = (short)point.Y } };
                    ErrorHandler.ThrowOnFailure(shell.ShowContextMenu(0, ref guid, PackageIds.DevHubViewMenu, points, null));
                }
                finally
                {
                    DevHubViewMenu.Current = null;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not show activity options. " + ex.Message);
            }
        }

        private async Task ToggleSnoozedItemsAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowSnoozedItems = !ShowSnoozedItems;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not change snoozed item visibility. " + ex.Message);
            }
        }

        private void InitializeActivityPreferences()
        {
            Unloaded += ActivityPreferences_Unloaded;
            IsVisibleChanged += ActivityPreferences_VisibilityChanged;
        }

        private void EnsureActivityPreferences()
        {
            var json = Options.Instance.DevHubItemPreferences;
            if (_activityPreferencesJson == json)
                return;
            var preferences = DevHubItemPreferences.FromJson(json);
            _activityPreferences = preferences;
            _activityPreferencesJson = json;
            _activity.SetPreferences(preferences, ShowSnoozedItems);
            ActivityPreferencesError.Visibility = Visibility.Collapsed;
        }

        private void ActivityPreferences_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible && IsLoaded)
                RefreshActivityPreferences();
            else
                _snoozeExpiryTimer?.Stop();
        }

        private void ActivityPreferences_Unloaded(object sender, RoutedEventArgs e)
        {
            _snoozeExpiryTimer?.Stop();
        }

        private void RefreshActivityPreferences()
        {
            if (_isDisposed)
                return;
            try
            {
                EnsureActivityPreferences();
                RefreshWorkspaceActivity();
                ScheduleSnoozeExpiry();
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
                ActivityPreferencesError.Text = "Could not load pin and snooze settings. Reopen Start Screen to retry. " + ex.Message;
                ActivityPreferencesError.Visibility = Visibility.Visible;
            }
        }

        private void RefreshSnoozedVisibility()
        {
            if (_isDisposed || ActivityPreferencesError == null)
                return;
            _activity.SetPreferences(_activityPreferences, ShowSnoozedItems);
            RefreshWorkspaceActivity();
        }

        private async Task SaveActivityPreferenceAsync(DevHubWorkItem item, int id)
        {
            if (_isSavingActivityPreference)
                throw new InvalidOperationException("Pin and snooze settings are still being saved. Try again shortly.");
            _isSavingActivityPreference = true;
            var options = Options.Instance;
            var previousJson = options.DevHubItemPreferences;
            var saved = false;
            try
            {
                var preferences = DevHubItemPreferences.FromJson(previousJson);
                var now = DateTime.UtcNow;
                if (id == PackageIds.DevHubPinItem)
                    preferences.SetPinned(item, !preferences.IsPinned(item), now);
                else
                {
                    var snoozed = preferences.GetSnoozedUntil(item) > now;
                    preferences.Snooze(item, snoozed ? null : now.AddHours(id == PackageIds.DevHubSnoozeItemDay ? 24 : 1), now);
                }
                options.DevHubItemPreferences = preferences.ToJson(now);
                await options.SaveAsync();
                saved = true;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_isDisposed)
                {
                    EnsureActivityPreferences();
                    RefreshWorkspaceActivity();
                    ScheduleSnoozeExpiry();
                }
            }
            finally
            {
                if (!saved)
                    options.DevHubItemPreferences = previousJson;
                _isSavingActivityPreference = false;
            }
        }

        private void ScheduleSnoozeExpiry()
        {
            _snoozeExpiryTimer?.Stop();
            if (_isDisposed || !IsVisible || !IsLoaded)
                return;
            var now = DateTime.UtcNow;
            var expiry = _activityPreferences.NextExpiry(now);
            if (!expiry.HasValue)
                return;
            if (_snoozeExpiryTimer == null)
            {
                _snoozeExpiryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
                _snoozeExpiryTimer.Tick += SnoozeExpiry_Tick;
            }
            _snoozeExpiryTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, Math.Min(60, (expiry.Value - now).TotalSeconds)));
            _snoozeExpiryTimer.Start();
        }

        private void SnoozeExpiry_Tick(object sender, EventArgs e)
        {
            _snoozeExpiryTimer.Stop();
            if (!_isDisposed && IsVisible && IsLoaded)
            {
                RefreshWorkspaceActivity();
                ScheduleSnoozeExpiry();
            }
        }

        private void DisposeActivityPreferences()
        {
            _snoozeExpiryTimer?.Stop();
            if (_snoozeExpiryTimer != null)
                _snoozeExpiryTimer.Tick -= SnoozeExpiry_Tick;
            Unloaded -= ActivityPreferences_Unloaded;
            IsVisibleChanged -= ActivityPreferences_VisibilityChanged;
        }
    }
}
