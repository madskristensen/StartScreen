using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Services;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace StartScreen.ToolWindows.Controls
{
    public partial class MruItemControl : UserControl
    {
        private MruItem MruItem => DataContext as MruItem;
        private readonly MenuItem _pinMenuItem;

        public event EventHandler<MruItem> PinToggleRequested;
        public event EventHandler<MruItem> RemoveRequested;

        public MruItemControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            ContextMenu menu = RootBorder.ContextMenu;
            ThemedContextMenuHelper.ApplyVsTheme(menu);

            if (menu != null)
            {
                ((MenuItem)menu.Items[0]).Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.Open);
                ((MenuItem)menu.Items[1]).Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.NewWindow);
                ((MenuItem)menu.Items[2]).Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.FolderOpened);
                ((MenuItem)menu.Items[3]).Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.Console);
                // Items[4] is Separator
                ((MenuItem)menu.Items[5]).Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.Copy);
                // Items[6] is Separator
                _pinMenuItem = (MenuItem)menu.Items[7];
                _pinMenuItem.Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.Pin);
                ((MenuItem)menu.Items[8]).Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.DeleteListItem);
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (MruItem != null)
            {
                // Set icon
                Icon.Moniker = FileIconHelper.GetIconForMruItem(MruItem);

                // Update pin icon and menu item text
                UpdatePinState();
            }
        }

        private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            RootBorder.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.CommandBarMouseOverBackgroundBeginBrushKey);
        }

        private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            RootBorder.Background = Brushes.Transparent;
        }

        private void RootBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && MruItem != null)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(() => OpenItemAsync()).FileAndForget(nameof(MruItemControl));
            }
        }

        private async System.Threading.Tasks.Task OpenItemAsync()
        {
            if (MruItem == null || string.IsNullOrWhiteSpace(MruItem.Path))
                return;

            try
            {
                await VsCommandService.OpenPathAsync(MruItem.Path);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", 
                    $"Failed to open: {ex.Message}");
            }
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            PinToggleRequested?.Invoke(this, MruItem);
            UpdatePinState();
        }

        private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(() => OpenItemAsync()).FileAndForget(nameof(MruItemControl));
        }

        private void OpenInNewInstanceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MruItem == null || string.IsNullOrWhiteSpace(MruItem.Path))
                return;

            try
            {
                VsCommandService.OpenInNewInstance(MruItem.Path);
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        private void OpenFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MruItem == null || string.IsNullOrWhiteSpace(MruItem.Path))
                return;

            try
            {
                var folder = MruItem.Type == MruItemType.Folder 
                    ? MruItem.Path 
                    : Path.GetDirectoryName(MruItem.Path);

                if (Directory.Exists(folder))
                {
                    Process.Start("explorer.exe", folder);
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        private void OpenTerminalMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MruItem == null || string.IsNullOrWhiteSpace(MruItem.Path))
                return;

            try
            {
                VsCommandService.OpenTerminalAtPath(MruItem.Path);
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MruItem != null && !string.IsNullOrWhiteSpace(MruItem.Path))
            {
                Clipboard.SetText(MruItem.Path);
            }
        }

        private void PinMenuItem_Click(object sender, RoutedEventArgs e)
        {
            PinToggleRequested?.Invoke(this, MruItem);
            UpdatePinState();
        }

        private void RemoveMenuItem_Click(object sender, RoutedEventArgs e)
        {
            RemoveRequested?.Invoke(this, MruItem);
        }

        private void UpdatePinState()
        {
            if (MruItem != null)
            {
                if (_pinMenuItem != null)
                {
                    _pinMenuItem.Header = MruItem.IsPinned ? "Unpin" : "Pin";
                }

                PinIcon.Moniker = MruItem.IsPinned
                    ? KnownMonikers.Unpin
                    : KnownMonikers.Pin;
                PinButton.ToolTip = MruItem.IsPinned ? "Unpin" : "Pin";
            }
        }
    }

    /// <summary>
    /// Converter that returns Collapsed for null values, Visible otherwise.
    /// </summary>
    public class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value == null ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
