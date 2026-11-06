using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class NewsCardLayoutTests
    {
        [STATestMethod]
        [DataRow(200d, 12d)]
        [DataRow(360d, 12d)]
        [DataRow(200d, 18d)]
        [DataRow(360d, 24d)]
        public void Cards_ReserveEqualFontScaledHeightsAndKeepMetadataAligned(double width, double environmentSize)
        {
            var shortCard = CreateCard("Short title", "Short summary");
            var longText = string.Join(" ", Enumerable.Repeat("Long text that wraps across many lines", 50));
            var longCard = CreateCard(longText, longText);
            foreach (var card in new[] { shortCard, longCard })
            {
                Typography.SetEnvironmentSize(card, environmentSize);
                FlushBindings();
                card.Measure(new Size(width, double.PositiveInfinity));
                card.Arrange(new Rect(0, 0, width, card.DesiredSize.Height));
                card.UpdateLayout();
            }

            Assert.AreEqual(shortCard.ActualHeight, longCard.ActualHeight, 0.01);
            var shortTitle = (TextBlock)shortCard.Children[0];
            var longTitle = (TextBlock)longCard.Children[0];
            var summary = (TextBlock)longCard.Children[1];
            Assert.AreEqual(2 * longTitle.LineHeight, longTitle.ActualHeight, 1);
            Assert.AreEqual(4 * summary.LineHeight, summary.ActualHeight, 1);
            Assert.AreEqual(longTitle.ActualHeight, shortTitle.ActualHeight, 0.01);
            Assert.AreEqual(TextTrimming.WordEllipsis, longTitle.TextTrimming);
            Assert.AreEqual(TextTrimming.WordEllipsis, summary.TextTrimming);
            Assert.AreEqual(longText, longTitle.ToolTip);
            Assert.AreEqual(longText, summary.ToolTip);
            Assert.AreEqual(shortCard.Children[2].TranslatePoint(new Point(), shortCard).Y,
                longCard.Children[2].TranslatePoint(new Point(), longCard).Y, 0.01);
            var summaryBottom = summary.TranslatePoint(new Point(0, summary.ActualHeight), longCard).Y;
            Assert.AreEqual(2d, longCard.Children[2].TranslatePoint(new Point(), longCard).Y - summaryBottom, 0.01);

            var previousHeight = longCard.ActualHeight;
            Typography.SetEnvironmentSize(longCard, environmentSize * 2);
            FlushBindings();
            longCard.Measure(new Size(width, double.PositiveInfinity));
            longCard.Arrange(new Rect(0, 0, width, longCard.DesiredSize.Height));
            longCard.UpdateLayout();
            Assert.IsGreaterThan(previousHeight, longCard.ActualHeight);
            Assert.AreEqual(2 * longTitle.LineHeight, longTitle.ActualHeight, 1);
        }

        private static void FlushBindings()
        {
            var frame = new DispatcherFrame();
#pragma warning disable VSTHRD001 // Same-thread dispatcher ordering barrier after bindings/layout, not a thread switch.
            var completion = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
#pragma warning restore VSTHRD001
            try
            {
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                completion.Abort();
            }
        }

        private static StackPanel CreateCard(string title, string summary)
        {
            using var stream = typeof(NewsCardLayoutTests).Assembly.GetManifestResourceStream("NewsItemControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var content = new XElement(document.Descendants(p + "TextBlock")
                .Single(element => element.Attribute("Text")?.Value == "{Binding Title}").Parent!);
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            content.AddFirst(new XElement(p + "StackPanel.Resources", new XElement(p + "BooleanToVisibilityConverter",
                new XAttribute(x + "Key", "BoolToVisibilityConverter"))));
            content.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:StartScreen.ToolWindows;assembly=StartScreen");
            var card = (StackPanel)SharedXamlReader.Parse(content.ToString());
            card.DataContext = new { Title = title, Summary = summary, Source = "Nov 5 in Test Feed", IsNew = false };
            return card;
        }
    }
}
