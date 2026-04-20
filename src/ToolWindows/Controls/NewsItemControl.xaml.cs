using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using StartScreen.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StartScreen.ToolWindows.Controls
{
    public partial class NewsItemControl : UserControl
    {
        private NewsPost NewsPost => DataContext as NewsPost;

        public event EventHandler<NewsPost> PinToggleRequested;
        public event EventHandler FocusMruRequested;

        public NewsItemControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            ContextMenu menu = RootBorder.ContextMenu;
            ThemedContextMenuHelper.ApplyVsTheme(menu);

            if (menu != null)
            {
                ((MenuItem)menu.Items[0]).Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.BrowserLink);
                // Items[1] is Separator
                ((MenuItem)menu.Items[2]).Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.Copy);
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            UpdatePinState();
        }

        private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            RootBorder.SetResourceReference(Border.BackgroundProperty,
                EnvironmentColors.CommandBarMouseOverBackgroundBeginBrushKey);

            if (NewsPost != null && !NewsPost.IsPinned)
            {
                PinButton.Visibility = Visibility.Visible;
            }
        }

        private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!RootBorder.IsKeyboardFocused)
            {
                RootBorder.Background = Brushes.Transparent;
            }

            if (NewsPost != null && !NewsPost.IsPinned)
            {
                PinButton.Visibility = Visibility.Collapsed;
            }
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
                RootBorder.Background = Brushes.Transparent;
            }
        }

        private void RootBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                OpenInBrowser();
            }
        }

        private void RootBorder_KeyDown(object sender, KeyEventArgs e)
        {
            if (NewsPost == null)
                return;

            if (e.Key == Key.Enter)
            {
                OpenInBrowser();
                e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopyUrlMenuItem_Click(sender, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.Key == Key.Up)
            {
                MoveVertically(e.Key == Key.Down);
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                if (IsInFirstColumn())
                {
                    FocusMruRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    MoveHorizontally(false);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                MoveHorizontally(true);
                e.Handled = true;
            }
        }

        private bool IsInFirstColumn()
        {
            FrameworkElement parent = FindNewsScrollViewer(this);
            if (parent == null)
                return true;

            var allItems = new List<NewsItemControl>();
            CollectNewsItemControls(parent, allItems);

            var index = allItems.IndexOf(this);
            if (index < 0)
                return true;

            var columnsPerRow = GetColumnsPerRow(allItems);
            return index % columnsPerRow == 0;
        }

        private void MoveVertically(bool down)
        {
            FrameworkElement parent = FindNewsScrollViewer(this);
            if (parent == null)
                return;

            var allItems = new List<NewsItemControl>();
            CollectNewsItemControls(parent, allItems);

            var index = allItems.IndexOf(this);
            if (index < 0)
                return;

            var columnsPerRow = GetColumnsPerRow(allItems);
            var next = down ? index + columnsPerRow : index - columnsPerRow;

            if (next >= 0 && next < allItems.Count)
            {
                allItems[next].RootBorder.Focus();
            }
        }

        private void MoveHorizontally(bool right)
        {
            FrameworkElement parent = FindNewsScrollViewer(this);
            if (parent == null)
                return;

            var allItems = new List<NewsItemControl>();
            CollectNewsItemControls(parent, allItems);

            var index = allItems.IndexOf(this);
            if (index < 0)
                return;

            var next = right ? index + 1 : index - 1;
            if (next >= 0 && next < allItems.Count)
            {
                allItems[next].RootBorder.Focus();
            }
        }

        private static int GetColumnsPerRow(List<NewsItemControl> items)
        {
            if (items.Count < 2)
                return 1;

            // Compare the Y position of consecutive items to find how many fit in one row
            var firstY = items[0].TranslatePoint(new Point(0, 0), items[0]).Y;
            Point firstItemPos = items[0].PointToScreen(new Point(0, 0));

            for (var i = 1; i < items.Count; i++)
            {
                Point pos = items[i].PointToScreen(new Point(0, 0));
                if (Math.Abs(pos.Y - firstItemPos.Y) > 5)
                    return i;
            }

            return items.Count;
        }

        private void MoveToAdjacentNewsItem(bool forward)
        {
            FrameworkElement parent = FindNewsScrollViewer(this);
            if (parent == null)
                return;

            var allItems = new List<NewsItemControl>();
            CollectNewsItemControls(parent, allItems);

            var index = allItems.IndexOf(this);
            if (index < 0)
                return;

            var next = forward ? index + 1 : index - 1;
            if (next >= 0 && next < allItems.Count)
            {
                allItems[next].RootBorder.Focus();
            }
        }

        private static FrameworkElement FindNewsScrollViewer(DependencyObject element)
        {
            DependencyObject current = element;
            while (current != null)
            {
                if (current is StackPanel sp && sp.Name == "NewsPanel")
                    return sp;

                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static void CollectNewsItemControls(DependencyObject parent, List<NewsItemControl> results)
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

        private void OpenInBrowser()
        {
            if (NewsPost != null && !string.IsNullOrWhiteSpace(NewsPost.Url))
            {
                try
                {
                    Process.Start(NewsPost.Url);
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            }
        }

        private void OpenInBrowserMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenInBrowser();
        }

        private void CopyUrlMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (NewsPost != null && !string.IsNullOrWhiteSpace(NewsPost.Url))
            {
                Clipboard.SetText(NewsPost.Url);
            }
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            PinToggleRequested?.Invoke(this, NewsPost);
            UpdatePinState();
        }

        private void UpdatePinState()
        {
            if (NewsPost != null)
            {
                PinIcon.Moniker = NewsPost.IsPinned
                    ? KnownMonikers.Unpin
                    : KnownMonikers.Pin;
                PinButton.ToolTip = NewsPost.IsPinned ? "Unpin" : "Pin";
                PinButton.Visibility = NewsPost.IsPinned
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }
}
