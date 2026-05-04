using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class AzureDevOpsServerUrlParsingTests
    {
        private readonly AzureDevOpsDevHubProvider _provider = new AzureDevOpsDevHubProvider();

        [TestMethod]
        public void CanHandle_OnPremGitUrl_ReturnsTrue()
        {
            Assert.IsTrue(_provider.CanHandle("https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_git/MyRepo"));
        }

        [TestMethod]
        public void CanHandle_OnPremNoTfsPrefix_ReturnsTrue()
        {
            Assert.IsTrue(_provider.CanHandle("https://devops.contoso.com/MyCollection/MyProject/_git/MyRepo"));
        }

        [TestMethod]
        public void CanHandle_OnPremWithPort_ReturnsTrue()
        {
            Assert.IsTrue(_provider.CanHandle("https://tfs.contoso.com:8080/tfs/DefaultCollection/MyProject/_git/MyRepo"));
        }

        [TestMethod]
        public void ParseRemoteUrl_OnPremWithTfsPrefix_PopulatesIdentifier()
        {
            var result = _provider.ParseRemoteUrl("https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_git/MyRepo");

            Assert.IsNotNull(result);
            Assert.AreEqual("tfs.contoso.com", result.Host);
            Assert.AreEqual("DefaultCollection", result.Owner);
            Assert.AreEqual("MyProject", result.Project);
            Assert.AreEqual("MyRepo", result.Repo);
            Assert.AreEqual("https://tfs.contoso.com/tfs/DefaultCollection", result.BaseUrl);
            Assert.IsTrue(result.IsAzureDevOpsServer);
        }

        [TestMethod]
        public void ParseRemoteUrl_OnPremNoTfsPrefix_PopulatesIdentifier()
        {
            var result = _provider.ParseRemoteUrl("https://devops.contoso.com/MyCollection/MyProject/_git/MyRepo");

            Assert.IsNotNull(result);
            Assert.AreEqual("devops.contoso.com", result.Host);
            Assert.AreEqual("MyCollection", result.Owner);
            Assert.AreEqual("MyProject", result.Project);
            Assert.AreEqual("MyRepo", result.Repo);
            Assert.AreEqual("https://devops.contoso.com/MyCollection", result.BaseUrl);
            Assert.IsTrue(result.IsAzureDevOpsServer);
        }

        [TestMethod]
        public void ParseRemoteUrl_OnPremWithPort_PreservesAuthority()
        {
            var result = _provider.ParseRemoteUrl("https://tfs.contoso.com:8080/tfs/DefaultCollection/MyProject/_git/MyRepo");

            Assert.IsNotNull(result);
            Assert.AreEqual("tfs.contoso.com", result.Host);
            Assert.AreEqual("https://tfs.contoso.com:8080/tfs/DefaultCollection", result.BaseUrl);
            Assert.IsTrue(result.IsAzureDevOpsServer);
        }

        [TestMethod]
        public void ParseRemoteUrl_DotGitSuffix_StrippedFromRepo()
        {
            var result = _provider.ParseRemoteUrl("https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_git/MyRepo.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("MyRepo", result.Repo);
        }

        [TestMethod]
        public void ParseRemoteUrl_CloudUrl_NotMarkedAsServer()
        {
            var result = _provider.ParseRemoteUrl("https://dev.azure.com/myorg/MyProject/_git/MyRepo");

            Assert.IsNotNull(result);
            Assert.IsFalse(result.IsAzureDevOpsServer);
            Assert.AreEqual("https://dev.azure.com/myorg", result.BaseUrl);
        }

        [TestMethod]
        public void GetRepoWebUrl_OnPrem_BuildsFromBaseUrl()
        {
            var result = _provider.ParseRemoteUrl("https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_git/MyRepo");

            Assert.AreEqual(
                "https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_git/MyRepo",
                _provider.GetRepoWebUrl(result));
        }

        [TestMethod]
        public void GetPullRequestsWebUrl_OnPrem_BuildsFromBaseUrl()
        {
            var result = _provider.ParseRemoteUrl("https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_git/MyRepo");

            Assert.AreEqual(
                "https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_git/MyRepo/pullrequests",
                _provider.GetPullRequestsWebUrl(result));
        }

        [TestMethod]
        public void GetIssuesWebUrl_OnPrem_BuildsFromBaseUrl()
        {
            var result = _provider.ParseRemoteUrl("https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_git/MyRepo");

            Assert.AreEqual(
                "https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_workitems",
                _provider.GetIssuesWebUrl(result));
        }

        [TestMethod]
        public void DiscoverServers_FindsOnPremRemotes()
        {
            var remotes = new[]
            {
                "https://github.com/owner/repo.git",
                "https://dev.azure.com/myorg/MyProject/_git/MyRepo",
                "https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_git/MyRepo",
                "https://devops.example.com/MyCollection/MyProject/_git/Other",
                "https://tfs.contoso.com/tfs/DefaultCollection/AnotherProject/_git/X",
            };

            var discovered = AzureDevOpsServerHelper.DiscoverServers(remotes);

            Assert.AreEqual(2, discovered.Count);
            CollectionAssert.AreEquivalent(
                new[] { "tfs.contoso.com", "devops.example.com" },
                discovered.Select(d => d.Host).ToArray());
        }

        [TestMethod]
        public void DiscoverServers_HonorsExclusions()
        {
            var remotes = new[]
            {
                "https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_git/MyRepo",
                "https://devops.example.com/MyCollection/MyProject/_git/Other",
            };

            var discovered = AzureDevOpsServerHelper.DiscoverServers(remotes, excludeHosts: new[] { "tfs.contoso.com" });

            Assert.AreEqual(1, discovered.Count);
            Assert.AreEqual("devops.example.com", discovered[0].Host);
        }
    }
}
