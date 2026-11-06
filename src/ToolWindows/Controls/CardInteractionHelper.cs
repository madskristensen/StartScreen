using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace StartScreen.ToolWindows.Controls
{
    internal sealed class CardClickGesture
    {
        private bool _pressedHere;

        internal void Begin(MouseButton button, bool isCardSurface)
        {
            _pressedHere = button == MouseButton.Left && isCardSurface;
        }

        internal bool Complete(MouseButton button, bool isCardSurface)
        {
            var activate = _pressedHere && button == MouseButton.Left && isCardSurface;
            Cancel();
            return activate;
        }

        internal void Cancel()
        {
            _pressedHere = false;
        }
    }

    internal static class CardInteractionHelper
    {
        internal static CardClickGesture TrackPress(Border border)
        {
            var gesture = new CardClickGesture();
            border.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((sender, e) =>
            {
                gesture.Begin(e.ChangedButton, IsCardSurface(border, e.OriginalSource as DependencyObject));
            }), true);
            border.MouseLeave += (sender, e) => gesture.Cancel();
            border.Unloaded += (sender, e) => gesture.Cancel();
            border.DataContextChanged += (sender, e) => gesture.Cancel();
            border.LostMouseCapture += (sender, e) => gesture.Cancel();
            return gesture;
        }

        internal static bool IsCardSurface(Border border, DependencyObject source)
        {
            while (source != null && source != border)
            {
                if (source is ButtonBase || source is Hyperlink || source is MenuItem)
                    return false;

                source = source is ContentElement content
                    ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                    : source is Visual || source is System.Windows.Media.Media3D.Visual3D
                        ? VisualTreeHelper.GetParent(source)
                        : LogicalTreeHelper.GetParent(source);
            }

            return source == border;
        }

        internal static bool CompleteClick(CardClickGesture gesture, Border border, MouseButtonEventArgs e)
        {
            var position = e.GetPosition(border);
            return gesture.Complete(e.ChangedButton,
                new Rect(border.RenderSize).Contains(position)
                && IsCardSurface(border, e.OriginalSource as DependencyObject));
        }

        internal static void UpdateHighlight(Border border)
        {
            SetHighlight(border, border.IsMouseOver, border.IsKeyboardFocusWithin,
                EnvironmentColors.CommandBarMouseOverBackgroundBeginBrushKey);
        }

        internal static void SetHighlight(Border border, bool isMouseOver, bool isKeyboardFocusWithin, object resourceKey)
        {
            if (isMouseOver || isKeyboardFocusWithin)
            {
                border.SetResourceReference(Border.BackgroundProperty, resourceKey);
            }
            else
            {
                // Restore style triggers, including MRU selection, instead of overriding them.
                border.ClearValue(Border.BackgroundProperty);
            }
        }
    }
}