using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.ToolWindows
{
    internal enum MruClearMode
    {
        All,
        AllButPinned,
        Missing
    }

    /// <summary>
    /// Main ViewModel for the Start Screen.
    /// </summary>
    public class StartScreenViewModel : INotifyPropertyChanged
    {
        private static readonly TimeSpan _autoRefreshInterval = TimeSpan.FromHours(4);
        private bool _isRefreshingNews;
        private bool _isNewsUnavailable;
        private bool _isRefreshingYouTube;
        private bool _isYouTubeUnavailable;
        private bool _isRefreshingDevHub;
        private bool _isUpdateAvailable;
        private string _searchFilter;
        private string _discoverySectionTitle;
        private string _discoverySectionVersion;
        private string _currentTip;
        private int _currentTipIndex;
        private SuggestedExtension _currentSuggestedExtension;
        private int _currentExtensionIndex;
        private ObservableCollection<MruItem> _allMruItems;
        private readonly BulkObservableCollection<MruItem> _visibleMruItems =
            new BulkObservableCollection<MruItem>();
        private readonly List<NewsPost> _allNewsPosts = new List<NewsPost>();
        private readonly List<YouTubeVideo> _allYouTubeVideos = new List<YouTubeVideo>();
        private int _newsColumnCount = 2;
        private int _youTubeColumnCount = 1;
        private Timer _autoRefreshTimer;
        private MruItem _selectedMruItem;
        private string _lastNewsRefreshText;
        private string _lastYouTubeRefreshText;
        private string _lastDevHubRefreshText;
        private bool _isRefreshingInBackground;
        private bool _hasNewBlogs;
        private bool _hasNewVideos;
        private readonly object _cancellationSync = new object();
        private CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();
        private CancellationTokenSource _newsRefreshCancellation;
        private CancellationTokenSource _youTubeRefreshCancellation;
        private CancellationTokenSource _mruMetadataCancellation;
        private bool _isDeferredContentInitializing;
        private bool _isDeferredContentInitialized;

        public ObservableCollection<MruItem> MruItems { get; private set; }
        public ObservableCollection<MruItem> PinnedItems { get; private set; }
        public ObservableCollection<MruTimeGroup> GroupedMruItems { get; private set; }
        public ObservableCollection<MruItem> VisibleMruItems => _visibleMruItems;
        public ObservableCollection<NewsPost> NewsPosts { get; private set; }
        public ObservableCollection<NewsPost> PinnedNewsPosts { get; private set; }
        public ObservableCollection<RecentTemplate> RecentTemplates { get; private set; }
        public ObservableCollection<YouTubeVideo> YouTubeVideos { get; private set; }

        public int NewsColumnCount
        {
            get => _newsColumnCount;
            private set
            {
                if (_newsColumnCount != value)
                {
                    _newsColumnCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public int YouTubeColumnCount
        {
            get => _youTubeColumnCount;
            private set
            {
                if (_youTubeColumnCount != value)
                {
                    _youTubeColumnCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsRefreshingNews
        {
            get => _isRefreshingNews;
            set
            {
                if (_isRefreshingNews != value)
                {
                    _isRefreshingNews = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsRefreshingAny));
                    OnPropertyChanged(nameof(UnifiedRefreshStatus));
                }
            }
        }

        public bool IsNewsUnavailable
        {
            get => _isNewsUnavailable;
            set
            {
                if (_isNewsUnavailable != value)
                {
                    _isNewsUnavailable = value;
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
                    OnPropertyChanged(nameof(IsRefreshingAny));
                    OnPropertyChanged(nameof(UnifiedRefreshStatus));
                }
            }
        }

        public bool IsRefreshingDevHub
        {
            get => _isRefreshingDevHub;
            set
            {
                if (_isRefreshingDevHub != value)
                {
                    _isRefreshingDevHub = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsRefreshingAny));
                    OnPropertyChanged(nameof(UnifiedRefreshStatus));
                }
            }
        }

        public bool IsRefreshingAny => _isRefreshingNews || _isRefreshingYouTube || _isRefreshingDevHub;

        public bool IsYouTubeUnavailable
        {
            get => _isYouTubeUnavailable;
            set
            {
                if (_isYouTubeUnavailable != value)
                {
                    _isYouTubeUnavailable = value;
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
                    OnPropertyChanged(nameof(UnifiedRefreshStatus));
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
                    OnPropertyChanged(nameof(UnifiedRefreshStatus));
                }
            }
        }

        public string LastDevHubRefreshText
        {
            get => _lastDevHubRefreshText;
            set
            {
                if (_lastDevHubRefreshText != value)
                {
                    _lastDevHubRefreshText = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UnifiedRefreshStatus));
                }
            }
        }

        public string UnifiedRefreshStatus
        {
            get
            {
                var refreshing = new List<string>();
                if (IsRefreshingDevHub)
                    refreshing.Add("Dev Hub");
                if (IsRefreshingNews)
                    refreshing.Add("news");
                if (IsRefreshingYouTube)
                    refreshing.Add("videos");

                return refreshing.Count > 0
                    ? $"Refreshing {string.Join(", ", refreshing)}..."
                    : LastDevHubRefreshText ?? string.Empty;
            }
        }

        /// <summary>
        /// True when at least one blog post arrived in the most recent refresh that is newer
        /// than the last time the user viewed the Blogs tab. Drives the green dot on the tab.
        /// </summary>
        public bool HasNewBlogs
        {
            get => _hasNewBlogs;
            private set
            {
                if (_hasNewBlogs != value)
                {
                    _hasNewBlogs = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// True when at least one YouTube video arrived in the most recent refresh that is newer
        /// than the last time the user viewed the Videos tab. Drives the green dot on the tab.
        /// </summary>
        public bool HasNewVideos
        {
            get => _hasNewVideos;
            private set
            {
                if (_hasNewVideos != value)
                {
                    _hasNewVideos = value;
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

            // Tip / suggested-extension initialization is deferred to LoadMruAsync.
            // The first call into TipProvider/SuggestedExtensionProvider triggers static
            // initializers that read embedded resources synchronously, and
            // InstalledExtensionChecker.IsInstalled hits the VS private registry hive.
            // Doing all of that on the UI thread inside the ctor is what trips the
            // "this extension delayed VS startup" InfoBar.

            FeedStore.FeedsChanged += OnFeedsChanged;
        }

        public void ActivateBackgroundWork()
        {
            lock (_cancellationSync)
            {
                if (_lifetimeCancellation.IsCancellationRequested)
                {
                    _lifetimeCancellation.Dispose();
                    _lifetimeCancellation = new CancellationTokenSource();
                }
            }
        }

        public void CancelBackgroundWork()
        {
            lock (_cancellationSync)
            {
                _lifetimeCancellation.Cancel();
                _newsRefreshCancellation?.Cancel();
                _youTubeRefreshCancellation?.Cancel();
                _mruMetadataCancellation?.Cancel();
            }

            IsRefreshingNews = false;
            IsRefreshingYouTube = false;
        }

        internal void EnsureDeferredContentInitialized()
        {
            IReadOnlyCollection<MruItem> mruItems;
            CancellationToken cancellationToken;

            lock (_cancellationSync)
            {
                if (_isDeferredContentInitialized || _isDeferredContentInitializing)
                    return;

                _isDeferredContentInitializing = true;
                mruItems = _allMruItems.ToList();
                cancellationToken = _lifetimeCancellation.Token;
            }

            StartScreenPackage.BackgroundTaskFactory.RunAsync(
                () => InitializeDeferredContentAsync(mruItems, cancellationToken))
                .FileAndForget(nameof(StartScreenViewModel));
        }

        internal StartScreenViewModel(IEnumerable<MruItem> mruItems)
            : this()
        {
            SetMruItems(mruItems);
        }

        internal void SetMruItems(IEnumerable<MruItem> mruItems)
        {
            _allMruItems = new ObservableCollection<MruItem>(
                mruItems ?? Enumerable.Empty<MruItem>());
            RefreshMruCollections();
        }

        private void OnFeedsChanged(object sender, EventArgs e)
        {
            // Force refresh news when feeds file changes
            ForceRefreshNews();
        }

        private CancellationToken GetBackgroundCancellationToken()
        {
            lock (_cancellationSync)
            {
                return _lifetimeCancellation.Token;
            }
        }

        private CancellationTokenSource ReplaceRefreshCancellation(
            ref CancellationTokenSource refreshCancellation)
        {
            lock (_cancellationSync)
            {
                refreshCancellation?.Cancel();
                refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeCancellation.Token);
                return refreshCancellation;
            }
        }

        private void CompleteRefresh(
            ref CancellationTokenSource currentCancellation,
            CancellationTokenSource completedCancellation,
            Action onCompleted)
        {
            var isCurrent = false;

            lock (_cancellationSync)
            {
                if (ReferenceEquals(currentCancellation, completedCancellation))
                {
                    currentCancellation = null;
                    isCurrent = true;
                }
            }

            completedCancellation.Dispose();

            if (isCurrent)
            {
                onCompleted();
            }
        }

        private void StartMruMetadataRefresh(IReadOnlyCollection<MruItem> items)
        {
            CancellationTokenSource refreshCancellation =
                ReplaceRefreshCancellation(ref _mruMetadataCancellation);

            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await Task.WhenAll(
                        MruService.PopulateGitStatusAsync(items, refreshCancellation.Token),
                        MruService.PopulateExistenceAsync(items, refreshCancellation.Token));
                }
                catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
                {
                }
                finally
                {
                    CompleteRefresh(ref _mruMetadataCancellation, refreshCancellation, () => { });
                }
            }).FileAndForget(nameof(StartScreenViewModel));
        }

        private async Task InitializeDeferredContentAsync(
            IReadOnlyCollection<MruItem> mruItems,
            CancellationToken cancellationToken)
        {
            try
            {
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
                cancellationToken.ThrowIfCancellationRequested();

                StartMruMetadataRefresh(mruItems);

                await TaskScheduler.Default;

                var tipIndex = DateTime.Now.DayOfYear % TipProvider.TipCount;
                var currentTip = TipProvider.GetTipOfTheDay();

                var extensionIndex = DateTime.Now.DayOfYear % SuggestedExtensionProvider.SuggestionCount;
                var suggestedExtension = SuggestedExtensionProvider.GetSuggestionOfTheDay();
                if (suggestedExtension != null)
                {
                    suggestedExtension.IsInstalled = InstalledExtensionChecker.IsInstalled(suggestedExtension.Id);
                }

                FeedStore.StartWatching();
                _autoRefreshTimer ??= new Timer(
                    OnAutoRefreshTimerTick,
                    null,
                    _autoRefreshInterval,
                    _autoRefreshInterval);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                _currentTipIndex = tipIndex;
                CurrentTip = currentTip;
                _currentExtensionIndex = extensionIndex;
                CurrentSuggestedExtension = suggestedExtension;

                lock (_cancellationSync)
                {
                    _isDeferredContentInitialized = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
            finally
            {
                lock (_cancellationSync)
                {
                    _isDeferredContentInitializing = false;
                }
            }
        }

        private void OnAutoRefreshTimerTick(object state)
        {
            // Timer fires on a ThreadPool thread. Check guards before dispatching work.
            if (FeedService.IsCacheStale() && !IsRefreshingNews)
            {
                ForceRefreshNews();
            }

            if (YouTubeService.IsCacheStale() && !IsRefreshingYouTube)
            {
                StartBackgroundYouTubeRefresh();
            }
        }

        /// <summary>
        /// Loads MRU data from VS's MRU store for initial display.
        /// MRU is loaded first so it appears in the UI as quickly as possible.
        /// News, YouTube, tips, and the extension suggestion are handled by
        /// RefreshInBackgroundAsync (called immediately after _loadTask completes),
        /// so there is no need to read those caches here.
        /// </summary>
        public async Task LoadMruAsync()
        {
            CancellationToken cancellationToken = GetBackgroundCancellationToken();

            // Get off the UI thread immediately. CreateAsync invokes us from the UI
            // thread; all work below must not run there.
            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();

            // 1) MRU FIRST. Only the bare minimum needed to fetch MRU runs before the
            //    UI is updated. Options is just a settings-store read and is needed
            //    for pinned-item state.
            Options options = await Options.GetLiveInstanceAsync();
            List<MruItem> mruItems = await MruService.GetMruItemsAsync(options);
            List<MruItem> extendedItems = await MruService.GetExtendedOnlyItemsAsync(mruItems, options);
            var allItems = mruItems.Concat(extendedItems).ToList();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            cancellationToken.ThrowIfCancellationRequested();
            SetMruItems(allItems);
            StartupDiagnostics.Mark("MRU displayed");

            EnsureDeferredContentInitialized();
        }

        /// <summary>
        /// Refreshes cached and remote content in the background after the UI is shown.
        /// Guarded against concurrent execution to prevent duplicate work.
        /// </summary>
        public async Task RefreshInBackgroundAsync(bool refreshMru = true)
        {
            if (_isRefreshingInBackground)
                return;

            CancellationToken cancellationToken = GetBackgroundCancellationToken();
            _isRefreshingInBackground = true;

            try
            {
                Task newsTask = RefreshNewsAsync(cancellationToken);
                Task youtubeTask = RefreshYouTubeAsync(cancellationToken);
                Task versionTask = RefreshVersionTitleAsync(cancellationToken);
                Task mruTask = refreshMru ? RefreshMruAsync(cancellationToken) : Task.CompletedTask;
                Task updateTask = CheckForUpdateAsync(cancellationToken);

                await Task.WhenAll(newsTask, youtubeTask, versionTask, mruTask, updateTask);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                _isRefreshingInBackground = false;
            }
        }

        private async Task RefreshMruAsync(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Options options = await Options.GetLiveInstanceAsync();
                List<MruItem> updatedMru = await MruService.GetMruItemsAsync(options);
                List<MruItem> extendedItems = await MruService.GetExtendedOnlyItemsAsync(updatedMru, options);
                var allItems = updatedMru.Concat(extendedItems).ToList();

                // Update on UI thread
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                cancellationToken.ThrowIfCancellationRequested();

                SetMruItems(allItems);

                StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
                {
                    await System.Windows.Threading.Dispatcher.Yield(
                        System.Windows.Threading.DispatcherPriority.ContextIdle);
                    cancellationToken.ThrowIfCancellationRequested();
                    StartMruMetadataRefresh(allItems);
                }).FileAndForget(nameof(StartScreenViewModel));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        private async Task RefreshNewsAsync(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Load from cache asynchronously to avoid blocking
                List<NewsPost> posts = await FeedService.GetCachedPostsAsync();
                var hasCachedItems = posts != null && posts.Count > 0;

                if (hasCachedItems)
                {
                    await UpdateNewsPostsOnUIThreadAsync(posts, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    LastNewsRefreshText = "Updated from cache";
                    StartupDiagnostics.Mark("news cache displayed");
                }

                // Trigger background sync if no cache, empty cache, or stale cache
                if (!hasCachedItems || FeedService.IsCacheStale())
                {
                    StartBackgroundFeedRefresh();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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
            CancellationTokenSource refreshCancellation = ReplaceRefreshCancellation(ref _newsRefreshCancellation);
            CancellationToken cancellationToken = refreshCancellation.Token;
            IsRefreshingNews = true;

            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                var minDisplay = Task.Delay(1000, cancellationToken);
                try
                {
                    List<FeedInfo> feeds = FeedStore.GetFeeds();
                    List<NewsPost> posts = await FeedService.DownloadFeedsAsync(feeds, cancellationToken);

                    if (posts != null)
                    {
                        await UpdateNewsPostsOnUIThreadAsync(posts, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        IsNewsUnavailable = false;
                        LastNewsRefreshText = "Updated just now";
                        StartupDiagnostics.Mark("news remote refresh completed");
                    }
                    else
                    {
                        IsNewsUnavailable = _allNewsPosts.Count == 0;
                        LastNewsRefreshText = _allNewsPosts.Count == 0
                            ? "Feed unavailable"
                            : "Showing cached content - refresh failed";
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    ex.Log();
                    IsNewsUnavailable = _allNewsPosts.Count == 0;
                    LastNewsRefreshText = _allNewsPosts.Count == 0
                        ? "Feed unavailable"
                        : "Showing cached content - refresh failed";
                }
                finally
                {
                    try
                    {
                        await minDisplay;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }

                    CompleteRefresh(ref _newsRefreshCancellation, refreshCancellation, () => IsRefreshingNews = false);
                }
            }).FileAndForget(nameof(StartScreenViewModel));
        }

        private async Task UpdateNewsPostsOnUIThreadAsync(
            List<NewsPost> posts,
            CancellationToken cancellationToken)
        {
            Options options = await Options.GetLiveInstanceAsync();

            // Update on UI thread
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            cancellationToken.ThrowIfCancellationRequested();

            _allNewsPosts.Clear();
            _allNewsPosts.AddRange(posts);
            ApplyPinnedStateToNews(options);
            UpdateNewsCollections();
            UpdateHasNewBlogs(options);
        }

        private async Task RefreshYouTubeAsync(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var videos = await YouTubeService.GetCachedVideosAsync();
                var hasCached = videos != null && videos.Count > 0;

                if (hasCached)
                {
                    await YouTubeService.LoadThumbnailsAsync(videos, cancellationToken);
                    await UpdateYouTubeOnUIThreadAsync(videos, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    LastYouTubeRefreshText = "Updated from cache";
                    StartupDiagnostics.Mark("video cache displayed");
                }

                if (!hasCached || YouTubeService.IsCacheStale())
                {
                    StartBackgroundYouTubeRefresh();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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
            CancellationTokenSource refreshCancellation = ReplaceRefreshCancellation(ref _youTubeRefreshCancellation);
            CancellationToken cancellationToken = refreshCancellation.Token;
            IsRefreshingYouTube = true;

            StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
            {
                var minDisplay = Task.Delay(1000, cancellationToken);
                try
                {
                    var videos = await YouTubeService.DownloadVideosAsync(cancellationToken);

                    if (videos != null)
                    {
                        await YouTubeService.LoadThumbnailsAsync(videos, cancellationToken);
                        await UpdateYouTubeOnUIThreadAsync(videos, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        IsYouTubeUnavailable = false;
                        LastYouTubeRefreshText = "Updated just now";
                        StartupDiagnostics.Mark("video remote refresh completed");
                    }
                    else
                    {
                        IsYouTubeUnavailable = _allYouTubeVideos.Count == 0;
                        LastYouTubeRefreshText = _allYouTubeVideos.Count == 0
                            ? "Feed unavailable"
                            : "Showing cached content - refresh failed";
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    ex.Log();
                    IsYouTubeUnavailable = _allYouTubeVideos.Count == 0;
                    LastYouTubeRefreshText = _allYouTubeVideos.Count == 0
                        ? "Feed unavailable"
                        : "Showing cached content - refresh failed";
                }
                finally
                {
                    try
                    {
                        await minDisplay;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }

                    CompleteRefresh(ref _youTubeRefreshCancellation, refreshCancellation, () => IsRefreshingYouTube = false);
                }
            }).FileAndForget(nameof(StartScreenViewModel));
        }

        private async Task UpdateYouTubeOnUIThreadAsync(
            List<YouTubeVideo> videos,
            CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            cancellationToken.ThrowIfCancellationRequested();
            _allYouTubeVideos.Clear();
            _allYouTubeVideos.AddRange(videos);
            UpdateYouTubeCollections();
            Options options = await Options.GetLiveInstanceAsync();
            UpdateHasNewVideos(options);
        }

        /// <summary>
        /// Recomputes HasNewBlogs based on the current posts and the persisted last-seen timestamp.
        /// First-ever load (LastSeen == MinValue) is suppressed so the user is not greeted with
        /// the indicator on first open.
        /// </summary>
        private void UpdateHasNewBlogs(Options options)
        {
            DateTime lastSeen = options.LastNewsBlogsSeen;
            if (lastSeen == DateTime.MinValue || _allNewsPosts.Count == 0)
            {
                HasNewBlogs = false;
                return;
            }

            HasNewBlogs = _allNewsPosts.Any(p => p.PublishDate > lastSeen);
        }

        /// <summary>
        /// Recomputes HasNewVideos based on the current videos and the persisted last-seen timestamp.
        /// </summary>
        private void UpdateHasNewVideos(Options options)
        {
            DateTime lastSeen = options.LastNewsVideosSeen;
            if (lastSeen == DateTime.MinValue || _allYouTubeVideos.Count == 0)
            {
                HasNewVideos = false;
                return;
            }

            HasNewVideos = _allYouTubeVideos.Any(v => v.PublishDate > lastSeen);
        }

        /// <summary>
        /// Marks the Blogs tab as seen, persisting the timestamp and clearing the indicator.
        /// </summary>
        public async Task MarkBlogsAsSeenAsync()
        {
            HasNewBlogs = false;
            Options options = await Options.GetLiveInstanceAsync();
            options.LastNewsBlogsSeen = DateTime.UtcNow;
            await options.SaveAsync();
        }

        /// <summary>
        /// Marks the Videos tab as seen, persisting the timestamp and clearing the indicator.
        /// </summary>
        public async Task MarkVideosAsSeenAsync()
        {
            HasNewVideos = false;
            Options options = await Options.GetLiveInstanceAsync();
            options.LastNewsVideosSeen = DateTime.UtcNow;
            await options.SaveAsync();
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
        /// Limits visible unpinned items to 10 per column.
        /// </summary>
        private void UpdateNewsCollections()
        {
            // Bulk-replace to fire a single CollectionChanged.Reset per collection
            PinnedNewsPosts = new ObservableCollection<NewsPost>(_allNewsPosts.Where(p => p.IsPinned));
            OnPropertyChanged(nameof(PinnedNewsPosts));

            NewsPosts = new ObservableCollection<NewsPost>(
                _allNewsPosts.Where(p => !p.IsPinned).Take(NewsColumnCount * 10));
            OnPropertyChanged(nameof(NewsPosts));
        }

        /// <summary>
        /// Rebuilds the visible YouTube collection from the master list.
        /// Updates items in-place to avoid a brief empty-collection state that causes layout shifts.
        /// </summary>
        private void UpdateYouTubeCollections()
        {
            var newVideos = _allYouTubeVideos.Take(YouTubeColumnCount * 5).ToList();

            // Remove trailing items if the new list is shorter
            for (var i = YouTubeVideos.Count - 1; i >= newVideos.Count; i--)
                YouTubeVideos.RemoveAt(i);

            // Update existing slots and append new ones
            for (var i = 0; i < newVideos.Count; i++)
            {
                if (i < YouTubeVideos.Count)
                    YouTubeVideos[i] = newVideos[i];
                else
                    YouTubeVideos.Add(newVideos[i]);
            }
        }

        /// <summary>
        /// Updates the number of news and YouTube columns based on available width.
        /// Called from the view when the right pane resizes.
        /// </summary>
        public void UpdateColumnCounts(int newsColumns, int youTubeColumns)
        {
            bool newsChanged = _newsColumnCount != newsColumns;
            bool ytChanged = _youTubeColumnCount != youTubeColumns;

            NewsColumnCount = newsColumns;
            YouTubeColumnCount = youTubeColumns;

            if (newsChanged)
            {
                UpdateNewsCollections();
            }

            if (ytChanged)
            {
                UpdateYouTubeCollections();
            }
        }

        private async Task RefreshVersionTitleAsync(CancellationToken cancellationToken)
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
                }, cancellationToken);

                // Update on UI thread
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                cancellationToken.ThrowIfCancellationRequested();
                DiscoverySectionTitle = title;
                DiscoverySectionVersion = version;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        /// <summary>
        /// Checks whether a Visual Studio update is available via the setup composition service.
        /// </summary>
        private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                cancellationToken.ThrowIfCancellationRequested();

                IVsSetupCompositionService setupService = await VS.GetServiceAsync<SVsSetupCompositionService, IVsSetupCompositionService>();
                if (setupService != null && setupService.IsManifestRefreshedAndUpdateAvailable)
                {
                    await Task.Delay(2000, cancellationToken);
                    IsUpdateAvailable = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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
        /// Removes an MRU item from VS's MRU store, the extended history list, and the
        /// in-memory collection.
        /// </summary>
        public async Task RemoveMruItemAsync(MruItem item)
        {
            if (item == null)
                return;

            Options options = await Options.GetLiveInstanceAsync();
            await MruService.RemoveItemsAsync([item], options);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _allMruItems.Remove(item);
            if (SelectedMruItem == item)
            {
                SelectedMruItem = null;
            }

            RefreshMruCollections();
        }

        internal bool CanClearMruItems(MruClearMode mode)
        {
            return _allMruItems.Any(item => ShouldClearMruItem(item, mode));
        }

        internal async Task ClearMruItemsAsync(MruClearMode mode)
        {
            List<MruItem> items = _allMruItems
                .Where(item => ShouldClearMruItem(item, mode))
                .ToList();

            if (items.Count == 0)
                return;

            await MruService.RemoveItemsAsync(items);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var removedKeys = new HashSet<string>(
                items.Select(item => MruService.GetDeduplicationKeyFromPath(item.Path, item.Type)),
                StringComparer.OrdinalIgnoreCase);
            _allMruItems = new ObservableCollection<MruItem>(
                _allMruItems.Where(item => !removedKeys.Contains(
                    MruService.GetDeduplicationKeyFromPath(item.Path, item.Type))));

            if (SelectedMruItem != null && removedKeys.Contains(
                MruService.GetDeduplicationKeyFromPath(SelectedMruItem.Path, SelectedMruItem.Type)))
            {
                SelectedMruItem = null;
            }

            RefreshMruCollections();
        }

        internal static bool ShouldClearMruItem(MruItem item, MruClearMode mode)
        {
            if (item == null)
                return false;

            switch (mode)
            {
                case MruClearMode.All:
                    return true;
                case MruClearMode.AllButPinned:
                    return !item.IsPinned;
                case MruClearMode.Missing:
                    return !item.Exists;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        private void RefreshMruCollections()
        {
            if (string.IsNullOrWhiteSpace(_searchFilter))
            {
                UpdateMruCollections();
            }
            else
            {
                FilterMru(_searchFilter);
            }

            OnPropertyChanged(nameof(HasNoSearchResults));
        }

        /// <summary>
        /// Filters the MRU list by search query.
        /// VS MRU matches appear in their normal time groups; extended-only matches
        /// appear below in the "Earlier" group.
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

            var unpinnedVs = filtered.Where(i => !i.IsPinned && !i.IsFromExtendedList);
            var unpinnedExtended = filtered
                .Where(i => !i.IsPinned && i.IsFromExtendedList)
                .OrderByDescending(i => i.LastAccessed)
                .ToList();

            var groups = BuildTimeGroups(unpinnedVs);
            if (unpinnedExtended.Count > 0)
            {
                var earlierGroup = new MruTimeGroup { GroupName = "Earlier" };
                foreach (var item in unpinnedExtended)
                    earlierGroup.Items.Add(item);
                groups.Add(earlierGroup);
            }

            GroupedMruItems = groups;
            OnPropertyChanged(nameof(GroupedMruItems));
            UpdateVisibleMruItems(PinnedItems.Concat(groups.SelectMany(group => group.Items)));
        }

        /// <summary>
        /// Updates the pinned and regular MRU collections with time grouping.
        /// VS MRU items appear in their normal time groups; extended-only items appear
        /// below them in a dedicated "Earlier" group.
        /// </summary>
        private void UpdateMruCollections()
        {
            var pinned = _allMruItems.Where(i => i.IsPinned).ToList();
            var unpinnedVs = _allMruItems
                .Where(i => !i.IsPinned && !i.IsFromExtendedList)
                .OrderByDescending(i => i.LastAccessed)
                .ToList();
            var unpinnedExtended = _allMruItems
                .Where(i => !i.IsPinned && i.IsFromExtendedList)
                .OrderByDescending(i => i.LastAccessed)
                .ToList();

            // Bulk-replace to fire a single CollectionChanged.Reset per collection
            PinnedItems = new ObservableCollection<MruItem>(pinned);
            OnPropertyChanged(nameof(PinnedItems));

            MruItems = new ObservableCollection<MruItem>(unpinnedVs);
            OnPropertyChanged(nameof(MruItems));

            var groups = BuildTimeGroups(unpinnedVs);
            if (unpinnedExtended.Count > 0)
            {
                var earlierGroup = new MruTimeGroup { GroupName = "Earlier" };
                foreach (var item in unpinnedExtended)
                    earlierGroup.Items.Add(item);
                groups.Add(earlierGroup);
            }

            GroupedMruItems = groups;
            OnPropertyChanged(nameof(GroupedMruItems));
            UpdateVisibleMruItems(pinned.Concat(unpinnedVs).Concat(unpinnedExtended));
        }

        private void UpdateVisibleMruItems(IEnumerable<MruItem> items)
        {
            _visibleMruItems.ReplaceAll(items);
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

        private sealed class BulkObservableCollection<T> : ObservableCollection<T>
        {
            public void ReplaceAll(IEnumerable<T> items)
            {
                Items.Clear();

                foreach (T item in items)
                {
                    Items.Add(item);
                }

                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                    System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
            }
        }
    }
}
