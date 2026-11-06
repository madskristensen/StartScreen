using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StartScreen.Models;

namespace StartScreen.ToolWindows.Controls
{
    public partial class YouTubeVideoControl : UserControl
    {
        private readonly CardClickGesture _clickGesture;

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
