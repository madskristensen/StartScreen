using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using StartScreen.Models;

namespace StartScreen.Services
{
    /// <summary>
    /// Manages the list of available feeds with JSON-based persistence.
    /// Reads feeds from %USERPROFILE%\.vs\StartScreen\newsfeeds.json if it exists,
    /// otherwise uses the embedded default feeds.
    /// </summary>
    public static class FeedStore
    {
        private static readonly string _userProfileFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vs", "StartScreen");

        private static readonly string _newsFeedsFile = Path.Combine(_userProfileFolder, "newsfeeds.json");

        private static FileSystemWatcher _watcher;

        /// <summary>
        /// Event raised when the newsfeeds.json file changes.
        /// </summary>
        public static event EventHandler FeedsChanged;

        /// <summary>
        /// Starts watching the newsfeeds.json file for changes.
        /// </summary>
        public static void StartWatching()
        {
            if (_watcher != null)
            {
                return;
            }

            try
            {
                // Ensure the folder exists
                Directory.CreateDirectory(_userProfileFolder);
                _watcher = new FileSystemWatcher(_userProfileFolder, "newsfeeds.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };

                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        /// <summary>
        /// Stops watching the newsfeeds.json file.
        /// </summary>
        public static void StopWatching()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileChanged;
                _watcher.Created -= OnFileChanged;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        private static Timer _debounceTimer;
        private static readonly object _debounceLock = new object();

        private static void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Timer-based debounce - file save can trigger multiple events
            lock (_debounceLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(_ => FeedsChanged?.Invoke(null, EventArgs.Empty), null, 200, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Gets the list of feeds synchronously.
        /// Creates the file from embedded resource if it doesn't exist.
        /// </summary>
        public static List<FeedInfo> GetFeeds()
        {
            try
            {
                // Always ensure the file exists
                if (!File.Exists(_newsFeedsFile))
                {
                    CopyEmbeddedResourceToUserProfile();
                }

                // Read from the file
                if (File.Exists(_newsFeedsFile))
                {
                    return ReadFeeds(_newsFeedsFile);
                }

                return GetHardcodedDefaultFeeds();
            }
            catch (Exception ex)
            {
                ex.Log();
                return GetHardcodedDefaultFeeds();
            }
        }

        internal static List<FeedInfo> ReadFeeds(string filePath)
        {
            var json = File.ReadAllText(filePath);
            NewsFeedsWrapper wrapper = JsonSerializer.Deserialize<NewsFeedsWrapper>(json);
            return wrapper?.Feeds ?? GetHardcodedDefaultFeeds();
        }

        /// <summary>
        /// Ensures the newsfeeds.json file exists in the user profile folder
        /// and returns the path. Creates the file from embedded resource if needed.
        /// </summary>
        public static string EnsureNewsFeedsFileAndGetPath()
        {
            try
            {
                if (!File.Exists(_newsFeedsFile))
                {
                    CopyEmbeddedResourceToUserProfile();
                }

                return _newsFeedsFile;
            }
            catch (Exception ex)
            {
                ex.Log();
                return null;
            }
        }

        /// <summary>
        /// Saves the feed list to disk asynchronously.
        /// </summary>
        public static async Task SaveFeedsAsync(IEnumerable<FeedInfo> feeds)
        {
            await SaveFeedsAsync(feeds, _newsFeedsFile);
        }

        internal static async Task SaveFeedsAsync(IEnumerable<FeedInfo> feeds, string filePath)
        {
            await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));

                    var wrapper = new NewsFeedsWrapper
                    {
                        Schema = "https://raw.githubusercontent.com/madskristensen/StartScreen/master/newsfeeds.schema.json",
                        Feeds = feeds.ToList()
                    };

                    var json = JsonSerializer.Serialize(wrapper, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    File.WriteAllText(filePath, json);
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            });
        }

        /// <summary>
        /// Adds a custom feed and saves.
        /// </summary>
        public static async Task AddCustomFeedAsync(string name, string url)
        {
            await AddCustomFeedAsync(name, url, _newsFeedsFile);
        }

        internal static async Task AddCustomFeedAsync(string name, string url, string filePath)
        {
            List<FeedInfo> feeds = File.Exists(filePath)
                ? ReadFeeds(filePath)
                : GetHardcodedDefaultFeeds();

            // Check for duplicates
            if (feeds.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            feeds.Add(new FeedInfo
            {
                Name = name,
                Url = url,
                Enabled = true
            });

            await SaveFeedsAsync(feeds, filePath);
        }

        /// <summary>
        /// Removes a feed by name and saves.
        /// </summary>
        public static async Task RemoveFeedAsync(string name)
        {
            await RemoveFeedAsync(name, _newsFeedsFile);
        }

        internal static async Task RemoveFeedAsync(string name, string filePath)
        {
            List<FeedInfo> feeds = File.Exists(filePath)
                ? ReadFeeds(filePath)
                : GetHardcodedDefaultFeeds();
            feeds.RemoveAll(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            await SaveFeedsAsync(feeds, filePath);
        }

        /// <summary>
        /// Copies the embedded newsfeeds.json to the user profile folder.
        /// Falls back to hardcoded defaults if embedded resource is not found.
        /// </summary>
        private static void CopyEmbeddedResourceToUserProfile()
        {
            try
            {
                Directory.CreateDirectory(_userProfileFolder);

                var assembly = Assembly.GetExecutingAssembly();

                // Try to find the resource
                var resourceNames = assembly.GetManifestResourceNames();
                var resourceName = resourceNames.FirstOrDefault(n => n.EndsWith("newsfeeds.json", StringComparison.OrdinalIgnoreCase));

                if (resourceName != null)
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream != null)
                        {
                            using (var reader = new StreamReader(stream))
                            {
                                var content = reader.ReadToEnd();
                                File.WriteAllText(_newsFeedsFile, content);
                                return;
                            }
                        }
                    }
                }

                // Fallback: write hardcoded defaults
                WriteHardcodedDefaultsToFile();
            }
            catch (Exception ex)
            {
                ex.Log();

                // Try to write hardcoded defaults
                try
                {
                    WriteHardcodedDefaultsToFile();
                }
                catch (Exception ex2)
                {
                    ex2.Log();
                }
            }
        }

        private static void WriteHardcodedDefaultsToFile()
        {
            var wrapper = new NewsFeedsWrapper
            {
                Schema = "https://raw.githubusercontent.com/madskristensen/StartScreen/master/newsfeeds.schema.json",
                Feeds = GetHardcodedDefaultFeeds()
            };

            var json = JsonSerializer.Serialize(wrapper, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_newsFeedsFile, json);
        }

        /// <summary>
        /// Gets hardcoded default feeds as ultimate fallback.
        /// </summary>
        private static List<FeedInfo> GetHardcodedDefaultFeeds()
        {
            return new List<FeedInfo>
            {
                new FeedInfo { Name = "Visual Studio", Url = "https://devblogs.microsoft.com/visualstudio/feed/", Enabled = true },
                new FeedInfo { Name = ".NET", Url = "https://devblogs.microsoft.com/dotnet/feed/", Enabled = false },
                new FeedInfo { Name = "Azure DevOps", Url = "https://devblogs.microsoft.com/devops/feed/", Enabled = false },
                new FeedInfo { Name = "C++", Url = "https://devblogs.microsoft.com/cppblog/feed/", Enabled = false },
                new FeedInfo { Name = "TypeScript", Url = "https://devblogs.microsoft.com/typescript/feed/", Enabled = false },
                new FeedInfo { Name = "Visual Studio Code", Url = "https://code.visualstudio.com/feed.xml", Enabled = false }
            };
        }

        /// <summary>
        /// Wrapper class for JSON deserialization with schema support.
        /// </summary>
        private class NewsFeedsWrapper
        {
            [System.Text.Json.Serialization.JsonPropertyName("$schema")]
            public string Schema { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("feeds")]
            public List<FeedInfo> Feeds { get; set; }
        }
    }
}
