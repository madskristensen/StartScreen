using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.ToolWindows
{
    /// <summary>
    /// Main ViewModel for the Start Screen.
    /// </summary>
    public class StartScreenViewModel : INotifyPropertyChanged
    {
        private bool _isRefreshingNews;
        private string _searchFilter;
        private string _discoverySectionTitle;
        private string _discoverySectionVersion;
        private string _currentTip;
        private ObservableCollection<MruItem> _allMruItems;
        private readonly List<NewsPost> _allNewsPosts = new List<NewsPost>();
        private readonly ITipProvider _tipProvider;

        public ObservableCollection<MruItem> MruItems { get; private set; }
        public ObservableCollection<MruItem> PinnedItems { get; private set; }
        public ObservableCollection<MruTimeGroup> GroupedMruItems { get; private set; }
        public ObservableCollection<NewsPost> NewsPosts { get; private set; }
        public ObservableCollection<NewsPost> PinnedNewsPosts { get; private set; }
        public ObservableCollection<RecentTemplate> RecentTemplates { get; private set; }

        public bool IsRefreshingNews
        {
            get => _isRefreshingNews;
            set
            {
                if (_isRefreshingNews != value)
                {
                    _isRefreshingNews = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DiscoverySectionVersion
        {
            get => _discoverySectionVersion;
            set
            {
                if (_discoverySectionVersion != value)
                {
                    _discoverySectionVersion = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SearchFilter
        {
            get => _searchFilter;
            set
            {
                if (_searchFilter != value)
                {
                    _searchFilter = value;
                    OnPropertyChanged();
                    FilterMru(value);
                    OnPropertyChanged(nameof(HasNoSearchResults));
                }
            }
        }

        /// <summary>
        /// True when a search filter is active but produced no matching results.
        /// </summary>
        public bool HasNoSearchResults =>
            !string.IsNullOrWhiteSpace(_searchFilter) &&
            PinnedItems.Count == 0 &&
            GroupedMruItems.Count == 0;

        public string DiscoverySectionTitle
        {
            get => _discoverySectionTitle;
            set
            {
                if (_discoverySectionTitle != value)
                {
                    _discoverySectionTitle = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// The current tip of the day to display.
        /// </summary>
        public string CurrentTip
        {
            get => _currentTip;
            set
            {
                if (_currentTip != value)
                {
                    _currentTip = value;
                    OnPropertyChanged();
                }
            }
        }

        public StartScreenViewModel()
            : this(new HardCodedTipProvider())
        {
        }

        public StartScreenViewModel(ITipProvider tipProvider)
        {
            _tipProvider = tipProvider;

            MruItems = new ObservableCollection<MruItem>();
            PinnedItems = new ObservableCollection<MruItem>();
            GroupedMruItems = new ObservableCollection<MruTimeGroup>();
            NewsPosts = new ObservableCollection<NewsPost>();
            PinnedNewsPosts = new ObservableCollection<NewsPost>();
            RecentTemplates = new ObservableCollection<RecentTemplate>();

            _allMruItems = new ObservableCollection<MruItem>();
            _discoverySectionTitle = "Discover what's new";
            _discoverySectionVersion = string.Empty;
            _currentTip = _tipProvider.GetTipOfTheDay();

            // Start watching for feed file changes and subscribe to event
            FeedStore.StartWatching();
            FeedStore.FeedsChanged += OnFeedsChanged;
        }

        private void OnFeedsChanged(object sender, EventArgs e)
        {
            // Force refresh news when feeds file changes
            ForceRefreshNews();
        }

        /// <summary>
        /// Loads MRU data from VS's MRU store and news from cache for initial display.
        /// </summary>
        public async Task LoadMruAsync()
        {
            // Load MRU and news in parallel
            var mruTask = MruService.GetMruItemsAsync();
            var feedTask = FeedService.GetCachedFeedAsync();

            await Task.WhenAll(mruTask, feedTask);

            var mruItems = await mruTask;
            var cachedFeed = await feedTask;

            // Switch to UI thread to update collections
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _allMruItems = new ObservableCollection<MruItem>(mruItems);
            UpdateMruCollections();

            if (cachedFeed != null)
            {
                var posts = FeedService.ConvertToNewsPosts(cachedFeed);
                _allNewsPosts.Clear();
                _allNewsPosts.AddRange(posts);
                await ApplyPinnedStateToNewsAsync();
                UpdateNewsCollections();
            }

            // Populate git status in background after UI is updated
            MruService.PopulateGitStatusAsync(mruItems).FileAndForget(nameof(StartScreenViewModel));
        }

        /// <summary>
        /// Refreshes news and version info in the background after the UI is shown.
        /// </summary>
        public async Task RefreshInBackgroundAsync()
        {
            var mruTask = RefreshMruAsync();
            var newsTask = RefreshNewsAsync();
            var versionTask = RefreshVersionTitleAsync();

            await Task.WhenAll(mruTask, newsTask, versionTask);
        }

        private async Task RefreshMruAsync()
        {
            try
            {
                var updatedMru = await MruService.GetMruItemsAsync();

                // Update on UI thread
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _allMruItems.Clear();
                foreach (var item in updatedMru)
                {
                    _allMruItems.Add(item);
                }

                UpdateMruCollections();

                // Populate git status in background after UI is updated
                MruService.PopulateGitStatusAsync(updatedMru).FileAndForget(nameof(StartScreenViewModel));
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        private async Task RefreshNewsAsync()
        {
            try
            {
                // Load from cache asynchronously to avoid blocking
                var cachedFeed = await FeedService.GetCachedFeedAsync();
                var posts = cachedFeed != null ? FeedService.ConvertToNewsPosts(cachedFeed) : null;
                var hasCachedItems = posts != null && posts.Count > 0;

                if (hasCachedItems)
                {
                    await UpdateNewsPostsOnUIThreadAsync(posts);
                }

                // Only trigger background sync if no cache or empty cache
                if (!hasCachedItems)
                {
                    StartBackgroundFeedRefresh();
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        /// <summary>
        /// Forces a refresh of news feeds regardless of cache state.
        /// </summary>
        public void ForceRefreshNews()
        {
            if (IsRefreshingNews)
                return;

            StartBackgroundFeedRefresh();
        }

        /// <summary>
        /// Starts a fire-and-forget background refresh of news feeds.
        /// </summary>
        private void StartBackgroundFeedRefresh()
        {
            IsRefreshingNews = true;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var feeds = FeedStore.GetFeeds();
                    var updatedFeed = await FeedService.DownloadFeedsAsync(feeds);

                    if (updatedFeed != null)
                    {
                        var posts = FeedService.ConvertToNewsPosts(updatedFeed);
                        await UpdateNewsPostsOnUIThreadAsync(posts);
                    }
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
                finally
                {
                    IsRefreshingNews = false;
                }
            }).FileAndForget(nameof(StartScreenViewModel));
        }

        private async Task UpdateNewsPostsOnUIThreadAsync(List<NewsPost> posts)
        {
            // Update on UI thread
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _allNewsPosts.Clear();
            _allNewsPosts.AddRange(posts);
            await ApplyPinnedStateToNewsAsync();
            UpdateNewsCollections();
        }

        /// <summary>
        /// Toggles the pinned state of a news article.
        /// </summary>
        public async Task ToggleNewsPinAsync(NewsPost post)
        {
            if (post == null)
                return;

            post.IsPinned = !post.IsPinned;

            var options = await Options.GetLiveInstanceAsync();
            var pinnedUrls = _allNewsPosts.Where(p => p.IsPinned).Select(p => p.Url);
            options.PinnedArticles = string.Join(";", pinnedUrls);
            await options.SaveAsync();

            UpdateNewsCollections();
        }

        /// <summary>
        /// Applies the persisted pinned state to the current news posts.
        /// </summary>
        private async Task ApplyPinnedStateToNewsAsync()
        {
            var options = await Options.GetLiveInstanceAsync();
            var pinnedUrls = new HashSet<string>(
                (options.PinnedArticles ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            foreach (var post in _allNewsPosts)
            {
                post.IsPinned = pinnedUrls.Contains(post.Url);
            }
        }

        /// <summary>
        /// Rebuilds the pinned and unpinned news collections from the master list.
        /// </summary>
        private void UpdateNewsCollections()
        {
            PinnedNewsPosts.Clear();
            NewsPosts.Clear();

            foreach (var post in _allNewsPosts.Where(p => p.IsPinned))
            {
                PinnedNewsPosts.Add(post);
            }

            foreach (var post in _allNewsPosts.Where(p => !p.IsPinned))
            {
                NewsPosts.Add(post);
            }
        }

        private async Task RefreshVersionTitleAsync()
        {
            try
            {
                // Perform file I/O on background thread to avoid blocking UI
                var (title, version) = await Task.Run(() =>
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "devenv.exe");
                    string resultTitle = "Visual Studio"; // fallback
                    string resultVersion = string.Empty;

                    if (File.Exists(path))
                    {
                        var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                        string description = versionInfo.FileDescription ?? "Microsoft Visual Studio";
                        if (description.StartsWith("Microsoft "))
                        {
                            resultTitle = description.Substring("Microsoft ".Length);
                        }
                        else
                        {
                            resultTitle = description;
                        }

                        resultVersion = $"v{versionInfo.FileMajorPart}.{versionInfo.FileMinorPart}";
                    }

                    return (resultTitle, resultVersion);
                });

                // Update on UI thread
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                DiscoverySectionTitle = title;
                DiscoverySectionVersion = version;
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        /// <summary>
        /// Toggles the pinned state of an MRU item.
        /// </summary>
        public async Task TogglePinAsync(MruItem item)
        {
            if (item == null)
                return;

            item.IsPinned = !item.IsPinned;

            // Save pinned items to options
            var options = await Options.GetLiveInstanceAsync();
            var pinnedPaths = _allMruItems.Where(i => i.IsPinned).Select(i => i.Path);
            options.PinnedItems = string.Join(";", pinnedPaths);
            await options.SaveAsync();

            UpdateMruCollections();
        }

        /// <summary>
        /// Removes an MRU item from VS's MRU store and the in-memory list.
        /// </summary>
        public async Task RemoveMruItemAsync(MruItem item)
        {
            if (item == null)
                return;

            _allMruItems.Remove(item);
            await MruService.RemoveItemAsync(item);

            UpdateMruCollections();
        }

        /// <summary>
        /// Filters the MRU list by search query.
        /// </summary>
        public void FilterMru(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                UpdateMruCollections();
                return;
            }

            var filtered = _allMruItems.Where(item =>
                item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            PinnedItems.Clear();
            MruItems.Clear();
            GroupedMruItems.Clear();

            foreach (var item in filtered.Where(i => i.IsPinned))
            {
                PinnedItems.Add(item);
            }

            BuildTimeGroups(filtered.Where(i => !i.IsPinned));
        }

        /// <summary>
        /// Updates the pinned and regular MRU collections with time grouping.
        /// </summary>
        private void UpdateMruCollections()
        {
            MruItems.Clear();
            PinnedItems.Clear();
            GroupedMruItems.Clear();

            var sorted = _allMruItems.OrderByDescending(i => i.IsPinned)
                                    .ThenByDescending(i => i.LastAccessed)
                                    .ToList();

            foreach (var item in sorted.Where(i => i.IsPinned))
            {
                PinnedItems.Add(item);
            }

            var unpinned = sorted.Where(i => !i.IsPinned);
            foreach (var item in unpinned)
            {
                MruItems.Add(item);
            }

            BuildTimeGroups(unpinned);
        }

        /// <summary>
        /// Builds time-grouped collections from the given items.
        /// Groups are ordered: Today, Yesterday, This week, This month, Older.
        /// </summary>
        private void BuildTimeGroups(IEnumerable<MruItem> items)
        {
            // Define the desired group order
            var groupOrder = new[] { "Today", "Yesterday", "This week", "This month", "Older" };

            var groups = items
                .GroupBy(i => i.TimeGroup)
                .OrderBy(g => Array.IndexOf(groupOrder, g.Key))
                .ToList();

            foreach (var group in groups)
            {
                var timeGroup = new MruTimeGroup { GroupName = group.Key };
                foreach (var item in group)
                {
                    timeGroup.Items.Add(item);
                }
                GroupedMruItems.Add(timeGroup);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
