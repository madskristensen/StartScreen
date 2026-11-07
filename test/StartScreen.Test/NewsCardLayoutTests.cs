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
        public void Cards_UseNaturalTitleHeightWithoutLeavingAGapAboveTheSummary(double width, double environmentSize)
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

            Assert.IsGreaterThan(shortCard.ActualHeight, longCard.ActualHeight);
            var shortTitle = (TextBlock)shortCard.Children[0];
            var longTitle = (TextBlock)longCard.Children[0];
            var summary = (TextBlock)longCard.Children[1];
            Assert.AreEqual(environmentSize * 13 / 12, summary.FontSize, 0.01);
            Assert.AreEqual(FontWeights.Normal, summary.FontWeight);
            Assert.AreEqual(FontWeights.SemiBold, longTitle.FontWeight);
            Assert.AreEqual(2 * longTitle.LineHeight, longTitle.ActualHeight, 1);
            Assert.AreEqual(2 * summary.LineHeight, summary.ActualHeight, 1);
            Assert.AreEqual(shortTitle.LineHeight, shortTitle.ActualHeight, 1);
            Assert.AreEqual(2 * longTitle.LineHeight, longTitle.MaxHeight, 0.01);
            Assert.IsTrue(double.IsNaN(shortTitle.Height));
            foreach (var card in new[] { shortCard, longCard })
            {
                var title = (TextBlock)card.Children[0];
                var description = (TextBlock)card.Children[1];
                var titleBottom = title.TranslatePoint(new Point(0, title.ActualHeight), card).Y;
                Assert.AreEqual(6d, description.TranslatePoint(new Point(), card).Y - titleBottom, 0.01);
            }
            Assert.AreEqual(TextTrimming.WordEllipsis, longTitle.TextTrimming);
            Assert.AreEqual(TextTrimming.WordEllipsis, summary.TextTrimming);
            Assert.AreEqual(longText, longTitle.ToolTip);
            Assert.AreEqual(longText, summary.ToolTip);
            Assert.AreEqual(longTitle.ActualHeight - shortTitle.ActualHeight,
                longCard.Children[2].TranslatePoint(new Point(), longCard).Y - shortCard.Children[2].TranslatePoint(new Point(), shortCard).Y, 0.01);
            var summaryBottom = summary.TranslatePoint(new Point(0, summary.ActualHeight), longCard).Y;
            Assert.AreEqual(6d, longCard.Children[2].TranslatePoint(new Point(), longCard).Y - summaryBottom, 0.01);

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
