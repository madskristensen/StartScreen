using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
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

        private StartScreenViewModel ViewModel => DataContext as StartScreenViewModel;

        public StartScreenControl(StartScreenViewModel viewModel, Task loadTask)
        {
            _viewModel = viewModel;
            _loadTask = loadTask;
            InitializeComponent();
            RestoreSplitterPosition();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // If already initialized, just refresh in background
                if (ViewModel != null)
                {
                    await ViewModel.RefreshInBackgroundAsync();
                    return;
                }

                // Yield to let the window paint first
                await Task.Yield();

                // Start Dev Hub cache load immediately (don't wait for MRU)
                var devHubTask = LoadDevHubAsync();

                // Wait for MRU load to complete (likely already done)
                await _loadTask;

                // Bind ViewModel to trigger UI update
                DataContext = _viewModel;

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

        private void RestoreSplitterPosition()
        {
            var saved = Options.Instance.SplitterPosition;
            if (saved >= 500)
            {
                LeftColumn.Width = new GridLength(saved);
            }
        }

        private void GridSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            Options.Instance.SplitterPosition = LeftColumn.ActualWidth;
            Options.Instance.SaveAsync().FileAndForget(nameof(StartScreenControl));
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
                    DevHubPanelControl.ShowLoading();
                    var progress = new Progress<DevHubDashboard>(_ => UpdateDevHubPanel());
                    await _devHubService.RefreshAsync(CancellationToken.None, progress);
                    UpdateDevHubPanel();
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    DevHubPanelControl.ShowError("Failed to refresh. Check your connection and try again.");
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
            // GCM handles auth automatically. Prompt user to push/pull to trigger credential flow.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                host == "dev.azure.com"
                    ? "https://dev.azure.com"
                    : "https://github.com/login")
            { UseShellExecute = true });
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

                // Try cache first for instant display
                var cached = await _devHubService.LoadFromCacheAsync();
                if (cached != null && cached.HasAuthentication)
                {
                    UpdateDevHubPanel();
                }
                else
                {
                    // Show not-connected state while we check
                    DevHubPanelControl.UpdateView(null);
                }

                // Refresh in background if stale or no cache
                if (_devHubService.IsCacheStale() || cached == null)
                {
                    if (cached == null || !cached.HasAuthentication)
                    {
                        DevHubPanelControl.ShowLoading("Signing in to GitHub and Azure DevOps...");
                    }

                    // Use progress callback to render data incrementally
                    var progress = new Progress<DevHubDashboard>(_ => UpdateDevHubPanel());
                    await _devHubService.RefreshAsync(CancellationToken.None, progress);
                    UpdateDevHubPanel();
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                DevHubPanelControl.ShowError("Could not load Dev Hub data.");
            }
        }

        private void UpdateDevHubPanel()
        {
            var dashboard = _devHubService.CurrentDashboard;
            RemoteRepoIdentifier filterRepo = null;

            if (ViewModel?.SelectedMruItem != null && !string.IsNullOrEmpty(ViewModel.SelectedMruItem.RemoteUrl))
            {
                filterRepo = RemoteRepoIdentifier.TryParse(ViewModel.SelectedMruItem.RemoteUrl);
            }

            DevHubPanelControl.UpdateView(dashboard, filterRepo);
        }

        private void NewsItemControl_FocusDevHubRequested(object sender, EventArgs e)
        {
            DevHubPanelControl.FocusFirstItem();
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
                FocusFirstMruItem();
                e.Handled = true;
            }
        }

        private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Oem3 && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        }

        private void PageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                ContentScroll.ScrollToHorizontalOffset(ContentScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void ContentScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                ContentScroll.ScrollToHorizontalOffset(ContentScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
            else
            {
                // Forward vertical scroll to the outer PageScroll
                PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void UserControl_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Only redirect focus to MRU when the outer control itself or a non-interactive
            // container gets focus (not when an inner control like Dev Hub items get focus)
            if (e.NewFocus == this || e.NewFocus == PageScroll || e.NewFocus == ContentScroll)
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
