using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using StartScreen.Models;

namespace StartScreen.ToolWindows.Controls
{
    public partial class YouTubeVideoControl : UserControl
    {
        public event EventHandler FocusNewsRequested;

        public YouTubeVideoControl()
        {
            InitializeComponent();

            ContextMenu menu = RootBorder.ContextMenu;
            ThemedContextMenuHelper.ApplyVsTheme(menu);

            if (menu != null)
            {
                ((MenuItem)menu.Items[0]).Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.BrowserLink);
                // Items[1] is Separator
                ((MenuItem)menu.Items[2]).Icon = ThemedContextMenuHelper.CreateMenuIcon(KnownMonikers.Copy);
            }
        }

        private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            RootBorder.Background = new SolidColorBrush(
                (Color)FindResource(EnvironmentColors.CommandBarMouseOverBackgroundBeginColorKey));
        }

        private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!RootBorder.IsKeyboardFocusWithin)
            {
                RootBorder.Background = Brushes.Transparent;
            }
        }

        private void RootBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && DataContext is YouTubeVideo video)
            {
                OpenVideo(video);
                e.Handled = true;
            }
        }

        private void RootBorder_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && DataContext is YouTubeVideo video)
            {
                OpenVideo(video);
                e.Handled = true;
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
                CopyUrlMenuItem_Click(sender, null);
                e.Handled = true;
            }
        }

        private void RootBorder_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            RootBorder.Background = new SolidColorBrush(
                (Color)FindResource(EnvironmentColors.CommandBarMouseOverBackgroundBeginColorKey));
        }

        private void RootBorder_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!RootBorder.IsMouseOver)
            {
                RootBorder.Background = Brushes.Transparent;
            }
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

        private static void OpenVideo(YouTubeVideo video)
        {
            if (!string.IsNullOrEmpty(video.Url))
            {
                Process.Start(video.Url);
            }
        }

        private void OpenInBrowserMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is YouTubeVideo video)
            {
                OpenVideo(video);
            }
        }

        private void CopyUrlMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is YouTubeVideo video && !string.IsNullOrEmpty(video.Url))
            {
                Clipboard.SetText(video.Url);
            }
        }
    }
}
