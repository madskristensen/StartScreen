using System.Collections.Generic;
using System.Linq;
using StartScreen.Models;
using StartScreen.Models.DevHub;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenViewModel
    {
        internal BackgroundProgress BackgroundProgress { get; } = new BackgroundProgress();
        private readonly Dictionary<string, BackgroundProgress.Operation> _sectionProgress = new Dictionary<string, BackgroundProgress.Operation>();

        public bool IsRefreshProgressActive => BackgroundProgress.IsBusy;
        public double RefreshProgressValue => BackgroundProgress.PercentComplete;
        public string RefreshProgressDetails => BackgroundProgress.Details;

        private void BackgroundProgress_Changed(object sender, EventArgs e)
        {
            OnPropertyChanged(nameof(IsRefreshProgressActive));
            OnPropertyChanged(nameof(RefreshProgressValue));
            OnPropertyChanged(nameof(RefreshProgressDetails));
            OnPropertyChanged(nameof(UnifiedRefreshStatus));
            OnPropertyChanged(nameof(UnifiedRefreshDetails));
        }

        private void UpdateSectionProgress()
        {
            SetSectionProgress("News", IsRefreshingNews);
            SetSectionProgress("Videos", IsRefreshingYouTube);
            SetSectionProgress("Events", IsRefreshingEvents);
            SetSectionProgress("Feedback", IsRefreshingFeedback);
            SetSectionProgress("Workspace", _isRefreshingWorkspaceSignals || WorkspaceRefreshState.IsLoading);
        }

        private void SetSectionProgress(string section, bool loading)
        {
            if (loading && !_sectionProgress.ContainsKey(section))
                _sectionProgress[section] = BackgroundProgress.Start(step: $"Refreshing {section.ToLowerInvariant()}...");
            else if (!loading && _sectionProgress.TryGetValue(section, out var operation))
            {
                _sectionProgress.Remove(section);
                operation.Dispose();
            }
        }

        internal SectionRefreshState NewsRefreshState => NewsSection.RefreshState;
        internal SectionRefreshState VideoRefreshState => VideoSection.RefreshState;
        internal SectionRefreshState FeedbackRefreshState { get; } = new SectionRefreshState();
        internal SectionRefreshState DevHubRefreshState { get; } = new SectionRefreshState();
        internal SectionRefreshState WorkspaceRefreshState { get; } = new SectionRefreshState();

        private SectionRefreshState[] RefreshStates =>
            [DevHubRefreshState, NewsRefreshState, VideoRefreshState, EventSection.RefreshState, FeedbackRefreshState, WorkspaceRefreshState];

        internal void SetRefreshState(SectionRefreshState state, string message,
            bool cached = false, bool stale = false, bool partial = false, bool unavailable = false,
            DateTimeOffset? successfulUtc = null)
        {
            if (_isDisposed)
                return;
            state.HasRefreshed = true;
            state.IsCached = cached;
            state.IsStale = stale;
            state.IsPartial = partial;
            state.IsUnavailable = unavailable;
            state.Message = message;
            if (successfulUtc.HasValue)
                state.LastSuccessfulRefreshUtc = successfulUtc;
            string section = ReferenceEquals(state, FeedbackRefreshState) ? "Feedback"
                : ReferenceEquals(state, DevHubRefreshState) ? "Activity" : "Service status";
            OutputLog.Refresh(section, message, state);
            NotifyRefreshStatusChanged();
        }

        internal void SetDevHubRefreshState(DevHubDashboard dashboard, bool cached = false, string error = null)
        {
            if (_isDisposed)
                return;
            if (dashboard == null && error == null)
                return;
            bool unavailable = error != null || dashboard.Status == DevHubActivityStatus.Unavailable
                || dashboard.Status == DevHubActivityStatus.Unauthorized;
            bool partial = dashboard?.Status == DevHubActivityStatus.Partial;
            DevHubRefreshState.IsLoading = IsRefreshingDevHub || dashboard?.IsRefreshing == true;
            string message = error ?? (dashboard?.IsRefreshing == true ? "Refreshing activity..."
                : unavailable ? "Activity unavailable. Refresh to retry."
                : partial ? "Some activity sources could not be refreshed. See the Start Screen Output pane."
                : cached ? "Showing cached activity" : "Activity updated");
            SetRefreshState(DevHubRefreshState, message, cached,
                stale: unavailable || partial, partial: partial, unavailable: unavailable,
                successfulUtc: !cached && !unavailable && !partial && dashboard?.IsRefreshing == false
                    && dashboard.FetchedAt != default ? new DateTimeOffset(dashboard.FetchedAt) : null);
        }

        private string GetUnifiedRefreshStatus()
        {
            var step = BackgroundProgress.CurrentStep;
            if (step != null)
                return step;
            if (IsRefreshingAny)
                return "Updating...";
            DateTimeOffset? latest = RefreshStates.Max(state => state.LastSuccessfulRefreshUtc);
            if (latest.HasValue)
                return DateTimeOffset.UtcNow - latest.Value < TimeSpan.FromMinutes(1)
                    ? "Updated just now" : $"Updated at {latest.Value.ToLocalTime():t}";
            return RefreshStates.Any(state => state.HasRefreshed) ? "Not updated yet" : string.Empty;
        }
    }
}