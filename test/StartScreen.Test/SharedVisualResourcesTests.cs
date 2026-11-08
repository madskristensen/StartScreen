using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using StartScreen.ToolWindows;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test
{
    [TestClass]
    public class SharedVisualResourcesTests
    {
        private static readonly XNamespace _presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace _xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        [TestMethod]
        [DataRow("StartScreenControl.xaml", "StartScreenResources.xaml")]
        [DataRow("DevHubPanel.xaml", "../StartScreenResources.xaml")]
        [DataRow("MruItemControl.xaml", "../StartScreenResources.xaml")]
        [DataRow("NewsItemControl.xaml", "../StartScreenResources.xaml")]
        [DataRow("YouTubeVideoControl.xaml", "../StartScreenResources.xaml")]
        public void EverySurface_MergesTheSharedDictionaryAndUsesEnvironmentTypography(string name, string source)
        {
            var document = Read(name);
            var merged = document.Root!.Element(_presentation + "UserControl.Resources")!
                .Element(_presentation + "ResourceDictionary")!.Element(_presentation + "ResourceDictionary.MergedDictionaries")!;

            Assert.HasCount(1, merged.Elements());
            Assert.AreEqual(source, merged.Elements().Single().Attribute("Source")?.Value);
            Assert.AreEqual("{DynamicResource StartScreen.Root}", document.Root.Attribute("Style")?.Value);
            Assert.IsFalse(document.Root.DescendantsAndSelf().Attributes().Any(attribute => attribute.Name.LocalName == "FontSize"));
            Assert.IsFalse(document.Descendants(_presentation + "Setter").Any(element => element.Attribute("Property")?.Value == "FontSize"));
            Assert.IsFalse(document.Root.DescendantsAndSelf().Attributes().Any(attribute => attribute.Name.LocalName == "FontFamily"));
        }

        [TestMethod]
        public void FontRoles_AreDynamicVsEnvironmentRolesWithoutFixedGlobalFonts()
        {
            Assert.AreEqual("{DynamicResource {x:Static shell:VsFonts.EnvironmentFontFamilyKey}}", SharedXamlReader.Setter("StartScreen.Root", "FontFamily"));
            Assert.AreEqual("{DynamicResource {x:Static shell:VsFonts.EnvironmentFontSizeKey}}", SharedXamlReader.Setter("StartScreen.Root", "local:Typography.EnvironmentSize"));
            Assert.AreEqual("Body", SharedXamlReader.Setter("StartScreen.Root", "local:Typography.Role"));
            Assert.AreEqual("Body", SharedXamlReader.Setter("StartScreen.Body", "local:Typography.Role"));
            Assert.AreEqual("Metadata", SharedXamlReader.Setter("StartScreen.Metadata", "local:Typography.Role"));
            Assert.AreEqual("Heading", SharedXamlReader.Setter("StartScreen.Heading", "local:Typography.Role"));
            Assert.AreEqual("Metadata", SharedXamlReader.Setter("StartScreen.HeaderCount", "local:Typography.Role"));
        }

        [STATestMethod]
        [DataRow("StartScreenControl.xaml")]
        [DataRow("DevHubPanel.xaml")]
        [DataRow("MruItemControl.xaml")]
        [DataRow("NewsItemControl.xaml")]
        [DataRow("YouTubeVideoControl.xaml")]
        public void RootStyle_LoadsWhenResourcesAreDeclaredOnTheControlItself(string name)
        {
            var styleReference = Read(name).Root!.Attribute("Style")!.Value;
            var control = (UserControl)SharedXamlReader.Parse(new XElement(_presentation + "UserControl",
                new XAttribute("Style", styleReference), new XElement(_presentation + "TextBlock", "Content")).ToString(), localResources: true);
            control.Measure(new Size(400, 200));

            Assert.IsNotNull(control.Style);
            Assert.AreSame(control.Resources["StartScreen.Root"], control.Style);
            Assert.AreEqual(TypographyRole.Body, StartScreen.ToolWindows.Typography.GetRole(control));
        }

        [STATestMethod]
        public void SharedDictionary_AllPrimitivesResolveInWpf()
        {
            var resources = (ResourceDictionary)SharedXamlReader.Parse(SharedXamlReader.ReadResources().ToString());
            foreach (var key in resources.Keys.Cast<object>().ToArray())
            {
                Assert.IsNotNull(resources[key], key.ToString());
                if (resources[key] is Style style)
                    style.Seal();
            }

            var badge = (Border)SharedXamlReader.Parse("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Style=\"{StaticResource StartScreen.StatusBadge}\"/>");
            Assert.AreEqual(new Thickness(7, 2, 7, 2), badge.Padding);
            Assert.AreEqual(new CornerRadius(9), badge.CornerRadius);
            Assert.AreEqual(new Thickness(1), badge.BorderThickness);
        }

        [STATestMethod]
        [DataRow(12d, 13d, 11d, 20d)]
        [DataRow(18d, 19.5d, 16.5d, 30d)]
        [DataRow(24d, 26d, 22d, 40d)]
        public void Typography_RespectsFontChangesWithoutCompoundingNestedRoles(double environment, double body, double metadata, double heading)
        {
            var root = (UserControl)SharedXamlReader.Parse("""
                <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Style="{StaticResource StartScreen.Root}">
                    <StackPanel>
                        <TextBlock Style="{StaticResource StartScreen.CardTitle}" Text="Title"/>
                        <TextBlock Style="{StaticResource StartScreen.Metadata}" Text="Metadata"/>
                        <TextBlock Style="{StaticResource StartScreen.Heading}">
                            <Run Text="Heading"/>
                            <Run Style="{StaticResource StartScreen.HeaderCount}" Text="Count"/>
                        </TextBlock>
                    </StackPanel>
                </UserControl>
                """);
            root.Resources["TestEnvironmentFontSize"] = 12d;
            root.Resources["TestEnvironmentFontFamily"] = new FontFamily("Consolas");
            root.Resources["TestEnvironmentFontSize"] = environment;
            root.Measure(new Size(600, double.PositiveInfinity));
            var children = ((StackPanel)root.Content).Children.OfType<TextBlock>().ToArray();

            Assert.AreEqual(body, root.FontSize, 0.001);
            Assert.AreEqual(body, children[0].FontSize, 0.001);
            Assert.AreEqual(metadata, children[1].FontSize, 0.001);
            Assert.AreEqual(heading, children[2].FontSize, 0.001);
            Assert.AreEqual(metadata, children[2].Inlines.OfType<Run>().Last().FontSize, 0.001);
            Assert.AreEqual("Consolas", children[0].FontFamily.Source);
            root.Resources["TestEnvironmentFontFamily"] = new FontFamily("Arial");
            Assert.AreEqual("Arial", children[0].FontFamily.Source);
        }

        [TestMethod]
        public void CardTitlesAndStatusBadges_UseTheSameSemanticRolesAcrossLayouts()
        {
            foreach (var name in new[] { "NewsItemControl.xaml", "YouTubeVideoControl.xaml", "MruItemControl.xaml" })
            {
                var title = Read(name).Descendants(_presentation + "TextBlock").Single(element =>
                    element.Attribute("Text")?.Value == (name == "MruItemControl.xaml" ? "{Binding Name}" : "{Binding Title}"));
                Assert.AreEqual("{StaticResource StartScreen.CardTitle}", title.Attribute("Style")?.Value);
            }
            var workTitle = Read("DevHubPanel.xaml").Descendants(_presentation + "TextBlock")
                .Single(element => element.Attribute("Text")?.Value == "{Binding DisplayTitle}");
            Assert.AreEqual("{StaticResource StartScreen.CardTitle}", workTitle.Element(_presentation + "TextBlock.Style")!
                .Element(_presentation + "Style")!.Attribute("BasedOn")?.Value);
            var feedback = Read("StartScreenControl.xaml").Descendants(_presentation + "TabItem")
                .Single(element => element.Attribute(_xaml + "Name")?.Value == "FeedbackTab");
            Assert.AreEqual("{StaticResource StartScreen.CardTitle}", feedback.Descendants(_presentation + "TextBlock")
                .Single(element => element.Descendants(_presentation + "Run").Any(run => run.Attribute("Text")?.Value == "{Binding Title, Mode=OneWay}"))
                .Attribute("Style")?.Value);
        }

        [TestMethod]
        public void PinnedAndUnpinnedNews_ShareOneTemplateAndPanelButNotOtherCardLayouts()
        {
            var document = Read("StartScreenControl.xaml");
            foreach (var binding in new[] { "{Binding PinnedNewsPosts}", "{Binding NewsPosts}" })
            {
                var list = document.Descendants(_presentation + "ItemsControl").Single(element => element.Attribute("ItemsSource")?.Value == binding);
                Assert.AreEqual("{StaticResource NewsCardTemplate}", list.Attribute("ItemTemplate")?.Value);
                Assert.AreEqual("{StaticResource NewsCardsPanel}", list.Attribute("ItemsPanel")?.Value);
            }
            Assert.HasCount(1, document.Descendants().Where(element => element.Name.LocalName == "NewsItemControl"));
            var work = Read("DevHubPanel.xaml");
            foreach (var name in new[] { "MyWorkList", "IncomingList" })
                Assert.AreEqual("{StaticResource WorkItemTemplate}", work.Descendants(_presentation + "ItemsControl")
                    .Single(element => element.Attribute(_xaml + "Name")?.Value == name).Attribute("ItemTemplate")?.Value);
        }

        [TestMethod]
        public void SettingsSelectors_InheritTheVisualStudioComboBoxStyle()
        {
            var style = SharedXamlReader.ReadResources().Descendants(_presentation + "Style")
                .Single(element => element.Attribute(_xaml + "Key")?.Value == "StartScreen.FilterSelector");
            Assert.AreEqual("{StaticResource {x:Static shell:VsResourceKeys.ThemedDialogComboBoxStyleKey}}",
                style.Attribute("BasedOn")?.Value);
            var panel = Read("DevHubPanel.xaml");
            foreach (var name in new[] { "GitHubAccountsComboBox", "DefaultTabComboBox", "SortOrderComboBox" })
                Assert.AreEqual("{StaticResource StartScreen.FilterSelector}", panel.Descendants(_presentation + "ComboBox")
                    .Single(element => element.Attribute(_xaml + "Name")?.Value == name).Attribute("Style")?.Value);
        }

        [STATestMethod]
        public void SettingsSelectors_ResolveHostStyleAndFollowThemeBrushChanges()
        {
            var selector = (ComboBox)SharedXamlReader.Parse("<ComboBox xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Style=\"{StaticResource StartScreen.FilterSelector}\"/>");
            Assert.IsNotNull(selector.Style.BasedOn);
            Assert.AreEqual(typeof(ComboBox), selector.Style.BasedOn.TargetType);
            Assert.AreEqual(24d, selector.MinHeight);
            selector.Resources["TestThemeBrush"] = SystemColors.ControlTextBrush;
            Assert.AreSame(SystemColors.ControlTextBrush, selector.Foreground);
            selector.Resources["TestThemeBrush"] = SystemColors.HighlightTextBrush;
            Assert.AreSame(SystemColors.HighlightTextBrush, selector.Foreground);
        }

        [TestMethod]
        public void SharedInteractionPrimitives_KeepDynamicHoverAndKeyboardFocus()
        {
            var document = SharedXamlReader.ReadResources();
            foreach (var pair in new[] { ("StartScreen.CardSurface", "IsKeyboardFocusWithin"), ("StartScreen.IconActionButton", "IsKeyboardFocused") })
            {
                var style = document.Descendants(_presentation + "Style").Single(element => element.Attribute(_xaml + "Key")?.Value == pair.Item1);
                foreach (var property in new[] { "IsMouseOver", pair.Item2 })
                {
                    var trigger = style.Descendants(_presentation + "Trigger").Single(element => element.Attribute("Property")?.Value == property);
                    StringAssert.StartsWith(trigger.Descendants(_presentation + "Setter").Single().Attribute("Value")!.Value, "{DynamicResource {x:Static platformui:");
                }
            }
        }

        [TestMethod]
        public void SharedProgress_OverlaysTheHeaderOutsideScrollableContent()
        {
            var document = Read("StartScreenControl.xaml");
            var progress = document.Descendants(_presentation + "ProgressBar").Single();
            var layout = document.Root!.Element(_presentation + "Grid")!;
            Assert.AreSame(layout, progress.Parent);
            Assert.AreEqual("UnifiedProgressBar", progress.Attribute(_xaml + "Name")?.Value);
            Assert.AreEqual("0", progress.Attribute("Grid.Row")?.Value);
            Assert.AreEqual("Bottom", progress.Attribute("VerticalAlignment")?.Value);
            Assert.AreEqual("24 0 24 0", progress.Attribute("Margin")?.Value);
            Assert.AreEqual("False", progress.Attribute("IsHitTestVisible")?.Value);
            Assert.AreEqual("{DynamicResource {x:Static platformui:EnvironmentColors.ControlEditHintTextBrushKey}}",
                progress.Attribute("Foreground")?.Value);
            var triggers = progress.Descendants(_presentation + "DataTrigger").ToArray();
            CollectionAssert.AreEquivalent(new[] { "{Binding IsRefreshProgressActive}" },
                triggers.Select(trigger => trigger.Attribute("Binding")!.Value).ToArray());
            Assert.AreEqual("{Binding RefreshProgressValue, Mode=OneWay}", progress.Attribute("Value")?.Value);
            Assert.AreEqual("False", progress.Attribute("IsIndeterminate")?.Value);
            Assert.IsFalse(progress.Descendants(_presentation + "Setter").Any(setter =>
                setter.Attribute("Property")?.Value == "IsIndeterminate" && setter.Attribute("Value")?.Value == "True"));

            var panel = Read("DevHubPanel.xaml");
            Assert.IsFalse(panel.Descendants(_presentation + "ProgressBar").Any());
            Assert.IsFalse(panel.Descendants().Any(element => element.Attribute(_xaml + "Name")?.Value is "LoadingPanel" or "LoadingText"));
            Assert.IsFalse(panel.Descendants().Attributes().Any(attribute => attribute.Value.Contains("CopilotActivityStatus")));
        }

        [STATestMethod]
        [DataRow(12d)]
        [DataRow(18d)]
        [DataRow(24d)]
        public void SharedProgress_RemoteAndProviderWorkNeverMoveContent(double environmentSize)
        {
            var layout = Read("StartScreenControl.xaml").Root!.Element(_presentation + "Grid")!;
            var progressElement = new XElement(layout.Elements(_presentation + "ProgressBar").Single());
            var header = new XElement(_presentation + "Border", layout.Elements(_presentation + "Border").First().Attributes(),
                new XAttribute(_xaml + "Name", "TestHeader"), new XElement(_presentation + "TextBlock",
                    new XAttribute("Style", "{StaticResource StartScreen.Heading}"), new XAttribute("Text", "Start Screen")));
            var surface = (UserControl)SharedXamlReader.Parse(new XElement(_presentation + "UserControl",
                new XAttribute("Style", "{StaticResource StartScreen.Root}"),
                new XElement(_presentation + "Grid", new XElement(layout.Element(_presentation + "Grid.RowDefinitions")!), header, progressElement,
                    new XElement(_presentation + "Border", new XAttribute("Grid.Row", "1"), new XAttribute(_xaml + "Name", "TestContent")))).ToString());
            var root = (Grid)surface.Content;
            surface.Resources["TestEnvironmentFontSize"] = environmentSize;
            root.DataContext = new { IsRefreshProgressActive = false, RefreshProgressValue = 0d };
            UpdateBindings(root);
            surface.Measure(new Size(900, 400));
            surface.Arrange(new Rect(0, 0, 900, 400));
            surface.UpdateLayout();
            var progress = (ProgressBar)root.FindName("UnifiedProgressBar");
            var content = (Border)root.FindName("TestContent");
            var toolbar = (Border)root.FindName("TestHeader");
            var contentOrigin = content.TranslatePoint(new Point(), root);
            var contentSize = content.RenderSize;
            var headerHeight = root.RowDefinitions[0].ActualHeight;
            var surfaceSize = root.RenderSize;
            var toolbarSize = toolbar.RenderSize;

            foreach (var state in new[] { (false, 0d), (true, 12.5d), (true, 50d), (true, 87.5d), (true, 100d), (false, 100d) })
            {
                root.DataContext = new { IsRefreshProgressActive = state.Item1, RefreshProgressValue = state.Item2 };
                foreach (var brush in new[] { Brushes.DodgerBlue, Brushes.DarkBlue, SystemColors.HighlightBrush })
                {
                    surface.Resources["TestThemeBrush"] = brush;
                    UpdateBindings(root);
                    surface.Measure(new Size(900, 400));
                    surface.Arrange(new Rect(0, 0, 900, 400));
                    root.UpdateLayout();
                    var busy = state.Item1;
                    Assert.AreEqual(busy ? Visibility.Visible : Visibility.Hidden, progress.Visibility);
                    Assert.IsFalse(progress.IsIndeterminate);
                    Assert.AreEqual(state.Item2, progress.Value);
                    Assert.AreSame(brush, progress.Foreground);
                    Assert.AreEqual(toolbar.ActualWidth, progress.ActualWidth);
                    Assert.AreEqual(contentOrigin, content.TranslatePoint(new Point(), root));
                    Assert.AreEqual(contentSize, content.RenderSize);
                    Assert.AreEqual(headerHeight, root.RowDefinitions[0].ActualHeight);
                    Assert.AreEqual(surfaceSize, root.RenderSize);
                    Assert.AreEqual(toolbarSize, toolbar.RenderSize);
                }
            }
        }

        [STATestMethod]
        [DataRow(12d)]
        [DataRow(18d)]
        [DataRow(24d)]
        public void RefreshCaption_ShowsStepsWithoutChangingItsGeometry(double environmentSize)
        {
            var element = Read("StartScreenControl.xaml").Descendants(_presentation + "TextBlock")
                .Single(candidate => candidate.Attribute(_xaml + "Name")?.Value == "LastRefreshText");
            var caption = (TextBlock)SharedXamlReader.Parse(element.ToString());
            caption.Resources["TestEnvironmentFontSize"] = environmentSize;
            caption.DataContext = new { IsRefreshingAny = false, UnifiedRefreshStatus = "Updated just now" };
            UpdateBindings(caption);
            caption.Measure(new Size(420, double.PositiveInfinity));
            var size = caption.DesiredSize;
            Assert.AreEqual(Visibility.Visible, caption.Visibility);
            Assert.AreEqual("Updated just now", caption.Text);
            Assert.AreEqual(220d + caption.Margin.Left + caption.Margin.Right, size.Width);
            Assert.AreEqual(TextTrimming.CharacterEllipsis, caption.TextTrimming);
            foreach (var step in new[] { "Checking connected provider accounts...",
                "Loading GitHub activity for a repository with a very long name that cannot fit in the caption...", "Updated just now" })
            {
                caption.DataContext = new { UnifiedRefreshStatus = step, UnifiedRefreshDetails = step + "\n3 of 8 work items finished." };
                UpdateBindings(caption);
                caption.Measure(new Size(420, double.PositiveInfinity));
                Assert.AreEqual(Visibility.Visible, caption.Visibility);
                Assert.AreEqual(step, caption.Text);
                Assert.AreEqual(size, caption.DesiredSize);
                StringAssert.Contains((string)caption.ToolTip, step);
            }
        }

        [STATestMethod]
        public void RecentOptions_DotsAreLargerAndFollowThemeChanges()
        {
            var button = Read("StartScreenControl.xaml").Descendants(_presentation + "Button")
                .Single(element => element.Attribute(_xaml + "Name")?.Value == "ClearRecentButton");
            var element = button.Element(_presentation + "TextBlock")!;
            Assert.AreEqual("SectionHeading", element.Attributes()
                .Single(attribute => attribute.Name.LocalName == "Typography.Role").Value);
            Assert.AreEqual("{DynamicResource {x:Static platformui:EnvironmentColors.CommandBarTextActiveBrushKey}}",
                element.Attribute("Foreground")?.Value);
            var dots = (TextBlock)SharedXamlReader.Parse(element.ToString());
            Assert.AreEqual("...", dots.Text);
            Assert.IsGreaterThan(13d, dots.FontSize);
            foreach (var brush in new[] { Brushes.White, Brushes.Black, Brushes.Yellow })
            {
                dots.Resources["TestThemeBrush"] = brush;
                UpdateBindings(dots);
                Assert.AreSame(brush, dots.Foreground);
            }
        }

        private static void UpdateBindings(FrameworkElement element)
        {
#pragma warning disable VSTHRD001 // Drain bindings on the current STA thread, not a thread switch.
            element.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
#pragma warning restore VSTHRD001
        }

        private static XDocument Read(string name)
        {
            using var stream = typeof(SharedVisualResourcesTests).Assembly.GetManifestResourceStream(name);
            return XDocument.Load(stream!);
        }
    }
}