using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using StartScreen.Models;

namespace StartScreen.ToolWindows.Controls
{
    public partial class YouTubeVideoControl : UserControl
    {
        public YouTubeVideoControl()
        {
            InitializeComponent();
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

        private static void OpenVideo(YouTubeVideo video)
        {
            if (!string.IsNullOrEmpty(video.Url))
            {
                Process.Start(video.Url);
            }
        }
    }
}
