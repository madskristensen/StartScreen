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
    public partial class StartScreenViewModel : INotifyPropertyChanged, IDisposable
    {
        private static readonly TimeSpan _autoRefreshInterval = TimeSpan.FromHours(4);
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
        private ObservableCollection<MruItem> _mruItems;
        private ObservableCollection<MruItem> _pinnedItems;
        private ObservableCollection<MruTimeGroup> _groupedMruItems;
        private string[] _visibleMruGroups = [];
        private bool _isMruFiltered;
        private readonly BulkObservableCollection<MruItem> _visibleMruItems =
            new BulkObservableCollection<MruItem>();
        private Timer _autoRefreshTimer;
        private MruItem _selectedMruItem;
        private string _lastDevHubRefreshText;
        private bool _isRefreshingInBackground;
        private readonly object _cancellationSync = new object();
        private readonly CancellationToken _shutdownToken;
        private CancellationTokenSource _lifetimeCancellation;
        private CancellationTokenSource _mruMetadataCancellation;
        private bool _isDeferredContentInitializing;
        private bool _isDeferredContentInitialized;
        private bool _initialMruOptionsNeedSave;
        private bool _isDisposed;

        public ObservableCollection<MruItem> MruItems => _mruItems ??= new ObservableCollection<MruItem>(
            _isMruFiltered ? Enumerable.Empty<MruItem>() : VisibleMruItems.Where(item => !item.IsPinned && !item.IsFromExtendedList));
        public ObservableCollection<MruItem> PinnedItems => _pinnedItems ??= new ObservableCollection<MruItem>(
            VisibleMruItems.Where(item => item.IsPinned));
        public ObservableCollection<MruTimeGroup> GroupedMruItems => _groupedMruItems ??= BuildMruTimeGroups();
        public ObservableCollection<MruItem> VisibleMruItems => _visibleMruItems;
        internal NewsSectionViewModel NewsSection { get; }
        internal VideoSectionViewModel VideoSection { get; }
        public ObservableCollection<NewsPost> NewsPosts => NewsSection.NewsPosts;
        public ObservableCollection<NewsPost> PinnedNewsPosts => NewsSection.PinnedNewsPosts;
        public ObservableCollection<RecentTemplate> RecentTemplates { get; private set; }
        public ObservableCollection<YouTubeVideo> YouTubeVideos => VideoSection.YouTubeVideos;
        public int NewsColumnCount => NewsSection.NewsColumnCount;
        public int YouTubeColumnCount => VideoSection.YouTubeColumnCount;

        public bool IsRefreshingNews
        {
            get => NewsSection.IsRefreshingNews;
            set => NewsSection.IsRefreshingNews = value;
        }

        public bool IsNewsUnavailable
        {
            get => NewsSection.IsNewsUnavailable;
            set => NewsSection.IsNewsUnavailable = value;
        }

        public bool IsRefreshingYouTube
        {
            get => VideoSection.IsRefreshingYouTube;
            set => VideoSection.IsRefreshingYouTube = value;
        }

        public bool IsRefreshingDevHub
        {
            get => _isRefreshingDevHub;
            set
            {
                if (_isRefreshingDevHub != value)
                {
                    _isRefreshingDevHub = value;
                    DevHubRefreshState.IsLoading = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsRefreshingAny));
                    NotifyRefreshStatusChanged();
                }
            }
        }

        public bool IsRefreshingAny => IsRefreshingNews || IsRefreshingYouTube || _isRefreshingDevHub
            || _isRefreshingWorkspaceSignals || IsRefreshingFeedback || RefreshStates.Any(state => state.IsLoading);

        public bool IsYouTubeUnavailable
        {
            get => VideoSection.IsYouTubeUnavailable;
            set => VideoSection.IsYouTubeUnavailable = value;
        }

        public string LastNewsRefreshText
        {
            get => NewsSection.LastNewsRefreshText;
            set => NewsSection.LastNewsRefreshText = value;
        }

        public string LastYouTubeRefreshText
        {
            get => VideoSection.LastYouTubeRefreshText;
            set => VideoSection.LastYouTubeRefreshText = value;
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
                    NotifyRefreshStatusChanged();
                }
            }
        }

        public string UnifiedRefreshStatus
        {
            get => GetUnifiedRefreshStatus();
        }

        public string UnifiedRefreshDetails
        {
            get
            {
                var step = BackgroundProgress.CurrentStep;
                if (step != null)
                    return $"{step}\n{BackgroundProgress.Details}";
                var refreshing = new List<string>();
                if (IsRefreshingDevHub)
                    refreshing.Add("Dev Hub");
                if (IsRefreshingNews)
                    refreshing.Add("news");
                if (IsRefreshingYouTube)
                    refreshing.Add("videos");
                if (IsRefreshingFeedback)
                    refreshing.Add("feedback");
                if (_isRefreshingWorkspaceSignals || WorkspaceRefreshState.IsLoading)
                    refreshing.Add("workspace status");

                if (refreshing.Count > 0)
                    return $"Refreshing {string.Join(", ", refreshing)}...";

                var statuses = new List<string>();
                string devHubMessage = DevHubRefreshState.HasRefreshed ? DevHubRefreshState.Message : LastDevHubRefreshText;
                if (!string.IsNullOrWhiteSpace(devHubMessage))
                    statuses.Add($"Dev Hub: {devHubMessage}");
                if (!string.IsNullOrWhiteSpace(LastNewsRefreshText))
                    statuses.Add($"News: {LastNewsRefreshText}");
                if (!string.IsNullOrWhiteSpace(LastYouTubeRefreshText))
                    statuses.Add($"Videos: {LastYouTubeRefreshText}");
                if (_feedbackRequested)
                    statuses.Add($"Feedback: {FeedbackStatus ?? LastFeedbackRefreshText}");
                if (WorkspaceRefreshState.HasRefreshed)
                    statuses.Add($"Workspace: {WorkspaceRefreshState.Message}");
                return string.Join("; ", statuses);
            }
        }

        private void NotifyRefreshStatusChanged()
        {
            UpdateSectionProgress();
            OnPropertyChanged(nameof(UnifiedRefreshStatus));
            OnPropertyChanged(nameof(UnifiedRefreshDetails));
        }

        /// <summary>
        /// True when at least one blog post arrived in the most recent refresh that is newer
        /// than the last time the user viewed the Blogs tab. Drives the green dot on the tab.
        /// </summary>
        public bool HasNewBlogs
        {
            get => NewsSection.HasNewBlogs;
        }

        /// <summary>
        /// True when at least one YouTube video arrived in the most recent refresh that is newer
        /// than the last time the user viewed the Videos tab. Drives the green dot on the tab.
        /// </summary>
        public bool HasNewVideos
        {
            get => VideoSection.HasNewVideos;
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
                if (_isDisposed)
                    return;
                if (_selectedMruItem != value)
                {
                    // Deselect previous
                    if (_selectedMruItem != null)
                    {
                        _selectedMruItem.PropertyChanged -= SelectedMruItem_PropertyChanged;
                        _selectedMruItem.IsSelected = false;
                    }

                    _selectedMruItem = value;
                    _repositoryScopeOverride = null;

                    // Select new
                    if (_selectedMruItem != null)
                    {
                        _selectedMruItem.IsSelected = true;
                        _selectedMruItem.PropertyChanged += SelectedMruItem_PropertyChanged;
                    }

                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSelectedMruItem));
                    OnPropertyChanged(nameof(WorkSectionTitle));
                    OnPropertyChanged(nameof(SelectedRepository));
                }
            }
        }

        /// <summary>
        /// Whether an MRU item is currently selected.
        /// </summary>
        public bool HasSelectedMruItem => _selectedMruItem != null || _repositoryScopeOverride != null;

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
            VisibleMruItems.Count == 0;

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
            : this(CancellationToken.None)
        {
        }

        internal StartScreenViewModel(CancellationToken shutdownToken,
            Func<CancellationToken, Task<ServiceHealthStatus>> getServiceHealthStatusAsync = null,
            Func<JoinableTaskFactory> getServiceHealthTaskFactory = null,
            Func<bool, CancellationToken, Task<FeedbackSnapshot>> getFeedbackAsync = null,
            Func<JoinableTaskFactory> getFeedbackTaskFactory = null,
            NewsSectionViewModel newsSection = null,
            VideoSectionViewModel videoSection = null,
            Func<CancellationToken, Task<FeedbackSnapshot>> getCachedFeedbackAsync = null)
        {
            _shutdownToken = shutdownToken;
            _repositorySummaries = new Services.DevHub.DevHubRepoSummaryService(shutdownToken);
            _getServiceHealthStatusAsync = getServiceHealthStatusAsync ?? ServiceHealthService.GetStatusAsync;
            _getServiceHealthTaskFactory = getServiceHealthTaskFactory ?? (() => StartScreenPackage.BackgroundTaskFactory);
            _getFeedbackAsync = getFeedbackAsync ?? FeedbackService.Instance.GetTicketsAsync;
            _getCachedFeedbackAsync = getCachedFeedbackAsync ?? (getFeedbackAsync == null
                ? FeedbackService.Instance.GetCachedTicketsAsync : _ => Task.FromResult<FeedbackSnapshot>(null));
            _getFeedbackTaskFactory = getFeedbackTaskFactory ?? (() => StartScreenPackage.BackgroundTaskFactory);
            _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            NewsSection = newsSection ?? new NewsSectionViewModel(GetBackgroundCancellationToken);
            VideoSection = videoSection ?? new VideoSectionViewModel(GetBackgroundCancellationToken);
            NewsSection.PropertyChanged += Section_PropertyChanged;
            VideoSection.PropertyChanged += Section_PropertyChanged;
            BackgroundProgress.Changed += BackgroundProgress_Changed;
            RecentTemplates = new ObservableCollection<RecentTemplate>();

            _allMruItems = new ObservableCollection<MruItem>();
            _discoverySectionTitle = "Discover what's new";
            _discoverySectionVersion = string.Empty;

            FeedStore.FeedsChanged += OnFeedsChanged;
        }

        public void ActivateBackgroundWork()
        {
            lock (_cancellationSync)
            {
                if (_isDisposed || _shutdownToken.IsCancellationRequested)
                    return;

                if (_lifetimeCancellation.IsCancellationRequested)
                {
                    _lifetimeCancellation.Dispose();
                    _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
                    NewsSection.Activate();
                    VideoSection.Activate();
                    _autoRefreshTimer?.Change(_autoRefreshInterval, _autoRefreshInterval);
                }
            }
        }

        public void CancelBackgroundWork()
        {
            lock (_cancellationSync)
            {
                if (_isDisposed)
                    return;
                _lifetimeCancellation.Cancel();
                NewsSection.Cancel();
                VideoSection.Cancel();
                _feedbackRefreshCancellation?.Cancel();
                _mruMetadataCancellation?.Cancel();
                _repositorySummaries.CancelPendingRequests();
                _serviceHealthRefreshCancellation?.Cancel();
                _serviceHealthRefreshTimer?.Stop();
                _autoRefreshTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }

            bool wasRefreshingWorkspace = _isRefreshingWorkspaceSignals;
            _isRefreshingWorkspaceSignals = false;
            WorkspaceRefreshState.IsLoading = false;
            _workspaceSignalsGeneration++;
            if (wasRefreshingWorkspace)
            {
                OnPropertyChanged(nameof(IsRefreshingAny));
                NotifyRefreshStatusChanged();
            }
            IsRefreshingNews = false;
            IsRefreshingYouTube = false;
            IsRefreshingDevHub = false;
            DevHubRefreshState.IsLoading = false;
            IsRefreshingFeedback = false;
            _feedbackRequested = false;
            FeedbackTickets.Clear();
            LastFeedbackRefreshText = null;
            FeedbackStatus = "Feedback loads in the background.";
            FeedbackRequiresSignIn = false;
            _sectionProgress.Clear();
            BackgroundProgress.Reset();
        }

        public void Dispose()
        {
            // Dispatcher timers are only created on the UI thread. Owners marshal cleanup there.
            _serviceHealthRefreshTimer?.Dispatcher.VerifyAccess();
            lock (_cancellationSync)
            {
                if (_isDisposed)
                    return;
                CancelBackgroundWork();
                _isDisposed = true;
                BackgroundProgress.Changed -= BackgroundProgress_Changed;
                NewsSection.PropertyChanged -= Section_PropertyChanged;
                VideoSection.PropertyChanged -= Section_PropertyChanged;
                NewsSection.Dispose();
                VideoSection.Dispose();
                FeedStore.FeedsChanged -= OnFeedsChanged;
                if (_selectedMruItem != null)
                    _selectedMruItem.PropertyChanged -= SelectedMruItem_PropertyChanged;
                _autoRefreshTimer?.Dispose();
                _autoRefreshTimer = null;
                if (_serviceHealthRefreshTimer != null)
                {
                    _serviceHealthRefreshTimer.Stop();
                    _serviceHealthRefreshTimer.Tick -= ServiceHealthRefreshTimer_Tick;
                    _serviceHealthRefreshTimer = null;
                }
                // Refresh sources remain owned by their in-flight operations until their finally blocks.
                _lifetimeCancellation.Dispose();
            }
        }

        internal void EnsureDeferredContentInitialized()
        {
            CancellationToken cancellationToken;

            lock (_cancellationSync)
            {
                if (_isDisposed || _lifetimeCancellation.IsCancellationRequested || _isDeferredContentInitialized || _isDeferredContentInitializing)
                    return;

                _isDeferredContentInitializing = true;
                cancellationToken = _lifetimeCancellation.Token;
            }

            StartScreenPackage.BackgroundTaskFactory.RunAsync(
                () => InitializeDeferredContentAsync(cancellationToken))
                .FileAndForget(nameof(StartScreenViewModel));
        }

        internal StartScreenViewModel(IEnumerable<MruItem> mruItems)
            : this()
        {
            SetMruItems(mruItems);
        }

        internal void SetMruItems(IEnumerable<MruItem> mruItems)
        {
            if (_isDisposed)
                return;
            string selectedPath = SelectedMruItem?.Path;
            _allMruItems = new ObservableCollection<MruItem>(
                mruItems ?? Enumerable.Empty<MruItem>());
            if (selectedPath != null)
            {
                SelectedMruItem = _allMruItems.FirstOrDefault(item =>
                    string.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            }
            RefreshMruCollections();
        }

        private void OnFeedsChanged(object sender, EventArgs e)
        {
            if (GetBackgroundCancellationToken().IsCancellationRequested)
                return;

            // Force refresh news when feeds file changes
            ForceRefreshNews();
        }

        private void Section_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isDisposed)
                return;
            if (e.PropertyName != nameof(NewsSectionViewModel.RefreshState))
                OnPropertyChanged(e.PropertyName);
            if (e.PropertyName == nameof(IsRefreshingNews) || e.PropertyName == nameof(IsRefreshingYouTube))
                OnPropertyChanged(nameof(IsRefreshingAny));
            if (e.PropertyName == nameof(IsRefreshingNews) || e.PropertyName == nameof(IsRefreshingYouTube)
                || e.PropertyName == nameof(LastNewsRefreshText) || e.PropertyName == nameof(LastYouTubeRefreshText)
                || e.PropertyName == nameof(NewsSectionViewModel.RefreshState))
                NotifyRefreshStatusChanged();
        }

        internal CancellationToken GetBackgroundCancellationToken()
        {
            lock (_cancellationSync)
            {
                return _isDisposed ? new CancellationToken(true) : _lifetimeCancellation.Token;
            }
        }

        private CancellationTokenSource ReplaceRefreshCancellation(
            ref CancellationTokenSource refreshCancellation, CancellationToken cancellationToken = default)
        {
            lock (_cancellationSync)
            {
                refreshCancellation?.Cancel();
                refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    GetBackgroundCancellationToken(), cancellationToken);
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

            if (isCurrent && !_isDisposed)
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
                var summaryTasks = new System.Collections.Concurrent.ConcurrentBag<Task>();
                try
                {
                    var prioritized = items.OrderByDescending(item => ReferenceEquals(item, SelectedMruItem))
                        .ThenByDescending(item => item.IsPinned).ToArray();
                    try
                    {
                        await Task.WhenAll(
                            MruService.PopulateGitStatusAsync(prioritized, refreshCancellation.Token,
                                batch =>
                                {
                                    RefreshWorkspaceActivity(batch);
                                    summaryTasks.Add(RefreshRepositorySummariesAsync(batch, refreshCancellation.Token));
                                }),
                            MruService.PopulateExistenceAsync(prioritized, refreshCancellation.Token));
                    }
                    finally
                    {
                        await Task.WhenAll(summaryTasks);
                    }
                }
                catch (OperationCanceledException ex) when (refreshCancellation.IsCancellationRequested)
                {
                    await ex.LogAsync();
                }
                finally
                {
                    CompleteRefresh(ref _mruMetadataCancellation, refreshCancellation, () => { });
                }
            }).FileAndForget(nameof(StartScreenViewModel));
        }

        private async Task InitializeDeferredContentAsync(CancellationToken cancellationToken)
        {
            try
            {
                await StartScreenPackage.BackgroundTaskFactory.YieldAsync(
                    System.Windows.Threading.DispatcherPriority.ContextIdle, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                await RefreshMruAsync(cancellationToken);

                if (_initialMruOptionsNeedSave)
                {
                    Options options = await Options.GetLiveInstanceAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    await options.SaveAsync();
                    _initialMruOptionsNeedSave = false;
                }

                await TaskScheduler.Default;

                lock (_cancellationSync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FeedStore.StartWatching();
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                lock (_cancellationSync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _autoRefreshTimer ??= new Timer(OnAutoRefreshTimerTick, null,
                        _autoRefreshInterval, _autoRefreshInterval);
                    _autoRefreshTimer.Change(_autoRefreshInterval, _autoRefreshInterval);
                    _isDeferredContentInitialized = true;
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
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

                if (cancellationToken.IsCancellationRequested && !GetBackgroundCancellationToken().IsCancellationRequested)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    EnsureDeferredContentInitialized();
                }
            }
        }

        private void OnAutoRefreshTimerTick(object state)
        {
            if (GetBackgroundCancellationToken().IsCancellationRequested)
                return;

            // Timer fires on a ThreadPool thread. Check guards before dispatching work.
            NewsSection.RefreshIfStale();
            VideoSection.RefreshIfStale();
        }

        /// <summary>
        /// Loads MRU data from VS's MRU store for initial display.
        /// MRU is loaded first so it appears in the UI as quickly as possible.
        /// News and YouTube are handled by
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
            bool hadHistory = !string.IsNullOrEmpty(options.ExtendedMruItems) || !string.IsNullOrEmpty(options.PinnedItems);
            List<MruItem> mruItems;
            try
            {
                mruItems = await MruService.GetMruItemsAsync(options, cancellationToken, includeMetadata: false);
            }
            finally
            {
                _initialMruOptionsNeedSave |= hadHistory && string.IsNullOrEmpty(options.ExtendedMruItems) && string.IsNullOrEmpty(options.PinnedItems);
            }
            List<MruItem> extendedItems = await MruService.GetExtendedOnlyItemsAsync(mruItems, options, cancellationToken, persistChanges: false);
            var allItems = mruItems.Concat(extendedItems).ToList();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _showSolutionsOnly = options.ShowSolutionsOnly;
            OnPropertyChanged(nameof(ShowSolutionsOnly));
            SetMruItems(allItems);
            StartupDiagnostics.Mark("MRU displayed");
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
                Task feedbackTask = RefreshFeedbackInBackgroundAsync(cancellationToken);
                Task newsTask = NewsSection.LoadAsync(cancellationToken);
                Task youtubeTask = VideoSection.LoadAsync(cancellationToken);
                Task versionTask = RefreshVersionTitleAsync(cancellationToken);
                Task mruTask = refreshMru ? RefreshMruAsync(cancellationToken) : Task.CompletedTask;
                Task updateTask = CheckForUpdateAsync(cancellationToken);

                await Task.WhenAll(newsTask, youtubeTask, versionTask, mruTask, updateTask, feedbackTask);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
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
                await TaskScheduler.Default;
                cancellationToken.ThrowIfCancellationRequested();
                Options options = await Options.GetLiveInstanceAsync();
                List<MruItem> updatedMru = await MruService.GetMruItemsAsync(options, cancellationToken);
                List<MruItem> extendedItems = await MruService.GetExtendedOnlyItemsAsync(updatedMru, options, cancellationToken);
                var allItems = updatedMru.Concat(extendedItems).ToList();

                // Update on UI thread
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                cancellationToken.ThrowIfCancellationRequested();

                SetMruItems(allItems);

                StartScreenPackage.BackgroundTaskFactory.RunAsync(async () =>
                {
                    await StartScreenPackage.BackgroundTaskFactory.YieldAsync(
                        System.Windows.Threading.DispatcherPriority.ContextIdle, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    StartMruMetadataRefresh(allItems);
                }).FileAndForget(nameof(StartScreenViewModel));
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        /// <summary>
        /// Forces a refresh of news feeds regardless of cache state.
        /// </summary>
        public void ForceRefreshNews()
        {
            if (!GetBackgroundCancellationToken().IsCancellationRequested)
                NewsSection.StartRefresh(force: true);
        }

        internal void UpdateNewsRefreshStatus(FeedRefreshResult result)
        {
            NewsSection.UpdateNewsRefreshStatus(result);
        }

        internal void UpdateNewsPosts(IReadOnlyList<NewsPost> posts, Options options)
        {
            NewsSection.UpdateNewsPosts(posts, options);
        }

        /// <summary>
        /// Forces a refresh of the YouTube feed regardless of cache state.
        /// </summary>
        public void ForceRefreshYouTube()
        {
            if (!GetBackgroundCancellationToken().IsCancellationRequested)
                VideoSection.StartRefresh();
        }

        internal void UpdateYouTubeVideos(IReadOnlyList<YouTubeVideo> videos, Options options)
        {
            VideoSection.UpdateYouTubeVideos(videos, options);
        }

        /// <summary>
        /// Marks the Blogs tab as seen, persisting the timestamp and clearing the indicator.
        /// </summary>
        public Task MarkBlogsAsSeenAsync()
        {
            return NewsSection.MarkBlogsAsSeenAsync();
        }

        /// <summary>
        /// Marks the Videos tab as seen, persisting the timestamp and clearing the indicator.
        /// </summary>
        public Task MarkVideosAsSeenAsync()
        {
            return VideoSection.MarkVideosAsSeenAsync();
        }

        /// <summary>
        /// Toggles the pinned state of a news article.
        /// </summary>
        public Task ToggleNewsPinAsync(NewsPost post)
        {
            return NewsSection.ToggleNewsPinAsync(post);
        }

        /// <summary>
        /// Updates the number of news and YouTube columns based on available width.
        /// Called from the view when the right pane resizes.
        /// </summary>
        public void UpdateColumnCounts(int newsColumns, int youTubeColumns)
        {
            NewsSection.NewsColumnCount = newsColumns;
            VideoSection.YouTubeColumnCount = youTubeColumns;
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
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
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
                IsUpdateAvailable = setupService?.IsManifestRefreshedAndUpdateAvailable == true;
                UpdateAvailableText = IsUpdateAvailable && !string.IsNullOrWhiteSpace(setupService.ChannelProductVersion)
                    ? $"VS {setupService.ChannelProductVersion} update available"
                    : "VS update available";
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
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

            RefreshMruCollections();
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

            RefreshMruCollections();
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
            UpdateMruCollections(query);
        }

        /// <summary>
        /// Updates the pinned and regular MRU collections with time grouping.
        /// VS MRU items appear in their normal time groups; extended-only items appear
        /// below them in a dedicated "Earlier" group.
        /// </summary>
        private void UpdateMruCollections(string query = null)
        {
            var ordered = new List<MruItem>();
            var unpinned = new List<MruItem>();
            var extended = new List<MruItem>();
            foreach (var item in _allMruItems)
            {
                if (!MatchesMruFilter(item, query, ShowSolutionsOnly))
                    continue;
                if (item.IsPinned)
                    ordered.Add(item);
                else if (item.IsFromExtendedList)
                    extended.Add(item);
                else
                    unpinned.Add(item);
            }

            ordered.AddRange(unpinned.OrderByDescending(item => item.LastAccessed));
            ordered.AddRange(extended.OrderByDescending(item => item.LastAccessed));
            var filtered = !string.IsNullOrWhiteSpace(query);
            var groups = ordered.Select(item => item.DisplayGroup).ToArray();
            var changed = !_visibleMruItems.SequenceEqual(ordered) || !_visibleMruGroups.SequenceEqual(groups);
            if (!changed && _isMruFiltered == filtered)
                return;

            _isMruFiltered = filtered;
            _mruItems = null;
            _pinnedItems = null;
            _groupedMruItems = null;
            _visibleMruGroups = groups;
            if (changed)
                _visibleMruItems.ReplaceAll(ordered);
            OnPropertyChanged(nameof(PinnedItems));
            OnPropertyChanged(nameof(MruItems));
            OnPropertyChanged(nameof(GroupedMruItems));
        }

        private ObservableCollection<MruTimeGroup> BuildMruTimeGroups()
        {
            var groups = BuildTimeGroups(VisibleMruItems.Where(item => !item.IsPinned && !item.IsFromExtendedList));
            var extended = VisibleMruItems.Where(item => !item.IsPinned && item.IsFromExtendedList).ToArray();
            if (extended.Length > 0)
                groups.Add(new MruTimeGroup { GroupName = "Earlier", Items = new ObservableCollection<MruItem>(extended) });
            return groups;
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
