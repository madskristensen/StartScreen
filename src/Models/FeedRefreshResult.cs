using System.Collections.Generic;

namespace StartScreen.Models
{
    public sealed class FeedRefreshResult
    {
        internal FeedRefreshResult(List<NewsPost> posts, int feedCount,
            IReadOnlyList<string> failedFeeds, IReadOnlyList<string> cachedFeeds)
        {
            Posts = posts;
            FeedCount = feedCount;
            FailedFeeds = failedFeeds;
            CachedFeeds = cachedFeeds;
        }

        public List<NewsPost> Posts { get; }
        public int FeedCount { get; }
        public IReadOnlyList<string> FailedFeeds { get; }
        public IReadOnlyList<string> CachedFeeds { get; }
        public bool Success => FailedFeeds.Count == 0;
    }
}