using StartScreen.Models.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubItemSorterTests
    {
        [TestMethod]
        public void Sort_Null_ReturnsEmptyList()
        {
            var result = DevHubItemSorter.Sort<DevHubIssue>(
                null,
                DevHubSortOrder.MostRecent,
                i => i.RepoIdentifier,
                i => i.UpdatedAt);

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Sort_MostRecent_OrdersByUpdatedDescendingIgnoringProvider()
        {
            var items = new[]
            {
                CreateIssue("github.com", "2025-01-01"),
                CreateIssue("dev.azure.com", "2025-03-01"),
                CreateIssue("github.com", "2025-02-01")
            };

            var result = DevHubItemSorter.Sort(
                items,
                DevHubSortOrder.MostRecent,
                i => i.RepoIdentifier,
                i => i.UpdatedAt);

            Assert.AreEqual("dev.azure.com", result[0].RepoIdentifier.Host);
            Assert.AreEqual(new DateTime(2025, 2, 1), result[1].UpdatedAt);
            Assert.AreEqual(new DateTime(2025, 1, 1), result[2].UpdatedAt);
        }

        [TestMethod]
        public void Sort_GitHubFirst_PlacesGitHubBeforeOthersThenByDate()
        {
            var items = new[]
            {
                CreateIssue("dev.azure.com", "2025-03-01"),
                CreateIssue("github.com", "2025-01-01"),
                CreateIssue("github.com", "2025-02-01")
            };

            var result = DevHubItemSorter.Sort(
                items,
                DevHubSortOrder.GitHubFirst,
                i => i.RepoIdentifier,
                i => i.UpdatedAt);

            Assert.AreEqual("github.com", result[0].RepoIdentifier.Host);
            Assert.AreEqual(new DateTime(2025, 2, 1), result[0].UpdatedAt);
            Assert.AreEqual("github.com", result[1].RepoIdentifier.Host);
            Assert.AreEqual(new DateTime(2025, 1, 1), result[1].UpdatedAt);
            Assert.AreEqual("dev.azure.com", result[2].RepoIdentifier.Host);
        }

        [TestMethod]
        public void Sort_AzureDevOpsFirst_PlacesCloudAndServerAdoBeforeGitHub()
        {
            var items = new[]
            {
                CreateIssue("github.com", "2025-05-01"),
                CreateIssue("dev.azure.com", "2025-01-01"),
                CreateIssue("tfs.contoso.com", "2025-02-01", isAzureDevOpsServer: true)
            };

            var result = DevHubItemSorter.Sort(
                items,
                DevHubSortOrder.AzureDevOpsFirst,
                i => i.RepoIdentifier,
                i => i.UpdatedAt);

            Assert.IsTrue(result[0].RepoIdentifier.Host == "tfs.contoso.com");
            Assert.IsTrue(result[1].RepoIdentifier.Host == "dev.azure.com");
            Assert.AreEqual("github.com", result[2].RepoIdentifier.Host);
        }

        private static DevHubIssue CreateIssue(string host, string updated, bool isAzureDevOpsServer = false)
        {
            return new DevHubIssue
            {
                ProviderName = host,
                RepoDisplayName = "owner/repo",
                RepoIdentifier = new RemoteRepoIdentifier(host, "owner", "repo", isAzureDevOpsServer: isAzureDevOpsServer),
                Title = "Issue",
                Number = "#1",
                NumericId = 1,
                Author = "user",
                State = "open",
                UpdatedAt = DateTime.Parse(updated, System.Globalization.CultureInfo.InvariantCulture),
                CreatedAt = DateTime.Parse(updated, System.Globalization.CultureInfo.InvariantCulture),
                WebUrl = "https://example.com/issues/1"
            };
        }
    }
}
