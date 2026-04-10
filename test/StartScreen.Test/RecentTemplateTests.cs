using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models;

namespace StartScreen.Test
{
    [TestClass]
    public class RecentTemplateTests
    {
        [TestMethod]
        public void ToString_WhenNameIsSet_ReturnsName()
        {
            var template = new RecentTemplate { Name = "Console App", TemplateId = "console-id" };

            Assert.AreEqual("Console App", template.ToString());
        }

        [TestMethod]
        public void ToString_WhenNameIsNull_ReturnsTemplateId()
        {
            var template = new RecentTemplate { TemplateId = "console-id" };

            Assert.AreEqual("console-id", template.ToString());
        }

        [TestMethod]
        public void ToString_WhenBothNull_ReturnsUnknownTemplate()
        {
            var template = new RecentTemplate();

            Assert.AreEqual("Unknown Template", template.ToString());
        }
    }
}
