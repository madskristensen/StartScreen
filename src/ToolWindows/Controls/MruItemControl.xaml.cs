using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.ToolWindows.Controls
{
    public partial class MruItemControl : UserControl
    {
        private MruItem MruItem => DataContext as MruItem;
        private MenuItem _pinMenuItem;
        private MenuItem _gitPullMenuItem;
        private MenuItem _deleteCopilotChatMenuItem;
        private Point? _dragStartPoint;
        private bool _isGitPullRunning;

        public event EventHandler<MruItem> PinToggleRequested;
        public event EventHandler<MruItem> RemoveRequested;
        public event EventHandler FocusSearchBoxRequested;
        public event EventHandler FocusDevHubRequested;
        public event EventHandler<MruItem> SelectionRequested;
        public event EventHandler<MruItem> RepositorySummaryRequested;
        public event EventHandler<MruItem> RepositoryReviewsRequested;

        public MruItemControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            // Only show tooltip on mouse hover, not keyboard focus
            ToolTipService.SetIsEnabled(RootBorder, false);

            ThemedContextMenuHelper.AttachLazyContextMenu(RootBorder, CreateContextMenu);
        }

        private ContextMenu CreateContextMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(ThemedContextMenuHelper.CreateMenuItem("Open", "Open", OpenMenuItem_Click, KnownMonikers.Open, "Enter"));
            menu.Items.Add(ThemedContextMenuHelper.CreateMenuItem("OpenInNewInstance", "Open in New Instance", OpenInNewInstanceMenuItem_Click, KnownMonikers.NewWindow, "Ctrl+Enter"));
            menu.Items.Add(ThemedContextMenuHelper.CreateMenuItem("OpenFolder", "Open Containing Folder", OpenFolderMenuItem_Click, KnownMonikers.FolderOpened, "O"));
            menu.Items.Add(ThemedContextMenuHelper.CreateMenuItem("OpenTerminal", "Open in Terminal", OpenTerminalMenuItem_Click, KnownMonikers.Console, "T"));
            _gitPullMenuItem = ThemedContextMenuHelper.CreateMenuItem("GitPull", "Git Pull", GitPullMenuItem_Click, KnownMonikers.GitRepository, "G");
            menu.Items.Add(_gitPullMenuItem);
            _deleteCopilotChatMenuItem = ThemedContextMenuHelper.CreateMenuItem("DeleteCopilotChat", "Delete Copilot Chat Sessions", DeleteCopilotChatMenuItem_Click, CopilotMonikers.GitHubCopilot);
            menu.Items.Add(_deleteCopilotChatMenuItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(ThemedContextMenuHelper.CreateMenuItem("CopyPath", "Copy Path", CopyPathMenuItem_Click, KnownMonikers.Copy, "Ctrl+C"));
            menu.Items.Add(new Separator());
            _pinMenuItem = ThemedContextMenuHelper.CreateMenuItem("Pin", "Pin", PinMenuItem_Click, KnownMonikers.Pin, "P");
            menu.Items.Add(_pinMenuItem);
            menu.Items.Add(ThemedContextMenuHelper.CreateMenuItem("Remove", "Remove from List", RemoveMenuItem_Click, KnownMonikers.Cancel, "Del"));
            ThemedContextMenuHelper.ApplyVsTheme(menu);
            menu.Opened += ContextMenu_Opened;
            return menu;
        }

        private void RepositoryReviews_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (MruItem != null)
            {
                SelectionRequested?.Invoke(this, MruItem);
                RepositoryReviewsRequested?.Invoke(this, MruItem);
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
            CardInteractionHelper.UpdateHighlight(RootBorder);
            ToolTipService.SetIsEnabled(RootBorder, true);
        }

        private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
            ToolTipService.SetIsEnabled(RootBorder, false);
        }

        private void RootBorder_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
            if (MruItem != null)
            {
                SelectionRequested?.Invoke(this, MruItem);
            }
        }

        private void RootBorder_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
        }

        /// <summary>
        /// Updates the visual selection state of this control.
        /// Call this when the item's IsSelected property changes.
        /// </summary>
        internal void UpdateSelectionVisual()
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
        }

        private void RootBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && MruItem != null)
            {
                // For pinned items, ignore if drag was in progress
                if (MruItem.IsPinned && !_dragStartPoint.HasValue)
                    return;

                _dragStartPoint = null;

                RootBorder.Focus();
                SelectionRequested?.Invoke(this, MruItem);
            }
        }

        private void RootBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && MruItem != null && !IsButtonSource(e.OriginalSource as DependencyObject))
            {
                _dragStartPoint = null;
                e.Handled = true;
                StartScreenPackage.BackgroundTaskFactory.RunAsync(OpenItemAsync)
                    .FileAndForget(nameof(MruItemControl));
            }
        }

        private static bool IsButtonSource(DependencyObject source)
        {
            while (source != null)
            {
                if (source is Button)
                    return true;
                source = source is ContentElement content
                    ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                    : VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void RepositorySummary_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (MruItem != null)
            {
                SelectionRequested?.Invoke(this, MruItem);
                RepositorySummaryRequested?.Invoke(this, MruItem);
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
            if ((e.Key == Key.Enter || e.Key == Key.Space) && IsButtonSource(e.OriginalSource as DependencyObject))
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
            else if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.None)
            {
                GitPullMenuItem_Click(sender, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                RemoveMenuItem_Click(sender, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Oem3 || e.SystemKey == Key.Oem3)
            {
                if (Keyboard.Modifiers == ModifierKeys.Alt)
                {
                    FocusSearchBoxRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Down || e.Key == Key.Up)
            {
                var moved = MoveToAdjacentMruItem(e.Key == Key.Down);
                if (!moved && e.Key == Key.Up)
                {
                    FocusSearchBoxRequested?.Invoke(this, EventArgs.Empty);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                FocusDevHubRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            UpdatePinState();
            UpdateGitPullState();
            UpdateDeleteCopilotChatState();
        }

        private bool MoveToAdjacentMruItem(bool forward)
        {
            var list = FindMruList(this);
            if (list == null)
                return false;

            var next = GetAdjacentItemIndex(list, MruItem, forward);
            if (next < 0)
                return false;

            GetItemControl(list, next)?.FocusItem();
            return true;
        }

        internal static int GetAdjacentItemIndex(ListBox list, object item, bool forward)
        {
            var index = list.Items.IndexOf(item);
            var next = forward ? index + 1 : index - 1;
            return index >= 0 && next >= 0 && next < list.Items.Count ? next : -1;
        }

        private static ListBox FindMruList(DependencyObject element)
        {
            DependencyObject current = element;
            while (current != null)
            {
                if (current is ListBox list && list.Name == "MruPanel")
                    return list;

                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        internal static ListBoxItem RealizeItemContainer(ListBox list, int index)
        {
            if (index < 0 || index >= list.Items.Count)
                return null;

            if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem realized && realized.IsMeasureValid)
            {
                realized.BringIntoView();
                return realized;
            }

            // Initialize the generator before scrolling, including when returning from another section.
            list.ApplyTemplate();
            list.UpdateLayout();
            list.ScrollIntoView(list.Items[index]);
            list.UpdateLayout();
            var container = list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            container?.ApplyTemplate();
            container?.UpdateLayout();
            return container;
        }

        internal static MruItemControl GetItemControl(ListBox list, int index)
        {
            var container = RealizeItemContainer(list, index);
            return container == null ? null : FindItemControl(container);
        }

        private static MruItemControl FindItemControl(DependencyObject parent)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is MruItemControl mru)
                    return mru;
                var match = FindItemControl(child);
                if (match != null)
                    return match;
            }
            return null;
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
            StartScreenPackage.BackgroundTaskFactory.RunAsync(() => OpenItemAsync())
                .FileAndForget(nameof(MruItemControl));
        }

        private void OpenInNewInstanceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            RunWorkspaceActionAsync(item => VsCommandService.OpenInNewInstance(item.Path))
                .FileAndForget(nameof(MruItemControl));
        }

        private void OpenFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            RunWorkspaceActionAsync(item =>
            {
                var folder = item.Type == MruItemType.Folder
                    ? item.Path
                    : Path.GetDirectoryName(item.Path);

                if (!Directory.Exists(folder))
                    throw new DirectoryNotFoundException($"The workspace folder could not be found: {folder}");

                Process.Start("explorer.exe", folder);
            }).FileAndForget(nameof(MruItemControl));
        }

        private void OpenTerminalMenuItem_Click(object sender, RoutedEventArgs e)
        {
            RunWorkspaceActionAsync(item => VsCommandService.OpenTerminalAtPath(item.Path))
                .FileAndForget(nameof(MruItemControl));
        }

        private async Task RunWorkspaceActionAsync(Action<MruItem> action)
        {
            MruItem item = MruItem;
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
                return;

            try
            {
                await System.Threading.Tasks.Task.Run(() => action(item));
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", $"Failed to open {item.Name}: {ex.Message}");
            }
        }

        private void GitPullMenuItem_Click(object sender, RoutedEventArgs e)
        {
            PullGitAsync().FileAndForget(nameof(MruItemControl));
        }

        private async Task PullGitAsync()
        {
            MruItem item = MruItem;

            if (_isGitPullRunning || item?.HasGitBranch != true)
                return;

            _isGitPullRunning = true;
            UpdateGitPullState();

            try
            {
                await VS.StatusBar.ShowMessageAsync($"Pulling Git changes for {item.Name}...");
                GitCommandResult result = await MruService.PullGitAsync(item, StartScreenPackage.ShutdownToken);
                StartScreenPackage.ShutdownToken.ThrowIfCancellationRequested();

                if (!result.Succeeded)
                {
                    await VS.StatusBar.ShowMessageAsync($"Git pull failed for {item.Name}.");
                    await VS.MessageBox.ShowErrorAsync("Start Screen", $"Git pull failed: {result.ErrorMessage}");
                }
                else
                {
                    await VS.StatusBar.ShowMessageAsync($"Git pull completed for {item.Name}.");
                }
            }
            catch (OperationCanceledException ex) when (StartScreenPackage.ShutdownToken.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.StatusBar.ShowMessageAsync($"Git pull failed for {item.Name}.");
                await VS.MessageBox.ShowErrorAsync("Start Screen", $"Git pull failed: {ex.Message}");
            }
            finally
            {
                _isGitPullRunning = false;
                UpdateGitPullState();
            }
        }

        private void UpdateGitPullState()
        {
            if (_gitPullMenuItem == null)
                return;

            _gitPullMenuItem.Header = _isGitPullRunning ? "Pulling..." : "Git Pull";
            _gitPullMenuItem.IsEnabled = !_isGitPullRunning && MruItem?.HasGitBranch == true && MruItem?.HasCurrentOperation != true;
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

        private void UpdateDeleteCopilotChatState()
        {
            if (_deleteCopilotChatMenuItem == null)
            {
                return;
            }

            var hasSessions = MruItem?.HasCopilotChatSessions == true;
            _deleteCopilotChatMenuItem.Visibility = hasSessions ? Visibility.Visible : Visibility.Collapsed;
            _deleteCopilotChatMenuItem.IsEnabled = hasSessions;
        }

        private void DeleteCopilotChatMenuItem_Click(object sender, RoutedEventArgs e)
        {
            DeleteCopilotChatAsync().FileAndForget(nameof(MruItemControl));
        }

        private async Task DeleteCopilotChatAsync()
        {
            MruItem item = MruItem;
            if (item == null || string.IsNullOrWhiteSpace(item.Path) || !item.HasCopilotChatSessions)
            {
                return;
            }

            try
            {
                var confirmed = await VS.MessageBox.ShowConfirmAsync(
                    "Start Screen",
                    $"Delete all GitHub Copilot Chat sessions for '{item.Name}'? This cannot be undone.");

                if (!confirmed)
                {
                    return;
                }

                var path = item.Path;
                MruItemType type = item.Type;

                var result = await System.Threading.Tasks.Task.Run(() =>
                {
                    var deleted = CopilotChatHelper.DeleteAllSessions(path, type);
                    return (Deleted: deleted, Remaining: CopilotChatHelper.CountSessions(path, type));
                });

                item.CopilotChatSessionCount = result.Remaining;

                await VS.StatusBar.ShowMessageAsync(result.Deleted == 1
                    ? $"Deleted 1 Copilot Chat session for {item.Name}."
                    : $"Deleted {result.Deleted} Copilot Chat sessions for {item.Name}.");
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", $"Failed to delete Copilot Chat sessions: {ex.Message}");
            }
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
