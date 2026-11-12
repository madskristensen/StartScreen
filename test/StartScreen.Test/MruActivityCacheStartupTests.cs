using LibGit2Sharp;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class MruActivityCacheStartupTests
    {
        [TestMethod]
        [DataRow(3)]
        [DataRow(12)]
        [DataRow(72)]
        public async Task CachedActivityDiscovery_UsesExpiredRemotesWithoutOpeningMissingWorkspaceAsync(int ageHours)
        {
            using var directory = new TemporaryDirectory();
            var now = DateTime.UtcNow;
            var path = directory.GetPath("disconnected.sln");
            var item = new MruItem { Path = path, Type = MruItemType.Solution, RemoteUrl = "https://github.com/owner/repo" };
            var cachePath = directory.GetPath("metadata.json");
            var cache = new MruMetadataCache(cachePath, () => now);
            cache.Update([item], local: true);
            await cache.SaveAsync([item], CancellationToken.None);
            now = now.AddHours(ageHours);
            var reopened = new MruMetadataCache(cachePath, () => now);
            await reopened.LoadAsync(CancellationToken.None);
            var restored = new MruItem { Path = path, Type = MruItemType.Solution };

            var urls = await MruService.GetRepositoryRemoteUrlsAsync([restored], CancellationToken.None, reopened);

            CollectionAssert.AreEqual(new[] { item.RemoteUrl }, urls.ToArray());
            Assert.IsTrue(reopened.IsStale(restored), "Restoring stale identity hints must not extend their freshness.");
            Assert.IsFalse(File.Exists(path));
            var wrongType = new MruItem { Path = path, Type = MruItemType.Folder };
            Assert.IsEmpty(await MruService.GetRepositoryRemoteUrlsAsync([wrongType], CancellationToken.None, reopened));
        }

        [TestMethod]
        public async Task FreshActivityDiscovery_RechecksChangedRemotesAndDiscoversUncachedWorkspacesAsync()
        {
            using var directory = new TemporaryDirectory();
            var root = directory.GetPath("repo");
            Repository.Init(root);
            const string currentRemote = "https://github.com/owner/current";
            using (var repo = new Repository(root))
                repo.Network.Remotes.Add("origin", currentRemote);
            var now = DateTime.UtcNow;
            var cache = new MruMetadataCache(utcNow: () => now);
            cache.Update([new MruItem { Path = root, Type = MruItemType.Folder, RemoteUrl = "https://github.com/owner/previous" }], local: true);
            now = now.AddHours(12);

            var cached = await MruService.GetRepositoryRemoteUrlsAsync(
                [new MruItem { Path = root, Type = MruItemType.Folder }], CancellationToken.None, cache);
            var fresh = await MruService.GetRepositoryRemoteUrlsAsync(
                [new MruItem { Path = root, Type = MruItemType.Folder }], CancellationToken.None);
            var unknown = await MruService.GetRepositoryRemoteUrlsAsync(
                [new MruItem { Path = root, Type = MruItemType.Folder }], CancellationToken.None, new MruMetadataCache());

            CollectionAssert.AreEqual(new[] { "https://github.com/owner/previous" }, cached.ToArray());
            CollectionAssert.AreEqual(new[] { currentRemote }, fresh.ToArray());
            CollectionAssert.AreEqual(new[] { currentRemote }, unknown.ToArray());
            Assert.IsFalse(File.Exists(Path.Combine(root, ".git", "FETCH_HEAD")));
        }
    }
}
