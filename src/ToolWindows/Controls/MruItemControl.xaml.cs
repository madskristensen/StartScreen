using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StartScreen.ToolWindows.Controls
{
    public partial class MruItemControl : UserControl
    {
        private MruItem MruItem => DataContext as MruItem;

        public event EventHandler<MruItem> PinToggleRequested;
        public event EventHandler<MruItem> RemoveRequested;

        public MruItemControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
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
        }

        private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
        {
        }

        private void RootBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && MruItem != null)
            {
                _ = OpenItemAsync();
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
            _ = OpenItemAsync();
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
                VS.MessageBox.ShowErrorAsync("Start Screen", $"Failed to open folder: {ex.Message}")
                    .FileAndForget(nameof(StartScreen));
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
                PinMenuItem.Header = MruItem.IsPinned ? "Unpin" : "Pin";
                PinIcon.Moniker = MruItem.IsPinned
                    ? KnownMonikers.Unpin
                    : KnownMonikers.Pin;
                PinButton.ToolTip = MruItem.IsPinned ? "Unpin" : "Pin";
            }
        }
    }
}
