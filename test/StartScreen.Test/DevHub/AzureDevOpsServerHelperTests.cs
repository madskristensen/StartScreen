using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class AzureDevOpsServerHelperTests
    {
        [TestMethod]
        public void NormalizeServerInput_HostOnly_ReturnsHost()
        {
            Assert.AreEqual("tfs.contoso.com", AzureDevOpsServerHelper.NormalizeServerInput("tfs.contoso.com"));
        }

        [TestMethod]
        public void NormalizeServerInput_FullHttpsUrlWithTfsPath_PreservesPath()
        {
            Assert.AreEqual(
                "tfs.contoso.com/tfs/DefaultCollection",
                AzureDevOpsServerHelper.NormalizeServerInput("https://tfs.contoso.com/tfs/DefaultCollection"));
        }

        [TestMethod]
        public void NormalizeServerInput_BareHostWithPath_PreservesPath()
        {
            Assert.AreEqual(
                "tfs.contoso.com/tfs",
                AzureDevOpsServerHelper.NormalizeServerInput("tfs.contoso.com/tfs"));
        }

        [TestMethod]
        public void NormalizeServerInput_TrailingSlash_IsTrimmed()
        {
            Assert.AreEqual(
                "tfs.contoso.com/tfs/DefaultCollection",
                AzureDevOpsServerHelper.NormalizeServerInput("https://tfs.contoso.com/tfs/DefaultCollection/"));
        }

        [TestMethod]
        public void NormalizeServerInput_HostOnlyUrlWithTrailingSlash_ReturnsHost()
        {
            Assert.AreEqual(
                "tfs.contoso.com",
                AzureDevOpsServerHelper.NormalizeServerInput("https://tfs.contoso.com/"));
        }

        [TestMethod]
        public void NormalizeServerInput_LeadingSlash_IsStripped()
        {
            Assert.AreEqual(
                "tfs.contoso.com/tfs",
                AzureDevOpsServerHelper.NormalizeServerInput("/tfs.contoso.com/tfs"));
        }

        [TestMethod]
        public void NormalizeServerInput_Whitespace_ReturnsNull()
        {
            Assert.IsNull(AzureDevOpsServerHelper.NormalizeServerInput("   "));
        }

        [TestMethod]
        public void NormalizeServerInput_Null_ReturnsNull()
        {
            Assert.IsNull(AzureDevOpsServerHelper.NormalizeServerInput(null));
        }
    }
}
