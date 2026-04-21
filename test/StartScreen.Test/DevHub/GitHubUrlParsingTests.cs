using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class GitHubUrlParsingTests
    {
        private readonly GitHubDevHubProvider _provider = new GitHubDevHubProvider();

        [TestMethod]
        public void CanHandle_GitHubHttps_ReturnsTrue()
        {
            Assert.IsTrue(_provider.CanHandle("https://github.com/owner/repo.git"));
        }

        [TestMethod]
        public void CanHandle_GitHubSsh_ReturnsTrue()
        {
            Assert.IsTrue(_provider.CanHandle("git@github.com:owner/repo.git"));
        }

        [TestMethod]
        public void CanHandle_AzureDevOps_ReturnsFalse()
        {
            Assert.IsFalse(_provider.CanHandle("https://dev.azure.com/org/project/_git/repo"));
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
        public void ParseRemoteUrl_GitHubHttps_ReturnsIdentifier()
        {
            var result = _provider.ParseRemoteUrl("https://github.com/madskristensen/StartScreen.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("github.com", result.Host);
            Assert.AreEqual("madskristensen", result.Owner);
            Assert.AreEqual("StartScreen", result.Repo);
        }

        [TestMethod]
        public void ParseRemoteUrl_GitHubSsh_ReturnsIdentifier()
        {
            var result = _provider.ParseRemoteUrl("git@github.com:madskristensen/StartScreen.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("madskristensen", result.Owner);
            Assert.AreEqual("StartScreen", result.Repo);
        }

        [TestMethod]
        public void ParseRemoteUrl_NonGitHubUrl_ReturnsNull()
        {
            var result = _provider.ParseRemoteUrl("https://gitlab.com/user/project.git");

            Assert.IsNull(result);
        }

        [TestMethod]
        public void ParseRepoIdentifierFromApiUrl_Valid_ParsesCorrectly()
        {
            var result = GitHubDevHubProvider.ParseRepoIdentifierFromApiUrl(
                "https://api.github.com/repos/madskristensen/StartScreen");

            Assert.IsNotNull(result);
            Assert.AreEqual("github.com", result.Host);
            Assert.AreEqual("madskristensen", result.Owner);
            Assert.AreEqual("StartScreen", result.Repo);
        }

        [TestMethod]
        public void ParseRepoIdentifierFromApiUrl_Null_ReturnsNull()
        {
            Assert.IsNull(GitHubDevHubProvider.ParseRepoIdentifierFromApiUrl(null));
        }

        [TestMethod]
        public void ParseRepoIdentifierFromApiUrl_Empty_ReturnsNull()
        {
            Assert.IsNull(GitHubDevHubProvider.ParseRepoIdentifierFromApiUrl(""));
        }

        [TestMethod]
        public void ParseRepoIdentifierFromApiUrl_WrongPrefix_ReturnsNull()
        {
            Assert.IsNull(GitHubDevHubProvider.ParseRepoIdentifierFromApiUrl(
                "https://github.com/repos/owner/repo"));
        }

        [TestMethod]
        public void GetRepoWebUrl_ReturnsCorrectUrl()
        {
            var repo = new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

            Assert.AreEqual("https://github.com/madskristensen/StartScreen", _provider.GetRepoWebUrl(repo));
        }

        [TestMethod]
        public void GetPullRequestsWebUrl_ReturnsCorrectUrl()
        {
            var repo = new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

            Assert.AreEqual("https://github.com/madskristensen/StartScreen/pulls", _provider.GetPullRequestsWebUrl(repo));
        }

        [TestMethod]
        public void GetIssuesWebUrl_ReturnsCorrectUrl()
        {
            var repo = new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

            Assert.AreEqual("https://github.com/madskristensen/StartScreen/issues", _provider.GetIssuesWebUrl(repo));
        }
    }
}
