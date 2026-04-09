using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenControl : UserControl
    {
        private readonly StartScreenViewModel _viewModel;
        private readonly Task _cacheLoadTask;

        private StartScreenViewModel ViewModel => DataContext as StartScreenViewModel;

        public StartScreenControl(StartScreenViewModel viewModel, Task cacheLoadTask)
        {
            _viewModel = viewModel;
            _cacheLoadTask = cacheLoadTask;
            InitializeComponent();
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

                // Wait for cache load to complete (likely already done)
                await _cacheLoadTask;

                // Bind ViewModel to trigger UI update
                DataContext = _viewModel;

                // Refresh from live sources in background
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
            if (ViewModel != null)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.TogglePinAsync(item)).FileAndForget(nameof(StartScreenControl));
            }
        }

        private void MruItemControl_RemoveRequested(object sender, MruItem item)
        {
            if (ViewModel != null)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => ViewModel.RemoveMruItemAsync(item)).FileAndForget(nameof(StartScreenControl));
            }
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
