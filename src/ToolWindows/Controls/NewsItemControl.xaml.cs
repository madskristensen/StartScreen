using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Imaging;
using StartScreen.Models;

namespace StartScreen.ToolWindows.Controls
{
    public partial class NewsItemControl : UserControl
    {
        private NewsPost NewsPost => DataContext as NewsPost;

        private readonly CardClickGesture _clickGesture;

        public event EventHandler<NewsPost> PinToggleRequested;

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
