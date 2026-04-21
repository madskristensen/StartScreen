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
        private Point? _dragStartPoint;

        public event EventHandler<MruItem> PinToggleRequested;
        public event EventHandler<MruItem> RemoveRequested;
        public event EventHandler<MruItem> SelectionRequested;
        public event EventHandler FocusSearchBoxRequested;
        public event EventHandler FocusDevHubRequested;
        public event EventHandler FocusActionBarRequested;

        public MruItemControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            // Only show tooltip on mouse hover, not keyboard focus
            ToolTipService.SetIsEnabled(RootBorder, false);

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
                ((MenuItem)menu.Items[8]).Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.Cancel);
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
            ToolTipService.SetIsEnabled(RootBorder, true);
        }

        private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!RootBorder.IsKeyboardFocused)
            {
                // Clear local value so the DataTrigger for IsSelected can take effect
                RootBorder.ClearValue(Border.BackgroundProperty);
            }
            ToolTipService.SetIsEnabled(RootBorder, false);
        }

        private void RootBorder_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            RootBorder.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.CommandBarMouseOverBackgroundBeginBrushKey);
        }

        private void RootBorder_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!RootBorder.IsMouseOver)
            {
                // Clear local value so the DataTrigger for IsSelected can take effect
                RootBorder.ClearValue(Border.BackgroundProperty);
            }
        }

        /// <summary>
        /// Updates the visual selection state of this control.
        /// Call this when the item's IsSelected property changes.
        /// </summary>
        internal void UpdateSelectionVisual()
        {
            if (MruItem?.IsSelected != true && !RootBorder.IsMouseOver && !RootBorder.IsKeyboardFocused)
            {
                RootBorder.ClearValue(Border.BackgroundProperty);
            }
        }

        private void RootBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && MruItem != null)
            {
                // For pinned items, ignore if drag was in progress
                if (MruItem.IsPinned && !_dragStartPoint.HasValue)
                    return;

                _dragStartPoint = null;

                // Single-click selects the item (Dev Hub filters to this repo)
                SelectionRequested?.Invoke(this, MruItem);
            }
        }

        private void RootBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && MruItem != null)
            {
                e.Handled = true;
                ThreadHelper.JoinableTaskFactory.RunAsync(() => OpenItemAsync()).FileAndForget(nameof(MruItemControl));
            }
        }

        internal void HandlePreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (MruItem != null && MruItem.IsPinned)
            {
                _dragStartPoint = e.GetPosition(this);
            }
        }

        internal void HandlePreviewMouseMove(MouseEventArgs e)
        {
            if (_dragStartPoint == null || e.LeftButton != MouseButtonState.Pressed || MruItem == null || !MruItem.IsPinned)
                return;

            Point currentPos = e.GetPosition(this);
            Vector diff = currentPos - _dragStartPoint.Value;

            if (Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance ||
                Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance)
            {
                _dragStartPoint = null;
                var data = new DataObject("PinnedMruItem", MruItem);
                DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
            }
        }

        private void RootBorder_KeyDown(object sender, KeyEventArgs e)
        {
            if (MruItem == null)
                return;

            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenInNewInstanceMenuItem_Click(sender, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                OpenMenuItem_Click(sender, null);
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.None)
            {
                OpenFolderMenuItem_Click(sender, null);
                e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopyPathMenuItem_Click(sender, null);
                e.Handled = true;
            }
            else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.None)
            {
                PinMenuItem_Click(sender, null);
                e.Handled = true;
            }
            else if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.None)
            {
                OpenTerminalMenuItem_Click(sender, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                RemoveMenuItem_Click(sender, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Oem3 && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                FocusSearchBoxRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.Key == Key.Up)
            {
                var moved = MoveToAdjacentMruItem(e.Key == Key.Down);
                if (!moved && e.Key == Key.Up)
                {
                    FocusActionBarRequested?.Invoke(this, EventArgs.Empty);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                FocusDevHubRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private bool MoveToAdjacentMruItem(bool forward)
        {
            FrameworkElement parent = FindMruScrollViewer(this);
            if (parent == null)
                return false;

            var allItems = new System.Collections.Generic.List<MruItemControl>();
            CollectMruItemControls(parent, allItems);

            var index = allItems.IndexOf(this);
            if (index < 0)
                return false;

            var next = forward ? index + 1 : index - 1;
            if (next >= 0 && next < allItems.Count)
            {
                allItems[next].RootBorder.Focus();
                return true;
            }
            return false;
        }

        private static FrameworkElement FindMruScrollViewer(DependencyObject element)
        {
            DependencyObject current = element;
            while (current != null)
            {
                if (current is StackPanel sp && sp.Name == "MruPanel")
                    return sp;

                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static void CollectMruItemControls(DependencyObject parent, System.Collections.Generic.List<MruItemControl> results)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is MruItemControl mru)
                {
                    results.Add(mru);
                }
                else
                {
                    CollectMruItemControls(child, results);
                }
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

        /// <summary>
        /// Returns true if the RootBorder currently has keyboard focus.
        /// </summary>
        internal bool IsItemFocused()
        {
            return RootBorder.IsKeyboardFocused;
        }

        /// <summary>
        /// Sets keyboard focus to the RootBorder.
        /// </summary>
        internal void FocusItem()
        {
            RootBorder.Focus();
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

    /// <summary>
    /// Converter that returns Strikethrough text decorations when the bound value is false (item does not exist).
    /// </summary>
    public class ExistsToTextDecorationsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool exists && !exists)
            {
                return TextDecorations.Strikethrough;
            }

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter that returns a dimmed opacity when the bound value is false (item does not exist).
    /// </summary>
    public class ExistsToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool exists && !exists)
            {
                return 0.5;
            }

            return 1.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
