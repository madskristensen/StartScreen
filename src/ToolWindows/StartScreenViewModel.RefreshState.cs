using System.Linq;
using StartScreen.Models;
using StartScreen.Models.DevHub;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenViewModel
    {
        internal SectionRefreshState NewsRefreshState { get; } = new SectionRefreshState();
        internal SectionRefreshState VideoRefreshState { get; } = new SectionRefreshState();
        internal SectionRefreshState FeedbackRefreshState { get; } = new SectionRefreshState();
        internal SectionRefreshState DevHubRefreshState { get; } = new SectionRefreshState();
        internal SectionRefreshState WorkspaceRefreshState { get; } = new SectionRefreshState();

        private SectionRefreshState[] RefreshStates =>
            [DevHubRefreshState, NewsRefreshState, VideoRefreshState, FeedbackRefreshState, WorkspaceRefreshState];

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
                : partial ? "Some activity sources could not be refreshed. See activity details."
                : cached ? "Showing cached activity" : "Activity updated");
            SetRefreshState(DevHubRefreshState, message, cached,
                stale: unavailable || partial, partial: partial, unavailable: unavailable,
                successfulUtc: !cached && !unavailable && !partial && dashboard?.IsRefreshing == false
                    && dashboard.FetchedAt != default ? new DateTimeOffset(dashboard.FetchedAt) : null);
        }

        private string GetUnifiedRefreshStatus()
        {
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