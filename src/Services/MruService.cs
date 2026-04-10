using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
            // IVsMRUItemsStore must be accessed on the main thread
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var rawEntries = new List<string>();
            IVsMRUItemsStore store = await VS.GetServiceAsync<SVsMRUItemsStore, IVsMRUItemsStore>();

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

            // Use caller-provided options or load from settings
            if (options == null)
            {
                options = await Options.GetLiveInstanceAsync();
            }

            var pinnedPaths = new HashSet<string>(
                (options.PinnedItems ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            // Parse MRU entries on background thread (includes file I/O for timestamps)
            // Deduplicate so .sln and .slnx in the same directory collapse to one entry,
            // keeping the most recently accessed.
            var vsItems = await Task.Run(() =>
            {
                var itemsByKey = new Dictionary<string, MruItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in rawEntries)
                {
                    var item = ParseMruEntry(raw);
                    if (item == null)
                        continue;

                    var key = GetDeduplicationKey(item);
                    if (itemsByKey.TryGetValue(key, out var existing))
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
                return itemsByKey.Values.ToList();
            });

            // Apply pinned state from Options and build an ordered list for stable pin ordering
            var pinnedOrderList = (options.PinnedItems ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var pinnedOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pinnedOrderList.Length; i++)
            {
                pinnedOrder[pinnedOrderList[i]] = i;
            }

            foreach (var item in vsItems)
            {
                item.IsPinned = pinnedPaths.Contains(item.Path);
            }

            // Sort: pinned first (in saved order), then unpinned by last accessed
            var pinned = vsItems.Where(i => i.IsPinned)
                                .OrderBy(i => pinnedOrder.TryGetValue(i.Path, out int idx) ? idx : int.MaxValue)
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

                // Process items in parallel - each gets its own LibGit2Sharp Repository instance
                Parallel.ForEach(itemList, item =>
                {
                    try
                    {
                        var status = GitHelper.GetGitStatus(item.Path);

                        // Update properties (INotifyPropertyChanged handles UI marshaling)
                        item.GitBranch = status.BranchName;
                        item.CommitsAhead = status.CommitsAhead;
                        item.CommitsBehind = status.CommitsBehind;
                        item.HasUncommittedChanges = status.HasUncommittedChanges;
                        item.LastCommitTime = status.LastCommitTime;
                    }
                    catch (Exception ex)
                    {
                        ex.Log();
                    }
                });
            });
        }

        /// <summary>
        /// Populates Git branch names for items in the background (legacy compatibility method).
        /// </summary>
        [Obsolete("Use PopulateGitStatusAsync instead for full Git status information.")]
        public static Task PopulateGitBranchesAsync(IEnumerable<MruItem> items)
        {
            return PopulateGitStatusAsync(items).Task;
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

                // Skip items that no longer exist on disk
                if (!File.Exists(rawPath) && !Directory.Exists(rawPath))
                    return null;

                var displayName = parts[3];
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = Path.GetFileNameWithoutExtension(rawPath);
                }

                var item = new MruItem
                {
                    Path = rawPath,
                    Name = displayName,
                    Type = DetermineType(rawPath),
                    LastAccessed = GetLastAccessTime(rawPath),
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

            if (extension == ".sln")
                return MruItemType.Solution;

            if (extension == ".csproj" || extension == ".vbproj" || extension == ".fsproj" || extension == ".vcxproj")
                return MruItemType.Project;

            if (Directory.Exists(path) || string.IsNullOrEmpty(extension))
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
        private static DateTime GetLastAccessTime(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    // Open Folder scenario: check the .vs/ folder inside it
                    var vsDir = System.IO.Path.Combine(path, ".vs");
                    if (Directory.Exists(vsDir))
                        return Directory.GetLastWriteTime(vsDir);

                    return Directory.GetLastWriteTime(path);
                }

                if (File.Exists(path))
                {
                    // Solution/project file: look for .suo in the .vs/ folder
                    var suoTime = FindSuoLastWriteTime(path);
                    if (suoTime.HasValue)
                        return suoTime.Value;

                    return File.GetLastWriteTime(path);
                }
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
                            var writeTime = File.GetLastWriteTime(suoPath);
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
            if (item.Type == MruItemType.Folder)
            {
                return NormalizePath(item.Path);
            }

            try
            {
                var dir = Path.GetDirectoryName(item.Path);

                return string.IsNullOrEmpty(dir)
                    ? NormalizePath(item.Path)
                    : NormalizePath(dir);
            }
            catch
            {
                return NormalizePath(item.Path);
            }
        }

            }
        }
