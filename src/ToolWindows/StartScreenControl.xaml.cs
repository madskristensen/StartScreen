using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Threading;
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
        private bool _isSynchronizingContentHorizontalScroll;

        private StartScreenViewModel ViewModel => DataContext as StartScreenViewModel;

        public StartScreenControl(StartScreenViewModel viewModel, Task loadTask)
        {
            _viewModel = viewModel;
            _loadTask = loadTask;
            // Kick off the DevHub cache read on a background thread so the file I/O
            // overlaps with MRU loading. This does NOT touch the UI - the panel is
            // only updated later, after MRU has painted.
            _devHubCacheTask = Task.Run(() => _devHubService.LoadFromCacheAsync());
            InitializeComponent();

            // Bind the ViewModel up front so the MRU list pops in as soon as
            // LoadMruAsync raises PropertyChanged on GroupedMruItems, instead of
            // waiting until the Loaded handler awaits _loadTask before binding.
            DataContext = _viewModel;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
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

                await ApplyShowTipsAndExtensionsAsync();

                UpdateResponsiveColumns();

                // Wait for MRU to finish so secondary loads (DevHub, news) don't
                // contend with the MRU paint. The DataContext is already bound,
                // so MRU items render the moment LoadMruAsync completes.
                await _loadTask;

                // Yield so MRU paints before we start any other work.
                await Task.Yield();

                // DevHub cache is already loading (started in the constructor); now
                // hook it up to the UI. News/splitter run in parallel.
                Task restoreSplitterTask = RestoreSplitterPositionAsync();
                Task devHubTask = LoadDevHubAsync();
                Task newsTask = _viewModel.RefreshInBackgroundAsync();

                await Task.WhenAll(restoreSplitterTask, newsTask, devHubTask);
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

            // DevHub fixed-width column + gap, the rest is the News-with-tabs column.
            const double devHubWidth = 520;
            const double devHubGap = 24;
            const double newsCardWidth = 372;   // NewsItemControl 360 + 12 right margin
            const double videoCardWidth = 292;  // YouTubeVideoControl 280 + 12 right margin
            const int maxNewsCols = 4;
            const int maxVideoCols = 4;

            double newsArea = Math.Max(0, available - devHubWidth - devHubGap);

            // Cards are fixed width, so we can only add another column when one fully
            // fits without overflow - otherwise the UniformGrid clips and the parent
            // ScrollViewer shows a horizontal scrollbar. Floor + Max(1, ...) prevents that.
            int newsColumns = (int)Math.Floor(newsArea / newsCardWidth);
            newsColumns = Math.Max(1, Math.Min(maxNewsCols, newsColumns));

            int videoColumns = (int)Math.Floor(newsArea / videoCardWidth);
            videoColumns = Math.Max(1, Math.Min(maxVideoCols, videoColumns));

            ViewModel.UpdateColumnCounts(newsColumns, videoColumns);
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

        private void AttachToProcessButton_Click(object sender, RoutedEventArgs e)
        {
            VsCommandService.AttachToProcessAsync().FileAndForget(nameof(StartScreenControl));
        }

        private void AttachToProcessButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Button button))
                return;

            Point position = e.GetPosition(button);
            const double dropDownWidth = 25;

            if (position.X >= button.ActualWidth - dropDownWidth)
            {
                e.Handled = true;
                ShowAttachToProcessMenu(button);
            }
        }

        private void ReattachToProcessMenuItem_Click(object sender, RoutedEventArgs e)
        {
            VsCommandService.ReattachToProcessAsync().FileAndForget(nameof(StartScreenControl));
        }

        private static void ShowAttachToProcessMenu(Button button)
        {
            var menu = button.ContextMenu;
            ThemedContextMenuHelper.ApplyVsTheme(menu);
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
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
                if (ViewModel != null)
                {
                    ViewModel.IsRefreshingDevHub = true;
                }

                try
                {
                    // Only show loading spinner if we have no data to display
                    if (_devHubService.CurrentDashboard == null || !_devHubService.CurrentDashboard.HasAuthentication)
                    {
                        DevHubPanelControl.ShowLoading();
                    }

                    var progress = new Progress<DevHubDashboard>(UpdateDevHubPanel);
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
                finally
                {
                    if (ViewModel != null)
                    {
                        ViewModel.IsRefreshingDevHub = false;
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
                    // already stored the freshly acquired credential in both the in-memory
                    // cache and Windows Credential Manager.
                    await RefreshDevHubInBackgroundAsync();
                }
                else
                {
                    if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                    {
                        DevHubPanelControl.ShowGitHubCredentialManagerUnavailable();
                    }
                    else
                    {
                        // GCM not available or user cancelled - fall back to showing panel state
                        UpdateDevHubPanel();
                    }
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
                // Construct Progress<T> while still on the UI thread so it captures
                // the UI SynchronizationContext and marshals UpdateDevHubPanel back
                // to the UI thread.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var progress = new Progress<DevHubDashboard>(UpdateDevHubPanel);

                // Now hop to the thread pool BEFORE doing any DevHub work. The
                // provider auth path eventually calls Process.Start("git", "credential
                // fill") and process.WaitForExit(...), both of which are synchronous
                // and would block the UI thread for several seconds per provider if
                // we stayed on the captured UI SynchronizationContext.
                await TaskScheduler.Default;

                await _devHubService.RefreshAsync(CancellationToken.None, progress);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                UpdateDevHubPanel();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                // Only show error if we have no cached data to fall back to
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_devHubService.CurrentDashboard == null || !_devHubService.CurrentDashboard.HasAuthentication)
                {
                    DevHubPanelControl.ShowError("Could not load Dev Hub data.");
                }
            }
        }

        private void UpdateDevHubPanel()
        {
            var dashboard = _devHubService.CurrentDashboard;
            UpdateDevHubPanel(dashboard);
        }

        private void UpdateDevHubPanel(DevHubDashboard dashboard)
        {
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
            if (Keyboard.Modifiers == ModifierKeys.Shift && ContentScroll != null && ContentScroll.ScrollableWidth > 0)
            {
                ContentScroll.ScrollToHorizontalOffset(ContentScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void ContentScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift && ContentScroll != null && ContentScroll.ScrollableWidth > 0)
            {
                ContentScroll.ScrollToHorizontalOffset(ContentScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }

            if (PageScroll != null)
            {
                PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void ContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (ContentHorizontalScrollbar == null)
            {
                return;
            }

            _isSynchronizingContentHorizontalScroll = true;

            try
            {
                var maximum = Math.Max(0, e.ExtentWidth - e.ViewportWidth);

                ContentHorizontalScrollbar.ViewportSize = e.ViewportWidth;
                ContentHorizontalScrollbar.Maximum = maximum;
                ContentHorizontalScrollbar.IsEnabled = maximum > 0;
                ContentHorizontalScrollbar.Visibility = maximum > 0 ? Visibility.Visible : Visibility.Collapsed;
                ContentHorizontalScrollbar.Value = Math.Max(0, Math.Min(maximum, e.HorizontalOffset));
            }
            finally
            {
                _isSynchronizingContentHorizontalScroll = false;
            }
        }

        private void ContentHorizontalScrollbar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSynchronizingContentHorizontalScroll || ContentScroll == null)
            {
                return;
            }

            ContentScroll.ScrollToHorizontalOffset(e.NewValue);
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

        private bool _suppressKeepVisibleChanged;

        private async void StartScreenSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Options options = await Options.GetLiveInstanceAsync();
                _suppressKeepVisibleChanged = true;
                KeepVisibleCheckBox.IsChecked = options.KeepVisibleOnSolutionLoad;
                ShowTipsAndExtensionsCheckBox.IsChecked = options.ShowTipsAndExtensions;
                _suppressKeepVisibleChanged = false;
                StartScreenSettingsPopup.IsOpen = true;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private async void KeepVisibleCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressKeepVisibleChanged)
            {
                return;
            }

            try
            {
                Options options = await Options.GetLiveInstanceAsync();
                options.KeepVisibleOnSolutionLoad = KeepVisibleCheckBox.IsChecked == true;
                await options.SaveAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private async void ShowTipsAndExtensionsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressKeepVisibleChanged)
            {
                return;
            }

            try
            {
                bool show = ShowTipsAndExtensionsCheckBox.IsChecked == true;
                Options options = await Options.GetLiveInstanceAsync();
                options.ShowTipsAndExtensions = show;
                await options.SaveAsync();
                TipsAndExtensionsSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private async Task ApplyShowTipsAndExtensionsAsync()
        {
            try
            {
                Options options = await Options.GetLiveInstanceAsync();
                TipsAndExtensionsSection.Visibility = options.ShowTipsAndExtensions
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private void DevHubPanel_LastRefreshChanged(object sender, string text)
        {
            if (LastRefreshText != null)
            {
                LastRefreshText.Text = text ?? string.Empty;
            }
        }

        private void UnifiedRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.ForceRefreshNews();
                ViewModel.ForceRefreshYouTube();
            }

            DevHubPanel_RefreshRequested(this, System.EventArgs.Empty);
        }

        private void DevHubSettingsLink_Click(object sender, RoutedEventArgs e)
        {
            StartScreenSettingsPopup.IsOpen = false;
            DevHubPanelControl?.ToggleSettings();
        }

        private void NewsFeedsSettingsLink_Click(object sender, RoutedEventArgs e)
        {
            StartScreenSettingsPopup.IsOpen = false;
            NewsFeedsSettings_Click(sender, e);
        }

        private void NewsTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Ignore selection changes that bubble up from nested ItemsControls.
            if (!ReferenceEquals(e.OriginalSource, NewsTabs))
            {
                return;
            }

            if (ViewModel == null)
            {
                return;
            }

            object selected = NewsTabs.SelectedItem;
            if (ReferenceEquals(selected, BlogsTab))
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.MarkBlogsAsSeenAsync())
                    .FileAndForget(nameof(StartScreenControl));
            }
            else if (ReferenceEquals(selected, VideosTab))
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.MarkVideosAsSeenAsync())
                    .FileAndForget(nameof(StartScreenControl));
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
    /// Converter that returns Visible if true, Hidden (not Collapsed) if false.
    /// Use this for elements that should stay in layout when inactive, preventing layout shifts.
    /// </summary>
    public class BoolToHiddenVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && b ? Visibility.Visible : Visibility.Hidden;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter that returns a reduced opacity when true (refreshing), full opacity when false.
    /// </summary>
    public class RefreshingToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && b ? 0.4 : 1.0;
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
    /// Converts a column count into a pixel width by multiplying by the per-column
    /// width passed in via ConverterParameter (item width plus its right margin).
    /// Used to bound a WrapPanel so it wraps after the desired number of columns
    /// instead of stretching across the whole available width.
    /// </summary>
    public class ColumnCountToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count && count > 0)
            {
                double perColumn = 292;
                if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                {
                    perColumn = parsed;
                }

                return count * perColumn;
            }

            return double.PositiveInfinity;
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
