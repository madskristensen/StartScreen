using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubProviderRegistryTests
    {
        [TestMethod]
        public void GetProvider_GitHubHttps_ReturnsGitHubProvider()
        {
            var provider = DevHubProviderRegistry.GetProvider("https://github.com/owner/repo.git");

            Assert.IsNotNull(provider);
            Assert.AreEqual("GitHub", provider.DisplayName);
        }

        [TestMethod]
        public void GetProvider_GitHubSsh_ReturnsGitHubProvider()
        {
            var provider = DevHubProviderRegistry.GetProvider("git@github.com:owner/repo.git");

            Assert.IsNotNull(provider);
            Assert.AreEqual("GitHub", provider.DisplayName);
        }

        [TestMethod]
        public void GetProvider_AzureDevOpsHttps_ReturnsAdoProvider()
        {
            var provider = DevHubProviderRegistry.GetProvider("https://dev.azure.com/org/project/_git/repo");

            Assert.IsNotNull(provider);
            Assert.AreEqual("Azure DevOps", provider.DisplayName);
        }

        [TestMethod]
        public void GetProvider_AzureDevOpsSsh_ReturnsAdoProvider()
        {
            var provider = DevHubProviderRegistry.GetProvider("git@ssh.dev.azure.com:v3/org/project/repo");

            Assert.IsNotNull(provider);
            Assert.AreEqual("Azure DevOps", provider.DisplayName);
        }

        [TestMethod]
        public void GetProvider_LegacyVisualStudioCom_ReturnsAdoProvider()
        {
            var provider = DevHubProviderRegistry.GetProvider("https://myorg.visualstudio.com/project/_git/repo");

            Assert.IsNotNull(provider);
            Assert.AreEqual("Azure DevOps", provider.DisplayName);
        }

        [TestMethod]
        public void GetProvider_UnknownHost_ReturnsNull()
        {
            var provider = DevHubProviderRegistry.GetProvider("https://example.com/owner/repo.git");

            Assert.IsNull(provider);
        }

        [TestMethod]
        public void GetProvider_Null_ReturnsNull()
        {
            Assert.IsNull(DevHubProviderRegistry.GetProvider(null));
        }

        [TestMethod]
        public void GetProvider_Empty_ReturnsNull()
        {
            Assert.IsNull(DevHubProviderRegistry.GetProvider(""));
        }

        [TestMethod]
        public void GetAllProviders_ReturnsAtLeastTwoProviders()
        {
            var providers = DevHubProviderRegistry.GetAllProviders();

            Assert.IsGreaterThanOrEqualTo(2, providers.Count);
        }

        [TestMethod]
        public void GetAllProviders_ContainsGitHub()
        {
            var providers = DevHubProviderRegistry.GetAllProviders();

            Assert.IsTrue(providers.Any(p => p.DisplayName == "GitHub"));
        }

        [TestMethod]
        public void GetAllProviders_ContainsAzureDevOps()
        {
            var providers = DevHubProviderRegistry.GetAllProviders();

            Assert.IsTrue(providers.Any(p => p.DisplayName == "Azure DevOps"));
        }
    }
}
