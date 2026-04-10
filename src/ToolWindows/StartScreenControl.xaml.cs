using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using StartScreen.Models;
using StartScreen.Services;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenControl : UserControl
    {
        private readonly StartScreenViewModel _viewModel;
        private readonly Task _loadTask;

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

                // Wait for MRU load to complete (likely already done)
                await _loadTask;

                // Bind ViewModel to trigger UI update
                DataContext = _viewModel;

                // Refresh news and version info in background
                await _viewModel.RefreshInBackgroundAsync();
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
            double saved = Options.Instance.SplitterPosition;
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

        private void MruItemControl_PinToggleRequested(object sender, MruItem item)
        {
            if (ViewModel == null)
                return;

            bool hadFocus = sender is MruItemControl ctrl && ctrl.IsItemFocused();

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

        private void MruItemControl_FocusSearchBoxRequested(object sender, EventArgs e)
        {
            SearchBox.Focus();
        }

        private void MruItemControl_FocusNewsRequested(object sender, EventArgs e)
        {
            var firstNews = FindFirstControl<NewsItemControl>(NewsPanel);
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

        private void NewsItemControl_FocusMruRequested(object sender, EventArgs e)
        {
            FocusFirstMruItem();
        }

        private void ActionBar_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!(e.OriginalSource is Button currentButton))
                return;

            int index = ActionBar.Children.IndexOf(currentButton);
            if (index < 0)
                return;

            if (e.Key == Key.Right)
            {
                int next = index + 1;
                if (next < ActionBar.Children.Count && ActionBar.Children[next] is Button nextBtn)
                {
                    nextBtn.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                int prev = index - 1;
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

        private void UserControl_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // When the tool window gets focus and no inner item is focused, focus the first MRU item
            if (e.NewFocus == this || e.NewFocus is ScrollViewer || e.NewFocus is Border b && b.Name != "RootBorder")
            {
                FocusFirstMruItem();
            }
        }

        private void FocusFirstMruItem()
        {
            var firstMru = FindFirstControl<MruItemControl>(MruPanel);
            firstMru?.FocusItem();
        }

        private static T FindFirstControl<T>(DependencyObject parent) where T : class
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                    return match;

                var result = FindFirstControl<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void FocusMruItemByPath(string path)
        {
            var mruControl = FindMruItemControlByPath(MruPanel, path);
            mruControl?.FocusItem();
        }

        private static MruItemControl FindMruItemControlByPath(DependencyObject parent, string path)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is MruItemControl mru && mru.DataContext is MruItem item && item.Path == path)
                {
                    return mru;
                }

                var result = FindMruItemControlByPath(child, path);
                if (result != null)
                    return result;
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

            string path = files[0];
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

            string ext = Path.GetExtension(path)?.ToLowerInvariant();
            return ext != null && OpenableExtensions.Contains(ext);
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
}
