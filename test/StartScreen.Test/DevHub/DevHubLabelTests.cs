using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubLabelTests
    {
        [TestMethod]
        public void ToString_WhenNameIsSet_ReturnsName()
        {
            var label = new DevHubLabel { Name = "bug", Color = "d73a4a" };

            Assert.AreEqual("bug", label.ToString());
        }

        [TestMethod]
        public void ToString_WhenNameIsNull_ReturnsEmpty()
        {
            var label = new DevHubLabel { Name = null };

            Assert.AreEqual(string.Empty, label.ToString());
        }
    }
}
