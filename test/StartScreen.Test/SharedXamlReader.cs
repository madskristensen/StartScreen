using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;

namespace StartScreen.Test
{
    internal static class SharedXamlReader
    {
        private static readonly XNamespace _presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace _xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        private const string TypographyNamespace = "clr-namespace:StartScreen.ToolWindows;assembly=StartScreen";

        public static XDocument ReadResources()
        {
            using var stream = typeof(SharedXamlReader).Assembly.GetManifestResourceStream("StartScreenResources.xaml");
            return XDocument.Load(stream!);
        }

        public static object Parse(string xaml, bool localResources = false)
        {
            var resources = ReadResources().Root!;
            resources.AddFirst(new XElement(_presentation + "Style",
                new XAttribute(_xaml + "Key", "TestComboBoxStyle"), new XAttribute("TargetType", "ComboBox"),
                new XElement(_presentation + "Setter", new XAttribute("Property", "MinHeight"), new XAttribute("Value", "24"))));
            var target = XElement.Parse(xaml);
            target.Attribute(_xaml + "Key")?.Remove();
            var wrapper = new XElement(_presentation + "ContentControl",
                new XAttribute(XNamespace.Xmlns + "x", _xaml.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "typography", TypographyNamespace),
                new XAttribute(XNamespace.Xmlns + "local", TypographyNamespace),
                new XElement(_presentation + "ContentControl.Resources", resources),
                new XElement(_presentation + "ContentControl.Content", target));
            if (localResources)
            {
                target.AddFirst(new XElement(_presentation + (target.Name.LocalName + ".Resources"), resources));
                wrapper = target;
                wrapper.Add(new XAttribute(XNamespace.Xmlns + "x", _xaml.NamespaceName));
                wrapper.Add(new XAttribute(XNamespace.Xmlns + "local", TypographyNamespace));
            }
            foreach (var attribute in wrapper.DescendantsAndSelf().Attributes().ToArray())
            {
                if (attribute.IsNamespaceDeclaration && attribute.Value == "clr-namespace:StartScreen.ToolWindows")
                    attribute.Value = TypographyNamespace;
                else if (attribute.Name.NamespaceName == "clr-namespace:StartScreen.ToolWindows")
                {
                    attribute.Parent!.Add(new XAttribute(XName.Get(attribute.Name.LocalName, TypographyNamespace), attribute.Value));
                    attribute.Remove();
                }
                else if (attribute.Value.StartsWith("{DynamicResource {x:Static platformui:", StringComparison.Ordinal))
                    attribute.Value = "{DynamicResource TestThemeBrush}";
                else if (attribute.Value.Contains("VsFonts.EnvironmentFontFamilyKey"))
                    attribute.Value = "{DynamicResource TestEnvironmentFontFamily}";
                else if (attribute.Value.Contains("VsFonts.EnvironmentFontSizeKey"))
                    attribute.Value = "{DynamicResource TestEnvironmentFontSize}";
                else if (attribute.Value == "{StaticResource {x:Static shell:VsResourceKeys.ThemedDialogComboBoxStyleKey}}")
                    attribute.Value = "{StaticResource TestComboBoxStyle}";
            }

            // Only host-owned resources are substituted; all shared styles and their references are parsed by WPF.
            if (localResources)
                return XamlReader.Parse(wrapper.ToString());
            var host = (ContentControl)XamlReader.Parse(wrapper.ToString());
            var result = host.Content;
            host.Content = null;
            if (result is FrameworkElement element)
            {
                NameScope.SetNameScope(element, NameScope.GetNameScope(host));
                element.Resources.MergedDictionaries.Insert(0, host.Resources);
            }
            return result;
        }

        public static string Setter(string styleKey, string property)
        {
            var style = ReadResources().Descendants(_presentation + "Style")
                .Single(element => element.Attribute(_xaml + "Key")?.Value == styleKey);
            return style.Elements(_presentation + "Setter").Single(element => element.Attribute("Property")?.Value == property)
                .Attribute("Value")!.Value;
        }
    }
}