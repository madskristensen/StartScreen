using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.ToolWindows
{
    /// <summary>
    /// Main ViewModel for the Start Screen.
    /// </summary>
    public class StartScreenViewModel : INotifyPropertyChanged
    {
        private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromHours(4);
        private bool _isRefreshingNews;
        private bool _isRefreshingYouTube;
        private bool _isUpdateAvailable;
        private string _searchFilter;
        private string _discoverySectionTitle;
        private string _discoverySectionVersion;
        private string _currentTip;
        private int _currentTipIndex;
        private SuggestedExtension _currentSuggestedExtension;
        private int _currentExtensionIndex;
        private ObservableCollection<MruItem> _allMruItems;
        private readonly List<NewsPost> _allNewsPosts = new List<NewsPost>();
        private Timer _autoRefreshTimer;
        private Timer _filterDebounceTimer;
        private MruItem _selectedMruItem;
        private string _lastNewsRefreshText;
        private string _lastYouTubeRefreshText;

        public ObservableCollection<MruItem> MruItems { get; private set; }
        public ObservableCollection<MruItem> PinnedItems { get; private set; }
        public ObservableCollection<MruTimeGroup> GroupedMruItems { get; private set; }
        public ObservableCollection<NewsPost> NewsPosts { get; private set; }
        public ObservableCollection<NewsPost> PinnedNewsPosts { get; private set; }
        public ObservableCollection<RecentTemplate> RecentTemplates { get; private set; }
        public ObservableCollection<YouTubeVideo> YouTubeVideos { get; private set; }

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

        public bool IsRefreshingYouTube
        {
            get => _isRefreshingYouTube;
            set
            {
                if (_isRefreshingYouTube != value)
                {
                    _isRefreshingYouTube = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LastNewsRefreshText
        {
            get => _lastNewsRefreshText;
            set
            {
                if (_lastNewsRefreshText != value)
                {
                    _lastNewsRefreshText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LastYouTubeRefreshText
        {
            get => _lastYouTubeRefreshText;
            set
            {
                if (_lastYouTubeRefreshText != value)
                {
                    _lastYouTubeRefreshText = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// The currently selected MRU item (single-click). Used to filter Dev Hub to this repo.
        /// Null means no selection (shows global dashboard).
        /// </summary>
        public MruItem SelectedMruItem
        {
            get => _selectedMruItem;
            set
            {
                if (_selectedMruItem != value)
                {
                    // Deselect previous
                    if (_selectedMruItem != null)
                    {
                        _selectedMruItem.IsSelected = false;
                    }

                    _selectedMruItem = value;

                    // Select new
                    if (_selectedMruItem != null)
                    {
                        _selectedMruItem.IsSelected = true;
                    }

                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSelectedMruItem));
                }
            }
        }

        /// <summary>
        /// Whether an MRU item is currently selected.
        /// </summary>
        public bool HasSelectedMruItem => _selectedMruItem != null;

        /// <summary>
        /// True when a Visual Studio update is available.
        /// </summary>
        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            set
            {
                if (_isUpdateAvailable != value)
                {
                    _isUpdateAvailable = value;
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
                    DebounceFilterMru(value);
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

        /// <summary>
        /// The current suggested extension to display.
        /// </summary>
        public SuggestedExtension CurrentSuggestedExtension
        {
            get => _currentSuggestedExtension;
            set
            {
                if (_currentSuggestedExtension != value)
                {
                    _currentSuggestedExtension = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Advances to the next tip.
        /// </summary>
        public void NextTip()
        {
            _currentTipIndex++;
            CurrentTip = TipProvider.GetTipAt(_currentTipIndex);
        }

        /// <summary>
        /// Goes back to the previous tip.
        /// </summary>
        public void PreviousTip()
        {
            _currentTipIndex--;
            CurrentTip = TipProvider.GetTipAt(_currentTipIndex);
        }

        /// <summary>
        /// Advances to the next suggested extension.
        /// </summary>
        public void NextExtension()
        {
            _currentExtensionIndex++;
            var extension = SuggestedExtensionProvider.GetSuggestionAt(_currentExtensionIndex);
            if (extension != null)
            {
                extension.IsInstalled = InstalledExtensionChecker.IsInstalled(extension.Id);
                CurrentSuggestedExtension = extension;
            }
        }

        /// <summary>
        /// Goes back to the previous suggested extension.
        /// </summary>
        public void PreviousExtension()
        {
            _currentExtensionIndex--;
            var extension = SuggestedExtensionProvider.GetSuggestionAt(_currentExtensionIndex);
            if (extension != null)
            {
                extension.IsInstalled = InstalledExtensionChecker.IsInstalled(extension.Id);
                CurrentSuggestedExtension = extension;
            }
        }

        public StartScreenViewModel()
        {
            MruItems = new ObservableCollection<MruItem>();
            PinnedItems = new ObservableCollection<MruItem>();
            GroupedMruItems = new ObservableCollection<MruTimeGroup>();
            NewsPosts = new ObservableCollection<NewsPost>();
            PinnedNewsPosts = new ObservableCollection<NewsPost>();
            RecentTemplates = new ObservableCollection<RecentTemplate>();
            YouTubeVideos = new ObservableCollection<YouTubeVideo>();

            _allMruItems = new ObservableCollection<MruItem>();
            _discoverySectionTitle = "Discover what's new";
            _discoverySectionVersion = string.Empty;
            _currentTipIndex = DateTime.Now.DayOfYear % TipProvider.TipCount;
            _currentTip = TipProvider.GetTipOfTheDay();

            // Initialize suggested extension
            _currentExtensionIndex = DateTime.Now.DayOfYear % SuggestedExtensionProvider.SuggestionCount;
            var suggestedExtension = SuggestedExtensionProvider.GetSuggestionOfTheDay();
            if (suggestedExtension != null)
            {
                suggestedExtension.IsInstalled = InstalledExtensionChecker.IsInstalled(suggestedExtension.Id);
                _currentSuggestedExtension = suggestedExtension;
            }

            FeedStore.FeedsChanged += OnFeedsChanged;
        }

        private void OnFeedsChanged(object sender, EventArgs e)
        {
            // Force refresh news when feeds file changes
            ForceRefreshNews();
        }

        private void OnAutoRefreshTimerTick(object state)
        {
            if (FeedService.IsCacheStale())
            {
                ForceRefreshNews();
            }

            if (YouTubeService.IsCacheStale())
            {
                StartBackgroundYouTubeRefresh();
            }
        }

        /// <summary>
        /// Loads MRU data from VS's MRU store and news from cache for initial display.
        /// </summary>
        public async Task LoadMruAsync()
        {
            // Deferred from constructor to avoid blocking tool window creation
            FeedStore.StartWatching();
            _autoRefreshTimer = new Timer(OnAutoRefreshTimerTick, null, AutoRefreshInterval, AutoRefreshInterval);

            // Load options once for both MRU pinned state and news pinned state
            Options options = await Options.GetLiveInstanceAsync();

            // Start feed task first (pure file I/O, no main thread needed)
            System.Threading.Tasks.Task<List<NewsPost>> feedTask = FeedService.GetCachedPostsAsync();
            System.Threading.Tasks.Task<List<YouTubeVideo>> youtubeTask = YouTubeService.GetCachedVideosAsync();

            // MRU needs the main thread for IVsMRUItemsStore
            List<MruItem> mruItems = await MruService.GetMruItemsAsync(options);

            // Show MRU immediately without waiting for the feed task
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _allMruItems = new ObservableCollection<MruItem>(mruItems);
            UpdateMruCollections();

            // Populate git status and file existence in background after UI is updated
            MruService.PopulateGitStatusAsync(mruItems).FileAndForget(nameof(StartScreenViewModel));
            MruService.PopulateExistenceAsync(mruItems).FileAndForget(nameof(StartScreenViewModel));

            // Now await the feed task and update news when ready
            List<NewsPost> cachedPosts = await feedTask;

            if (cachedPosts != null && cachedPosts.Count > 0)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _allNewsPosts.Clear();
                _allNewsPosts.AddRange(cachedPosts);
                ApplyPinnedStateToNews(options);
                UpdateNewsCollections();
            }

            // Await YouTube cache and update
            List<YouTubeVideo> cachedVideos = await youtubeTask;

            if (cachedVideos != null && cachedVideos.Count > 0)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                YouTubeVideos = new ObservableCollection<YouTubeVideo>(cachedVideos);
                OnPropertyChanged(nameof(YouTubeVideos));
            }
        }

        /// <summary>
        /// Refreshes news and version info in the background after the UI is shown.
        /// </summary>
        public async Task RefreshInBackgroundAsync()
        {
            Task newsTask = RefreshNewsAsync();
            Task youtubeTask = RefreshYouTubeAsync();
            Task versionTask = RefreshVersionTitleAsync();
            Task mruTask = RefreshMruAsync();
            Task updateTask = CheckForUpdateAsync();

            await Task.WhenAll(newsTask, youtubeTask, versionTask, mruTask, updateTask);
        }

        private async Task RefreshMruAsync()
        {
            try
            {
                List<MruItem> updatedMru = await MruService.GetMruItemsAsync();

                // Update on UI thread
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _allMruItems = new ObservableCollection<MruItem>(updatedMru);
                UpdateMruCollections();

                // Populate git status and file existence in background after UI is updated
                MruService.PopulateGitStatusAsync(updatedMru).FileAndForget(nameof(StartScreenViewModel));
                MruService.PopulateExistenceAsync(updatedMru).FileAndForget(nameof(StartScreenViewModel));
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
                List<NewsPost> posts = await FeedService.GetCachedPostsAsync();
                var hasCachedItems = posts != null && posts.Count > 0;

                if (hasCachedItems)
                {
                    await UpdateNewsPostsOnUIThreadAsync(posts);
                    LastNewsRefreshText = "Updated from cache";
                }

                // Trigger background sync if no cache, empty cache, or stale cache
                if (!hasCachedItems || FeedService.IsCacheStale())
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
                    List<FeedInfo> feeds = FeedStore.GetFeeds();
                    List<NewsPost> posts = await FeedService.DownloadFeedsAsync(feeds);

                    if (posts != null)
                    {
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
                    LastNewsRefreshText = "Updated just now";
                }
            }).FileAndForget(nameof(StartScreenViewModel));
        }

        private async Task UpdateNewsPostsOnUIThreadAsync(List<NewsPost> posts)
        {
            Options options = await Options.GetLiveInstanceAsync();

            // Update on UI thread
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _allNewsPosts.Clear();
            _allNewsPosts.AddRange(posts);
            ApplyPinnedStateToNews(options);
            UpdateNewsCollections();
        }

        private async Task RefreshYouTubeAsync()
        {
            try
            {
                var videos = await YouTubeService.GetCachedVideosAsync();
                var hasCached = videos != null && videos.Count > 0;

                if (hasCached)
                {
                    await UpdateYouTubeOnUIThreadAsync(videos);
                    LastYouTubeRefreshText = "Updated from cache";
                }

                if (!hasCached || YouTubeService.IsCacheStale())
                {
                    StartBackgroundYouTubeRefresh();
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        /// <summary>
        /// Forces a refresh of the YouTube feed regardless of cache state.
        /// </summary>
        public void ForceRefreshYouTube()
        {
            if (IsRefreshingYouTube)
                return;

            StartBackgroundYouTubeRefresh();
        }

        /// <summary>
        /// Starts a fire-and-forget background refresh of the YouTube feed.
        /// </summary>
        private void StartBackgroundYouTubeRefresh()
        {
            IsRefreshingYouTube = true;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var videos = await YouTubeService.DownloadVideosAsync();

                    if (videos != null)
                    {
                        await UpdateYouTubeOnUIThreadAsync(videos);
                    }
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
                finally
                {
                    IsRefreshingYouTube = false;
                    LastYouTubeRefreshText = "Updated just now";
                }
            }).FileAndForget(nameof(StartScreenViewModel));
        }

        private async Task UpdateYouTubeOnUIThreadAsync(List<YouTubeVideo> videos)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            YouTubeVideos = new ObservableCollection<YouTubeVideo>(videos);
            OnPropertyChanged(nameof(YouTubeVideos));
        }

        /// <summary>
        /// Toggles the pinned state of a news article.
        /// </summary>
        public async Task ToggleNewsPinAsync(NewsPost post)
        {
            if (post == null)
                return;

            post.IsPinned = !post.IsPinned;

            Options options = await Options.GetLiveInstanceAsync();
            IEnumerable<string> pinnedUrls = _allNewsPosts.Where(p => p.IsPinned).Select(p => p.Url);
            options.PinnedArticles = string.Join(";", pinnedUrls);
            await options.SaveAsync();

            UpdateNewsCollections();
        }

        /// <summary>
        /// Applies the persisted pinned state to the current news posts.
        /// </summary>
        private void ApplyPinnedStateToNews(Options options)
        {
            var pinnedUrls = new HashSet<string>(
                (options.PinnedArticles ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            foreach (NewsPost post in _allNewsPosts)
            {
                post.IsPinned = pinnedUrls.Contains(post.Url);
            }
        }

        /// <summary>
        /// Rebuilds the pinned and unpinned news collections from the master list.
        /// </summary>
        private void UpdateNewsCollections()
        {
            // Bulk-replace to fire a single CollectionChanged.Reset per collection
            PinnedNewsPosts = new ObservableCollection<NewsPost>(_allNewsPosts.Where(p => p.IsPinned));
            OnPropertyChanged(nameof(PinnedNewsPosts));

            NewsPosts = new ObservableCollection<NewsPost>(_allNewsPosts.Where(p => !p.IsPinned));
            OnPropertyChanged(nameof(NewsPosts));
        }

        private async Task RefreshVersionTitleAsync()
        {
            try
            {
                // Perform file I/O on background thread to avoid blocking UI
                (var title, var version) = await Task.Run(() =>
                {
                    var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "devenv.exe");
                    var resultTitle = "Visual Studio"; // fallback
                    var resultVersion = string.Empty;

                    if (File.Exists(path))
                    {
                        var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                        var description = versionInfo.FileDescription ?? "Microsoft Visual Studio";
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
        /// Checks whether a Visual Studio update is available via the setup composition service.
        /// </summary>
        private async Task CheckForUpdateAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                IVsSetupCompositionService setupService = await VS.GetServiceAsync<SVsSetupCompositionService, IVsSetupCompositionService>();
                if (setupService != null)
                {
                    IsUpdateAvailable = setupService.IsManifestRefreshedAndUpdateAvailable;
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        /// <summary>
        /// Triggers the Visual Studio update flow.
        /// </summary>
        public async Task UpdateVisualStudioAsync()
        {
            try
            {
                //await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                //var setupService = await VS.GetServiceAsync<SVsSetupCompositionService, IVsSetupCompositionService3>();
                //setupService?.UpdateVisualStudioInstance();
                await VS.Commands.ExecuteAsync("Help.Help.CheckForUpdates");
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        /// <summary>
        /// Moves a pinned item to a new position within the pinned list and persists the order.
        /// </summary>
        public async Task MovePinnedItemAsync(MruItem item, int newIndex)
        {
            if (item == null || !item.IsPinned)
                return;

            // Build the current pinned list in order
            var pinned = _allMruItems.Where(i => i.IsPinned).ToList();
            var oldIndex = pinned.IndexOf(item);
            if (oldIndex < 0 || oldIndex == newIndex)
                return;

            // Clamp
            if (newIndex < 0) newIndex = 0;
            if (newIndex >= pinned.Count) newIndex = pinned.Count - 1;

            // Reorder in _allMruItems: remove and reinsert at the correct position
            _allMruItems.Remove(item);

            // Find the target position in _allMruItems relative to other pinned items
            var pinnedInAll = _allMruItems.Where(i => i.IsPinned).ToList();
            if (newIndex >= pinnedInAll.Count)
            {
                // Insert after the last pinned item
                var lastPinnedIdx = _allMruItems.IndexOf(pinnedInAll.Last());
                _allMruItems.Insert(lastPinnedIdx + 1, item);
            }
            else
            {
                var targetIdx = _allMruItems.IndexOf(pinnedInAll[newIndex]);
                _allMruItems.Insert(targetIdx, item);
            }

            // Persist new order
            Options options = await Options.GetLiveInstanceAsync();
            IEnumerable<string> pinnedPaths = _allMruItems.Where(i => i.IsPinned).Select(i => i.Path);
            options.PinnedItems = string.Join(";", pinnedPaths);
            await options.SaveAsync();

            UpdateMruCollections();
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
            Options options = await Options.GetLiveInstanceAsync();
            IEnumerable<string> pinnedPaths = _allMruItems.Where(i => i.IsPinned).Select(i => i.Path);
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
        /// Debounces the search filter to avoid rebuilding collections on every keystroke.
        /// </summary>
        private void DebounceFilterMru(string query)
        {
            _filterDebounceTimer?.Dispose();
            _filterDebounceTimer = new Timer(_ =>
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    FilterMru(query);
                    OnPropertyChanged(nameof(HasNoSearchResults));
                }).FileAndForget(nameof(StartScreenViewModel));
            }, null, 200, Timeout.Infinite);
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

            // Bulk-replace to fire a single CollectionChanged.Reset per collection
            PinnedItems = new ObservableCollection<MruItem>(filtered.Where(i => i.IsPinned));
            OnPropertyChanged(nameof(PinnedItems));

            MruItems = new ObservableCollection<MruItem>();
            OnPropertyChanged(nameof(MruItems));

            GroupedMruItems = BuildTimeGroups(filtered.Where(i => !i.IsPinned));
            OnPropertyChanged(nameof(GroupedMruItems));
        }

        /// <summary>
        /// Updates the pinned and regular MRU collections with time grouping.
        /// </summary>
        private void UpdateMruCollections()
        {
            var pinned = _allMruItems.Where(i => i.IsPinned).ToList();
            var unpinned = _allMruItems.Where(i => !i.IsPinned)
                                       .OrderByDescending(i => i.LastAccessed)
                                       .ToList();

            // Bulk-replace to fire a single CollectionChanged.Reset per collection
            PinnedItems = new ObservableCollection<MruItem>(pinned);
            OnPropertyChanged(nameof(PinnedItems));

            MruItems = new ObservableCollection<MruItem>(unpinned);
            OnPropertyChanged(nameof(MruItems));

            GroupedMruItems = BuildTimeGroups(unpinned);
            OnPropertyChanged(nameof(GroupedMruItems));
        }

        /// <summary>
        /// Builds time-grouped collections from the given items.
        /// Groups are ordered: Today, Yesterday, This week, This month, Older.
        /// </summary>
        private static ObservableCollection<MruTimeGroup> BuildTimeGroups(IEnumerable<MruItem> items)
        {
            var groupOrder = new[] { "Today", "Yesterday", "This week", "This month", "Older" };

            IOrderedEnumerable<IGrouping<string, MruItem>> groups = items
                .GroupBy(i => i.TimeGroup)
                .OrderBy(g => Array.IndexOf(groupOrder, g.Key));

            var result = new ObservableCollection<MruTimeGroup>();
            foreach (IGrouping<string, MruItem> group in groups)
            {
                var timeGroup = new MruTimeGroup { GroupName = group.Key };
                foreach (MruItem item in group)
                {
                    timeGroup.Items.Add(item);
                }
                result.Add(timeGroup);
            }
            return result;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
