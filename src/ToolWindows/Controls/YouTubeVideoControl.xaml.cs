using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StartScreen.Models;

namespace StartScreen.ToolWindows.Controls
{
    public partial class YouTubeVideoControl : UserControl
    {
        private readonly CardClickGesture _clickGesture;

        public event EventHandler FocusNewsRequested;

        public YouTubeVideoControl()
        {
            InitializeComponent();
            _clickGesture = CardInteractionHelper.TrackPress(RootBorder);
            RootBorder.ContextMenu = ThemedContextMenuHelper.CreateLinkMenu(() => (DataContext as YouTubeVideo)?.Url);
            RootBorder.MouseLeftButtonDown += (sender, e) =>
            {
                if (CardInteractionHelper.IsCardSurface(RootBorder, e.OriginalSource as DependencyObject))
                    RootBorder.Focus();
            };
        }

        private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
        }

        private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
        }

        private void RootBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (CardInteractionHelper.CompleteClick(_clickGesture, RootBorder, e) && DataContext is YouTubeVideo video)
            {
                e.Handled = true;
                ThemedContextMenuHelper.OpenLink(video.Url);
            }
        }

        private void RootBorder_KeyDown(object sender, KeyEventArgs e)
        {
            if (!RootBorder.IsKeyboardFocused)
                return;

            if ((e.Key == Key.Enter || e.Key == Key.Space) && Keyboard.Modifiers == ModifierKeys.None && DataContext is YouTubeVideo video)
            {
                e.Handled = true;
                if (!e.IsRepeat)
                    ThemedContextMenuHelper.OpenLink(video.Url);
            }
            else if (e.Key == Key.Up || e.Key == Key.Down)
            {
                MoveVertically(e.Key == Key.Down);
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                FocusNewsRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                ThemedContextMenuHelper.CopyLink((DataContext as YouTubeVideo)?.Url);
            }
        }

        private void RootBorder_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
        }

        private void RootBorder_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CardInteractionHelper.UpdateHighlight(RootBorder);
        }

        private void MoveVertically(bool down)
        {
            var allItems = CollectSiblingControls();
            var index = allItems.IndexOf(this);
            if (index < 0)
                return;

            var next = down ? index + 1 : index - 1;
            if (next >= 0 && next < allItems.Count)
            {
                allItems[next].RootBorder.Focus();
            }
        }

        private List<YouTubeVideoControl> CollectSiblingControls()
        {
            var results = new List<YouTubeVideoControl>();
            DependencyObject current = VisualTreeHelper.GetParent(this);

            while (current != null)
            {
                if (current is ItemsControl)
                {
                    CollectYouTubeControls(current, results);
                    return results;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return results;
        }

        private static void CollectYouTubeControls(DependencyObject parent, List<YouTubeVideoControl> results)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is YouTubeVideoControl yt)
                {
                    results.Add(yt);
                }
                else
                {
                    CollectYouTubeControls(child, results);
                }
            }
        }

        private void OpenInBrowserMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThemedContextMenuHelper.OpenLink((DataContext as YouTubeVideo)?.Url);
        }

        private void CopyUrlMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThemedContextMenuHelper.CopyLink((DataContext as YouTubeVideo)?.Url);
        }
    }
}
