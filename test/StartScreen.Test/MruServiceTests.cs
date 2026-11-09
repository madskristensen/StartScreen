using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using LibGit2Sharp;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.CodeContainerManagement;
using StartScreen.Models;
using StartScreen.Services;
using StartScreen.ToolWindows;
using CodeContainer = Microsoft.VisualStudio.Shell.CodeContainerManagement.CodeContainer;

namespace StartScreen.Test
{
    [TestClass]
    public class MruServiceTests
    {
        [TestMethod]
        public void RecentProjectsList_UsesHostRegistryAndPreservesSharedList()
        {
            var list = (ISettingsList)new SettingsProxy(typeof(ISettingsList), _ =>
                throw new AssertFailedException("Acquiring the registry must not modify the MRU list.")).GetTransparentProxy();
            var acquisitions = 0;
            var manager = (ISettingsManager)new SettingsProxy(typeof(ISettingsManager), call =>
            {
                Assert.AreEqual("GetOrCreateList", call.MethodName);
                Assert.AreEqual("CodeContainers.Offline", call.Args[0]);
                Assert.IsTrue((bool)call.Args[1]);
                acquisitions++;
                return list;
            }).GetTransparentProxy();

            Assert.AreSame(list, MruService.GetRecentProjectsList(manager));
            Assert.AreSame(list, MruService.GetRecentProjectsList(manager));
            Assert.AreEqual(2, acquisitions);
            Assert.IsNull(MruService.GetRecentProjectsList(null));
        }

        private sealed class SettingsProxy(Type type, Func<IMethodCallMessage, object?> invoke) : RealProxy(type)
        {
            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message;
                return new ReturnMessage(invoke(call), null, 0, call.LogicalCallContext, call);
            }
        }

        [TestMethod]
        [DataRow(@"C:\Unavailable\NewFolder", MruItemType.Folder)]
        [DataRow(@"C:\Unavailable\NewSolution.slnx", MruItemType.Solution)]
        [DataRow(@"C:\Unavailable\NewProject.csproj", MruItemType.Project)]
        public void RecentAccessTimes_GroupNewWorkspacesBeforeMetadataLoads(string path, MruItemType type)
        {
            var opened = DateTimeOffset.UtcNow;
            var container = new CodeContainer(new CodeContainerLocalProperties(path, CodeContainerType.Folder, null), null, false, opened);
            var list = (ISettingsList)new SettingsProxy(typeof(ISettingsList), call =>
            {
                Assert.AreEqual("GetValueOrDefault", call.MethodName);
                Assert.AreEqual(typeof(CodeContainer), call.MethodBase.GetGenericArguments().Single());
                Assert.AreEqual(path, call.Args[0]);
                return container;
            }).GetTransparentProxy();
            var item = new MruItem { Path = path, Type = type, LastAccessed = DateTime.Today.AddYears(-1) };

            MruService.ApplyRecentAccessTimes([item], list);

            Assert.AreEqual(opened.LocalDateTime, item.LastAccessed);
            Assert.AreEqual("Today", item.TimeGroup);
            Assert.IsTrue(item.HasRecentAccessTime);
            using var model = new StartScreenViewModel([item, new MruItem { LastAccessed = DateTime.Today.AddDays(-2) }]);
            Assert.AreSame(item, model.VisibleMruItems[0]);
            item.ApplyFallbackAccessTime(DateTime.Today.AddYears(-1));
            item.ApplyMetadata(new MruItem.Metadata { LastAccessed = DateTime.Today.AddDays(1) }, includeActivity: false);
            Assert.AreEqual(opened.LocalDateTime, item.LastAccessed, "Filesystem and cached metadata must not replace the VS timestamp.");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RecentAccessTimes_MissingOrUndatedEntryRetainsFallback(bool undated)
        {
            var item = new MruItem { Path = @"C:\Unavailable\Folder", Type = MruItemType.Folder, LastAccessed = DateTime.Today.AddDays(-2) };
            var container = undated ? new CodeContainer(new CodeContainerLocalProperties(item.Path, CodeContainerType.Folder, null), null, false, default) : null;
            var list = (ISettingsList)new SettingsProxy(typeof(ISettingsList), _ => container).GetTransparentProxy();

            MruService.ApplyRecentAccessTimes([item], null);
            MruService.ApplyRecentAccessTimes([item], list);

            Assert.IsFalse(item.HasRecentAccessTime);
            Assert.AreEqual(DateTime.Today.AddDays(-2), item.LastAccessed);
            item.ApplyFallbackAccessTime(DateTime.Today.AddDays(-3));
            Assert.AreEqual(DateTime.Today.AddDays(-2), item.LastAccessed);
            item.ApplyFallbackAccessTime(DateTime.Today);
            Assert.AreEqual(DateTime.Today, item.LastAccessed);
        }

        [TestMethod]
        public void ParseFolder_WithoutVsDirectoryUsesWorkspaceTimestamp()
        {
            using var directory = new TemporaryDirectory();
            var written = DateTime.Today.AddDays(-2);
            Directory.SetLastWriteTime(directory.Path, written);

            MruItem item = MruService.ParseMruEntry(directory.Path + "|{guid}|False|Folder");

            Assert.AreEqual(written, item.LastAccessed);
            Assert.IsFalse(Directory.Exists(Path.Combine(directory.Path, ".vs")));
        }

        [TestMethod]
        public async Task RepositoryRemoteUrls_ReadFreshMruPathsAcrossCheckoutLocationsWithoutFetchingAsync()
        {
            using var directory = new TemporaryDirectory();
            var firstRoot = directory.GetPath(@"Projects\First");
            var secondRoot = directory.GetPath(@"GitHub\Second");
            Repository.Init(firstRoot);
            Repository.Init(secondRoot);
            const string firstRemote = "https://dev.azure.com/org/project/_git/First";
            const string secondRemote = "https://github.com/owner/Second.git";
            using (var first = new Repository(firstRoot))
                first.Network.Remotes.Add("origin", firstRemote);
            using (var second = new Repository(secondRoot))
                second.Network.Remotes.Add("origin", secondRemote);
            var solution = Path.Combine(firstRoot, "App.sln");
            File.WriteAllText(solution, "");
            var nested = Directory.CreateDirectory(Path.Combine(firstRoot, "src")).FullName;
            var items = new[]
            {
                new MruItem { Path = solution, Type = MruItemType.Solution },
                new MruItem { Path = nested, Type = MruItemType.Folder },
                new MruItem { Path = secondRoot, Type = MruItemType.Folder },
                new MruItem { Path = directory.GetPath("missing.sln"), Type = MruItemType.Solution },
            };

            var urls = await MruService.GetRepositoryRemoteUrlsAsync(items, CancellationToken.None);

            CollectionAssert.AreEquivalent(new[] { firstRemote, secondRemote }, urls.ToArray());
            Assert.IsTrue(items.All(item => item.RemoteUrl == null), "Discovery must not mutate bound MRU items from a worker thread.");
            Assert.IsFalse(File.Exists(Path.Combine(firstRoot, ".git", "FETCH_HEAD")));
            Assert.IsFalse(File.Exists(Path.Combine(secondRoot, ".git", "FETCH_HEAD")));
        }

        [TestMethod]
        public async Task RepositoryRemoteUrls_PreserveKnownRemotesAndSkipLocalRepositoriesAsync()
        {
            using var directory = new TemporaryDirectory();
            Repository.Init(directory.Path);
            const string remote = "https://dev.azure.com/org/project/_git/repo";
            var urls = await MruService.GetRepositoryRemoteUrlsAsync(
            [
                new MruItem { RemoteUrl = remote },
                new MruItem { RemoteUrl = remote.ToUpperInvariant() },
                new MruItem { Path = directory.Path, Type = MruItemType.Folder },
                new MruItem(),
            ], CancellationToken.None);

            CollectionAssert.AreEqual(new[] { remote }, urls.ToArray());
        }

        [TestMethod]
        public async Task RepositoryRemoteUrls_CancellationStopsDiscoveryAsync()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                MruService.GetRepositoryRemoteUrlsAsync([], cancellation.Token));
        }

        [TestMethod]
        public async Task GetExtendedOnlyItems_CancellationPreservesHistoryAsync()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var options = new Options { ExtendedMruItems = "history", PinnedItems = "pinned" };

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                MruService.GetExtendedOnlyItemsAsync([], options, cancellation.Token));

            Assert.AreEqual("history", options.ExtendedMruItems);
            Assert.AreEqual("pinned", options.PinnedItems);
        }

        [TestMethod]
        public void TryReserveBackgroundFetch_ThrottlesReopensAndExpiresAtBoundary()
        {
            var repoPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsTrue(MruService.TryReserveBackgroundFetch(repoPath, now));
            Assert.IsFalse(MruService.TryReserveBackgroundFetch(repoPath.ToUpperInvariant(), now.AddMinutes(14)));
            Assert.IsTrue(MruService.TryReserveBackgroundFetch(repoPath, now.AddMinutes(15)));
            Assert.IsFalse(MruService.TryReserveBackgroundFetch(repoPath, now.AddMinutes(16)));
        }

        [TestMethod]
        public void TryReserveBackgroundFetch_ConcurrentRefreshesReserveOnlyOnce()
        {
            var repoPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var now = DateTime.UtcNow;
            var reservations = 0;

            Parallel.For(0, 20, _ =>
            {
                if (MruService.TryReserveBackgroundFetch(repoPath, now))
                    Interlocked.Increment(ref reservations);
            });

            Assert.AreEqual(1, reservations);
            Assert.IsTrue(MruService.TryReserveBackgroundFetch(repoPath + ".other", now));
        }

        [TestMethod]
        public void MergeCurrentRecentProjects_WhenSettingsUnavailable_PreservesLegacyItems()
        {
            var item = new MruItem { Path = @"C:\Projects\App\App.sln", Type = MruItemType.Solution };

            List<MruItem> result = MruService.MergeCurrentRecentProjects([item], null);

            CollectionAssert.AreEqual(new[] { item }, result);
        }

        [TestMethod]
        public void MergeCurrentRecentProjects_WhenRecentListCleared_RemovesLegacyItems()
        {
            var item = new MruItem { Path = @"C:\Projects\App\App.sln", Type = MruItemType.Solution };

            List<MruItem> result = MruService.MergeCurrentRecentProjects([item], []);

            Assert.IsEmpty(result);
        }

        [TestMethod]
        public void MergeCurrentRecentProjects_UsesCurrentSolutionPathAndPreservesLegacyRemovalEntries()
        {
            var item = new MruItem { Path = @"C:\Projects\App\App.sln", Type = MruItemType.Solution };
            item.RawMruEntries.Add(@"C:\Projects\App\App.sln|{guid}|False|App");
            string[] recentPaths = [@"C:\Projects\App\App.slnx"];

            List<MruItem> result = MruService.MergeCurrentRecentProjects([item], recentPaths);

            Assert.HasCount(1, result);
            Assert.AreEqual(recentPaths[0], result[0].Path);
            Assert.AreEqual("App", result[0].Name);
            CollectionAssert.AreEqual(item.RawMruEntries, result[0].RawMruEntries);
        }

        [TestMethod]
        public void MergeCurrentRecentProjects_RemovesEntriesMissingFromCurrentList()
        {
            var current = new MruItem { Path = @"C:\Projects\Current\Current.sln", Type = MruItemType.Solution };
            var stale = new MruItem { Path = @"C:\Projects\Stale\Stale.sln", Type = MruItemType.Solution };
            string[] recentPaths = [current.Path];

            List<MruItem> result = MruService.MergeCurrentRecentProjects([current, stale], recentPaths);

            CollectionAssert.AreEqual(new[] { current }, result);
        }

        [TestMethod]
        public void MergeCurrentRecentProjects_EmptyLegacyStoreLoadsSolutionsProjectsAndFolders()
        {
            string[] recentPaths = [@"C:\Projects\App\App.slnx", @"C:\Projects\Library\Library.csproj", @"C:\Projects\Folder"];

            List<MruItem> result = MruService.MergeCurrentRecentProjects([], recentPaths);

            CollectionAssert.AreEqual(recentPaths, result.Select(item => item.Path).ToArray());
            CollectionAssert.AreEqual(new[] { "App", "Library", "Folder" }, result.Select(item => item.Name).ToArray());
            CollectionAssert.AreEqual(new[] { MruItemType.Solution, MruItemType.Project, MruItemType.Folder }, result.Select(item => item.Type).ToArray());
            Assert.IsTrue(result.All(item => !item.IsFromExtendedList));
        }

        [TestMethod]
        public void MergeCurrentRecentProjects_PartialLegacyStoreKeepsMetadataAndAddsMissingItems()
        {
            var current = new MruItem { Path = @"C:\Projects\App\App.slnx", Name = "Custom name", Type = MruItemType.Solution, LastAccessed = DateTime.Now };
            current.RawMruEntries.Add(@"C:\Projects\App\App.slnx|{guid}|False|Custom name");
            string[] recentPaths = [current.Path, @"C:\Projects\Other\Other.sln", current.Path.ToUpperInvariant(), "", " "];

            List<MruItem> result = MruService.MergeCurrentRecentProjects([current], recentPaths);

            Assert.HasCount(2, result);
            Assert.AreSame(current, result[0]);
            Assert.AreEqual("Custom name", result[0].Name);
            Assert.AreEqual(current.LastAccessed, result[0].LastAccessed);
            Assert.AreEqual(recentPaths[1], result[1].Path);
        }

        [TestMethod]
        public void ClearPersistedHistoryIfRecentProjectsEmpty_ClearsHistoryAndPins()
        {
            var options = new Options
            {
                ExtendedMruItems = "history",
                PinnedItems = @"C:\Projects\App\App.sln"
            };

            bool changed = MruService.ClearPersistedHistoryIfRecentProjectsEmpty([], options);

            Assert.IsTrue(changed);
            Assert.AreEqual(string.Empty, options.ExtendedMruItems);
            Assert.AreEqual(string.Empty, options.PinnedItems);
        }

        [TestMethod]
        public void ClearPersistedHistoryIfRecentProjectsEmpty_WhenSettingsUnavailable_PreservesHistory()
        {
            var options = new Options { ExtendedMruItems = "history" };

            bool changed = MruService.ClearPersistedHistoryIfRecentProjectsEmpty(null, options);

            Assert.IsFalse(changed);
            Assert.AreEqual("history", options.ExtendedMruItems);
        }

        [TestMethod]
        [DataRow(@"C:\Code\App.sln", MruItemType.Solution)]
        [DataRow(@"C:\Code\App.slnx", MruItemType.Solution)]
        [DataRow(@"C:\Code\App.csproj", MruItemType.Project)]
        [DataRow(@"C:\Code\App.vbproj", MruItemType.Project)]
        [DataRow(@"C:\Code\App.fsproj", MruItemType.Project)]
        [DataRow(@"C:\Code\App.vcxproj", MruItemType.Project)]
        [DataRow(@"C:\Code\Folder", MruItemType.Folder)]
        public void DetermineType_ReturnsExpectedType(string path, MruItemType expected)
        {
            Assert.AreEqual(expected, MruService.DetermineType(path));
        }

        [TestMethod]
        public void ParseMruEntry_WithValidEntry_MapsFieldsAndPreservesRawValue()
        {
            const string raw = @"C:\Code\App.sln|{guid}|False|Display App|ignored";

            MruItem item = MruService.ParseMruEntry(raw);

            Assert.IsNotNull(item);
            Assert.AreEqual(@"C:\Code\App.sln", item.Path);
            Assert.AreEqual("Display App", item.Name);
            Assert.AreEqual(MruItemType.Solution, item.Type);
            CollectionAssert.AreEqual(new[] { raw }, item.RawMruEntries);
        }

        [TestMethod]
        public void ParseMruEntry_WithMissingDisplayName_UsesFileName()
        {
            MruItem item = MruService.ParseMruEntry(@"C:\Code\App.csproj|{guid}|False||ignored");

            Assert.AreEqual("App", item.Name);
        }

        [TestMethod]
        public void ParseMruEntry_WithMalformedEntry_ReturnsNull()
        {
            Assert.IsNull(MruService.ParseMruEntry("not|enough|parts"));
        }

        [TestMethod]
        public void FindSuoLastWriteTime_ReturnsNewestSuo()
        {
            using var directory = new TemporaryDirectory();
            var solutionPath = directory.GetPath("App.sln");
            File.WriteAllText(solutionPath, string.Empty);
            var first = directory.GetPath(@".vs\App\v17\.suo");
            var second = directory.GetPath(@".vs\App\v18\.suo");
            Directory.CreateDirectory(Path.GetDirectoryName(first));
            Directory.CreateDirectory(Path.GetDirectoryName(second));
            File.WriteAllText(first, string.Empty);
            File.WriteAllText(second, string.Empty);
            var older = new DateTime(2026, 1, 1, 0, 0, 0);
            var newer = older.AddDays(1);
            File.SetLastWriteTime(first, older);
            File.SetLastWriteTime(second, newer);

            DateTime? result = MruService.FindSuoLastWriteTime(solutionPath);

            Assert.AreEqual(newer, result);
        }

        [TestMethod]
        public async Task GetExtendedOnlyItemsAsync_ExcludesCurrentItemsAndRestoresPinsAsync()
        {
            var current = new MruItem
            {
                Path = @"C:\Code\Current\Current.sln",
                Name = "Current",
                Type = MruItemType.Solution,
            };
            var options = new Options
            {
                ExtendedMruItems =
                    "[{\"Path\":\"C:\\\\Code\\\\Current\\\\Current.sln\",\"Name\":\"Old current\",\"Type\":0,\"LastAccessed\":\"2026-01-01T00:00:00\"}," +
                    "{\"Path\":\"C:\\\\Code\\\\Older\\\\Older.sln\",\"Name\":\"Older\",\"Type\":0,\"LastAccessed\":\"2025-01-01T00:00:00\"}]",
                PinnedItems = @"C:\Code\Older\Older.sln",
            };

            List<MruItem> result = await MruService.GetExtendedOnlyItemsAsync([current], options);

            Assert.HasCount(1, result);
            Assert.AreEqual("Older", result[0].Name);
            Assert.IsTrue(result[0].IsFromExtendedList);
            Assert.IsTrue(result[0].IsPinned);
        }
    }
}
