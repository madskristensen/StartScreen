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
    /// <summary>Owns video inventory, presentation state and cache/remote refresh work.</summary>
    internal sealed class VideoSectionViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly List<YouTubeVideo> _allYouTubeVideos = new List<YouTubeVideo>();
        private readonly Func<CancellationToken> _getLifetimeToken;
        private readonly Func<JoinableTaskFactory> _getTaskFactory;
        private readonly Func<CancellationToken, Task<List<YouTubeVideo>>> _getCachedVideosAsync;
        private readonly Func<bool, CancellationToken, Task<List<YouTubeVideo>>> _downloadAsync;
        private readonly Func<List<YouTubeVideo>, CancellationToken, Task> _loadThumbnailsAsync;
        private readonly Func<bool> _isCacheStale;
        private readonly Func<Task<Options>> _getOptionsAsync;
        private readonly Func<Options, Task> _saveOptionsAsync;
        private readonly object _sync = new object();
        private CancellationTokenSource _workCancellation = new CancellationTokenSource();
        private CancellationTokenSource _refreshCancellation;
        private bool _isDisposed;
        private bool _isRefreshingYouTube;
        private bool _isYouTubeUnavailable;
        private bool _hasNewVideos;
        private string _lastYouTubeRefreshText;
        private int _youTubeColumnCount = 1;

        internal VideoSectionViewModel(Func<CancellationToken> getLifetimeToken = null,
            Func<JoinableTaskFactory> getTaskFactory = null,
            Func<CancellationToken, Task<List<YouTubeVideo>>> getCachedVideosAsync = null,
            Func<CancellationToken, Task<List<YouTubeVideo>>> downloadAsync = null,
            Func<List<YouTubeVideo>, CancellationToken, Task> loadThumbnailsAsync = null,
            Func<bool> isCacheStale = null, Func<Task<Options>> getOptionsAsync = null,
            Func<Options, Task> saveOptionsAsync = null)
        {
            _getLifetimeToken = getLifetimeToken ?? (() => CancellationToken.None);
            _getTaskFactory = getTaskFactory ?? (() => StartScreenPackage.BackgroundTaskFactory);
            _getCachedVideosAsync = getCachedVideosAsync ?? YouTubeService.GetCachedVideosAsync;
            _downloadAsync = downloadAsync == null ? YouTubeService.RefreshVideosAsync : (_, token) => downloadAsync(token);
            _loadThumbnailsAsync = loadThumbnailsAsync ?? YouTubeService.LoadThumbnailsAsync;
            _isCacheStale = isCacheStale ?? YouTubeService.IsCacheStale;
            _getOptionsAsync = getOptionsAsync ?? Options.GetLiveInstanceAsync;
            _saveOptionsAsync = saveOptionsAsync ?? (options => options.SaveAsync());
        }

        public ObservableCollection<YouTubeVideo> YouTubeVideos { get; } = new ObservableCollection<YouTubeVideo>();
        internal SectionRefreshState RefreshState { get; } = new SectionRefreshState();

        public bool IsRefreshingYouTube
        {
            get => _isRefreshingYouTube;
            set
            {
                if (_isDisposed || _isRefreshingYouTube == value)
                    return;
                _isRefreshingYouTube = value;
                RefreshState.IsLoading = value;
                OnPropertyChanged();
            }
        }

        public bool IsYouTubeUnavailable
        {
            get => _isYouTubeUnavailable;
            set
            {
                if (_isDisposed || _isYouTubeUnavailable == value)
                    return;
                _isYouTubeUnavailable = value;
                OnPropertyChanged();
            }
        }

        public string LastYouTubeRefreshText
        {
            get => _lastYouTubeRefreshText;
            set
            {
                if (_isDisposed || _lastYouTubeRefreshText == value)
                    return;
                _lastYouTubeRefreshText = value;
                OnPropertyChanged();
            }
        }

        public bool HasNewVideos
        {
            get => _hasNewVideos;
            private set
            {
                if (_hasNewVideos == value)
                    return;
                _hasNewVideos = value;
                OnPropertyChanged();
            }
        }

        public int YouTubeColumnCount
        {
            get => _youTubeColumnCount;
            internal set
            {
                if (_isDisposed || _youTubeColumnCount == value)
                    return;
                _youTubeColumnCount = value;
                OnPropertyChanged();
                UpdateYouTubeCollections();
            }
        }

        internal void UpdateYouTubeVideos(IReadOnlyList<YouTubeVideo> videos, Options options)
        {
            if (_isDisposed)
                return;
            var refreshed = videos.Select(video =>
            {
                var existing = _allYouTubeVideos.FirstOrDefault(item => item.Url == video.Url);
                if (existing == null)
                    return video;
                existing.Title = video.Title;
                existing.PublishDate = video.PublishDate;
                if (existing.ThumbnailUrl != video.ThumbnailUrl || existing.Thumbnail == null)
                    existing.Thumbnail = video.Thumbnail;
                existing.ThumbnailUrl = video.ThumbnailUrl;
                return existing;
            }).ToList();
            _allYouTubeVideos.Clear();
            _allYouTubeVideos.AddRange(refreshed);
            UpdateYouTubeCollections();
            HasNewVideos = options.LastNewsVideosSeen != DateTime.MinValue
                && _allYouTubeVideos.Any(video => video.PublishDate > options.LastNewsVideosSeen);
        }

        public async Task MarkVideosAsSeenAsync()
        {
            if (_isDisposed)
                return;
            HasNewVideos = false;
            Options options = await _getOptionsAsync();
            if (_isDisposed)
                return;
            options.LastNewsVideosSeen = DateTime.UtcNow;
            await _saveOptionsAsync(options);
        }

        internal async Task LoadAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource work = CreateWorkCancellation(cancellationToken);
            CancellationToken token = work.Token;
            try
            {
                token.ThrowIfCancellationRequested();
                List<YouTubeVideo> videos = await _getCachedVideosAsync(token);
                var hasCachedItems = videos != null;
                if (hasCachedItems)
                {
                    await UpdateOnUIThreadAsync(videos, token);
                    LastYouTubeRefreshText = "Showing cached content";
                    SetRefreshState(cached: true);
                    StartupDiagnostics.Mark("video cache displayed");
                    await _loadThumbnailsAsync(videos, token);
                    await UpdateOnUIThreadAsync(videos, token);
                }
                await TaskScheduler.Default;
                var needsRefresh = !hasCachedItems || _isCacheStale();
                await _getTaskFactory().SwitchToMainThreadAsync(token);
                token.ThrowIfCancellationRequested();
                RefreshState.IsStale = hasCachedItems && needsRefresh;
                if (needsRefresh)
                    StartRefresh(false);
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
            if (!IsWorkCanceled() && !IsRefreshingYouTube && _isCacheStale())
                StartRefresh(false);
        }

        internal void StartRefresh(bool force = true)
        {
            if (IsWorkCanceled() || IsRefreshingYouTube)
                return;
            CancellationTokenSource request = CreateWorkCancellation(default);
            _getTaskFactory().RunAsync(async () =>
            {
                using (request)
                {
                    if (!request.IsCancellationRequested)
                        await RefreshAsync(request.Token, force);
                }
            }).FileAndForget(nameof(VideoSectionViewModel));
        }

        internal async Task RefreshAsync(CancellationToken cancellationToken = default, bool force = true)
        {
            JoinableTaskFactory factory = _getTaskFactory();
            await factory.SwitchToMainThreadAsync(cancellationToken);
            if (IsWorkCanceled() || IsRefreshingYouTube)
                return;
            CancellationTokenSource refresh = CreateWorkCancellation(cancellationToken);
            _refreshCancellation = refresh;
            CancellationToken token = refresh.Token;
            IsRefreshingYouTube = true;
            try
            {
                await TaskScheduler.Default;
                token.ThrowIfCancellationRequested();
                List<YouTubeVideo> videos = await _downloadAsync(force, token);
                if (videos != null)
                {
                    await UpdateOnUIThreadAsync(videos, token);
                    await factory.SwitchToMainThreadAsync(token);
                    token.ThrowIfCancellationRequested();
                    IsYouTubeUnavailable = false;
                    LastYouTubeRefreshText = "Updated just now";
                    SetRefreshState(successfulUtc: DateTimeOffset.UtcNow);
                    StartupDiagnostics.Mark("video remote refresh completed");
                    await _loadThumbnailsAsync(videos, token);
                    await UpdateOnUIThreadAsync(videos, token);
                }
                else
                {
                    await factory.SwitchToMainThreadAsync(token);
                    token.ThrowIfCancellationRequested();
                    ReportFailure();
                }
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
                await factory.SwitchToMainThreadAsync();
                if (ReferenceEquals(_refreshCancellation, refresh))
                {
                    _refreshCancellation = null;
                    IsRefreshingYouTube = false;
                }
                refresh.Dispose();
            }
        }

        private void ReportFailure()
        {
            IsYouTubeUnavailable = _allYouTubeVideos.Count == 0;
            LastYouTubeRefreshText = IsYouTubeUnavailable ? "Feed unavailable. Use Refresh to retry."
                : "Showing cached content - refresh failed. Use Refresh to retry.";
            SetRefreshState(cached: !IsYouTubeUnavailable, stale: true, unavailable: IsYouTubeUnavailable);
        }

        private void SetRefreshState(bool cached = false, bool stale = false, bool unavailable = false,
            DateTimeOffset? successfulUtc = null)
        {
            RefreshState.HasRefreshed = true;
            RefreshState.IsCached = cached;
            RefreshState.IsStale = stale;
            RefreshState.IsPartial = false;
            RefreshState.IsUnavailable = unavailable;
            RefreshState.Message = LastYouTubeRefreshText;
            if (successfulUtc.HasValue)
                RefreshState.LastSuccessfulRefreshUtc = successfulUtc;
            OutputLog.Refresh("Videos", $"{LastYouTubeRefreshText}; {_allYouTubeVideos.Count} videos.", RefreshState);
            OnPropertyChanged(nameof(RefreshState));
        }

        private async Task UpdateOnUIThreadAsync(List<YouTubeVideo> videos, CancellationToken token)
        {
            Options options = await _getOptionsAsync();
            await _getTaskFactory().SwitchToMainThreadAsync(token);
            token.ThrowIfCancellationRequested();
            UpdateYouTubeVideos(videos, options);
        }

        private void UpdateYouTubeCollections()
        {
            CollectionSynchronization.Synchronize(YouTubeVideos, _allYouTubeVideos.Take(YouTubeColumnCount * 5).ToList());
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
            IsRefreshingYouTube = false;
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