using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenControl
    {
        private readonly ConditionalWeakTable<Border, CardClickGesture> _eventClickGestures = new ConditionalWeakTable<Border, CardClickGesture>();

        private void ActivateEventsTab(bool force = false)
        {
            StartScreenPackage.BackgroundTaskFactory.RunAsync(() => ViewModel.EventSection.ActivateTabAsync(force))
                .FileAndForget(nameof(StartScreenControl));
        }

        private void EventCard_Loaded(object sender, RoutedEventArgs e)
        {
            _eventClickGestures.GetValue((Border)sender, CardInteractionHelper.TrackPress);
        }

        private void EventCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var card = (Border)sender;
            if (CardInteractionHelper.IsCardSurface(card, e.OriginalSource as DependencyObject))
                card.Focus();
        }

        private void EventCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var card = (Border)sender;
            if (CardInteractionHelper.CompleteClick(_eventClickGestures.GetValue(card, CardInteractionHelper.TrackPress), card, e)
                && card.DataContext is ConferenceEvent item)
            {
                e.Handled = true;
                OpenEventLinkAsync(item.Url).FileAndForget(nameof(StartScreenControl));
            }
        }

        private void EventCard_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is Border card && card.IsKeyboardFocused && card.DataContext is ConferenceEvent item
                && (e.Key == Key.Enter || e.Key == Key.Space) && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                if (!e.IsRepeat)
                    OpenEventLinkAsync(item.Url).FileAndForget(nameof(StartScreenControl));
            }
        }

        private void EventLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            e.Handled = true;
            OpenEventLinkAsync(e.Uri.AbsoluteUri).FileAndForget(nameof(StartScreenControl));
        }

        private static async Task OpenEventLinkAsync(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("The event link must use HTTPS.");
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await VS.MessageBox.ShowErrorAsync("Start Screen", "Could not open the event link. " + ex.Message);
            }
        }
    }
}
