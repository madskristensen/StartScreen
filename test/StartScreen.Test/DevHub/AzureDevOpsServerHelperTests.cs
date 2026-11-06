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

        [TestMethod]
        [DataRow("https://tfs.contoso.com:8443/tfs/Collection/", "tfs.contoso.com:8443/tfs/Collection")]
        [DataRow("tfs.contoso.com:8443/tfs/Collection", "tfs.contoso.com:8443/tfs/Collection")]
        [DataRow("http://tfs.contoso.com:8080/tfs/Collection", "http://tfs.contoso.com:8080/tfs/Collection")]
        public void NormalizeServerInput_PreservesPortAndHttpEndpoints(string input, string expected)
        {
            Assert.AreEqual(expected, AzureDevOpsServerHelper.NormalizeServerInput(input));
        }

        [TestMethod]
        public void DiscoverServers_PreservesDistinctPortsAndExcludesConfiguredEndpointPaths()
        {
            var remotes = new[]
            {
                "https://tfs.contoso.com:8443/tfs/Collection/Project/_git/First",
                "https://tfs.contoso.com:9443/tfs/Collection/Project/_git/Second",
                "https://tfs.contoso.com:9443/tfs/Collection/Other/_git/Third",
            };

            var all = AzureDevOpsServerHelper.DiscoverServers(remotes);
            Assert.HasCount(2, all);
            CollectionAssert.AreEquivalent(new[] { "tfs.contoso.com:8443", "tfs.contoso.com:9443" }, all.Select(server => server.Host).ToArray());

            var remaining = AzureDevOpsServerHelper.DiscoverServers(remotes, ["tfs.contoso.com:8443/tfs/Collection"]);
            Assert.AreEqual("tfs.contoso.com:9443", remaining.Single().Host);
            Assert.AreEqual("https://tfs.contoso.com:9443/tfs/Collection", remaining.Single().SampleBaseUrl);
        }

        [TestMethod]
        public async Task DiscoveryReadsActualRepositoryRemotesOnceRatherThanUnpopulatedMruMetadataAsync()
        {
            var scans = 0;
            var servers = await AzureDevOpsServerHelper.DiscoverFromMruAsync(() =>
            {
                scans++;
                return Task.FromResult<IReadOnlyList<string>>(["http://tfs.contoso.com:8080/tfs/Collection/Project/_git/Repo"]);
            }, []);

            Assert.AreEqual(1, scans);
            Assert.AreEqual("http://tfs.contoso.com:8080", servers.Single().Host);
            Assert.AreEqual("http://tfs.contoso.com:8080/tfs/Collection", servers.Single().SampleBaseUrl);
        }
    }
}
