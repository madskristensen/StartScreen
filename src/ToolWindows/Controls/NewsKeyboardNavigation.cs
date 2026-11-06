using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace StartScreen.ToolWindows.Controls
{
    internal static class NewsKeyboardNavigation
    {
        internal static bool FocusFirstItem(TabControl tabs)
        {
            tabs.UpdateLayout();
            var targets = GetTargets(tabs);
            return targets.Count > 0
                ? Focus(targets[0].Target)
                : (tabs.SelectedItem as TabItem)?.Focus() == true;
        }

        internal static bool HandleKey(TabControl tabs, DependencyObject focused, Key key, Action moveLeft)
        {
            if (key != Key.Left && key != Key.Right && key != Key.Up && key != Key.Down)
                return false;

            var selected = tabs.SelectedItem as TabItem;
            if (selected == null)
                return false;

            if (focused is TabItem header && tabs.Items.Contains(header))
            {
                if (key == Key.Down)
                    FocusFirstItem(tabs);
                else if (key == Key.Left || key == Key.Right)
                {
                    var step = key == Key.Right ? 1 : -1;
                    var index = tabs.Items.IndexOf(header) + step;
                    while (index >= 0 && index < tabs.Items.Count)
                    {
                        if (tabs.Items[index] is TabItem next && next.IsVisible && next.IsEnabled)
                        {
                            next.IsSelected = true;
                            next.Focus();
                            return true;
                        }
                        index += step;
                    }
                    if (key == Key.Left)
                        moveLeft();
                }
                return true;
            }

            var targets = GetTargets(tabs);
            var current = targets.FindIndex(target => IsInside(focused, target.Target));
            if (current < 0)
                return false;

            var positions = targets.Select(target => target.Element.TranslatePoint(new Point(), tabs)).ToList();
            var adjacent = GetAdjacentIndex(positions, current, key);
            if (adjacent >= 0)
                Focus(targets[adjacent].Target);
            else if (key == Key.Up)
                selected.Focus();
            else if (key == Key.Left)
                moveLeft();
            return true;
        }

        internal static int GetAdjacentIndex(IReadOnlyList<Point> positions, int current, Key key)
        {
            if (current < 0 || current >= positions.Count)
                return -1;

            var origin = positions[current];
            var horizontal = key == Key.Left || key == Key.Right;
            var forward = key == Key.Right || key == Key.Down;
            var best = -1;
            var bestDistance = double.MaxValue;
            var bestAlignment = double.MaxValue;
            for (var index = 0; index < positions.Count; index++)
            {
                var dx = positions[index].X - origin.X;
                var dy = positions[index].Y - origin.Y;
                var distance = (horizontal ? dx : dy) * (forward ? 1 : -1);
                var alignment = Math.Abs(horizontal ? dy : dx);
                // Cards in a row share a top edge, including rows in separate pinned/unpinned grids.
                if (distance <= 5 || (horizontal && alignment > 5))
                    continue;

                if (distance < bestDistance - 5 || (Math.Abs(distance - bestDistance) <= 5 && alignment < bestAlignment))
                {
                    best = index;
                    bestDistance = distance;
                    bestAlignment = alignment;
                }
            }
            return best;
        }

        private static List<(FrameworkElement Element, DependencyObject Target)> GetTargets(TabControl tabs)
        {
            var targets = new List<(FrameworkElement, DependencyObject)>();
            if (tabs.SelectedContent is DependencyObject content)
                CollectTargets(content, targets);
            return targets;
        }

        private static void CollectTargets(DependencyObject parent, List<(FrameworkElement, DependencyObject)> targets)
        {
            if (parent is UIElement element && (!element.IsVisible || !element.IsEnabled))
                return;

            if (parent is Border border && border.Focusable)
            {
                targets.Add((border, border));
                return;
            }
            if (parent is TextBlock text)
                CollectLinks(text, text.Inlines, targets);

            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                CollectTargets(VisualTreeHelper.GetChild(parent, index), targets);
        }

        private static void CollectLinks(TextBlock text, InlineCollection inlines, List<(FrameworkElement, DependencyObject)> targets)
        {
            foreach (Inline inline in inlines)
            {
                if (inline is Hyperlink link && link.Focusable && link.IsEnabled)
                    targets.Add((text, link));
                else if (inline is Span span)
                    CollectLinks(text, span.Inlines, targets);
            }
        }

        private static bool IsInside(DependencyObject element, DependencyObject ancestor)
        {
            while (element != null)
            {
                if (ReferenceEquals(element, ancestor))
                    return true;
                element = element is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        private static bool Focus(DependencyObject target)
        {
            if (target is FrameworkElement element)
                element.BringIntoView();
            else if (target is FrameworkContentElement content)
                content.BringIntoView();
            return target is IInputElement input && ReferenceEquals(Keyboard.Focus(input), input);
        }
    }
}