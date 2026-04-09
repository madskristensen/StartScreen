using System.Globalization;
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
            ViewModel?.TogglePinAsync(item);
        }

        private void MruItemControl_RemoveRequested(object sender, MruItem item)
        {
            ViewModel?.RemoveMruItemAsync(item);
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
