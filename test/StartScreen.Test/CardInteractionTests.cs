using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test
{
    [TestClass]
    public class CardInteractionTests
    {
        [TestMethod]
        [DataRow(MouseButton.Left, true, MouseButton.Left, true, true)]
        [DataRow(MouseButton.Left, false, MouseButton.Left, true, false)]
        [DataRow(MouseButton.Left, true, MouseButton.Left, false, false)]
        [DataRow(MouseButton.Right, true, MouseButton.Right, true, false)]
        [DataRow(MouseButton.Right, true, MouseButton.Left, true, false)]
        [DataRow(MouseButton.Left, true, MouseButton.Right, true, false)]
        public void Click_RequiresLeftPressAndReleaseOnSameCardSurface(MouseButton press, bool pressOnSurface,
            MouseButton release, bool releaseOnSurface, bool expected)
        {
            var gesture = new CardClickGesture();
            gesture.Begin(press, pressOnSurface);

            Assert.AreEqual(expected, gesture.Complete(release, releaseOnSurface));
            Assert.IsFalse(gesture.Complete(MouseButton.Left, true), "A release cannot reuse a previous press.");
        }

        [TestMethod]
        public void Click_CannotActivateAnotherCardOrAnInterruptedGesture()
        {
            var first = new CardClickGesture();
            var second = new CardClickGesture();
            first.Begin(MouseButton.Left, true);
            Assert.IsFalse(second.Complete(MouseButton.Left, true));
            first.Cancel();
            Assert.IsFalse(first.Complete(MouseButton.Left, true));
            first.Begin(MouseButton.Left, true);
            first.Begin(MouseButton.Right, true);
            Assert.IsFalse(first.Complete(MouseButton.Left, true));
        }

        [STATestMethod]
        public void CardSurface_ExcludesPinActionsLinksAndOtherCards()
        {
            var panel = new StackPanel();
            var border = new Border { Child = panel };
            var title = new TextBlock { Text = "Title" };
            var pinContent = new Border();
            var pin = new Button { Content = pinContent };
            panel.Children.Add(title);
            panel.Children.Add(pin);
            var linkText = new Run("Link");
            title.Inlines.Add(new Hyperlink(linkText));

            Assert.IsTrue(CardInteractionHelper.IsCardSurface(border, title));
            Assert.IsTrue(CardInteractionHelper.IsCardSurface(border, border));
            Assert.IsFalse(CardInteractionHelper.IsCardSurface(border, pin));
            Assert.IsFalse(CardInteractionHelper.IsCardSurface(border, pinContent));
            Assert.IsFalse(CardInteractionHelper.IsCardSurface(border, linkText));
            Assert.IsFalse(CardInteractionHelper.IsCardSurface(border, new Border()));
            Assert.IsFalse(CardInteractionHelper.IsCardSurface(border, null!));
        }

        [STATestMethod]
        public void PressTracking_ResetsOnUnloadAndDataContextReplacement()
        {
            var border = new Border();
            var gesture = CardInteractionHelper.TrackPress(border);
            gesture.Begin(MouseButton.Left, true);
            border.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.IsFalse(gesture.Complete(MouseButton.Left, true));

            gesture.Begin(MouseButton.Left, true);
            border.DataContext = new object();
            Assert.IsFalse(gesture.Complete(MouseButton.Left, true));
        }

        [STATestMethod]
        [DataRow(true, false, true)]
        [DataRow(false, true, true)]
        [DataRow(true, true, true)]
        [DataRow(false, false, false)]
        public void Highlight_RemainsWhileHoveredOrKeyboardFocusIsWithin(bool isMouseOver, bool isKeyboardFocusWithin, bool expected)
        {
            const string highlightKey = "CardHighlight";
            var border = new Border();
            var highlight = new SolidColorBrush(Colors.Blue);
            border.Resources[highlightKey] = highlight;

            CardInteractionHelper.SetHighlight(border, isMouseOver, isKeyboardFocusWithin, highlightKey);

            Assert.AreEqual(expected, ReferenceEquals(highlight, border.Background));
        }

        [STATestMethod]
        public void Highlight_TracksResourceReplacementAndRestoresSelectionStyle()
        {
            const string highlightKey = "CardHighlight";
            var selectedBrush = new SolidColorBrush(Colors.Green);
            var firstThemeBrush = new SolidColorBrush(Colors.Blue);
            var nextThemeBrush = new SolidColorBrush(Colors.Red);
            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(Border.BackgroundProperty, selectedBrush));
            var border = new Border { Style = style };
            border.Resources[highlightKey] = firstThemeBrush;

            CardInteractionHelper.SetHighlight(border, true, false, highlightKey);
            Assert.AreSame(firstThemeBrush, border.Background);
            border.Resources[highlightKey] = nextThemeBrush;
            Assert.AreSame(nextThemeBrush, border.Background);
            CardInteractionHelper.SetHighlight(border, false, false, highlightKey);
            Assert.AreSame(DependencyProperty.UnsetValue, border.ReadLocalValue(Border.BackgroundProperty));
            Assert.AreSame(selectedBrush, border.Background);
        }

        [STATestMethod]
        public void MenuCommands_KeepIconsAndHandlersWhenItemsMoveOrAreInserted()
        {
            var invoked = new List<string>();
            var browserIcon = new object();
            var copyIcon = new object();
            var browser = ThemedContextMenuHelper.CreateCommandMenuItem("OpenInBrowser", "Open in Browser",
                (_, _) => invoked.Add("open"), browserIcon, "Enter");
            var copy = ThemedContextMenuHelper.CreateCommandMenuItem("CopyUrl", "Copy URL",
                (_, _) => invoked.Add("copy"), copyIcon, "Ctrl+C");
            var menu = new ContextMenu();
            menu.Items.Add(copy);
            menu.Items.Add(new Separator());
            menu.Items.Insert(0, new MenuItem { Header = "Additional action" });
            menu.Items.Add(browser);

            foreach (var command in menu.Items.OfType<MenuItem>().Where(item => item.Name != string.Empty))
                command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            CollectionAssert.AreEqual(new[] { "copy", "open" }, invoked);
            Assert.AreSame(browserIcon, browser.Icon);
            Assert.AreSame(copyIcon, copy.Icon);
            Assert.AreEqual("OpenInBrowser", browser.Name);
            Assert.AreEqual("Ctrl+C", copy.InputGestureText);
        }

        [STATestMethod]
        [DataRow(-1.0, -1.0)]
        [DataRow(20.0, 30.0)]
        public void LazyMenu_FirstMouseOrKeyboardRequestBuildsOnceAndUsesCurrentContext(double left, double top)
        {
            var border = new Border { DataContext = "initial" };
            var builds = 0;
            object? invokedContext = null;
            ThemedContextMenuHelper.AttachLazyContextMenu(border, () =>
            {
                builds++;
                var menu = new ContextMenu { Tag = border.DataContext };
                var command = new MenuItem { Header = "Open" };
                command.Click += (_, _) => invokedContext = border.DataContext;
                menu.Items.Add(command);
                return menu;
            });
            var placeholder = border.ContextMenu;
            Assert.IsNotNull(placeholder);
            Assert.AreEqual(0, builds);
            Assert.IsEmpty(placeholder.Items);
            border.DataContext = "first request";

            RaiseOpening();

            var populated = border.ContextMenu;
            Assert.AreNotSame(placeholder, populated);
            Assert.HasCount(1, populated.Items);
            Assert.AreEqual("first request", populated.Tag);
            border.DataContext = "recycled card";
            RaiseOpening();
            Assert.AreEqual(1, builds);
            Assert.AreSame(populated, border.ContextMenu);
            ((MenuItem)populated.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.AreEqual("recycled card", invokedContext);

            void RaiseOpening()
            {
                var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, [border, true, left, top], null)!;
                border.RaiseEvent(args);
                Assert.IsFalse(args.Handled, "WPF must still perform normal placement and open the menu on the first request.");
            }
        }

        [STATestMethod]
        public void MenuTheme_RepeatedAttachmentKeepsOneOpeningHandler()
        {
            var menu = new ContextMenu();
            ThemedContextMenuHelper.AttachOpeningTheme(menu);
            ThemedContextMenuHelper.AttachOpeningTheme(menu);
            ThemedContextMenuHelper.AttachOpeningTheme(menu);
            var store = typeof(UIElement).GetProperty("EventHandlersStore",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(menu)!;
            var handlers = (Array)store.GetType().GetMethod("GetRoutedEventHandlers")!
                .Invoke(store, [ContextMenu.OpenedEvent])!;
            Assert.HasCount(1, handlers);
            var handler = (Delegate)handlers.GetValue(0)!.GetType().GetProperty("Handler")!
                .GetValue(handlers.GetValue(0))!;
            Assert.AreEqual("Menu_Opened", handler.Method.Name);

            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        }
    }
}