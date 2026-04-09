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
        private StartScreenViewModel ViewModel => DataContext as StartScreenViewModel;

        public StartScreenControl()
        {
            InitializeComponent();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                await ViewModel.RefreshInBackgroundAsync();
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

        private async void MruItemControl_PinToggleRequested(object sender, MruItem e)
        {
            if (ViewModel != null)
            {
                await ViewModel.TogglePinAsync(e);
            }
        }

        private async void MruItemControl_RemoveRequested(object sender, MruItem e)
        {
            if (ViewModel != null)
            {
                await ViewModel.RemoveMruItemAsync(e);
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
