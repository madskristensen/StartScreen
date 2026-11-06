using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;
using StartScreen.ToolWindows;

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
            Assert.AreEqual("{StaticResource StartScreen.Root}", document.Root.Attribute("Style")?.Value);
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

        private static XDocument Read(string name)
        {
            using var stream = typeof(SharedVisualResourcesTests).Assembly.GetManifestResourceStream(name);
            return XDocument.Load(stream!);
        }
    }
}