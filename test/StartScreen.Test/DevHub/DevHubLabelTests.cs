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

        [TestMethod]
        public void Color_CanBeNull()
        {
            var label = new DevHubLabel { Name = "test", Color = null };

            Assert.IsNull(label.Color);
        }

        [TestMethod]
        public void Color_StoresHexWithoutHash()
        {
            var label = new DevHubLabel { Name = "enhancement", Color = "a2eeef" };

            Assert.AreEqual("a2eeef", label.Color);
        }
    }
}
