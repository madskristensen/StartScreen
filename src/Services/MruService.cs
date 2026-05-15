using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;
using static Microsoft.VisualStudio.VSConstants;

namespace StartScreen.Services
{
    /// <summary>
    /// Manages the Most Recently Used (MRU) list by reading from VS's IVsMRUItemsStore.
    /// Pinned state is stored in VS Options.
    /// </summary>
    internal static class MruService
    {
        private const uint MaxVsItems = 50;

        /// <summary>
        /// Reads VS's MRU via IVsMRUItemsStore and applies pinned state from Options.
        /// </summary>
        public static async Task<List<MruItem>> GetMruItemsAsync(Options options = null)
        {
            // Acquire the service first - this is free-threaded and does not require
            // the UI thread. Doing it before the SwitchToMainThreadAsync below means
            // the UI-thread region is as small as possible.
            IVsMRUItemsStore store = await VS.GetServiceAsync<SVsMRUItemsStore, IVsMRUItemsStore>();

            // Use caller-provided options or load from settings (also free-threaded).
            if (options == null)
            {
                options = await Options.GetLiveInstanceAsync();
            }

            // Only the COM call itself requires the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var rawEntries = new List<string>();
            if (store != null)
            {
                var buffer = new string[MaxVsItems];
                Guid projectsGuid = MruList.Projects;
                var count = store.GetMRUItems(ref projectsGuid, string.Empty, MaxVsItems, buffer);

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
            Parallel.For(0, rawEntries.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
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

            List<MruItem> vsItems = itemsByKey.Values.ToList();

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

        /// <summary>
        /// Removes an item from VS's MRU store via IVsMRUItemsStore.DeleteMRUItem.
        /// </summary>
        public static async Task RemoveItemAsync(MruItem item)
        {
            if (item == null)
                return;

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                IVsMRUItemsStore store = await VS.GetServiceAsync<SVsMRUItemsStore, IVsMRUItemsStore>();
                if (store == null)
                    return;

                Guid projectsGuid = MruList.Projects;
                foreach (var rawEntry in item.RawMruEntries)
                {
                    store.DeleteMRUItem(ref projectsGuid, rawEntry);
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        /// <summary>
        /// Populates Git status information for items in the background.
        /// Phase 1: Reads local status (branch, stash, dirty, operation) immediately.
        /// Phase 2: Fetches from remotes and updates ahead/behind counts as they complete.
        /// Updates each item's Git properties as they resolve, triggering UI updates via INotifyPropertyChanged.
        /// Uses JoinableTaskFactory to avoid blocking and ensure proper thread handling.
        /// </summary>
        public static JoinableTask PopulateGitStatusAsync(IEnumerable<MruItem> items)
        {
            return ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                // Switch to background thread for parallel Git operations
                await TaskScheduler.Default;

                var itemList = items.ToList();

                // Track item-to-repo mapping for phase 2, including upstream
                // coordinates so we can run a targeted fetch instead of fetch --all.
                var itemRepoMap = new System.Collections.Concurrent.ConcurrentDictionary<string, RepoFetchInfo>(StringComparer.OrdinalIgnoreCase);

                // Phase 1: Read local status (fast, no network)
                Parallel.ForEach(itemList, new ParallelOptions { MaxDegreeOfParallelism = 4 }, item =>
                {
                    try
                    {
                        GitStatus status = GitHelper.GetGitStatus(item.Path);

                        // Update properties (INotifyPropertyChanged handles UI marshaling)
                        item.ApplyGitStatus(status);

                        // Track repo path for phase 2 (if item is in a git repo
                        // and the current branch has an upstream we can fetch).
                        if (status.IsGitRepository
                            && !string.IsNullOrEmpty(status.UpstreamRemoteName)
                            && !string.IsNullOrEmpty(status.UpstreamBranchRef))
                        {
                            var startDir = Directory.Exists(item.Path) ? item.Path : Path.GetDirectoryName(item.Path);
                            var repoPath = GitHelper.FindRepositoryPath(startDir);
                            if (!string.IsNullOrEmpty(repoPath))
                            {
                                itemRepoMap.AddOrUpdate(
                                    repoPath,
                                    _ => new RepoFetchInfo(status.UpstreamRemoteName, status.UpstreamBranchRef, item),
                                    (_, info) => { lock (info.Items) { info.Items.Add(item); } return info; });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ex.Log();
                    }
                });

                // Phase 2: Fetch and update ahead/behind (slow, network).
                // Run on a pure thread-pool task outside JoinableTaskFactory so that VS's
                // hang-detection does not attribute these blocking git-fetch calls (up to
                // 5 s each) to the extension and show the "stopped responding" bar.
                if (itemRepoMap.Count > 0)
                {
                    _ = Task.Run(() =>
                    {
                        Parallel.ForEach(itemRepoMap, new ParallelOptions { MaxDegreeOfParallelism = 2 }, kvp =>
                        {
                            var repoPath = kvp.Key;
                            RepoFetchInfo info = kvp.Value;

                            try
                            {
                                // Targeted fetch of just the upstream ref of the current
                                // branch (best-effort, silent on failure).
                                GitHelper.FetchUpstream(repoPath, info.Remote, info.UpstreamRef);

                                // Re-read ahead/behind for all items in this repo
                                (var ahead, var behind) = GitHelper.GetUpdatedAheadBehind(repoPath);

                                foreach (MruItem item in info.Items)
                                {
                                    // Only update if fetch succeeded and tracking branch exists
                                    if (ahead.HasValue || behind.HasValue)
                                    {
                                        item.CommitsAhead = ahead;
                                        item.CommitsBehind = behind;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                ex.Log();
                            }
                        });
                    });
                }
            });
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
        /// Checks file/folder existence for each MRU item on a background thread.
        /// Updates each item's Exists property, triggering UI updates via INotifyPropertyChanged.
        /// </summary>
        public static JoinableTask PopulateExistenceAsync(IEnumerable<MruItem> items)
        {
            return ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;

                var itemList = items.ToList();

                Parallel.ForEach(itemList, new ParallelOptions { MaxDegreeOfParallelism = 4 }, item =>
                {
                    try
                    {
                        item.RefreshExists();
                    }
                    catch (Exception ex)
                    {
                        ex.Log();
                    }
                });
            });
        }

        /// <summary>
        /// Parses a single MRU entry from IVsMRUItemsStore into an MruItem.
        /// </summary>
        /// <remarks>
        /// Format: path|{guid}|bool|displayName|...|{guid}
        /// The first segment is the path (may contain environment variables).
        /// The fourth segment is the display name.
        /// </remarks>
        private static MruItem ParseMruEntry(string raw)
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
        private static MruItemType DetermineType(string path)
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
        private static DateTime? FindSuoLastWriteTime(string filePath)
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
            IEnumerable<MruItem> vsItems, Options options)
        {
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

        /// <summary>
        /// Removes an item from the persisted extended MRU list.
        /// Safe to call for items that are not in the extended list (no-op).
        /// </summary>
        internal static async Task RemoveFromExtendedAsync(MruItem item, Options options = null)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
                return;

            try
            {
                if (options == null)
                    options = await Options.GetLiveInstanceAsync();

                var entries = LoadExtendedEntries(options);
                if (entries.Count == 0)
                    return;

                var key = GetDeduplicationKeyFromPath(item.Path, item.Type);
                var before = entries.Count;
                entries.RemoveAll(e =>
                    GetDeduplicationKeyFromPath(e.Path, (MruItemType)e.Type)
                        .Equals(key, StringComparison.OrdinalIgnoreCase));

                if (entries.Count != before)
                {
                    options.ExtendedMruItems = JsonSerializer.Serialize(entries, _jsonOptions);
                    await options.SaveAsync();
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }
    }
}
