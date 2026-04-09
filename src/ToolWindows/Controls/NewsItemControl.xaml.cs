using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using StartScreen.Models;
using System;
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
            RootBorder.Background = Brushes.Transparent;

            if (NewsPost != null && !NewsPost.IsPinned)
            {
                PinButton.Visibility = Visibility.Collapsed;
            }
        }

        private void RootBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                OpenInBrowser();
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
