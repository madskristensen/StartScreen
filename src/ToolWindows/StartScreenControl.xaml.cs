using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;
using StartScreen.Services.DevHub;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenControl : UserControl
    {
        private readonly StartScreenViewModel _viewModel;
        private readonly Task _loadTask;
        private DropIndicatorAdorner _dropAdorner;
        private readonly DevHubService _devHubService = new DevHubService();
        private readonly System.Threading.Tasks.Task<DevHubDashboard> _devHubCacheTask;
        private bool _isInitialized;

        private StartScreenViewModel ViewModel => DataContext as StartScreenViewModel;

        public StartScreenControl(StartScreenViewModel viewModel, Task loadTask)
        {
            _viewModel = viewModel;
            _loadTask = loadTask;
            // Start DevHub cache read immediately (same head start as MRU/News)
            _devHubCacheTask = _devHubService.LoadFromCacheAsync();
            InitializeComponent();
            // Splitter position is restored asynchronously in UserControl_Loaded so that
            // the synchronous Options.Instance read does not block the UI thread during
            // first paint.
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // If already initialized, just refresh in background
                if (_isInitialized)
                {
                    await ViewModel?.RefreshInBackgroundAsync();
                    return;
                }

                _isInitialized = true;

                // Yield to let the window paint first
                await Task.Yield();

                // Restore splitter position from settings (async to avoid sync settings I/O)
                await RestoreSplitterPositionAsync();

                // Start Dev Hub cache load immediately (don't wait for MRU)
                var devHubTask = LoadDevHubAsync();

                // Wait for MRU load to complete (likely already done)
                await _loadTask;

                // Bind ViewModel to trigger UI update
                DataContext = _viewModel;
                UpdateResponsiveColumns();

                // Refresh news/version in background, Dev Hub already started
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
                var newsTask = _viewModel.RefreshInBackgroundAsync();
                await Task.WhenAll(newsTask, devHubTask);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private async void ReleaseNotesLink_Click(object sender, RoutedEventArgs e)
        {
            await VS.Commands.ExecuteAsync("Help.ReleaseNotes");
        }

        private async Task RestoreSplitterPositionAsync()
        {
            try
            {
                Options options = await Options.GetLiveInstanceAsync();
                if (options.SplitterPosition >= 500)
                {
                    LeftColumn.Width = new GridLength(options.SplitterPosition);
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void GridSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            Options.Instance.SplitterPosition = LeftColumn.ActualWidth;
            Options.Instance.SaveAsync().FileAndForget(nameof(StartScreenControl));
        }

        private void RightPaneBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateResponsiveColumns();
        }

        private void UpdateResponsiveColumns()
        {
            if (ViewModel == null || RightPaneBorder.ActualWidth == 0)
                return;

            double available = RightPaneBorder.ActualWidth - 64; // subtract left+right margins (32+32)

            // DevHub(520) + gap(24) = 544, news col = 372 (360+12), YouTube col = 292 (280+12), section gap = 24
            const double baseWidth = 544 + 24; // DevHub + gap after it
            const double newsCol = 372;
            const double youTubeCol = 292;
            const double sectionGap = 24;
            const int maxNewsCols = 4;
            const int maxYouTubeCols = 2;

            // Start with minimum: 2 news columns, 1 YouTube column
            double used = baseWidth + 2 * newsCol + sectionGap + youTubeCol;
            int newsColumns = 2;
            int youTubeColumns = 1;

            // Expansion order: +news, +youtube, +news, +youtube, ...
            bool tryNews = true;

            while (true)
            {
                bool canAddNews = newsColumns < maxNewsCols && used + newsCol <= available;
                bool canAddYouTube = youTubeColumns < maxYouTubeCols && used + youTubeCol <= available;

                if (tryNews && canAddNews)
                {
                    newsColumns++;
                    used += newsCol;
                    tryNews = false;
                }
                else if (!tryNews && canAddYouTube)
                {
                    youTubeColumns++;
                    used += youTubeCol;
                    tryNews = true;
                }
                else if (canAddNews)
                {
                    newsColumns++;
                    used += newsCol;
                    tryNews = false;
                }
                else if (canAddYouTube)
                {
                    youTubeColumns++;
                    used += youTubeCol;
                    tryNews = true;
                }
                else
                {
                    break;
                }
            }

            ViewModel.UpdateColumnCounts(newsColumns, youTubeColumns);
        }

        private async void NewProjectButton_Click(object sender, RoutedEventArgs e)
        {
            await VsCommandService.NewProjectAsync();
        }

        private async void OpenProjectButton_Click(object sender, RoutedEventArgs e)
        {
            await VsCommandService.OpenProjectAsync();
        }

        private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            await VsCommandService.OpenFolderAsync();
        }

        private async void CloneRepoButton_Click(object sender, RoutedEventArgs e)
        {
            await VsCommandService.CloneRepositoryAsync();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.SearchFilter = SearchBox.Text;
            }
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Clear();
                }
                else
                {
                    FocusFirstMruItem();
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                MruItemControl firstMru = FindFirstControl<MruItemControl>(MruPanel);
                if (firstMru != null)
                {
                    firstMru.FocusItem();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Up)
            {
                if (ActionBar.Children.Count > 0 && ActionBar.Children[0] is Button btn)
                {
                    btn.Focus();
                }
                e.Handled = true;
            }
        }

        private void MruItemControl_PinToggleRequested(object sender, MruItem item)
        {
            if (ViewModel == null)
                return;

            var hadFocus = sender is MruItemControl ctrl && ctrl.IsItemFocused();

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ViewModel.TogglePinAsync(item);

                if (hadFocus)
                {
                    // Allow layout to rebuild after collection change
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
                    FocusMruItemByPath(item.Path);
                }
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void MruItemControl_RemoveRequested(object sender, MruItem item)
        {
            if (ViewModel != null)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.RemoveMruItemAsync(item)).FileAndForget(nameof(StartScreenControl));
            }
        }

        private void MruItemControl_SelectionRequested(object sender, MruItem item)
        {
            if (ViewModel != null)
            {
                // Toggle selection: clicking the already-selected item deselects it
                ViewModel.SelectedMruItem = ViewModel.SelectedMruItem == item ? null : item;
            }
        }

        private void MruItemControl_FocusSearchBoxRequested(object sender, EventArgs e)
        {
            SearchBox.Focus();
        }

        private void MruItemControl_FocusDevHubRequested(object sender, EventArgs e)
        {
            DevHubPanelControl.FocusFirstItem();
        }

        private void DevHubPanel_FocusMruRequested(object sender, EventArgs e)
        {
            // Focus the currently selected MRU item, or the first one
            MruItemControl mru = FindFirstControl<MruItemControl>(MruPanel);
            if (mru != null)
            {
                mru.RootBorder.Focus();
            }
        }

        private void DevHubPanel_FocusNewsRequested(object sender, EventArgs e)
        {
            FocusFirstNewsItem();
        }

        private void FocusFirstNewsItem()
        {
            NewsItemControl firstNews = FindFirstControl<NewsItemControl>(NewsPanel);
            if (firstNews != null)
            {
                firstNews.RootBorder.Focus();
            }
        }

        private void MruItemControl_FocusActionBarRequested(object sender, EventArgs e)
        {
            // Focus the first action button
            if (ActionBar.Children.Count > 0 && ActionBar.Children[0] is Button btn)
            {
                btn.Focus();
            }
        }

        private void DevHubPanel_RefreshRequested(object sender, EventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // Only show loading spinner if we have no data to display
                    if (_devHubService.CurrentDashboard == null || !_devHubService.CurrentDashboard.HasAuthentication)
                    {
                        DevHubPanelControl.ShowLoading();
                    }

                    var progress = new Progress<DevHubDashboard>(_ => UpdateDevHubPanel());
                    await _devHubService.RefreshAsync(CancellationToken.None, progress);
                    UpdateDevHubPanel();
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();

                    // Only show error if we have no cached data to fall back to
                    if (_devHubService.CurrentDashboard == null || !_devHubService.CurrentDashboard.HasAuthentication)
                    {
                        DevHubPanelControl.ShowError("Failed to refresh. Check your connection and try again.");
                    }
                }
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void DevHubPanel_ClearFilterRequested(object sender, EventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.SelectedMruItem = null;
            }
        }

        private void DevHubPanel_ConnectAccountRequested(object sender, string host)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                DevHubPanelControl.ShowLoading($"Connecting to {host}...");

                await TaskScheduler.Default;
                var connected = await Services.DevHub.DevHubCredentialHelper.ConnectInteractiveAsync(
                    host, System.Threading.CancellationToken.None);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (connected)
                {
                    // Note: do NOT call ClearCachedCredentials here. ConnectInteractiveAsync
                    // already stored the freshly acquired credential in the in-memory cache,
                    // and GCM may not have persisted the OAuth token to the OS credential
                    // store yet. Clearing would force a non-interactive GCM lookup that can
                    // return nothing, leaving the dashboard appearing not-connected until
                    // the user manually clicks Reload.
                    await RefreshDevHubInBackgroundAsync();
                }
                else
                {
                    // GCM not available or user cancelled - fall back to showing panel state
                    UpdateDevHubPanel();
                }
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StartScreenViewModel.SelectedMruItem))
            {
                UpdateDevHubPanel();
            }
        }

        private async Task LoadDevHubAsync()
        {
            try
            {
                if (!Options.Instance.DevHubEnabled)
                    return;

                // Use the cache task that was started in the constructor
                var cached = await _devHubCacheTask;
                if (cached != null && cached.HasAuthentication)
                {
                    // Show cached data immediately (same pattern as News/MRU)
                    UpdateDevHubPanel();

                    // Refresh in background if stale - don't block the UI
                    if (_devHubService.IsCacheStale())
                    {
                        ThreadHelper.JoinableTaskFactory.RunAsync(() => RefreshDevHubInBackgroundAsync()).FileAndForget(nameof(StartScreenControl));
                    }
                }
                else
                {
                    // No usable cache - must do a full load
                    DevHubPanelControl.ShowLoading("Signing in to GitHub and Azure DevOps...");
                    await RefreshDevHubInBackgroundAsync();
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                DevHubPanelControl.ShowError("Could not load Dev Hub data.");
            }
        }

        private async Task RefreshDevHubInBackgroundAsync()
        {
            try
            {
                var progress = new Progress<DevHubDashboard>(_ => UpdateDevHubPanel());
                await _devHubService.RefreshAsync(CancellationToken.None, progress);
                UpdateDevHubPanel();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                // Only show error if we have no cached data to fall back to
                if (_devHubService.CurrentDashboard == null || !_devHubService.CurrentDashboard.HasAuthentication)
                {
                    DevHubPanelControl.ShowError("Could not load Dev Hub data.");
                }
            }
        }

        private void UpdateDevHubPanel()
        {
            var dashboard = _devHubService.CurrentDashboard;
            DevHubPanelControl.UpdateView(dashboard, null);
        }

        private void NewsItemControl_FocusDevHubRequested(object sender, EventArgs e)
        {
            DevHubPanelControl.FocusFirstItem();
        }

        private void NewsItemControl_FocusYouTubeRequested(object sender, EventArgs e)
        {
            FocusFirstYouTubeItem();
        }

        private void YouTubeVideoControl_FocusNewsRequested(object sender, EventArgs e)
        {
            FocusTopOfSecondNewsColumn();
        }

        private void FocusFirstYouTubeItem()
        {
            YouTubeVideoControl firstYt = FindFirstControl<YouTubeVideoControl>(NewsPanel);
            if (firstYt != null)
            {
                firstYt.RootBorder.Focus();
            }
        }

        private void FocusTopOfSecondNewsColumn()
        {
            var allItems = new System.Collections.Generic.List<NewsItemControl>();
            CollectNewsItemControls(NewsPanel, allItems);

            if (allItems.Count >= 2)
            {
                // The second column starts at index 1 in a multi-column grid
                allItems[1].RootBorder.Focus();
            }
            else if (allItems.Count > 0)
            {
                allItems[0].RootBorder.Focus();
            }
        }

        private static void CollectNewsItemControls(DependencyObject parent, System.Collections.Generic.List<NewsItemControl> results)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is NewsItemControl news)
                {
                    results.Add(news);
                }
                else
                {
                    CollectNewsItemControls(child, results);
                }
            }
        }

        private void ActionBar_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!(e.OriginalSource is Button currentButton))
                return;

            var index = ActionBar.Children.IndexOf(currentButton);
            if (index < 0)
                return;

            if (e.Key == Key.Right)
            {
                var next = index + 1;
                if (next < ActionBar.Children.Count && ActionBar.Children[next] is Button nextBtn)
                {
                    nextBtn.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                var prev = index - 1;
                if (prev >= 0 && ActionBar.Children[prev] is Button prevBtn)
                {
                    prevBtn.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        }

        private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Oem3 && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        }

        private void PageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                PageScroll.ScrollToHorizontalOffset(PageScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void UserControl_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Only redirect focus to MRU when the outer control itself or a non-interactive
            // container gets focus (not when an inner control like Dev Hub items get focus)
            if (e.NewFocus == this || e.NewFocus == PageScroll)
            {
                FocusFirstMruItem();
            }
        }

        private void FocusFirstMruItem()
        {
            MruItemControl firstMru = FindFirstControl<MruItemControl>(MruPanel);
            firstMru?.FocusItem();
        }

        private static T FindFirstControl<T>(DependencyObject parent) where T : class
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                    return match;

                T result = FindFirstControl<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void FocusMruItemByPath(string path)
        {
            MruItemControl mruControl = FindMruItemControlByPath(MruPanel, path);
            mruControl?.FocusItem();
        }

        private static MruItemControl FindMruItemControlByPath(DependencyObject parent, string path)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is MruItemControl mru && mru.DataContext is MruItem item && item.Path == path)
                {
                    return mru;
                }

                MruItemControl result = FindMruItemControlByPath(child, path);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void PinnedItems_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            MruItemControl mruControl = FindAncestor<MruItemControl>(e.OriginalSource as DependencyObject);
            mruControl?.HandlePreviewMouseLeftButtonDown(e);
        }

        private void PinnedItems_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            MruItemControl mruControl = FindAncestor<MruItemControl>(e.OriginalSource as DependencyObject);
            mruControl?.HandlePreviewMouseMove(e);
        }

        private void PinnedItems_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("PinnedMruItem"))
            {
                return;
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // Find the MruItemControl under the cursor to show drop indicator
            MruItemControl target = FindAncestor<MruItemControl>(e.OriginalSource as DependencyObject);
            if (target == null)
            {
                RemoveDropAdorner();
                return;
            }

            // Determine if dropping above or below the target
            Point pos = e.GetPosition(target);
            var insertAfter = pos.Y > target.ActualHeight / 2;

            ShowDropAdorner(target, insertAfter);
        }

        private void PinnedItems_DragLeave(object sender, DragEventArgs e)
        {
            RemoveDropAdorner();
        }

        private void PinnedItems_Drop(object sender, DragEventArgs e)
        {
            RemoveDropAdorner();

            if (!e.Data.GetDataPresent("PinnedMruItem") || ViewModel == null)
                return;

            var draggedItem = e.Data.GetData("PinnedMruItem") as MruItem;
            if (draggedItem == null)
                return;

            // Find the target MruItemControl
            MruItemControl target = FindAncestor<MruItemControl>(e.OriginalSource as DependencyObject);
            if (target == null || !(target.DataContext is MruItem targetItem) || !targetItem.IsPinned)
                return;

            // Determine insert index
            var targetIndex = ViewModel.PinnedItems.IndexOf(targetItem);
            if (targetIndex < 0)
                return;

            Point pos = e.GetPosition(target);
            if (pos.Y > target.ActualHeight / 2)
            {
                targetIndex++;
            }

            // Adjust if dragging from before the target position
            var currentIndex = ViewModel.PinnedItems.IndexOf(draggedItem);
            if (currentIndex >= 0 && currentIndex < targetIndex)
            {
                targetIndex--;
            }

            e.Handled = true;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ViewModel.MovePinnedItemAsync(draggedItem, targetIndex);
            }).FileAndForget(nameof(StartScreenControl));
        }

        private void ShowDropAdorner(MruItemControl target, bool below)
        {
            RemoveDropAdorner();

            var layer = AdornerLayer.GetAdornerLayer(target);
            if (layer == null)
                return;

            _dropAdorner = new DropIndicatorAdorner(target, below);
            layer.Add(_dropAdorner);
        }

        private void RemoveDropAdorner()
        {
            if (_dropAdorner != null)
            {
                var layer = AdornerLayer.GetAdornerLayer(_dropAdorner.AdornedElement);
                layer?.Remove(_dropAdorner);
                _dropAdorner = null;
            }
        }

        private static T FindAncestor<T>(DependencyObject element) where T : DependencyObject
        {
            while (element != null)
            {
                if (element is T match)
                    return match;

                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private void NewsItemControl_PinToggleRequested(object sender, NewsPost post)
        {
            if (ViewModel != null)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.ToggleNewsPinAsync(post)).FileAndForget(nameof(StartScreenControl));
            }
        }

        private async void NewsFeedsSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var filePath = FeedStore.EnsureNewsFeedsFileAndGetPath();
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    // Start watching for changes
                    FeedStore.StartWatching();

                    // Open in VS
                    await VS.Documents.OpenAsync(filePath);
                }
                else
                {
                    await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not create the news feeds configuration file.");
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void RefreshNewsButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.ForceRefreshNews();
            }
        }

        private void RefreshYouTubeButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.ForceRefreshYouTube();
            }
        }

        private async void UpdateNowButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ViewModel != null)
                {
                    await ViewModel.UpdateVisualStudioAsync();
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void NextTipButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.NextTip();
        }

        private void PrevTipButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.PreviousTip();
        }

        private void NextExtensionButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.NextExtension();
        }

        private void PrevExtensionButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.PreviousExtension();
        }

        private void InstallExtension_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ViewModel?.CurrentSuggestedExtension != null &&
                    !string.IsNullOrWhiteSpace(ViewModel.CurrentSuggestedExtension.MarketplaceUrl))
                {
                    System.Diagnostics.Process.Start(ViewModel.CurrentSuggestedExtension.MarketplaceUrl);
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        private static readonly string[] OpenableExtensions = { ".sln", ".slnx", ".csproj", ".vbproj", ".fsproj", ".vcxproj" };

        private void UserControl_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);

                if (files != null && files.Length > 0 && IsOpenablePath(files[0]))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                    return;
                }
            }

            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private async void UserControl_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0)
                return;

            var path = files[0];
            if (!IsOpenablePath(path))
                return;

            try
            {
                await VsCommandService.OpenPathAsync(path);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private static bool IsOpenablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (Directory.Exists(path))
                return true;

            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            return ext != null && OpenableExtensions.Contains(ext);
        }
    }

    /// <summary>
    /// Converter that returns Visible if false, Collapsed if true (inverse of BooleanToVisibilityConverter).
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b)
            {
                return Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter that returns Visible if count > 0, Collapsed otherwise.
    /// </summary>
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                return count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Adorner that draws a horizontal line to indicate where a dragged pinned item will be dropped.
    /// </summary>
    internal sealed class DropIndicatorAdorner : Adorner
    {
        private readonly bool _below;
        private static readonly Pen IndicatorPen = CreateIndicatorPen();

        public DropIndicatorAdorner(UIElement adornedElement, bool below)
            : base(adornedElement)
        {
            _below = below;
            IsHitTestVisible = false;
        }

        private static Pen CreateIndicatorPen()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0, 122, 204)), 2);
            pen.Freeze();
            return pen;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            Size size = AdornedElement.RenderSize;
            var y = _below ? size.Height : 0;
            drawingContext.DrawLine(IndicatorPen, new Point(0, y), new Point(size.Width, y));
        }
    }
}
