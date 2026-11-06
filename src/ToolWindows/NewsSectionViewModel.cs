using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.ToolWindows
{
    /// <summary>Owns news inventory, presentation state and cache/remote refresh work.</summary>
    internal sealed class NewsSectionViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly List<NewsPost> _allNewsPosts = new List<NewsPost>();
        private readonly Func<CancellationToken> _getLifetimeToken;
        private readonly Func<JoinableTaskFactory> _getTaskFactory;
        private readonly Func<CancellationToken, Task<List<NewsPost>>> _getCachedPostsAsync;
        private readonly Func<bool, CancellationToken, Task<FeedRefreshResult>> _downloadAsync;
        private readonly Func<bool> _isCacheStale;
        private readonly Func<Task<Options>> _getOptionsAsync;
        private readonly Func<Options, Task> _saveOptionsAsync;
        private readonly object _sync = new object();
        private CancellationTokenSource _workCancellation = new CancellationTokenSource();
        private CancellationTokenSource _refreshCancellation;
        private bool _isDisposed;
        private bool _isRefreshingNews;
        private bool _isNewsUnavailable;
        private bool _hasNewBlogs;
        private string _lastNewsRefreshText;
        private int _newsColumnCount = 2;

        internal NewsSectionViewModel(Func<CancellationToken> getLifetimeToken = null,
            Func<JoinableTaskFactory> getTaskFactory = null,
            Func<CancellationToken, Task<List<NewsPost>>> getCachedPostsAsync = null,
            Func<bool, CancellationToken, Task<FeedRefreshResult>> downloadAsync = null,
            Func<bool> isCacheStale = null, Func<Task<Options>> getOptionsAsync = null,
            Func<Options, Task> saveOptionsAsync = null)
        {
            _getLifetimeToken = getLifetimeToken ?? (() => CancellationToken.None);
            _getTaskFactory = getTaskFactory ?? (() => StartScreenPackage.BackgroundTaskFactory);
            _getCachedPostsAsync = getCachedPostsAsync ?? FeedService.GetCachedPostsAsync;
            _downloadAsync = downloadAsync ?? DownloadAsync;
            _isCacheStale = isCacheStale ?? FeedService.IsCacheStale;
            _getOptionsAsync = getOptionsAsync ?? Options.GetLiveInstanceAsync;
            _saveOptionsAsync = saveOptionsAsync ?? (options => options.SaveAsync());
        }

        public ObservableCollection<NewsPost> NewsPosts { get; } = new ObservableCollection<NewsPost>();
        public ObservableCollection<NewsPost> PinnedNewsPosts { get; } = new ObservableCollection<NewsPost>();
        internal SectionRefreshState RefreshState { get; } = new SectionRefreshState();

        public bool IsRefreshingNews
        {
            get => _isRefreshingNews;
            set
            {
                if (_isDisposed || _isRefreshingNews == value)
                    return;
                _isRefreshingNews = value;
                RefreshState.IsLoading = value;
                OnPropertyChanged();
            }
        }

        public bool IsNewsUnavailable
        {
            get => _isNewsUnavailable;
            set
            {
                if (_isDisposed || _isNewsUnavailable == value)
                    return;
                _isNewsUnavailable = value;
                OnPropertyChanged();
            }
        }

        public string LastNewsRefreshText
        {
            get => _lastNewsRefreshText;
            set
            {
                if (_isDisposed || _lastNewsRefreshText == value)
                    return;
                _lastNewsRefreshText = value;
                OnPropertyChanged();
            }
        }

        public bool HasNewBlogs
        {
            get => _hasNewBlogs;
            private set
            {
                if (_hasNewBlogs == value)
                    return;
                _hasNewBlogs = value;
                OnPropertyChanged();
            }
        }

        public int NewsColumnCount
        {
            get => _newsColumnCount;
            internal set
            {
                if (_isDisposed || _newsColumnCount == value)
                    return;
                _newsColumnCount = value;
                OnPropertyChanged();
                UpdateNewsCollections();
            }
        }

        internal void UpdateNewsPosts(IReadOnlyList<NewsPost> posts, Options options)
        {
            if (_isDisposed)
                return;
            var refreshed = posts.Select(post =>
            {
                var existing = _allNewsPosts.FirstOrDefault(item => item.Url == post.Url);
                if (existing == null)
                    return post;
                existing.Title = post.Title;
                existing.Summary = post.Summary;
                existing.PublishDate = post.PublishDate;
                existing.Source = post.Source;
                existing.ToolTip = post.ToolTip;
                existing.HasDescription = post.HasDescription;
                return existing;
            }).ToList();
            _allNewsPosts.Clear();
            _allNewsPosts.AddRange(refreshed);
            var pinnedUrls = new HashSet<string>(
                (options.PinnedArticles ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            foreach (NewsPost post in _allNewsPosts)
                post.IsPinned = pinnedUrls.Contains(post.Url);
            UpdateNewsCollections();
            HasNewBlogs = options.LastNewsBlogsSeen != DateTime.MinValue
                && _allNewsPosts.Any(post => post.PublishDate > options.LastNewsBlogsSeen);
        }

        public async Task MarkBlogsAsSeenAsync()
        {
            if (_isDisposed)
                return;
            HasNewBlogs = false;
            Options options = await _getOptionsAsync();
            if (_isDisposed)
                return;
            options.LastNewsBlogsSeen = DateTime.UtcNow;
            await _saveOptionsAsync(options);
        }

        public async Task ToggleNewsPinAsync(NewsPost post)
        {
            if (_isDisposed || post == null)
                return;
            post.IsPinned = !post.IsPinned;
            Options options = await _getOptionsAsync();
            if (_isDisposed)
                return;
            options.PinnedArticles = string.Join(";", _allNewsPosts.Where(item => item.IsPinned).Select(item => item.Url));
            await _saveOptionsAsync(options);
            if (!_isDisposed)
                UpdateNewsCollections();
        }

        internal async Task LoadAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource work = CreateWorkCancellation(cancellationToken);
            CancellationToken token = work.Token;
            try
            {
                token.ThrowIfCancellationRequested();
                List<NewsPost> posts = await _getCachedPostsAsync(token);
                var hasCachedItems = posts != null && posts.Count > 0;
                if (hasCachedItems)
                {
                    await UpdateOnUIThreadAsync(posts, token);
                    LastNewsRefreshText = "Showing cached content";
                    SetRefreshState(cached: true);
                    StartupDiagnostics.Mark("news cache displayed");
                }
                await TaskScheduler.Default;
                var needsRefresh = !hasCachedItems || _isCacheStale();
                await _getTaskFactory().SwitchToMainThreadAsync(token);
                token.ThrowIfCancellationRequested();
                RefreshState.IsStale = hasCachedItems && needsRefresh;
                if (needsRefresh)
                    StartRefresh();
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        internal void RefreshIfStale()
        {
            if (!IsWorkCanceled() && !IsRefreshingNews && _isCacheStale())
                StartRefresh();
        }

        internal void StartRefresh(bool force = false)
        {
            if (IsWorkCanceled() || IsRefreshingNews)
                return;
            CancellationTokenSource request = CreateWorkCancellation(default);
            _getTaskFactory().RunAsync(async () =>
            {
                using (request)
                {
                    if (!request.IsCancellationRequested)
                        await RefreshAsync(force, request.Token);
                }
            }).FileAndForget(nameof(NewsSectionViewModel));
        }

        internal async Task RefreshAsync(bool force, CancellationToken cancellationToken = default)
        {
            JoinableTaskFactory factory = _getTaskFactory();
            await factory.SwitchToMainThreadAsync(cancellationToken);
            if (IsWorkCanceled() || IsRefreshingNews)
                return;
            CancellationTokenSource refresh = CreateWorkCancellation(cancellationToken);
            _refreshCancellation = refresh;
            CancellationToken token = refresh.Token;
            IsRefreshingNews = true;
            var minDisplay = Task.Delay(1000, token);
            try
            {
                await TaskScheduler.Default;
                token.ThrowIfCancellationRequested();
                FeedRefreshResult result = await _downloadAsync(force, token);
                if (result.Success || result.Posts.Count > 0)
                    await UpdateOnUIThreadAsync(result.Posts, token);
                await factory.SwitchToMainThreadAsync(token);
                token.ThrowIfCancellationRequested();
                UpdateNewsRefreshStatus(result);
                StartupDiagnostics.Mark("news refresh completed");
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            {
                await ex.LogAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await factory.SwitchToMainThreadAsync();
                if (!token.IsCancellationRequested)
                    ReportFailure();
            }
            finally
            {
                try
                {
                    await minDisplay;
                }
                catch (OperationCanceledException ex) when (token.IsCancellationRequested)
                {
                    await ex.LogAsync();
                }
                await factory.SwitchToMainThreadAsync();
                if (ReferenceEquals(_refreshCancellation, refresh))
                {
                    _refreshCancellation = null;
                    IsRefreshingNews = false;
                }
                refresh.Dispose();
            }
        }

        internal void UpdateNewsRefreshStatus(FeedRefreshResult result)
        {
            if (_isDisposed)
                return;
            var hasContent = result.Posts.Count > 0 || _allNewsPosts.Count > 0;
            IsNewsUnavailable = !result.Success && !hasContent;
            if (!result.Success)
            {
                var status = result.FailedFeeds.Count < result.FeedCount ? "Partially refreshed" : "Refresh failed";
                var cached = result.CachedFeeds.Count > 0
                    ? $" Showing cached content from {string.Join(", ", result.CachedFeeds)}."
                    : hasContent && result.Posts.Count == 0 ? " Showing cached content." : string.Empty;
                LastNewsRefreshText = $"{status} - unavailable: {string.Join(", ", result.FailedFeeds)}.{cached} Use Refresh to retry.";
            }
            else if (result.FeedCount == 0)
                LastNewsRefreshText = "No news feeds enabled";
            else if (result.CachedFeeds.Count > 0)
                LastNewsRefreshText = $"Showing cached content from {string.Join(", ", result.CachedFeeds)}";
            else
                LastNewsRefreshText = "Updated just now";
            SetRefreshState(cached: result.CachedFeeds.Count > 0 || !result.Success && hasContent,
                stale: !result.Success, partial: !result.Success && result.FailedFeeds.Count < result.FeedCount,
                unavailable: IsNewsUnavailable,
                successfulUtc: result.Success && result.FeedCount > 0 && result.CachedFeeds.Count == 0 ? DateTimeOffset.UtcNow : null);
        }

        private void ReportFailure()
        {
            IsNewsUnavailable = _allNewsPosts.Count == 0;
            LastNewsRefreshText = IsNewsUnavailable ? "Feed unavailable. Use Refresh to retry."
                : "Showing cached content - refresh failed. Use Refresh to retry.";
            SetRefreshState(cached: !IsNewsUnavailable, stale: true, unavailable: IsNewsUnavailable);
        }

        private void SetRefreshState(bool cached = false, bool stale = false, bool partial = false,
            bool unavailable = false, DateTimeOffset? successfulUtc = null)
        {
            RefreshState.HasRefreshed = true;
            RefreshState.IsCached = cached;
            RefreshState.IsStale = stale;
            RefreshState.IsPartial = partial;
            RefreshState.IsUnavailable = unavailable;
            RefreshState.Message = LastNewsRefreshText;
            if (successfulUtc.HasValue)
                RefreshState.LastSuccessfulRefreshUtc = successfulUtc;
            OutputLog.Info("News", $"{LastNewsRefreshText}; {_allNewsPosts.Count} articles (cache={cached}, stale={stale}, unavailable={unavailable}).");
            OnPropertyChanged(nameof(RefreshState));
        }

        private async Task UpdateOnUIThreadAsync(List<NewsPost> posts, CancellationToken token)
        {
            Options options = await _getOptionsAsync();
            await _getTaskFactory().SwitchToMainThreadAsync(token);
            token.ThrowIfCancellationRequested();
            UpdateNewsPosts(posts, options);
        }

        private static async Task<FeedRefreshResult> DownloadAsync(bool force, CancellationToken token)
        {
            List<FeedInfo> feeds = FeedStore.GetFeeds();
            return await FeedService.DownloadFeedsAsync(feeds, token, force);
        }

        private void UpdateNewsCollections()
        {
            CollectionSynchronization.Synchronize(PinnedNewsPosts, _allNewsPosts.Where(post => post.IsPinned).ToList());
            CollectionSynchronization.Synchronize(NewsPosts, _allNewsPosts.Where(post => !post.IsPinned).Take(NewsColumnCount * 10).ToList());
        }

        private CancellationTokenSource CreateWorkCancellation(CancellationToken token)
        {
            CancellationToken lifetimeToken = _getLifetimeToken();
            lock (_sync)
            {
                return CancellationTokenSource.CreateLinkedTokenSource(
                    _isDisposed ? new CancellationToken(true) : _workCancellation.Token, lifetimeToken, token);
            }
        }

        private bool IsWorkCanceled()
        {
            CancellationToken lifetimeToken = _getLifetimeToken();
            lock (_sync)
                return _isDisposed || _workCancellation.IsCancellationRequested || lifetimeToken.IsCancellationRequested;
        }

        internal void Activate()
        {
            lock (_sync)
            {
                if (_isDisposed || !_workCancellation.IsCancellationRequested)
                    return;
                _workCancellation.Dispose();
                _workCancellation = new CancellationTokenSource();
            }
        }

        internal void Cancel()
        {
            lock (_sync)
            {
                if (_isDisposed)
                    return;
                _workCancellation.Cancel();
                _refreshCancellation = null;
            }
            IsRefreshingNews = false;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_isDisposed)
                    return;
                Cancel();
                _isDisposed = true;
                _workCancellation.Dispose();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            if (!_isDisposed)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}