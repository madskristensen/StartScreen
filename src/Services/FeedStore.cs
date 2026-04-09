using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StartScreen.Models;

namespace StartScreen.Services
{
    /// <summary>
    /// Manages the list of available feeds with JSON-based persistence.
    /// </summary>
    public static class FeedStore
    {
        private static readonly string LocalAppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StartScreen");

        private static readonly string FeedsFile = Path.Combine(LocalAppDataFolder, "feeds.json");

        private static readonly List<FeedInfo> DefaultFeeds = new()
        {
            new() { Name = "!Visual Studio", Url = "https://devblogs.microsoft.com/visualstudio/feed/", IsSelected = true, IsBuiltIn = true },
            new() { Name = "?.NET", Url = "https://devblogs.microsoft.com/dotnet/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Azure AI Foundry", Url = "https://devblogs.microsoft.com/foundry/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Azure Blog", Url = "https://azurecomcdn.azureedge.net/en-us/blog/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Azure DevOps", Url = "https://devblogs.microsoft.com/devops/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Azure SDKs", Url = "https://devblogs.microsoft.com/azure-sdk/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?C++", Url = "https://devblogs.microsoft.com/cppblog/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Command Line", Url = "https://devblogs.microsoft.com/commandline/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?CosmosDB", Url = "https://devblogs.microsoft.com/cosmosdb/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?DirectX", Url = "https://devblogs.microsoft.com/directx/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?IoT", Url = "https://devblogs.microsoft.com/iotdev/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Microsoft 365", Url = "https://devblogs.microsoft.com/microsoft365dev/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Microsoft Blog", Url = "https://blogs.microsoft.com/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Microsoft Education", Url = "https://www.microsoft.com/en-us/education/blog/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Microsoft News", Url = "https://news.microsoft.com/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?NuGet", Url = "https://devblogs.microsoft.com/nuget/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?PowerShell", Url = "https://devblogs.microsoft.com/powershell/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Python", Url = "https://devblogs.microsoft.com/python/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?SQL Server", Url = "https://cloudblogs.microsoft.com/sqlserver/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?TypeScript", Url = "https://devblogs.microsoft.com/typescript/feed/", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Visual Studio Code", Url = "https://code.visualstudio.com/feed.xml", IsSelected = false, IsBuiltIn = true },
            new() { Name = "?Windows Developer", Url = "https://blogs.windows.com/windowsdeveloper/feed/", IsSelected = false, IsBuiltIn = true }
        };

        /// <summary>
        /// Gets the list of feeds synchronously (fast, reads from cache).
        /// Seeds default feeds on first run.
        /// </summary>
        public static List<FeedInfo> GetFeeds()
        {
            try
            {
                if (!File.Exists(FeedsFile))
                {
                    // First run: seed default feeds
                    SeedDefaultFeeds();
                }

                var json = File.ReadAllText(FeedsFile);
                var feeds = JsonSerializer.Deserialize<List<FeedInfo>>(json) ?? new List<FeedInfo>();

                // Apply selection state from names (! = mandatory selected, ? = optional unselected by default)
                foreach (var feed in feeds.Where(f => f.IsBuiltIn))
                {
                    if (feed.Name.StartsWith("!"))
                        feed.IsSelected = true;
                    else if (feed.Name.StartsWith("?") && !feed.IsSelected)
                        feed.IsSelected = false;
                }

                return feeds;
            }
            catch
            {
                // On error, return defaults
                SeedDefaultFeeds();
                return new List<FeedInfo>(DefaultFeeds);
            }
        }

        /// <summary>
        /// Saves the feed list to disk asynchronously.
        /// </summary>
        public static async Task SaveFeedsAsync(IEnumerable<FeedInfo> feeds)
        {
            await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(LocalAppDataFolder);

                    var json = JsonSerializer.Serialize(feeds, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    File.WriteAllText(FeedsFile, json);
                }
                catch
                {
                    // Fail silently
                }
            });
        }

        /// <summary>
        /// Adds a custom feed and saves.
        /// </summary>
        public static async Task AddCustomFeedAsync(string name, string url)
        {
            var feeds = GetFeeds();

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
                IsSelected = true,
                IsBuiltIn = false
            });

            await SaveFeedsAsync(feeds);
        }

        /// <summary>
        /// Removes a feed by name and saves.
        /// </summary>
        public static async Task RemoveFeedAsync(string name)
        {
            var feeds = GetFeeds();
            feeds.RemoveAll(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            await SaveFeedsAsync(feeds);
        }

        /// <summary>
        /// Seeds the default feed list to disk on first run.
        /// </summary>
        private static void SeedDefaultFeeds()
        {
            try
            {
                Directory.CreateDirectory(LocalAppDataFolder);

                var json = JsonSerializer.Serialize(DefaultFeeds, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(FeedsFile, json);
            }
            catch
            {
                // Fail silently
            }
        }
    }
}
