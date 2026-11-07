using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenViewModel
    {
        private readonly Func<bool, CancellationToken, Task<FeedbackSnapshot>> _getFeedbackAsync;
        private readonly Func<CancellationToken, Task<FeedbackSnapshot>> _getCachedFeedbackAsync;
        private readonly Func<JoinableTaskFactory> _getFeedbackTaskFactory;
        private CancellationTokenSource _feedbackRefreshCancellation;
        private Task _feedbackRefreshTask = Task.CompletedTask;
        private bool _feedbackRequested;
        private bool _isRefreshingFeedback;
        private string _lastFeedbackRefreshText;
        private string _feedbackStatus = "Feedback loads in the background.";
        private bool _feedbackRequiresSignIn;

        public ObservableCollection<FeedbackTicket> FeedbackTickets { get; } = new ObservableCollection<FeedbackTicket>();

        public bool FeedbackRequiresSignIn
        {
            get => _feedbackRequiresSignIn;
            private set
            {
                if (_feedbackRequiresSignIn == value)
                    return;
                _feedbackRequiresSignIn = value;
                OnPropertyChanged();
            }
        }

        public bool IsRefreshingFeedback
        {
            get => _isRefreshingFeedback;
            private set
            {
                if (_isRefreshingFeedback != value)
                {
                    _isRefreshingFeedback = value;
                    FeedbackRefreshState.IsLoading = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsRefreshingAny));
                    NotifyRefreshStatusChanged();
                }
            }
        }

        public string LastFeedbackRefreshText
        {
            get => _lastFeedbackRefreshText;
            private set
            {
                if (_lastFeedbackRefreshText == value)
                    return;
                _lastFeedbackRefreshText = value;
                OnPropertyChanged();
                NotifyRefreshStatusChanged();
            }
        }

        public string FeedbackStatus
        {
            get => _feedbackStatus;
            private set
            {
                if (_feedbackStatus == value)
                    return;
                _feedbackStatus = value;
                OnPropertyChanged();
                NotifyRefreshStatusChanged();
            }
        }

        internal Task RefreshFeedbackInBackgroundAsync(CancellationToken cancellationToken = default)
        {
            _feedbackRequested = true;
            return StartFeedbackRefreshAsync(false, cancellationToken);
        }

        public void EnsureFeedbackLoaded()
        {
            if (_feedbackRequested)
                return;
            _feedbackRequested = true;
            StartFeedbackRefreshAsync(false).Forget();
        }

        public void ForceRefreshFeedback()
        {
            if (_feedbackRequested)
                StartFeedbackRefreshAsync(true).Forget();
        }

        internal void ReportFeedbackNavigationFailure()
        {
            FeedbackStatus = "The ticket could not be opened in your browser. Try the link again.";
        }

        private Task StartFeedbackRefreshAsync(bool force, CancellationToken cancellationToken = default)
        {
            if (GetBackgroundCancellationToken().IsCancellationRequested)
                return Task.CompletedTask;
            if (IsRefreshingFeedback)
                return _feedbackRefreshTask;
            return _feedbackRefreshTask = _getFeedbackTaskFactory().RunAsync(() => RefreshFeedbackAsync(force, cancellationToken)).Task;
        }

        internal async Task RefreshFeedbackAsync(bool force, CancellationToken cancellationToken = default)
        {
            JoinableTaskFactory factory = _getFeedbackTaskFactory();
            await factory.SwitchToMainThreadAsync(cancellationToken);
            if (GetBackgroundCancellationToken().IsCancellationRequested)
                return;

            CancellationTokenSource refresh = ReplaceRefreshCancellation(ref _feedbackRefreshCancellation, cancellationToken);
            CancellationToken token = refresh.Token;
            IsRefreshingFeedback = true;
            FeedbackTickets.Clear();
            LastFeedbackRefreshText = null;
            FeedbackStatus = "Feedback loading...";
            FeedbackRequiresSignIn = false;
            FeedbackSnapshot cached = null;
            try
            {
                if (!force)
                {
                    cached = await _getCachedFeedbackAsync(token);
                    await factory.SwitchToMainThreadAsync(token);
                    token.ThrowIfCancellationRequested();
                    if (cached != null)
                        ApplyFeedbackSnapshot(cached, cached: true);
                }

                FeedbackSnapshot snapshot = await _getFeedbackAsync(true, token);
                await factory.SwitchToMainThreadAsync(token);
                token.ThrowIfCancellationRequested();
                ApplyFeedbackSnapshot(snapshot, cached: false);
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
                {
                    var authentication = ex is UnauthorizedAccessException || ex is CredentialUnavailableException || ex is AuthenticationFailedException;
                    FeedbackRequiresSignIn = authentication;
                    var retainCache = cached != null && !authentication;
                    if (!retainCache)
                        FeedbackTickets.Clear();
                    LastFeedbackRefreshText = retainCache ? "Showing cached feedback" : "Feedback unavailable";
                    FeedbackStatus = authentication
                        ? "Sign in or reauthenticate in Visual Studio, select your account under Tools > Options > Azure Service Authentication, then use Refresh."
                        : retainCache ? "Showing cached feedback - Developer Community is unavailable. Use Refresh to retry."
                        : "Developer Community is unavailable. Use Refresh to retry.";
                    SetRefreshState(FeedbackRefreshState, FeedbackStatus, cached: retainCache,
                        stale: retainCache, unavailable: !retainCache);
                }
            }
            finally
            {
                await factory.SwitchToMainThreadAsync();
                CompleteRefresh(ref _feedbackRefreshCancellation, refresh, () => IsRefreshingFeedback = false);
            }
        }

        private void ApplyFeedbackSnapshot(FeedbackSnapshot snapshot, bool cached)
        {
            FeedbackTickets.Clear();
            foreach (FeedbackTicket ticket in snapshot.Tickets.OrderByDescending(ticket => ticket.UpdatedUtc)
                .ThenByDescending(ticket => ticket.Id).Take(FeedbackService.MaximumTicketCount))
                FeedbackTickets.Add(ticket);
            LastFeedbackRefreshText = DateTime.UtcNow - snapshot.UpdatedUtc < TimeSpan.FromMinutes(1)
                ? "Updated just now" : $"Updated at {snapshot.UpdatedUtc.ToLocalTime():t}";
            FeedbackStatus = cached ? "Showing cached feedback. Refreshing from Developer Community..."
                : snapshot.Tickets.Count == 0 ? "No created or currently upvoted tickets found for this Visual Studio account." : null;
            SetRefreshState(FeedbackRefreshState, FeedbackStatus ?? LastFeedbackRefreshText,
                cached: cached, successfulUtc: new DateTimeOffset(snapshot.UpdatedUtc));
        }
    }
}