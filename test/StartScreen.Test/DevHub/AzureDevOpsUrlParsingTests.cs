using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class AzureDevOpsUrlParsingTests
    {
        private readonly AzureDevOpsDevHubProvider _provider = new AzureDevOpsDevHubProvider();

        [TestMethod]
        public void CanHandle_DevAzureCom_ReturnsTrue()
        {
            Assert.IsTrue(_provider.CanHandle("https://dev.azure.com/org/project/_git/repo"));
        }

        [TestMethod]
        public void CanHandle_VisualStudioCom_ReturnsTrue()
        {
            Assert.IsTrue(_provider.CanHandle("https://myorg.visualstudio.com/project/_git/repo"));
        }

        [TestMethod]
        public void CanHandle_SshDevAzureCom_ReturnsTrue()
        {
            Assert.IsTrue(_provider.CanHandle("git@ssh.dev.azure.com:v3/org/project/repo"));
        }

        [TestMethod]
        public void CanHandle_GitHub_ReturnsFalse()
        {
            Assert.IsFalse(_provider.CanHandle("https://github.com/owner/repo"));
        }

        [TestMethod]
        public void CanHandle_Null_ReturnsFalse()
        {
            Assert.IsFalse(_provider.CanHandle(null));
        }

        [TestMethod]
        public void CanHandle_Empty_ReturnsFalse()
        {
            Assert.IsFalse(_provider.CanHandle(""));
        }

        [TestMethod]
        public void ParseRemoteUrl_DevAzureComHttps_ReturnsIdentifier()
        {
            var result = _provider.ParseRemoteUrl("https://dev.azure.com/myorg/MyProject/_git/MyRepo");

            Assert.IsNotNull(result);
            Assert.AreEqual("dev.azure.com", result.Host);
            Assert.AreEqual("myorg", result.Owner);
            Assert.AreEqual("MyRepo", result.Repo);
            Assert.AreEqual("MyProject", result.Project);
        }

        [TestMethod]
        public void ParseRemoteUrl_SshDevAzureCom_ReturnsIdentifier()
        {
            var result = _provider.ParseRemoteUrl("git@ssh.dev.azure.com:v3/myorg/MyProject/MyRepo");

            Assert.IsNotNull(result);
            Assert.AreEqual("dev.azure.com", result.Host);
            Assert.AreEqual("myorg", result.Owner);
            Assert.AreEqual("MyRepo", result.Repo);
            Assert.AreEqual("MyProject", result.Project);
        }

        [TestMethod]
        public void ParseRemoteUrl_LegacyVisualStudioCom_ReturnsIdentifier()
        {
            var result = _provider.ParseRemoteUrl("https://myorg.visualstudio.com/MyProject/_git/MyRepo");

            Assert.IsNotNull(result);
            Assert.AreEqual("dev.azure.com", result.Host);
            Assert.AreEqual("myorg", result.Owner);
            Assert.AreEqual("MyRepo", result.Repo);
            Assert.AreEqual("MyProject", result.Project);
        }

        [TestMethod]
        [DataRow("https://devdiv.visualstudio.com/DevDiv/_git/_optimized/VSEditor", "https://devdiv.visualstudio.com", false)]
        [DataRow("https://dev.azure.com/devdiv/DevDiv/_git/_optimized/VSEditor", "https://dev.azure.com/devdiv", false)]
        [DataRow("https://tfs.contoso.com/tfs/devdiv/DevDiv/_git/_optimized/VSEditor.git", "https://tfs.contoso.com/tfs/devdiv", true)]
        [DataRow("https://devdiv.visualstudio.com/DevDiv/_git/_OPTIMIZED/VSEditor/", "https://devdiv.visualstudio.com", false)]
        public void ParseRemoteUrl_OptimizedTransportSegmentIsNotRepositoryName(string url, string baseUrl, bool server)
        {
            var result = _provider.ParseRemoteUrl(url);

            Assert.IsNotNull(result);
            Assert.AreEqual("devdiv", result.Owner);
            Assert.AreEqual("DevDiv", result.Project);
            Assert.AreEqual("VSEditor", result.Repo);
            Assert.AreEqual(baseUrl, result.BaseUrl);
            Assert.AreEqual(server, result.IsAzureDevOpsServer);
            Assert.AreEqual($"{baseUrl}/DevDiv/_git/VSEditor", _provider.GetRepoWebUrl(result));
        }

        [TestMethod]
        public void ParseRemoteUrl_NonAdoUrl_ReturnsNull()
        {
            var result = _provider.ParseRemoteUrl("https://github.com/owner/repo.git");

            Assert.IsNull(result);
        }

        [TestMethod]
        public void GetRepoWebUrl_ReturnsCorrectUrl()
        {
            var repo = new RemoteRepoIdentifier("dev.azure.com", "myorg", "MyRepo", "MyProject");

            Assert.AreEqual("https://dev.azure.com/myorg/MyProject/_git/MyRepo", _provider.GetRepoWebUrl(repo));
        }

        [TestMethod]
        public void GetPullRequestsWebUrl_ReturnsCorrectUrl()
        {
            var repo = new RemoteRepoIdentifier("dev.azure.com", "myorg", "MyRepo", "MyProject");

            Assert.AreEqual("https://dev.azure.com/myorg/MyProject/_git/MyRepo/pullrequests", _provider.GetPullRequestsWebUrl(repo));
        }

        [TestMethod]
        public void GetIssuesWebUrl_ReturnsCorrectUrl()
        {
            var repo = new RemoteRepoIdentifier("dev.azure.com", "myorg", "MyRepo", "MyProject");

            Assert.AreEqual("https://dev.azure.com/myorg/MyProject/_workitems", _provider.GetIssuesWebUrl(repo));
        }

        [TestMethod]
        public void GetCloudProfileBaseUrls_IncludesModernAndLegacyOrganizations()
        {
            var urls = new[]
            {
                "https://dev.azure.com/myorg/MyProject/_git/MyRepo",
                "https://devdiv.visualstudio.com/DevDiv/_git/Repo",
                "https://devdiv.visualstudio.com/DevDiv/_git/Repo/",
                "https://github.com/owner/repo",
            };

            var results = AzureDevOpsDevHubProvider.GetCloudProfileBaseUrls(urls);

            CollectionAssert.AreEqual(new[] { "https://dev.azure.com/myorg", "https://devdiv.visualstudio.com" }, results.ToArray());
        }
    }
}
