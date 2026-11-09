using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.ToolWindows
{
    internal sealed class EventSectionViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly Func<CancellationToken> _getLifetimeToken;
        private readonly Func<JoinableTaskFactory> _getTaskFactory;
        private readonly Func<bool, CancellationToken, Task<EventFeedResult>> _getEventsAsync;
        private readonly Func<IReadOnlyList<ConferenceEvent>, CancellationToken, Task<int>> _loadThumbnailsAsync;
        private CancellationTokenSource _request;
        private bool _isDisposed;
        private bool _isRefreshingEvents;
        private string _eventsStatus;

        internal EventSectionViewModel(Func<CancellationToken> getLifetimeToken = null,
            Func<JoinableTaskFactory> getTaskFactory = null,
            Func<bool, CancellationToken, Task<EventFeedResult>> getEventsAsync = null,
            Func<IReadOnlyList<ConferenceEvent>, CancellationToken, Task<int>> loadThumbnailsAsync = null)
        {
            _getLifetimeToken = getLifetimeToken ?? (() => CancellationToken.None);
            _getTaskFactory = getTaskFactory ?? (() => StartScreenPackage.BackgroundTaskFactory);
            _getEventsAsync = getEventsAsync ?? EventService.Instance.GetAsync;
            _loadThumbnailsAsync = loadThumbnailsAsync ?? EventService.Instance.LoadThumbnailsAsync;
        }

        public ObservableCollection<ConferenceEvent> Events { get; } = new ObservableCollection<ConferenceEvent>();
        internal SectionRefreshState RefreshState { get; } = new SectionRefreshState();
        public bool IsRefreshingEvents => _isRefreshingEvents;
        public string EventsStatus => _eventsStatus;

        internal async Task ActivateTabAsync(bool force = false)
        {
            if (_isDisposed)
                return;
            var factory = _getTaskFactory();
            var lifetime = _getLifetimeToken();
            if (_isDisposed || lifetime.IsCancellationRequested)
                return;
            await factory.SwitchToMainThreadAsync(lifetime);
            if (_isDisposed || _request != null)
                return;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _request = request;
            _isRefreshingEvents = true;
            RefreshState.IsLoading = true;
            OnPropertyChanged(nameof(IsRefreshingEvents));
            SetStatus("Loading events...");
            try
            {
                var result = await _getEventsAsync(force, request.Token);
                await factory.SwitchToMainThreadAsync(request.Token);
                request.Token.ThrowIfCancellationRequested();
                CollectionSynchronization.Synchronize(Events, result.Events);
                RefreshState.HasRefreshed = true;
                RefreshState.IsCached = result.IsCached;
                RefreshState.IsStale = result.IsStale;
                RefreshState.IsUnavailable = result.IsUnavailable;
                RefreshState.IsPartial = false;
                RefreshState.LastSuccessfulRefreshUtc = result.LastSuccessfulRefreshUtc;
                SetStatus(result.Message);
                var failures = await _loadThumbnailsAsync(result.Events, request.Token);
                await factory.SwitchToMainThreadAsync(request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (failures > 0)
                {
                    RefreshState.IsPartial = true;
                    SetStatus(result.Message + " Some event thumbnails could not be cached or loaded.");
                }
                OutputLog.Refresh("Events", EventsStatus, RefreshState);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await factory.SwitchToMainThreadAsync();
                if (!request.IsCancellationRequested && !_isDisposed)
                {
                    RefreshState.HasRefreshed = true;
                    RefreshState.IsStale = true;
                    RefreshState.IsUnavailable = Events.Count == 0;
                    SetStatus("Events could not be updated. Select Events again or use Refresh to retry.");
                }
            }
            finally
            {
                await factory.SwitchToMainThreadAsync();
                if (ReferenceEquals(_request, request))
                {
                    _request = null;
                    _isRefreshingEvents = false;
                    RefreshState.IsLoading = false;
                    OnPropertyChanged(nameof(IsRefreshingEvents));
                    OnPropertyChanged(nameof(RefreshState));
                }
            }
        }

        internal void Cancel()
        {
            if (_request == null && !_isRefreshingEvents)
                return;
            _request?.Cancel();
            _request = null;
            _isRefreshingEvents = false;
            RefreshState.IsLoading = false;
            OnPropertyChanged(nameof(IsRefreshingEvents));
        }

        private void SetStatus(string status)
        {
            _eventsStatus = status;
            RefreshState.Message = status;
            OnPropertyChanged(nameof(EventsStatus));
            OnPropertyChanged(nameof(RefreshState));
        }

        public void Dispose()
        {
            Cancel();
            _isDisposed = true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            if (!_isDisposed)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
