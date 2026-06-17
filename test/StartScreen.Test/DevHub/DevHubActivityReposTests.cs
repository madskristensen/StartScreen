using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubActivityReposTests
    {
        private readonly AzureDevOpsDevHubProvider _ado = new AzureDevOpsDevHubProvider();
        private readonly GitHubDevHubProvider _gitHub = new GitHubDevHubProvider();

        [TestMethod]
        public async System.Threading.Tasks.Task Ado_ReturnsCloudRepoWithProject()
        {
            var urls = new[] { "https://dev.azure.com/org/project/_git/repo" };

            var repos = await _ado.GetActivityReposAsync(urls, CancellationToken.None);

            Assert.AreEqual(1, repos.Count);
            Assert.AreEqual("org", repos[0].Owner);
            Assert.AreEqual("project", repos[0].Project);
            Assert.AreEqual("repo", repos[0].Repo);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Ado_DeduplicatesIdenticalRepos()
        {
            var urls = new[]
            {
                "https://dev.azure.com/org/project/_git/repo",
                "git@ssh.dev.azure.com:v3/org/project/repo",
            };

            var repos = await _ado.GetActivityReposAsync(urls, CancellationToken.None);

            Assert.AreEqual(1, repos.Count);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Ado_IgnoresNonAdoUrls()
        {
            var urls = new[]
            {
                "https://github.com/owner/repo",
                "https://bitbucket.org/owner/repo",
            };

            var repos = await _ado.GetActivityReposAsync(urls, CancellationToken.None);

            Assert.AreEqual(0, repos.Count);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Ado_IgnoresNullAndEmptyEntries()
        {
            var urls = new[] { null, "", "   ", "not a url" };

            var repos = await _ado.GetActivityReposAsync(urls, CancellationToken.None);

            Assert.AreEqual(0, repos.Count);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Ado_NullInput_ReturnsEmpty()
        {
            var repos = await _ado.GetActivityReposAsync(null, CancellationToken.None);

            Assert.AreEqual(0, repos.Count);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Ado_OnPremServerRepo_IsIncluded()
        {
            var urls = new[] { "https://tfs.contoso.com/tfs/DefaultCollection/MyProject/_git/MyRepo" };

            var repos = await _ado.GetActivityReposAsync(urls, CancellationToken.None);

            Assert.AreEqual(1, repos.Count);
            Assert.IsTrue(repos[0].IsAzureDevOpsServer);
            Assert.AreEqual("MyProject", repos[0].Project);
            Assert.AreEqual("MyRepo", repos[0].Repo);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task GitHub_ReturnsEmpty()
        {
            var urls = new[] { "https://github.com/owner/repo" };

            var repos = await _gitHub.GetActivityReposAsync(urls, CancellationToken.None);

            Assert.AreEqual(0, repos.Count);
        }
    }
}
