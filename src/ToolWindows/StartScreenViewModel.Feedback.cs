using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenViewModel
    {
        private readonly Func<bool, CancellationToken, Task<FeedbackSnapshot>> _getFeedbackAsync;
        private readonly Func<JoinableTaskFactory> _getFeedbackTaskFactory;
        private CancellationTokenSource _feedbackRefreshCancellation;
        private bool _feedbackRequested;
        private bool _isRefreshingFeedback;
        private string _lastFeedbackRefreshText;
        private string _feedbackStatus = "Select Feedback to load your Developer Community tickets.";

        public ObservableCollection<FeedbackTicket> FeedbackTickets { get; } = new ObservableCollection<FeedbackTicket>();

        public bool IsRefreshingFeedback
        {
            get => _isRefreshingFeedback;
            private set
            {
                if (_isRefreshingFeedback != value)
                {
                    _isRefreshingFeedback = value;
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

        public void EnsureFeedbackLoaded()
        {
            if (_feedbackRequested)
                return;
            _feedbackRequested = true;
            StartFeedbackRefresh(false);
        }

        public void ForceRefreshFeedback()
        {
            if (_feedbackRequested)
                StartFeedbackRefresh(true);
        }

        internal void ReportFeedbackNavigationFailure()
        {
            FeedbackStatus = "The ticket could not be opened in your browser. Try the link again.";
        }

        private void StartFeedbackRefresh(bool force)
        {
            if (GetBackgroundCancellationToken().IsCancellationRequested || IsRefreshingFeedback)
                return;
            _getFeedbackTaskFactory().RunAsync(() => RefreshFeedbackAsync(force))
                .Task.Forget();
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
            try
            {
                FeedbackSnapshot snapshot = await _getFeedbackAsync(force, token);
                await factory.SwitchToMainThreadAsync(token);
                token.ThrowIfCancellationRequested();
                foreach (FeedbackTicket ticket in snapshot.Tickets)
                    FeedbackTickets.Add(ticket);
                LastFeedbackRefreshText = DateTime.UtcNow - snapshot.UpdatedUtc < TimeSpan.FromMinutes(1)
                    ? "Updated just now" : $"Updated at {snapshot.UpdatedUtc.ToLocalTime():t}";
                FeedbackStatus = snapshot.Tickets.Count == 0
                    ? "No created or currently upvoted tickets found for this Visual Studio account."
                    : null;
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
                    LastFeedbackRefreshText = "Feedback unavailable";
                    FeedbackStatus = ex is UnauthorizedAccessException || ex is CredentialUnavailableException || ex is AuthenticationFailedException
                        ? "Sign in or reauthenticate in Visual Studio, select your account under Tools > Options > Azure Service Authentication, then use Refresh."
                        : "Developer Community is unavailable. Use Refresh to retry.";
                }
            }
            finally
            {
                await factory.SwitchToMainThreadAsync();
                CompleteRefresh(ref _feedbackRefreshCancellation, refresh, () => IsRefreshingFeedback = false);
            }
        }
    }
}