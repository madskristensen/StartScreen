using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using static Microsoft.VisualStudio.VSConstants;

namespace StartScreen.Services
{
    /// <summary>
    /// Manages the Most Recently Used (MRU) list by reading from VS's IVsMRUItemsStore
    /// and merging with a private JSON cache for pinning support.
    /// </summary>
    public static class MruService
    {
        private const uint MaxVsItems = 50;

        private static readonly string LocalAppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartScreen");

        private static string _mruCacheFile;

        /// <summary>
        /// Gets the MRU cache file path, which varies by VS version and root suffix.
        /// </summary>
        /// <remarks>
        /// The cache file is named {MajorVersion}{RootSuffix}-mru.json, e.g.:
        /// - 17-mru.json (VS 2022 regular)
        /// - 17Exp-mru.json (VS 2022 experimental)
        /// - 18-mru.json (VS 2025/2026 regular)
        /// - 18Exp-mru.json (VS 2025/2026 experimental)
        /// </remarks>
        private static async Task<string> GetMruCacheFileAsync()
        {
            if (_mruCacheFile != null)
            {
                return _mruCacheFile;
            }

            int majorVersion = 17; // Fallback to VS 2022
            string suffix = "";

            try
            {
                // Ensure we're on the main thread for VS services
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                Version version = await VS.Shell.GetVsVersionAsync();
                if (version != null)
                {
                    majorVersion = version.Major;
                }

                PackageUtilities.IsExperimentalVersionOfVsForVsipDevelopment(out string rootSuffix);
                suffix = string.IsNullOrEmpty(rootSuffix) ? "" : rootSuffix;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }

            _mruCacheFile = Path.Combine(LocalAppDataFolder, $"{majorVersion}{suffix}-mru.json");
            return _mruCacheFile;
        }

        /// <summary>
        /// Gets cached MRU items asynchronously for display on window open.
        /// </summary>
        public static async Task<List<MruItem>> GetCachedMruItemsAsync()
        {
            try
            {
                string mruCacheFile = await GetMruCacheFileAsync();

                // Switch to background thread for file I/O to avoid blocking the UI thread
                await TaskScheduler.Default;

                if (!File.Exists(mruCacheFile))
                    return new List<MruItem>();

                string json;
                using (var stream = new FileStream(mruCacheFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                using (var reader = new StreamReader(stream))
                {
                    json = await reader.ReadToEndAsync();
                }

                var items = JsonSerializer.Deserialize<List<MruItem>>(json) ?? new List<MruItem>();

                // Deduplicate by solution directory so .sln and .slnx in the same
                // folder collapse to one entry, keeping the most recently accessed.
                var deduped = items
                    .GroupBy(i => GetDeduplicationKey(i), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(i => i.LastAccessed).First())
                    .ToList();

                // Sort: pinned first, then by last accessed
                return deduped.OrderByDescending(i => i.IsPinned)
                              .ThenByDescending(i => i.LastAccessed)
                              .ToList();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return new List<MruItem>();
            }
        }

        /// <summary>
        /// Reads VS's MRU via IVsMRUItemsStore and merges with cached items asynchronously.
        /// </summary>
        public static async Task<List<MruItem>> GetMruItemsAsync()
        {
            // Read cached items (preserves pinned state) on background thread
            var cachedItems = await GetCachedMruItemsAsync();

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
                            itemsByKey[key] = item;
                        }
                    }
                    else
                    {
                        itemsByKey[key] = item;
                    }
                }
                return itemsByKey.Values.ToList();
            });

            // Merge: VS items are primary (correct order), cached items provide pinned state
            var pinnedKeys = new HashSet<string>(
                cachedItems.Where(c => c.IsPinned).Select(c => GetDeduplicationKey(c)),
                StringComparer.OrdinalIgnoreCase);

            // Apply pinned state from cache to VS items
            foreach (var item in vsItems)
            {
                if (pinnedKeys.Contains(GetDeduplicationKey(item)))
                {
                    item.IsPinned = true;
                }
            }

            // Sort: pinned first, then by last accessed (date and time)
            var mruItems = vsItems.OrderByDescending(i => i.IsPinned)
                                  .ThenByDescending(i => i.LastAccessed)
                                  .ToList();

            // Save merged list back to cache
            await SyncAndSaveAsync(mruItems);

            return mruItems;
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

                return new MruItem
                {
                    Path = rawPath,
                    Name = displayName,
                    Type = DetermineType(rawPath),
                    LastAccessed = GetLastAccessTime(rawPath),
                    IsPinned = false
                };
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

                // Search for .suo files recursively under .vs/
                DateTime? latest = null;
                foreach (var suo in Directory.EnumerateFiles(vsDir, ".suo", SearchOption.AllDirectories))
                {
                    var writeTime = File.GetLastWriteTime(suo);
                    if (!latest.HasValue || writeTime > latest.Value)
                    {
                        latest = writeTime;
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

        /// <summary>
        /// Saves the MRU list to the JSON cache file.
        /// </summary>
        public static async Task SyncAndSaveAsync(IEnumerable<MruItem> items)
        {
            try
            {
                string mruCacheFile = await GetMruCacheFileAsync();

                await Task.Run(() =>
                {
                    Directory.CreateDirectory(LocalAppDataFolder);

                    var json = JsonSerializer.Serialize(items, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    File.WriteAllText(mruCacheFile, json);
                });
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        /// <summary>
        /// Removes an item from the MRU cache.
        /// </summary>
        public static async Task RemoveItemAsync(string path)
        {
            var items = await GetCachedMruItemsAsync();
            items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            await SyncAndSaveAsync(items);
        }
    }
}
