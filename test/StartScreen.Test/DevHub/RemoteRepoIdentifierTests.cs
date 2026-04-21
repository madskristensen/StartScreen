using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class RemoteRepoIdentifierTests
    {
        // --- GitHub HTTPS ---

        [TestMethod]
        public void TryParse_GitHubHttps_ParsesOwnerAndRepo()
        {
            var result = RemoteRepoIdentifier.TryParse("https://github.com/madskristensen/StartScreen.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("github.com", result.Host);
            Assert.AreEqual("madskristensen", result.Owner);
            Assert.AreEqual("StartScreen", result.Repo);
            Assert.IsNull(result.Project);
        }

        [TestMethod]
        public void TryParse_GitHubHttpsWithoutDotGit_ParsesCorrectly()
        {
            var result = RemoteRepoIdentifier.TryParse("https://github.com/madskristensen/StartScreen");

            Assert.IsNotNull(result);
            Assert.AreEqual("github.com", result.Host);
            Assert.AreEqual("madskristensen", result.Owner);
            Assert.AreEqual("StartScreen", result.Repo);
        }

        [TestMethod]
        public void TryParse_GitHubHttpsWithTrailingSlash_ParsesCorrectly()
        {
            var result = RemoteRepoIdentifier.TryParse("https://github.com/madskristensen/StartScreen/");

            Assert.IsNotNull(result);
            Assert.AreEqual("StartScreen", result.Repo);
        }

        // --- GitHub SSH ---

        [TestMethod]
        public void TryParse_GitHubSsh_ParsesOwnerAndRepo()
        {
            var result = RemoteRepoIdentifier.TryParse("git@github.com:madskristensen/StartScreen.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("github.com", result.Host);
            Assert.AreEqual("madskristensen", result.Owner);
            Assert.AreEqual("StartScreen", result.Repo);
            Assert.IsNull(result.Project);
        }

        [TestMethod]
        public void TryParse_GitHubSshWithoutDotGit_ParsesCorrectly()
        {
            var result = RemoteRepoIdentifier.TryParse("git@github.com:madskristensen/StartScreen");

            Assert.IsNotNull(result);
            Assert.AreEqual("madskristensen", result.Owner);
            Assert.AreEqual("StartScreen", result.Repo);
        }

        // --- Azure DevOps HTTPS ---

        [TestMethod]
        public void TryParse_AdoHttps_ParsesOrgProjectAndRepo()
        {
            var result = RemoteRepoIdentifier.TryParse("https://dev.azure.com/myorg/MyProject/_git/MyRepo");

            Assert.IsNotNull(result);
            Assert.AreEqual("dev.azure.com", result.Host);
            Assert.AreEqual("myorg", result.Owner);
            Assert.AreEqual("MyRepo", result.Repo);
            Assert.AreEqual("MyProject", result.Project);
        }

        [TestMethod]
        public void TryParse_AdoHttpsWithDotGit_ParsesCorrectly()
        {
            var result = RemoteRepoIdentifier.TryParse("https://dev.azure.com/myorg/MyProject/_git/MyRepo.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("dev.azure.com", result.Host);
            Assert.AreEqual("myorg", result.Owner);
            Assert.AreEqual("MyRepo", result.Repo);
            Assert.AreEqual("MyProject", result.Project);
        }

        // --- Azure DevOps SSH ---

        [TestMethod]
        public void TryParse_AdoSsh_ParsesOrgProjectAndRepo()
        {
            var result = RemoteRepoIdentifier.TryParse("git@ssh.dev.azure.com:v3/myorg/MyProject/MyRepo");

            Assert.IsNotNull(result);
            Assert.AreEqual("dev.azure.com", result.Host);
            Assert.AreEqual("myorg", result.Owner);
            Assert.AreEqual("MyRepo", result.Repo);
            Assert.AreEqual("MyProject", result.Project);
        }

        // --- Azure DevOps Legacy (visualstudio.com) ---

        [TestMethod]
        public void TryParse_AdoLegacyHttps_ParsesCorrectly()
        {
            var result = RemoteRepoIdentifier.TryParse("https://myorg.visualstudio.com/MyProject/_git/MyRepo");

            Assert.IsNotNull(result);
            Assert.AreEqual("dev.azure.com", result.Host);
            Assert.AreEqual("myorg", result.Owner);
            Assert.AreEqual("MyRepo", result.Repo);
            Assert.AreEqual("MyProject", result.Project);
        }

        // --- Bitbucket ---

        [TestMethod]
        public void TryParse_BitbucketHttps_ParsesOwnerAndRepo()
        {
            var result = RemoteRepoIdentifier.TryParse("https://bitbucket.org/myteam/myrepo.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("bitbucket.org", result.Host);
            Assert.AreEqual("myteam", result.Owner);
            Assert.AreEqual("myrepo", result.Repo);
            Assert.IsNull(result.Project);
        }

        [TestMethod]
        public void TryParse_BitbucketSsh_ParsesOwnerAndRepo()
        {
            var result = RemoteRepoIdentifier.TryParse("git@bitbucket.org:myteam/myrepo.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("bitbucket.org", result.Host);
            Assert.AreEqual("myteam", result.Owner);
            Assert.AreEqual("myrepo", result.Repo);
        }

        // --- GitLab ---

        [TestMethod]
        public void TryParse_GitLabHttps_ParsesOwnerAndRepo()
        {
            var result = RemoteRepoIdentifier.TryParse("https://gitlab.com/myuser/myproject.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("gitlab.com", result.Host);
            Assert.AreEqual("myuser", result.Owner);
            Assert.AreEqual("myproject", result.Repo);
        }

        [TestMethod]
        public void TryParse_GitLabSsh_ParsesOwnerAndRepo()
        {
            var result = RemoteRepoIdentifier.TryParse("git@gitlab.com:myuser/myproject.git");

            Assert.IsNotNull(result);
            Assert.AreEqual("gitlab.com", result.Host);
            Assert.AreEqual("myuser", result.Owner);
            Assert.AreEqual("myproject", result.Repo);
        }

        // --- Invalid inputs ---

        [TestMethod]
        public void TryParse_Null_ReturnsNull()
        {
            Assert.IsNull(RemoteRepoIdentifier.TryParse(null));
        }

        [TestMethod]
        public void TryParse_EmptyString_ReturnsNull()
        {
            Assert.IsNull(RemoteRepoIdentifier.TryParse(""));
        }

        [TestMethod]
        public void TryParse_WhitespaceOnly_ReturnsNull()
        {
            Assert.IsNull(RemoteRepoIdentifier.TryParse("   "));
        }

        [TestMethod]
        public void TryParse_InvalidUrl_ReturnsNull()
        {
            Assert.IsNull(RemoteRepoIdentifier.TryParse("not-a-url"));
        }

        // --- DisplayName ---

        [TestMethod]
        public void DisplayName_WithoutProject_ReturnsOwnerSlashRepo()
        {
            var id = new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

            Assert.AreEqual("madskristensen/StartScreen", id.DisplayName);
        }

        [TestMethod]
        public void DisplayName_WithProject_ReturnsOwnerSlashProjectSlashRepo()
        {
            var id = new RemoteRepoIdentifier("dev.azure.com", "myorg", "MyRepo", "MyProject");

            Assert.AreEqual("myorg/MyProject/MyRepo", id.DisplayName);
        }

        // --- Equality ---

        [TestMethod]
        public void Equals_SameValues_ReturnsTrue()
        {
            var a = new RemoteRepoIdentifier("github.com", "owner", "repo");
            var b = new RemoteRepoIdentifier("github.com", "owner", "repo");

            Assert.IsTrue(a.Equals(b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        [TestMethod]
        public void Equals_CaseInsensitive_ReturnsTrue()
        {
            var a = new RemoteRepoIdentifier("GitHub.com", "Owner", "Repo");
            var b = new RemoteRepoIdentifier("github.com", "owner", "repo");

            Assert.IsTrue(a.Equals(b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        [TestMethod]
        public void Equals_DifferentHost_ReturnsFalse()
        {
            var a = new RemoteRepoIdentifier("github.com", "owner", "repo");
            var b = new RemoteRepoIdentifier("gitlab.com", "owner", "repo");

            Assert.IsFalse(a.Equals(b));
        }

        [TestMethod]
        public void Equals_DifferentOwner_ReturnsFalse()
        {
            var a = new RemoteRepoIdentifier("github.com", "owner1", "repo");
            var b = new RemoteRepoIdentifier("github.com", "owner2", "repo");

            Assert.IsFalse(a.Equals(b));
        }

        [TestMethod]
        public void Equals_Null_ReturnsFalse()
        {
            var a = new RemoteRepoIdentifier("github.com", "owner", "repo");

            Assert.IsFalse(a.Equals(null));
        }

        [TestMethod]
        public void Equals_WithProject_BothMatch_ReturnsTrue()
        {
            var a = new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project");
            var b = new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project");

            Assert.IsTrue(a.Equals(b));
        }

        [TestMethod]
        public void Equals_WithProject_DifferentProject_ReturnsFalse()
        {
            var a = new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project1");
            var b = new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project2");

            Assert.IsFalse(a.Equals(b));
        }

        // --- ToString ---

        [TestMethod]
        public void ToString_ReturnsHostSlashDisplayName()
        {
            var id = new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

            Assert.AreEqual("github.com/madskristensen/StartScreen", id.ToString());
        }
    }
}
