using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class BuildSearchQueryTests
    {
        [TestMethod]
        public void BuildSearchQuery_NoCustomQuery_ReturnsDefaultIssueQuery()
        {
            var result = GitHubDevHubProvider.BuildSearchQuery("is:issue", "testuser");

            Assert.AreEqual("is:issue is:open involves:testuser", result);
        }

        [TestMethod]
        public void BuildSearchQuery_NoCustomQuery_ReturnsDefaultPrQuery()
        {
            var result = GitHubDevHubProvider.BuildSearchQuery("is:pr", "testuser");

            Assert.AreEqual("is:pr is:open involves:testuser", result);
        }

        [TestMethod]
        public void BuildSearchQuery_NullCustomQuery_ReturnsDefault()
        {
            var result = GitHubDevHubProvider.BuildSearchQuery("is:issue", "testuser", null);

            Assert.AreEqual("is:issue is:open involves:testuser", result);
        }

        [TestMethod]
        public void BuildSearchQuery_EmptyCustomQuery_ReturnsDefault()
        {
            var result = GitHubDevHubProvider.BuildSearchQuery("is:issue", "testuser", "");

            Assert.AreEqual("is:issue is:open involves:testuser", result);
        }

        [TestMethod]
        public void BuildSearchQuery_WhitespaceCustomQuery_ReturnsDefault()
        {
            var result = GitHubDevHubProvider.BuildSearchQuery("is:issue", "testuser", "   ");

            Assert.AreEqual("is:issue is:open involves:testuser", result);
        }

        [TestMethod]
        public void BuildSearchQuery_CustomQuery_PrependsPrPrefix()
        {
            var custom = "state:open archived:false (user:madskristensen OR org:VsixCommunity)";

            var result = GitHubDevHubProvider.BuildSearchQuery("is:pr", "madskristensen", custom);

            Assert.AreEqual("is:pr state:open archived:false (user:madskristensen OR org:VsixCommunity)", result);
        }

        [TestMethod]
        public void BuildSearchQuery_CustomQuery_PrependsIssuePrefix()
        {
            var custom = "state:open archived:false sort:updated-desc (user:madskristensen OR org:VsixCommunity OR org:ligershark)";

            var result = GitHubDevHubProvider.BuildSearchQuery("is:issue", "madskristensen", custom);

            Assert.AreEqual("is:issue state:open archived:false sort:updated-desc (user:madskristensen OR org:VsixCommunity OR org:ligershark)", result);
        }

        [TestMethod]
        public void BuildSearchQuery_CustomQueryWithLoginPlaceholder_ReplacesLogin()
        {
            var custom = "state:open involves:{login} org:MyOrg";

            var result = GitHubDevHubProvider.BuildSearchQuery("is:issue", "johndoe", custom);

            Assert.AreEqual("is:issue state:open involves:johndoe org:MyOrg", result);
        }

        [TestMethod]
        public void BuildSearchQuery_CustomQueryWithMultipleLoginPlaceholders_ReplacesAll()
        {
            var custom = "author:{login} assignee:{login}";

            var result = GitHubDevHubProvider.BuildSearchQuery("is:pr", "johndoe", custom);

            Assert.AreEqual("is:pr author:johndoe assignee:johndoe", result);
        }

        [TestMethod]
        public void BuildSearchQuery_CustomQueryWithoutPlaceholder_UsesQueryAsIs()
        {
            var custom = "state:open user:specificuser";

            var result = GitHubDevHubProvider.BuildSearchQuery("is:issue", "otheruser", custom);

            Assert.AreEqual("is:issue state:open user:specificuser", result);
        }
    }
}
