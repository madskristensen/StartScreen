namespace StartScreen.Models
{
    internal sealed class SectionRefreshState
    {
        internal bool IsLoading { get; set; }
        internal bool IsCached { get; set; }
        internal bool IsStale { get; set; }
        internal bool IsPartial { get; set; }
        internal bool IsUnavailable { get; set; }
        internal bool HasRefreshed { get; set; }
        internal DateTimeOffset? LastSuccessfulRefreshUtc { get; set; }
        internal string Message { get; set; }
    }
}