using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using StartScreen.Models;

namespace StartScreen.ToolWindows.Controls
{
    public partial class NewsItemControl : UserControl
    {
        private NewsPost NewsPost => DataContext as NewsPost;

        private readonly CardClickGesture _clickGesture;

        public event EventHandler<NewsPost> PinToggleRequested;
        public event EventHandler FocusDevHubRequested;
        public event EventHandler FocusYouTubeRequested;

        public NewsItemControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            _clickGesture = CardInteractionHelper.TrackPress(RootBorder);
            RootBorder.ContextMenu = ThemedContextMenuHelper.CreateLinkMenu(() => NewsPost?.Url);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            UpdatePinState();
        }

        private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
            UpdatePinState();
        }

        private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
            UpdatePinState();
        }

        private void RootBorder_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
            UpdatePinState();
        }

        private void RootBorder_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
            UpdatePinState();
        }

        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (CardInteractionHelper.IsCardSurface(RootBorder, e.OriginalSource as DependencyObject))
                RootBorder.Focus();
        }

        private void RootBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (CardInteractionHelper.CompleteClick(_clickGesture, RootBorder, e))
            {
                e.Handled = true;
                OpenInBrowser();
            }
        }

        private void RootBorder_KeyDown(object sender, KeyEventArgs e)
        {
            if (NewsPost == null || !RootBorder.IsKeyboardFocused)
                return;

            if ((e.Key == Key.Enter || e.Key == Key.Space) && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                if (!e.IsRepeat)
                    OpenInBrowser();
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                ThemedContextMenuHelper.CopyLink(NewsPost.Url);
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
                    FocusDevHubRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    MoveHorizontally(false);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                if (IsInLastColumn())
                {
                    FocusYouTubeRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    MoveHorizontally(true);
                }
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

        private bool IsInLastColumn()
        {
            FrameworkElement parent = FindNewsScrollViewer(this);
            if (parent == null)
                return false;

            var allItems = new List<NewsItemControl>();
            CollectNewsItemControls(parent, allItems);

            var index = allItems.IndexOf(this);
            if (index < 0)
                return false;

            var columnsPerRow = GetColumnsPerRow(allItems);
            return index == allItems.Count - 1 || index % columnsPerRow == columnsPerRow - 1;
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
                if (current is FrameworkElement fe && fe.Name == "NewsPanel")
                    return fe;

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
            ThemedContextMenuHelper.OpenLink(NewsPost?.Url);
        }

        private void OpenInBrowserMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenInBrowser();
        }

        private void CopyUrlMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThemedContextMenuHelper.CopyLink(NewsPost?.Url);
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            _clickGesture.Cancel();
            e.Handled = true;
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
                PinButton.Visibility = NewsPost.IsPinned || RootBorder.IsMouseOver || RootBorder.IsKeyboardFocusWithin
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }
}
