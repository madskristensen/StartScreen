using System.Linq;
using System.Windows.Documents;
using StartScreen.Helpers;

namespace StartScreen.Test
{
    [TestClass]
    public class InlineTextHelperTests
    {
        [TestMethod]
        public void ParseInlines_PlainText_ReturnsSingleRun()
        {
            var inlines = InlineTextHelper.ParseInlines("Just plain text.");

            Assert.HasCount(1, inlines);
            Assert.IsInstanceOfType(inlines[0], typeof(Run));
            Assert.AreEqual("Just plain text.", ((Run)inlines[0]).Text);
        }

        [TestMethod]
        public void ParseInlines_SingleLink_ReturnsHyperlink()
        {
            var inlines = InlineTextHelper.ParseInlines("[click here](https://example.com)");

            Assert.HasCount(1, inlines);
            Assert.IsInstanceOfType(inlines[0], typeof(Hyperlink));

            var hyperlink = (Hyperlink)inlines[0];
            Assert.AreEqual("https://example.com/", hyperlink.NavigateUri.ToString());
            Assert.AreEqual("click here", ((Run)hyperlink.Inlines.FirstInline).Text);
        }

        [TestMethod]
        public void ParseInlines_TextBeforeAndAfterLink_ReturnsThreeInlines()
        {
            var inlines = InlineTextHelper.ParseInlines("Visit [Azure](https://azure.microsoft.com) for cloud services.");

            Assert.HasCount(3, inlines);
            Assert.IsInstanceOfType(inlines[0], typeof(Run));
            Assert.IsInstanceOfType(inlines[1], typeof(Hyperlink));
            Assert.IsInstanceOfType(inlines[2], typeof(Run));

            Assert.AreEqual("Visit ", ((Run)inlines[0]).Text);
            Assert.AreEqual(" for cloud services.", ((Run)inlines[2]).Text);

            var hyperlink = (Hyperlink)inlines[1];
            Assert.AreEqual("Azure", ((Run)hyperlink.Inlines.FirstInline).Text);
        }

        [TestMethod]
        public void ParseInlines_MultipleLinks_ParsesAll()
        {
            var inlines = InlineTextHelper.ParseInlines("Use [VS](https://visualstudio.com) and [Azure](https://azure.com) together.");

            Assert.HasCount(5, inlines);
            Assert.IsInstanceOfType(inlines[0], typeof(Run));
            Assert.IsInstanceOfType(inlines[1], typeof(Hyperlink));
            Assert.IsInstanceOfType(inlines[2], typeof(Run));
            Assert.IsInstanceOfType(inlines[3], typeof(Hyperlink));
            Assert.IsInstanceOfType(inlines[4], typeof(Run));
        }

        [TestMethod]
        public void ParseInlines_EmptyString_ReturnsEmpty()
        {
            var inlines = InlineTextHelper.ParseInlines("");

            Assert.IsEmpty(inlines);
        }

        [TestMethod]
        public void ParseInlines_MalformedUrl_RendersAsPlainText()
        {
            var inlines = InlineTextHelper.ParseInlines("[text](not a url)");

            Assert.HasCount(1, inlines);
            Assert.IsInstanceOfType(inlines[0], typeof(Run));
            Assert.AreEqual("[text](not a url)", ((Run)inlines[0]).Text);
        }

        [TestMethod]
        public void ParseInlines_NoLinks_ReturnsSingleRun()
        {
            var inlines = InlineTextHelper.ParseInlines("Press Ctrl+Q to search.");

            Assert.HasCount(1, inlines);
            Assert.IsInstanceOfType(inlines[0], typeof(Run));
            Assert.AreEqual("Press Ctrl+Q to search.", ((Run)inlines[0]).Text);
        }

        [TestMethod]
        public void ParseInlines_LinkAtStart_ReturnsCorrectOrder()
        {
            var inlines = InlineTextHelper.ParseInlines("[Start](https://example.com) of the line.");

            Assert.HasCount(2, inlines);
            Assert.IsInstanceOfType(inlines[0], typeof(Hyperlink));
            Assert.IsInstanceOfType(inlines[1], typeof(Run));
            Assert.AreEqual(" of the line.", ((Run)inlines[1]).Text);
        }

        [TestMethod]
        public void ParseInlines_LinkAtEnd_ReturnsCorrectOrder()
        {
            var inlines = InlineTextHelper.ParseInlines("End of the line [here](https://example.com)");

            Assert.HasCount(2, inlines);
            Assert.IsInstanceOfType(inlines[0], typeof(Run));
            Assert.IsInstanceOfType(inlines[1], typeof(Hyperlink));
            Assert.AreEqual("End of the line ", ((Run)inlines[0]).Text);
        }
    }
}
