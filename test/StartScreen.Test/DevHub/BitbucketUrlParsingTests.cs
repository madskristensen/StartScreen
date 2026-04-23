using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class BitbucketUrlParsingTests
    {
        private readonly BitbucketDevHubProvider _provider = new BitbucketDevHubProvider();

        [TestMethod]
        public void CanHandle_BitbucketHttps_ReturnsTrue()
        {
            Assert.IsTrue(_provider.CanHandle("https://bitbucket.org/owner/repo.git"));
        }

        [TestMethod]
        public void CanHandle_BitbucketSsh_ReturnsTrue()
        {
            Assert.IsTrue(_provider.CanHandle("git@bitbucket.org:owner/repo.git"));
        }

        [TestMethod]
        public void CanHandle_GitHub_ReturnsFalse()
        {
            Assert.IsFalse(_provider.CanHandle("https://github.com/owner/repo"));
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
        public void ParseRemoteUrl_BitbucketHttps_ReturnsIdentifier()
        {
            var result = _provider.ParseRemoteUrl("https://bitbucket.org/myworkspace/myrepo.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("bitbucket.org", result.Host);
            Assert.AreEqual("myworkspace", result.Owner);
            Assert.AreEqual("myrepo", result.Repo);
        }

        [TestMethod]
        public void ParseRemoteUrl_BitbucketSsh_ReturnsIdentifier()
        {
            var result = _provider.ParseRemoteUrl("git@bitbucket.org:myworkspace/myrepo.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("bitbucket.org", result.Host);
            Assert.AreEqual("myworkspace", result.Owner);
            Assert.AreEqual("myrepo", result.Repo);
        }

        [TestMethod]
        public void ParseRemoteUrl_BitbucketHttpsNoGitSuffix_ReturnsIdentifier()
        {
            var result = _provider.ParseRemoteUrl("https://bitbucket.org/myworkspace/myrepo");

            Assert.IsNotNull(result);
            Assert.AreEqual("myworkspace", result.Owner);
            Assert.AreEqual("myrepo", result.Repo);
        }

        [TestMethod]
        public void ParseRemoteUrl_NonBitbucketUrl_ReturnsNull()
        {
            var result = _provider.ParseRemoteUrl("https://github.com/owner/repo.git");

            Assert.IsNull(result);
        }

        [TestMethod]
        public void ParseRemoteUrl_Null_ReturnsNull()
        {
            Assert.IsNull(_provider.ParseRemoteUrl(null));
        }

        [TestMethod]
        public void GetRepoWebUrl_ReturnsCorrectUrl()
        {
            var repo = new RemoteRepoIdentifier("bitbucket.org", "myworkspace", "myrepo");

            Assert.AreEqual("https://bitbucket.org/myworkspace/myrepo", _provider.GetRepoWebUrl(repo));
        }

        [TestMethod]
        public void GetPullRequestsWebUrl_ReturnsCorrectUrl()
        {
            var repo = new RemoteRepoIdentifier("bitbucket.org", "myworkspace", "myrepo");

            Assert.AreEqual("https://bitbucket.org/myworkspace/myrepo/pull-requests", _provider.GetPullRequestsWebUrl(repo));
        }

        [TestMethod]
        public void GetIssuesWebUrl_ReturnsCorrectUrl()
        {
            var repo = new RemoteRepoIdentifier("bitbucket.org", "myworkspace", "myrepo");

            Assert.AreEqual("https://bitbucket.org/myworkspace/myrepo/issues", _provider.GetIssuesWebUrl(repo));
        }

        [TestMethod]
        public void MapBitbucketPrState_Open_ReturnsOpen()
        {
            Assert.AreEqual("open", BitbucketDevHubProvider.MapBitbucketPrState("OPEN"));
        }

        [TestMethod]
        public void MapBitbucketPrState_Merged_ReturnsMerged()
        {
            Assert.AreEqual("merged", BitbucketDevHubProvider.MapBitbucketPrState("MERGED"));
        }

        [TestMethod]
        public void MapBitbucketPrState_Declined_ReturnsClosed()
        {
            Assert.AreEqual("closed", BitbucketDevHubProvider.MapBitbucketPrState("DECLINED"));
        }

        [TestMethod]
        public void MapBitbucketPrState_Superseded_ReturnsClosed()
        {
            Assert.AreEqual("closed", BitbucketDevHubProvider.MapBitbucketPrState("SUPERSEDED"));
        }

        [TestMethod]
        public void MapBitbucketPrState_Null_ReturnsOpen()
        {
            Assert.AreEqual("open", BitbucketDevHubProvider.MapBitbucketPrState(null));
        }

        [TestMethod]
        public void MapBitbucketPipelineStatus_Pending_ReturnsPending()
        {
            Assert.AreEqual("pending", BitbucketDevHubProvider.MapBitbucketPipelineStatus("PENDING", null));
        }

        [TestMethod]
        public void MapBitbucketPipelineStatus_Building_ReturnsPending()
        {
            Assert.AreEqual("pending", BitbucketDevHubProvider.MapBitbucketPipelineStatus("BUILDING", null));
        }

        [TestMethod]
        public void MapBitbucketPipelineStatus_CompletedSuccessful_ReturnsSuccess()
        {
            Assert.AreEqual("success", BitbucketDevHubProvider.MapBitbucketPipelineStatus("COMPLETED", "SUCCESSFUL"));
        }

        [TestMethod]
        public void MapBitbucketPipelineStatus_CompletedFailed_ReturnsFailure()
        {
            Assert.AreEqual("failure", BitbucketDevHubProvider.MapBitbucketPipelineStatus("COMPLETED", "FAILED"));
        }

        [TestMethod]
        public void MapBitbucketPipelineStatus_CompletedStopped_ReturnsCancelled()
        {
            Assert.AreEqual("cancelled", BitbucketDevHubProvider.MapBitbucketPipelineStatus("COMPLETED", "STOPPED"));
        }

        [TestMethod]
        public void MapBitbucketPipelineStatus_CompletedError_ReturnsFailure()
        {
            Assert.AreEqual("failure", BitbucketDevHubProvider.MapBitbucketPipelineStatus("COMPLETED", "ERROR"));
        }

        [TestMethod]
        public void MapBitbucketPipelineStatus_CompletedExpired_ReturnsCancelled()
        {
            Assert.AreEqual("cancelled", BitbucketDevHubProvider.MapBitbucketPipelineStatus("COMPLETED", "EXPIRED"));
        }
    }
}
