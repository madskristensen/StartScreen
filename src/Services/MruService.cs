using Microsoft.VisualStudio.Shell.Interop;
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

        private static readonly string MruCacheFile = Path.Combine(LocalAppDataFolder, "mru.json");

        /// <summary>
        /// Gets cached MRU items asynchronously for display on window open.
        /// </summary>
        public static async Task<List<MruItem>> GetCachedMruItemsAsync()
        {
            try
            {
                if (!File.Exists(MruCacheFile))
                    return new List<MruItem>();

                string json;
                using (var stream = new FileStream(MruCacheFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                using (var reader = new StreamReader(stream))
                {
                    json = await reader.ReadToEndAsync();
                }

                var items = JsonSerializer.Deserialize<List<MruItem>>(json) ?? new List<MruItem>();

                // Sort: pinned first, then by last accessed
                return items.OrderByDescending(i => i.IsPinned)
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

            var vsItems = new List<MruItem>();
            IVsMRUItemsStore store = await VS.GetServiceAsync<SVsMRUItemsStore, IVsMRUItemsStore>();

            if (store != null)
            {
                var buffer = new string[MaxVsItems];
                Guid projectsGuid = MruList.Projects;
                var count = store.GetMRUItems(ref projectsGuid, string.Empty, MaxVsItems, buffer);

                for (uint i = 0; i < count; i++)
                {
                    MruItem item = ParseMruEntry(buffer[i]);
                    if (item != null)
                    {
                        vsItems.Add(item);
                    }
                }
            }

            // Merge: VS items are primary (correct order), cached items provide pinned state
            var pinnedPaths = new HashSet<string>(
                cachedItems.Where(c => c.IsPinned).Select(c => c.Path),
                StringComparer.OrdinalIgnoreCase);

            // Apply pinned state from cache to VS items
            foreach (var item in vsItems)
            {
                if (pinnedPaths.Contains(item.Path))
                {
                    item.IsPinned = true;
                }
            }

            // Sort: pinned first, then preserve MRU order from VS
            var mruItems = vsItems.OrderByDescending(i => i.IsPinned).ToList();

            // Save merged list back to cache
            await SyncAndSaveAsync(mruItems);

            return mruItems;
        }

        /// <summary>
        /// Populates Git branch names for items in the background.
        /// Updates each item's GitBranch property as it resolves, triggering UI updates via INotifyPropertyChanged.
        /// </summary>
        public static Task PopulateGitBranchesAsync(IEnumerable<MruItem> items)
        {
            return Task.Run(() =>
            {
                Parallel.ForEach(items, item =>
                {
                    try
                    {
                        item.GitBranch = GitHelper.GetCurrentBranch(item.Path);
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
        /// Saves the MRU list to the JSON cache file.
        /// </summary>
        public static async Task SyncAndSaveAsync(IEnumerable<MruItem> items)
        {
            await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(LocalAppDataFolder);

                    var json = JsonSerializer.Serialize(items, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    File.WriteAllText(MruCacheFile, json);
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            });
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
