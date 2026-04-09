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

        public NewsItemControl()
        {
            InitializeComponent();
        }

        private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
        {
        }

        private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
        {
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
    }
}
