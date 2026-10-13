using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;
using static Microsoft.VisualStudio.VSConstants;

namespace StartScreen.Services
{
    /// <summary>
    /// Manages the Most Recently Used (MRU) list by reading from VS's recent-project stores.
    /// Pinned state is stored in VS Options.
    /// </summary>
    internal static class MruService
    {
        private const string _recentProjectsListName = "CodeContainers.Offline";
        private const uint _maxVsItems = 50;

        [Guid("9b164e40-c3a2-4363-9bc5-eb4039def653")]
        private sealed class SVsSettingsPersistenceManager
        {
        }

        /// <summary>
        /// Reads VS's MRU via IVsMRUItemsStore and applies pinned state from Options.
        /// </summary>
        public static async Task<List<MruItem>> GetMruItemsAsync(Options options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // CodeContainers.Offline drives Visual Studio's Recent Projects UI. The legacy
            // MRU store supplies richer entry metadata, but can retain entries after that
            // list is cleared.
            ISettingsManager settingsManager = await VS.GetServiceAsync<SVsSettingsPersistenceManager, ISettingsManager>();
            string[] recentProjectPaths = settingsManager?
                .GetOrCreateList(_recentProjectsListName, isMachineLocal: true)
                .Keys;

            IVsMRUItemsStore store = await VS.GetServiceAsync<SVsMRUItemsStore, IVsMRUItemsStore>();

            // Use caller-provided options or load from settings (also free-threaded).
            if (options == null)
            {
                options = await Options.GetLiveInstanceAsync();
            }

            if (ClearPersistedHistoryIfRecentProjectsEmpty(recentProjectPaths, options))
            {
                await options.SaveAsync();
            }

            // Only the legacy COM call requires the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var rawEntries = new List<string>();
            if (store != null)
            {
                var buffer = new string[_maxVsItems];
                Guid projectsGuid = MruList.Projects;
                var count = store.GetMRUItems(ref projectsGuid, string.Empty, _maxVsItems, buffer);

                for (uint i = 0; i < count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(buffer[i]))
                    {
                        rawEntries.Add(buffer[i]);
                    }
                }
            }

            // Hop off the UI thread for all post-processing (parsing, dedup, pin/sort).
            // The caller is responsible for switching back to the UI thread before
            // touching the bound ObservableCollections.
            await TaskScheduler.Default;

            var pinnedPaths = new HashSet<string>(
                (options.PinnedItems ?? "").Split([';'], StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            // Parse MRU entries in parallel. The expensive part is the per-item file
            // I/O in GetLastAccessTime (it enumerates .vs/{name}/v*/ subdirectories
            // to locate .suo files). Deduplicate so .sln and .slnx in the same
            // directory collapse to one entry, keeping the most recently accessed.
            var parsed = new MruItem[rawEntries.Count];
            Parallel.For(0, rawEntries.Count, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 2
            }, i =>
            {
                parsed[i] = ParseMruEntry(rawEntries[i]);
            });

            var itemsByKey = new Dictionary<string, MruItem>(StringComparer.OrdinalIgnoreCase);
            foreach (MruItem item in parsed)
            {
                if (item == null)
                    continue;

                var key = GetDeduplicationKey(item);
                if (itemsByKey.TryGetValue(key, out MruItem existing))
                {
                    if (item.LastAccessed > existing.LastAccessed)
                    {
                        // Keep the newer item but collect all raw entries
                        item.RawMruEntries.AddRange(existing.RawMruEntries);
                        itemsByKey[key] = item;
                    }
                    else
                    {
                        existing.RawMruEntries.AddRange(item.RawMruEntries);
                    }
                }
                else
                {
                    itemsByKey[key] = item;
                }
            }

            List<MruItem> vsItems = FilterToCurrentRecentProjects(itemsByKey.Values, recentProjectPaths);

            // Apply pinned state from Options and build an ordered list for stable pin ordering
            var pinnedOrderList = (options.PinnedItems ?? "").Split([';'], StringSplitOptions.RemoveEmptyEntries);
            var pinnedOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < pinnedOrderList.Length; i++)
            {
                pinnedOrder[pinnedOrderList[i]] = i;
            }

            foreach (MruItem item in vsItems)
            {
                item.IsPinned = pinnedPaths.Contains(item.Path);
            }

            // Sort: pinned first (in saved order), then unpinned by last accessed
            var pinned = vsItems.Where(i => i.IsPinned)
                                .OrderBy(i => pinnedOrder.TryGetValue(i.Path, out var idx) ? idx : int.MaxValue)
                                .ToList();
            var unpinned = vsItems.Where(i => !i.IsPinned)
                                  .OrderByDescending(i => i.LastAccessed)
                                  .ToList();

            pinned.AddRange(unpinned);
            return pinned;
        }

        internal static List<MruItem> FilterToCurrentRecentProjects(
            IEnumerable<MruItem> items, IEnumerable<string> recentProjectPaths)
        {
            var itemList = items.ToList();
            if (recentProjectPaths == null)
                return itemList;

            var recentKeys = new HashSet<string>(
                recentProjectPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => GetDeduplicationKeyFromPath(path, DetermineType(path))),
                StringComparer.OrdinalIgnoreCase);

            return itemList
                .Where(item => recentKeys.Contains(GetDeduplicationKey(item)))
                .ToList();
        }

        internal static bool ClearPersistedHistoryIfRecentProjectsEmpty(
            IReadOnlyCollection<string> recentProjectPaths, Options options)
        {
            if (recentProjectPaths == null
                || recentProjectPaths.Count > 0
                || (string.IsNullOrEmpty(options.ExtendedMruItems) && string.IsNullOrEmpty(options.PinnedItems)))
            {
                return false;
            }

            options.ExtendedMruItems = "";
            options.PinnedItems = "";
            return true;
        }

        /// <summary>
        /// Removes items from VS's MRU store and the persisted Start Screen history.
        /// </summary>
        public static async Task RemoveItemsAsync(IEnumerable<MruItem> items, Options options = null)
        {
            List<MruItem> itemList = items?
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Path))
                .ToList();

            if (itemList == null || itemList.Count == 0)
                return;

            try
            {
                IVsMRUItemsStore store = await VS.GetServiceAsync<SVsMRUItemsStore, IVsMRUItemsStore>();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (store != null)
                {
                    Guid projectsGuid = MruList.Projects;
                    foreach (MruItem item in itemList)
                    {
                        foreach (var rawEntry in item.RawMruEntries)
                        {
                            store.DeleteMRUItem(ref projectsGuid, rawEntry);
                        }
                    }
                }

                await TaskScheduler.Default;
                options ??= await Options.GetLiveInstanceAsync();

                var removedKeys = new HashSet<string>(
                    itemList.Select(item => GetDeduplicationKeyFromPath(item.Path, item.Type)),
                    StringComparer.OrdinalIgnoreCase);
                List<ExtendedMruEntry> entries = LoadExtendedEntries(options);
                entries.RemoveAll(entry => removedKeys.Contains(
                    GetDeduplicationKeyFromPath(entry.Path, (MruItemType)entry.Type)));

                var removedPaths = new HashSet<string>(
                    itemList.Select(item => item.Path),
                    StringComparer.OrdinalIgnoreCase);
                IEnumerable<string> pinnedPaths = (options.PinnedItems ?? "")
                    .Split([';'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(path => !removedPaths.Contains(path));

                options.ExtendedMruItems = JsonSerializer.Serialize(entries, _jsonOptions);
                options.PinnedItems = string.Join(";", pinnedPaths);
                await options.SaveAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        /// <summary>
        /// Skip a per-repo background fetch if we already fetched it this recently.
        /// Collapses repeated tool-window shows, solution-close events, and refresh
        /// ticks into a single network call per repo per window.
        /// </summary>
        private static readonly TimeSpan _fetchThrottleWindow = TimeSpan.FromMinutes(15);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _fetchAttempts =
            new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        internal static bool TryReserveBackgroundFetch(string repoPath, DateTime utcNow)
        {
            while (true)
            {
                if (_fetchAttempts.TryGetValue(repoPath, out DateTime lastAttempt))
                {
                    if (utcNow - lastAttempt < _fetchThrottleWindow)
                        return false;

                    if (_fetchAttempts.TryUpdate(repoPath, utcNow, lastAttempt))
                        return true;
                }
                else if (_fetchAttempts.TryAdd(repoPath, utcNow))
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Populates Git status information for items in the background.
        /// Phase 1: Reads local status (branch, stash, dirty, operation) immediately.
        /// Phase 2: Fetches from remotes and updates ahead/behind counts as they complete.
        /// Background results are applied to bound items on the UI thread in small batches.
        /// </summary>
        public static async Task PopulateGitStatusAsync(
            IEnumerable<MruItem> items,
            System.Threading.CancellationToken cancellationToken = default)
        {
            // Switch to background thread for parallel Git operations
            await TaskScheduler.Default;

            var itemList = items.ToList();

            // Track item-to-repo mapping for phase 2, including upstream
            // coordinates so we can run a targeted fetch instead of fetch --all.
            var itemRepoMap = new System.Collections.Concurrent.ConcurrentDictionary<string, RepoFetchInfo>(StringComparer.OrdinalIgnoreCase);

            // Memoize FindRepositoryPath results within this refresh so multiple
            // MRU items in the same repo (solution + projects + folders) don't each
            // walk the parent chain independently.
            var repoPathCache = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var statusCache = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<GitStatus>>(StringComparer.OrdinalIgnoreCase);

            var localUpdates =
                new System.Collections.Concurrent.ConcurrentBag<(MruItem Item, GitStatus Status, int SessionCount)>();

            // Phase 1: Read local status (fast, no network)
            Parallel.ForEach(itemList, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 2
            }, item =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Skip items whose target no longer exists on disk; otherwise we'd
                    // pay for a parent-chain walk and a libgit2 Repository open per
                    // dead path for nothing.
                    if (string.IsNullOrWhiteSpace(item.Path)
                        || (!Directory.Exists(item.Path) && !File.Exists(item.Path)))
                    {
                        return;
                    }

                    var startDir = item.Type == MruItemType.Folder ? item.Path : Path.GetDirectoryName(item.Path);
                    var repoPath = repoPathCache.GetOrAdd(startDir ?? string.Empty, GitHelper.FindRepositoryPath);
                    GitStatus status = repoPath == null
                        ? new GitStatus()
                        : statusCache.GetOrAdd(repoPath,
                            path => new Lazy<GitStatus>(() => GitHelper.GetGitStatus(path))).Value;

                    // Detect Copilot Chat sessions stored under .vs\*\copilot-chat\*\sessions
                    var sessionCount = CopilotChatHelper.CountSessions(item.Path, item.Type);
                    localUpdates.Add((item, status, sessionCount));

                    // Track repo path for phase 2 (if item is in a git repo
                    // and the current branch has an upstream we can fetch).
                    if (status.IsGitRepository
                        && !string.IsNullOrEmpty(status.UpstreamRemoteName)
                        && !string.IsNullOrEmpty(status.UpstreamBranchRef))
                    {
                        if (!string.IsNullOrEmpty(repoPath))
                        {
                            itemRepoMap.AddOrUpdate(
                                repoPath,
                                _ => new RepoFetchInfo(status.UpstreamRemoteName, status.UpstreamBranchRef, item),
                                (_, info) => { lock (info.Items) { info.Items.Add(item); } return info; });
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ex.LogAsync().FileAndForget(nameof(MruService));
                }
            });

            await ApplyGitStatusUpdatesAsync(localUpdates, cancellationToken);

            // Phase 2: Fetch and update ahead/behind (slow, network).
            if (itemRepoMap.Count == 0)
                return;

            // Skip the entire phase 2 burst when the machine has no usable network
            // interface. Otherwise each fetch sits at the 5 s timeout before being
            // killed, turning an offline laptop into a 25+ second git.exe storm.
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                return;

            await TaskScheduler.Default;
            var remoteUpdates =
                new System.Collections.Concurrent.ConcurrentBag<(MruItem Item, int? Ahead, int? Behind)>();

            using (var semaphore = new SemaphoreSlim(2))
            {
                await Task.WhenAll(itemRepoMap.Select(async kvp =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    var repoPath = kvp.Key;
                    RepoFetchInfo info = kvp.Value;

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Throttle: if FETCH_HEAD was written within the throttle window,
                        // the ahead/behind we already read in phase 1 is fresh enough.
                        // Skip both the fetch and the post-fetch re-read entirely.
                        if (GitHelper.WasRecentlyFetched(repoPath, _fetchThrottleWindow)
                            || !TryReserveBackgroundFetch(repoPath, DateTime.UtcNow))
                            return;

                        GitCommandResult result = await GitHelper.FetchUpstreamAsync(
                            repoPath, info.Remote, info.UpstreamRef, cancellationToken);
                        if (!result.Succeeded)
                        {
                            System.Diagnostics.Trace.TraceWarning(result.ErrorMessage);
                            return;
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        // Re-read ahead/behind for all items in this repo
                        (var ahead, var behind) = GitHelper.GetUpdatedAheadBehind(repoPath);

                        foreach (MruItem item in info.Items)
                        {
                            // Only update if fetch succeeded and tracking branch exists
                            if (ahead.HasValue || behind.HasValue)
                            {
                                remoteUpdates.Add((item, ahead, behind));
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await ex.LogAsync();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await ApplyAheadBehindUpdatesAsync(remoteUpdates, cancellationToken);
        }

        private static async Task ApplyGitStatusUpdatesAsync(
            IEnumerable<(MruItem Item, GitStatus Status, int SessionCount)> updates,
            System.Threading.CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var index = 0;

            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                update.Item.ApplyGitStatus(update.Status);
                update.Item.CopilotChatSessionCount = update.SessionCount;

                if (++index % 8 == 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
        }

        private static async Task ApplyAheadBehindUpdatesAsync(
            IEnumerable<(MruItem Item, int? Ahead, int? Behind)> updates,
            System.Threading.CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var index = 0;

            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                update.Item.CommitsAhead = update.Ahead;
                update.Item.CommitsBehind = update.Behind;

                if (++index % 8 == 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
        }

        /// <summary>
        /// Per-repository fetch coordinates used by <see cref="PopulateGitStatusAsync"/>
        /// to run a single targeted fetch covering all MRU items that resolve to the
        /// same git repository.
        /// </summary>
        private sealed class RepoFetchInfo
        {
            public RepoFetchInfo(string remote, string upstreamRef, MruItem firstItem)
            {
                Remote = remote;
                UpstreamRef = upstreamRef;
                Items = [firstItem];
            }

            public string Remote { get; }
            public string UpstreamRef { get; }
            public List<MruItem> Items { get; }
        }

        /// <summary>
        /// Pulls the repository for an MRU item and refreshes its Git status afterward.
        /// </summary>
        public static async Task<GitCommandResult> PullGitAsync(MruItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
                return new GitCommandResult(false, "The MRU item does not have a valid path.");

            await TaskScheduler.Default;

            var startDir = Directory.Exists(item.Path) ? item.Path : Path.GetDirectoryName(item.Path);
            var repoPath = GitHelper.FindRepositoryPath(startDir);
            if (string.IsNullOrEmpty(repoPath))
                return new GitCommandResult(false, "The MRU item is not in a Git repository.");

            GitCommandResult result = await GitHelper.PullAsync(repoPath);
            item.ApplyGitStatus(GitHelper.GetGitStatus(item.Path));

            return result;
        }

        /// <summary>
        /// Checks file/folder existence on background threads, then applies bound state
        /// on the UI thread in small batches.
        /// </summary>
        public static async Task PopulateExistenceAsync(
            IEnumerable<MruItem> items,
            System.Threading.CancellationToken cancellationToken = default)
        {
            await TaskScheduler.Default;

            var itemList = items.ToList();
            var updates =
                new System.Collections.Concurrent.ConcurrentBag<(MruItem Item, bool Exists)>();

            Parallel.ForEach(itemList, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 2
            }, item =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    updates.Add((item, item.CalculateExists()));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            });

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var index = 0;

            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                update.Item.ApplyExists(update.Exists);

                if (++index % 16 == 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
        }

        /// <summary>
        /// Parses a single MRU entry from IVsMRUItemsStore into an MruItem.
        /// </summary>
        /// <remarks>
        /// Format: path|{guid}|bool|displayName|...|{guid}
        /// The first segment is the path (may contain environment variables).
        /// The fourth segment is the display name.
        /// </remarks>
        internal static MruItem ParseMruEntry(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            try
            {
                var parts = raw.Split('|');
                if (parts.Length < 4)
                    return null;

                var rawPath = Environment.ExpandEnvironmentVariables(parts[0]);
                if (string.IsNullOrWhiteSpace(rawPath))
                    return null;

                var displayName = parts[3];
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = Path.GetFileNameWithoutExtension(rawPath);
                }

                MruItemType type = DetermineType(rawPath);

                var item = new MruItem
                {
                    Path = rawPath,
                    Name = displayName,
                    Type = type,
                    LastAccessed = GetLastAccessTime(rawPath, type),
                    IsPinned = false
                };

                item.RawMruEntries.Add(raw);
                return item;
            }
            catch (Exception ex)
            {
                ex.Log();
                return null;
            }
        }

        /// <summary>
        /// Determines the MRU item type from file path.
        /// </summary>
        internal static MruItemType DetermineType(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return MruItemType.Solution;

            var extension = Path.GetExtension(path)?.ToLowerInvariant();

            if (extension == ".sln" || extension == ".slnx")
                return MruItemType.Solution;

            if (extension == ".csproj" || extension == ".vbproj" || extension == ".fsproj" || extension == ".vcxproj")
                return MruItemType.Project;

            // No extension means it is a folder path (avoids Directory.Exists I/O)
            if (string.IsNullOrEmpty(extension))
                return MruItemType.Folder;

            return MruItemType.Solution;
        }

        /// <summary>
        /// Gets the most accurate "last opened" time for an MRU path.
        /// </summary>
        /// <remarks>
        /// For solutions/projects, VS writes the .suo file on every open/close,
        /// so its last write time is the best proxy for "last opened in VS".
        /// The .suo lives at .vs/{SolutionName}/v{N}/.suo next to the solution file.
        /// For Open Folder, the .vs/ directory itself is updated on each session.
        /// Falls back to the path's own last write time if no .suo is found.
        /// </remarks>
        private static DateTime GetLastAccessTime(string path, MruItemType type)
        {
            try
            {
                if (type == MruItemType.Folder)
                {
                    // Open Folder scenario: check the .vs/ folder inside it
                    var vsDir = System.IO.Path.Combine(path, ".vs");
                    try
                    {
                        return Directory.GetLastWriteTime(vsDir);
                    }
                    catch
                    {
                        return Directory.GetLastWriteTime(path);
                    }
                }

                // Solution/project file: look for .suo in the .vs/ folder
                DateTime? suoTime = FindSuoLastWriteTime(path);
                if (suoTime.HasValue)
                    return suoTime.Value;

                return File.GetLastWriteTime(path);
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            return DateTime.Now;
        }

        /// <summary>
        /// Searches for a .suo file inside .vs/{name}/v*/ next to the given solution/project path.
        /// Returns the last write time of the most recently modified .suo found, or null.
        /// </summary>
        /// <remarks>
        /// The .suo lives at .vs/{SolutionName}/v{N}/.suo (exactly 2 levels deep).
        /// Using a targeted search avoids walking the entire .vs tree which may contain
        /// large intellisense caches, build artifacts, and other VS data.
        /// </remarks>
        internal static DateTime? FindSuoLastWriteTime(string filePath)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(filePath);
                if (string.IsNullOrEmpty(dir))
                    return null;

                var vsDir = System.IO.Path.Combine(dir, ".vs");
                if (!Directory.Exists(vsDir))
                    return null;

                // Search only 2 levels deep: .vs/{name}/v{N}/.suo
                DateTime? latest = null;
                foreach (var subDir in Directory.EnumerateDirectories(vsDir))
                {
                    foreach (var versionDir in Directory.EnumerateDirectories(subDir, "v*"))
                    {
                        var suoPath = System.IO.Path.Combine(versionDir, ".suo");
                        if (File.Exists(suoPath))
                        {
                            DateTime writeTime = File.GetLastWriteTime(suoPath);
                            if (!latest.HasValue || writeTime > latest.Value)
                            {
                                latest = writeTime;
                            }
                        }
                    }
                }

                return latest;
            }
            catch (Exception ex)
            {
                ex.Log();
                return null;
            }
        }

        /// <summary>
        /// Normalizes a file or directory path for consistent deduplication.
        /// </summary>
        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path)
                           .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// Gets a deduplication key that collapses solution files (.sln, .slnx) in the
        /// same directory into one entry. This prevents duplicates when VS lists both
        /// a .sln and a .slnx for the same project.
        /// Folder entries keep their own normalized path as key.
        /// </summary>
        private static string GetDeduplicationKey(MruItem item)
        {
            return GetDeduplicationKeyFromPath(item.Path, item.Type);
        }

        /// <summary>
        /// Computes the deduplication key from a path and type without requiring an MruItem.
        /// </summary>
        internal static string GetDeduplicationKeyFromPath(string path, MruItemType type)
        {
            if (type == MruItemType.Folder)
            {
                return NormalizePath(path);
            }

            try
            {
                var dir = Path.GetDirectoryName(path);

                return string.IsNullOrEmpty(dir)
                    ? NormalizePath(path)
                    : NormalizePath(dir);
            }
            catch
            {
                return NormalizePath(path);
            }
        }

        // ------------------------------------------------------------------ //
        //  Extended (unlimited) MRU list                                      //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Lightweight DTO used for JSON serialization of the extended MRU list.
        /// </summary>
        private sealed class ExtendedMruEntry
        {
            public string Path { get; set; }
            public string Name { get; set; }
            public int Type { get; set; }
            public DateTime LastAccessed { get; set; }
        }

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        /// <summary>
        /// Deserializes the extended MRU list from the Options JSON string.
        /// Returns an empty list on parse failure.
        /// </summary>
        private static List<ExtendedMruEntry> LoadExtendedEntries(Options options)
        {
            var json = options?.ExtendedMruItems;
            if (string.IsNullOrWhiteSpace(json))
                return [];

            try
            {
                return JsonSerializer.Deserialize<List<ExtendedMruEntry>>(json, _jsonOptions) ?? [];
            }
            catch (Exception ex)
            {
                ex.Log();
                return [];
            }
        }

        /// <summary>
        /// Returns items that exist in the extended list but are not already represented in
        /// <paramref name="vsItems"/>. Also merges <paramref name="vsItems"/> into the
        /// extended list and saves it so the history grows automatically.
        /// Must be called from a background thread.
        /// </summary>
        internal static async Task<List<MruItem>> GetExtendedOnlyItemsAsync(
            IEnumerable<MruItem> vsItems, Options options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vsItemList = vsItems.ToList();

            // Build a set of dedup keys already covered by the VS MRU store.
            var vsKeys = new HashSet<string>(
                vsItemList.Select(i => GetDeduplicationKeyFromPath(i.Path, i.Type)),
                StringComparer.OrdinalIgnoreCase);

            // Reload from options to get the latest persisted state.
            if (options == null)
                options = await Options.GetLiveInstanceAsync();

            var entries = LoadExtendedEntries(options);

            // Index by dedup key; last-writer-wins on collision (shouldn't normally happen).
            var byKey = new Dictionary<string, ExtendedMruEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Path))
                    continue;

                var key = GetDeduplicationKeyFromPath(entry.Path, (MruItemType)entry.Type);
                byKey[key] = entry;
            }

            // Merge in VS items: add new entries or update LastAccessed when VS has a newer time.
            var changed = false;
            foreach (var vsItem in vsItemList)
            {
                var key = GetDeduplicationKeyFromPath(vsItem.Path, vsItem.Type);
                if (!byKey.TryGetValue(key, out var existing) ||
                    vsItem.LastAccessed > existing.LastAccessed)
                {
                    byKey[key] = new ExtendedMruEntry
                    {
                        Path = vsItem.Path,
                        Name = vsItem.Name,
                        Type = (int)vsItem.Type,
                        LastAccessed = vsItem.LastAccessed
                    };
                    changed = true;
                }
            }

            if (changed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                options.ExtendedMruItems = JsonSerializer.Serialize(byKey.Values.ToList(), _jsonOptions);
                await options.SaveAsync();
            }

            // Build the pinned set so extended items can inherit their pinned state.
            var pinnedPaths = new HashSet<string>(
                (options.PinnedItems ?? "").Split([';'], StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            // Return entries that are NOT already covered by the VS MRU list.
            var result = new List<MruItem>();
            foreach (var kvp in byKey)
            {
                if (vsKeys.Contains(kvp.Key))
                    continue;

                var entry = kvp.Value;
                if (string.IsNullOrWhiteSpace(entry.Path))
                    continue;

                var item = new MruItem
                {
                    Path = entry.Path,
                    Name = entry.Name,
                    Type = (MruItemType)entry.Type,
                    LastAccessed = entry.LastAccessed,
                    IsPinned = pinnedPaths.Contains(entry.Path),
                    IsFromExtendedList = true
                };

                result.Add(item);
            }

            return [.. result.OrderByDescending(i => i.LastAccessed)];
        }

    }
}
