using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class DiscoverySectionViewModelTests
    {
        [STATestMethod]
        public void Videos_ConstructingResumingAndResizingTheScreenDoesNotLoadContent()
        {
            using var context = CreateContext();
            var cacheReads = 0;
            var downloads = 0;
            var thumbnails = 0;
            var freshnessChecks = 0;
            using var videos = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedVideosAsync: _ => { cacheReads++; return Task.FromResult(new List<YouTubeVideo>()); },
                downloadAsync: _ => { downloads++; return Task.FromResult(new List<YouTubeVideo>()); },
                loadThumbnailsAsync: (_, _) => { thumbnails++; return Task.CompletedTask; },
                isCacheStale: () => { freshnessChecks++; return true; });
            using var screen = new StartScreenViewModel(CancellationToken.None, videoSection: videos);

            screen.ActivateBackgroundWork();
            videos.YouTubeColumnCount = 2;
            screen.CancelBackgroundWork();
            screen.ActivateBackgroundWork();

            Assert.AreEqual(0, cacheReads);
            Assert.AreEqual(0, downloads);
            Assert.AreEqual(0, thumbnails);
            Assert.AreEqual(0, freshnessChecks);
            Assert.IsEmpty(screen.YouTubeVideos);
            Assert.IsFalse(videos.RefreshState.HasRefreshed);
        }

        [STATestMethod]
        [DataRow(true, false)]
        [DataRow(true, true)]
        [DataRow(false, false)]
        public void VideoTabActivation_ReadsCacheAndDownloadsOnlyWhenMissingOrStale(bool cached, bool stale)
        {
            using var context = CreateContext();
            var options = new Options();
            var cacheReads = 0;
            var downloads = 0;
            var saves = 0;
            var refreshed = Completion<bool>();
            var needsDownload = !cached || stale;
            using var videos = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedVideosAsync: _ =>
                {
                    Interlocked.Increment(ref cacheReads);
                    return Task.FromResult<List<YouTubeVideo>>(cached ? [Video("cached")] : null!);
                }, downloadAsync: _ =>
                {
                    Interlocked.Increment(ref downloads);
                    return Task.FromResult(new List<YouTubeVideo> { Video("fresh") });
                }, loadThumbnailsAsync: (_, _) => Task.CompletedTask, isCacheStale: () => stale,
                getOptionsAsync: () => Task.FromResult(options),
                saveOptionsAsync: _ => { saves++; return Task.CompletedTask; });
            videos.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(videos.IsRefreshingYouTube) && !videos.IsRefreshingYouTube)
                    refreshed.TrySetResult(true);
            };

            context.Factory.Run(async () =>
            {
                await videos.ActivateTabAsync();
                if (needsDownload)
                    await refreshed.Task;
            });

            Assert.AreEqual(1, cacheReads);
            Assert.AreEqual(needsDownload ? 1 : 0, downloads);
            Assert.AreEqual(needsDownload ? "fresh" : "cached", videos.YouTubeVideos.Single().Title);
            Assert.AreEqual(!needsDownload, videos.RefreshState.IsCached);
            Assert.AreEqual(1, saves);
            Assert.AreNotEqual(DateTime.MinValue, options.LastNewsVideosSeen);
            Assert.IsFalse(videos.IsRefreshingYouTube);
        }

        [STATestMethod]
        public void ManualVideoRefresh_LoadsWithoutActivatingTheTabAndBypassesFreshCache()
        {
            using var context = CreateContext();
            var cacheReads = 0;
            var downloads = 0;
            using var videos = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedVideosAsync: _ => { cacheReads++; return Task.FromResult(new List<YouTubeVideo>()); },
                downloadAsync: _ => { downloads++; return Task.FromResult(new List<YouTubeVideo> { Video("manual") }); },
                loadThumbnailsAsync: (_, _) => Task.CompletedTask, isCacheStale: () => false,
                getOptionsAsync: () => Task.FromResult(new Options()));
            using var screen = new StartScreenViewModel(CancellationToken.None, videoSection: videos);
            context.Factory.Run(() => videos.RefreshAsync(force: true));

            Assert.AreEqual(0, cacheReads);
            Assert.AreEqual(1, downloads);
            Assert.AreEqual("manual", screen.YouTubeVideos.Single().Title);
            Assert.IsFalse(videos.RefreshState.IsCached);
        }

        [STATestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CanceledVideoTabActivation_DoesNotPublishOrMarkUnseenContentAsSeen(bool reactivate)
        {
            using var context = CreateContext();
            var cached = Completion<List<YouTubeVideo>>();
            var saves = 0;
            using var videos = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedVideosAsync: _ => cached.Task,
                loadThumbnailsAsync: (_, _) => Task.CompletedTask, isCacheStale: () => false,
                getOptionsAsync: () => Task.FromResult(new Options()),
                saveOptionsAsync: _ => { saves++; return Task.CompletedTask; });

            context.Factory.Run(async () =>
            {
                Task activation = videos.ActivateTabAsync();
                videos.Cancel();
                if (reactivate)
                    videos.Activate();
                cached.SetResult([Video("late")]);
                await activation;
            });

            Assert.AreEqual(0, saves);
            Assert.IsEmpty(videos.YouTubeVideos);
            Assert.IsFalse(videos.RefreshState.HasRefreshed);
        }

        [STATestMethod]
        public void HiddenVideos_PublishCachedMetadataAndCheckFreshnessWithoutThumbnails()
        {
            using var context = CreateContext();
            var thumbnails = 0;
            var freshnessChecks = 0;
            using var section = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedVideosAsync: _ => Task.FromResult(new List<YouTubeVideo> { Video("cached") }),
                loadThumbnailsAsync: (_, _) => { thumbnails++; return Task.CompletedTask; },
                isCacheStale: () => { freshnessChecks++; return false; },
                getOptionsAsync: () => Task.FromResult(new Options()));

            context.Factory.Run(() => section.LoadAsync(default, loadThumbnails: false));
            Assert.AreEqual(0, thumbnails);
            Assert.AreEqual(1, freshnessChecks);
            Assert.AreEqual("cached", section.YouTubeVideos.Single().Title);
            Assert.IsTrue(section.RefreshState.IsCached);
            section.EnsureThumbnailsLoaded();
            Assert.AreEqual(1, thumbnails);
        }

        [STATestMethod]
        public void ThumbnailLoading_IsLimitedToVisibleCardsAndDoesNotGateFreshness()
        {
            using var context = CreateContext();
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var freshnessChecks = 0;
            using var section = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedVideosAsync: _ => Task.FromResult(Enumerable.Range(0, 10).Select(index => Video(index.ToString())).ToList()),
                loadThumbnailsAsync: (videos, _) =>
                {
                    Assert.HasCount(5, videos);
                    Assert.AreEqual(1, freshnessChecks);
                    started.TrySetResult(true);
                    return pending.Task;
                }, isCacheStale: () => { freshnessChecks++; return false; },
                getOptionsAsync: () => Task.FromResult(new Options()));
            context.Factory.Run(async () =>
            {
                Task load = section.LoadAsync(default);
                await started.Task;
                Assert.HasCount(5, section.YouTubeVideos);
                pending.TrySetResult(true);
                await load;
            });
        }
        [STATestMethod]
        public void ExpandingVideosDuringThumbnailLoading_LoadsNewlyVisibleCards()
        {
            using var context = CreateContext();
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var expanded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var batches = new List<int>();
            var image = new BitmapImage();
            image.Freeze();
            using var section = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedVideosAsync: _ => Task.FromResult(Enumerable.Range(0, 10).Select(index => Video(index.ToString())).ToList()),
                loadThumbnailsAsync: (videos, _) =>
                {
                    batches.Add(videos.Count);
                    foreach (var video in videos)
                        video.Thumbnail = image;
                    if (batches.Count == 1)
                        return pending.Task;
                    expanded.TrySetResult(true);
                    return Task.CompletedTask;
                }, isCacheStale: () => false, getOptionsAsync: () => Task.FromResult(new Options()));

            context.Factory.Run(async () =>
            {
                await section.LoadAsync(default, loadThumbnails: false);
                section.EnsureThumbnailsLoaded();
                Assert.HasCount(1, batches);
                section.YouTubeColumnCount = 2;
                pending.TrySetResult(true);
                await expanded.Task;
                CollectionAssert.AreEqual(new[] { 5, 5 }, batches);
                Assert.IsTrue(section.YouTubeVideos.All(video => ReferenceEquals(video.Thumbnail, image)));
            });
        }

        [TestMethod]
        public async Task NewsSection_OwnsStableInventoryPinsLimitsAndSeenStateAsync()
        {
            var options = new Options { LastNewsBlogsSeen = DateTime.UtcNow.AddDays(-1) };
            var saves = 0;
            using var section = new NewsSectionViewModel(getOptionsAsync: () => Task.FromResult(options),
                saveOptionsAsync: _ => { saves++; return Task.CompletedTask; });
            var posts = Enumerable.Range(0, 25).Select(index => Post(index.ToString())).ToArray();
            var visible = section.NewsPosts;
            var pins = section.PinnedNewsPosts;

            section.UpdateNewsPosts(posts, options);
            Assert.HasCount(20, visible);
            Assert.IsTrue(section.HasNewBlogs);
            section.NewsColumnCount = 1;
            Assert.HasCount(10, visible);

            await section.ToggleNewsPinAsync(posts[24]);

            Assert.AreSame(posts[24], pins.Single());
            Assert.AreEqual(posts[24].Url, options.PinnedArticles);
            section.UpdateNewsPosts(posts.Select(post => Post(post.Title)).ToArray(), options);
            Assert.AreSame(posts[0], visible[0]);
            Assert.AreSame(posts[24], pins.Single());
            Assert.AreSame(visible, section.NewsPosts);
            Assert.AreSame(pins, section.PinnedNewsPosts);

            await section.MarkBlogsAsSeenAsync();

            Assert.IsFalse(section.HasNewBlogs);
            Assert.IsTrue(options.LastNewsBlogsSeen > DateTime.UtcNow.AddMinutes(-1));
            Assert.AreEqual(2, saves);
        }

        [TestMethod]
        public async Task VideoSection_OwnsStableInventoryLimitsAndSeenStateAsync()
        {
            var options = new Options { LastNewsVideosSeen = DateTime.UtcNow.AddDays(-1) };
            var saves = 0;
            using var section = new VideoSectionViewModel(getOptionsAsync: () => Task.FromResult(options),
                saveOptionsAsync: _ => { saves++; return Task.CompletedTask; });
            var videos = Enumerable.Range(0, 12).Select(index => Video(index.ToString())).ToArray();
            var visible = section.YouTubeVideos;

            section.UpdateYouTubeVideos(videos, options);
            Assert.HasCount(5, visible);
            Assert.IsTrue(section.HasNewVideos);
            section.YouTubeColumnCount = 3;
            section.UpdateYouTubeVideos(videos.Select(video => Video(video.Title)).ToArray(), options);

            CollectionAssert.AreEqual(videos, visible.ToArray());
            Assert.AreSame(visible, section.YouTubeVideos);
            await section.MarkVideosAsSeenAsync();
            Assert.IsFalse(section.HasNewVideos);
            Assert.IsTrue(options.LastNewsVideosSeen > DateTime.UtcNow.AddMinutes(-1));
            Assert.AreEqual(1, saves);
        }

        [TestMethod]
        public void FirstLoad_SectionsSuppressNewIndicatorsIndependently()
        {
            using var news = new NewsSectionViewModel();
            using var videos = new VideoSectionViewModel();
            var options = new Options();

            news.UpdateNewsPosts([Post("news")], options);
            videos.UpdateYouTubeVideos([Video("video")], options);

            Assert.IsFalse(news.HasNewBlogs);
            Assert.IsFalse(videos.HasNewVideos);
            options.LastNewsBlogsSeen = DateTime.UtcNow.AddDays(-1);
            news.UpdateNewsPosts([Post("news")], options);
            Assert.IsTrue(news.HasNewBlogs);
            Assert.IsFalse(videos.HasNewVideos);
        }

        [TestMethod]
        public void Screen_ForwardsSectionBindingsAndUnifiedStatusWithoutDuplicatingState()
        {
            using var news = new NewsSectionViewModel();
            using var videos = new VideoSectionViewModel();
            using var screen = new StartScreenViewModel(CancellationToken.None, newsSection: news, videoSection: videos);
            var changed = new List<string?>();
            screen.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            news.IsRefreshingNews = true;

            Assert.IsTrue(screen.IsRefreshingNews);
            Assert.IsTrue(screen.IsRefreshingAny);
            Assert.AreEqual("Refreshing news...", screen.UnifiedRefreshStatus);
            CollectionAssert.Contains(changed, nameof(screen.IsRefreshingNews));
            CollectionAssert.Contains(changed, nameof(screen.IsRefreshingAny));
            CollectionAssert.Contains(changed, nameof(screen.UnifiedRefreshStatus));
            CollectionAssert.Contains(changed, nameof(screen.UnifiedRefreshDetails));
            changed.Clear();
            screen.IsRefreshingNews = true;
            Assert.IsEmpty(changed);

            screen.IsRefreshingNews = false;
            news.UpdateNewsRefreshStatus(new FeedRefreshResult([Post("news")], 1, [], []));
            Assert.AreSame(news.RefreshState, screen.NewsRefreshState);
            Assert.AreSame(videos.RefreshState, screen.VideoRefreshState);
            Assert.AreSame(news.NewsPosts, screen.NewsPosts);
            Assert.AreSame(news.PinnedNewsPosts, screen.PinnedNewsPosts);
            Assert.AreSame(videos.YouTubeVideos, screen.YouTubeVideos);
            Assert.AreEqual("Updated just now", screen.UnifiedRefreshStatus);
            videos.LastYouTubeRefreshText = "Independent video status";
            CollectionAssert.Contains(changed, nameof(screen.LastYouTubeRefreshText));
            StringAssert.Contains(screen.UnifiedRefreshDetails, "Videos: Independent video status");
            Assert.AreEqual("Updated just now", screen.LastNewsRefreshText);

            screen.UpdateColumnCounts(1, 2);
            Assert.AreEqual(1, news.NewsColumnCount);
            Assert.AreEqual(2, videos.YouTubeColumnCount);
            CollectionAssert.Contains(changed, nameof(screen.NewsColumnCount));
            CollectionAssert.Contains(changed, nameof(screen.YouTubeColumnCount));
        }

        [STATestMethod]
        public void EmptyFreshCaches_DoNotDownload()
        {
            using var context = CreateContext();
            var downloads = 0;
            using var news = new NewsSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedPostsAsync: _ => Task.FromResult(new List<NewsPost>()),
                downloadAsync: (_, _) => { downloads++; return Task.FromResult(new FeedRefreshResult([], 1, [], [])); },
                isCacheStale: () => false, getOptionsAsync: () => Task.FromResult(new Options()));
            using var videos = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedVideosAsync: _ => Task.FromResult(new List<YouTubeVideo>()),
                downloadAsync: _ => { downloads++; return Task.FromResult(new List<YouTubeVideo>()); },
                loadThumbnailsAsync: (_, _) => Task.CompletedTask, isCacheStale: () => false,
                getOptionsAsync: () => Task.FromResult(new Options()));
            context.Factory.Run(() => Task.WhenAll(news.LoadAsync(default), videos.LoadAsync(default)));
            Assert.AreEqual(0, downloads);
            Assert.IsTrue(news.RefreshState.IsCached);
            Assert.IsTrue(videos.RefreshState.IsCached);
        }

        [STATestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void VideoCards_ArePublishedBeforeThumbnails(bool cached)
        {
            using var context = CreateContext();
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var items = new List<YouTubeVideo> { Video("visible") };
            using var section = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedVideosAsync: _ => Task.FromResult(items), downloadAsync: _ => Task.FromResult(items),
                loadThumbnailsAsync: (_, _) => { started.SetResult(true); return pending.Task; }, isCacheStale: () => false,
                getOptionsAsync: () => Task.FromResult(new Options()));
            context.Factory.Run(async () =>
            {
                Task load = cached ? section.LoadAsync(default) : section.RefreshAsync();
                await started.Task;
                await context.Factory.SwitchToMainThreadAsync();
                Assert.AreSame(items[0], section.YouTubeVideos.Single());
                Assert.IsFalse(load.IsCompleted);
                pending.SetResult(true);
                await load;
            });
        }

        [STATestMethod]
        public void Sections_LoadFreshCachesWithoutRemoteRequests()
        {
            using var context = CreateContext();
            var options = new Options();
            var requests = 0;
            var thumbnails = 0;
            using var news = new NewsSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedPostsAsync: _ => Task.FromResult(new List<NewsPost> { Post("cached") }),
                downloadAsync: (_, _) => { requests++; return Task.FromResult(new FeedRefreshResult([], 0, [], [])); },
                isCacheStale: () => false, getOptionsAsync: () => Task.FromResult(options));
            using var videos = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedVideosAsync: _ => Task.FromResult(new List<YouTubeVideo> { Video("cached") }),
                downloadAsync: _ => { requests++; return Task.FromResult(new List<YouTubeVideo>()); },
                loadThumbnailsAsync: (_, _) => { thumbnails++; return Task.CompletedTask; },
                isCacheStale: () => false, getOptionsAsync: () => Task.FromResult(options));

            context.Factory.Run(() => Task.WhenAll(news.LoadAsync(default), videos.LoadAsync(default)));

            Assert.AreEqual(0, requests);
            Assert.AreEqual(1, thumbnails);
            Assert.AreEqual("cached", news.NewsPosts.Single().Title);
            Assert.AreEqual("cached", videos.YouTubeVideos.Single().Title);
            Assert.IsTrue(news.RefreshState.IsCached);
            Assert.IsTrue(videos.RefreshState.IsCached);
            Assert.IsFalse(news.RefreshState.IsStale);
            Assert.IsFalse(videos.RefreshState.IsStale);
            Assert.IsNull(news.RefreshState.LastSuccessfulRefreshUtc);
            Assert.IsNull(videos.RefreshState.LastSuccessfulRefreshUtc);
        }

        [STATestMethod]
        [DataRow(true, false)]
        [DataRow(false, false)]
        [DataRow(true, true)]
        [DataRow(false, true)]
        public void CancelingOrDisposingOneSection_DoesNotCancelTheOther(bool cancelNews, bool dispose)
        {
            using var context = CreateContext();
            var options = new Options();
            var newsStarted = Completion<CancellationToken>();
            var videosStarted = Completion<CancellationToken>();
            var newsResult = Completion<FeedRefreshResult>();
            var videosResult = Completion<List<YouTubeVideo>>();
            using var news = new NewsSectionViewModel(getTaskFactory: () => context.Factory,
                downloadAsync: (_, token) => { newsStarted.SetResult(token); return newsResult.Task; },
                getOptionsAsync: () => Task.FromResult(options));
            using var videos = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                downloadAsync: token => { videosStarted.SetResult(token); return videosResult.Task; },
                loadThumbnailsAsync: (_, _) => Task.CompletedTask, getOptionsAsync: () => Task.FromResult(options));
            using var screen = new StartScreenViewModel(CancellationToken.None, newsSection: news, videoSection: videos);

            context.Factory.Run(async () =>
            {
                var newsRefresh = context.Factory.RunAsync(() => news.RefreshAsync(true));
                var videosRefresh = context.Factory.RunAsync(() => videos.RefreshAsync());
                CancellationToken newsToken = await newsStarted.Task;
                CancellationToken videoToken = await videosStarted.Task;
                if (cancelNews)
                {
                    if (dispose)
                        news.Dispose();
                    else
                        news.Cancel();
                }
                else
                {
                    if (dispose)
                        videos.Dispose();
                    else
                        videos.Cancel();
                }

                Assert.AreEqual(cancelNews, newsToken.IsCancellationRequested);
                Assert.AreEqual(!cancelNews, videoToken.IsCancellationRequested);
                Assert.AreEqual(!cancelNews, screen.IsRefreshingNews);
                Assert.AreEqual(cancelNews, screen.IsRefreshingYouTube);
                Assert.IsTrue(screen.IsRefreshingAny);
                newsResult.SetResult(new FeedRefreshResult([Post("remote")], 1, [], []));
                videosResult.SetResult([Video("remote")]);
                await newsRefresh;
                await videosRefresh;
            });

            Assert.HasCount(cancelNews ? 0 : 1, screen.NewsPosts);
            Assert.HasCount(cancelNews ? 1 : 0, screen.YouTubeVideos);
            Assert.IsFalse(screen.IsRefreshingAny);
        }

        [STATestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void FailedRemoteRefresh_RetainsInventoryAndSuccessfulTimestamp(bool hasContent)
        {
            using var context = CreateContext();
            var options = new Options();
            using var news = new NewsSectionViewModel(getTaskFactory: () => context.Factory,
                downloadAsync: (_, _) => Task.FromResult(new FeedRefreshResult([], 1, ["Test source"], [])),
                getOptionsAsync: () => Task.FromResult(options));
            using var videos = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                downloadAsync: _ => Task.FromResult<List<YouTubeVideo>>(null!),
                loadThumbnailsAsync: (_, _) => Task.CompletedTask, getOptionsAsync: () => Task.FromResult(options));
            var post = Post("retained");
            var video = Video("retained");
            if (hasContent)
            {
                news.UpdateNewsPosts([post], options);
                videos.UpdateYouTubeVideos([video], options);
            }
            var successful = DateTimeOffset.UtcNow.AddDays(-1);
            news.RefreshState.LastSuccessfulRefreshUtc = successful;
            videos.RefreshState.LastSuccessfulRefreshUtc = successful;

            context.Factory.Run(() => Task.WhenAll(news.RefreshAsync(true), videos.RefreshAsync()));

            Assert.AreEqual(!hasContent, news.IsNewsUnavailable);
            Assert.AreEqual(!hasContent, videos.IsYouTubeUnavailable);
            Assert.AreEqual(hasContent, news.RefreshState.IsCached);
            Assert.AreEqual(hasContent, videos.RefreshState.IsCached);
            Assert.IsTrue(news.RefreshState.IsStale);
            Assert.IsTrue(videos.RefreshState.IsStale);
            Assert.AreEqual(successful, news.RefreshState.LastSuccessfulRefreshUtc);
            Assert.AreEqual(successful, videos.RefreshState.LastSuccessfulRefreshUtc);
            Assert.IsFalse(news.RefreshState.IsLoading);
            Assert.IsFalse(videos.RefreshState.IsLoading);
            if (hasContent)
            {
                Assert.AreSame(post, news.NewsPosts.Single());
                Assert.AreSame(video, videos.YouTubeVideos.Single());
            }
            else
            {
                Assert.IsEmpty(news.NewsPosts);
                Assert.IsEmpty(videos.YouTubeVideos);
            }
        }

        [STATestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ScreenCancelOrDispose_PreventsLateCachePublication(bool dispose)
        {
            using var context = CreateContext();
            var options = new Options();
            var newsResult = Completion<List<NewsPost>>();
            var videosResult = Completion<List<YouTubeVideo>>();
            CancellationToken newsToken = default;
            CancellationToken videoToken = default;
            using var news = new NewsSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedPostsAsync: token => { newsToken = token; return newsResult.Task; },
                getOptionsAsync: () => Task.FromResult(options));
            using var videos = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                getCachedVideosAsync: token => { videoToken = token; return videosResult.Task; },
                loadThumbnailsAsync: (_, _) => Task.CompletedTask, getOptionsAsync: () => Task.FromResult(options));
            using var screen = new StartScreenViewModel(CancellationToken.None, newsSection: news, videoSection: videos);
            var changes = new List<string?>();
            screen.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

            context.Factory.Run(async () =>
            {
                Task newsLoad = news.LoadAsync(default);
                Task videoLoad = videos.LoadAsync(default);
                if (dispose)
                    screen.Dispose();
                else
                    screen.CancelBackgroundWork();
                Assert.IsTrue(newsToken.IsCancellationRequested);
                Assert.IsTrue(videoToken.IsCancellationRequested);
                changes.Clear();
                newsResult.SetResult([Post("late")]);
                videosResult.SetResult([Video("late")]);
                await Task.WhenAll(newsLoad, videoLoad);
            });

            Assert.IsEmpty(screen.NewsPosts);
            Assert.IsEmpty(screen.YouTubeVideos);
            Assert.IsEmpty(changes);
            Assert.IsFalse(news.RefreshState.HasRefreshed);
            Assert.IsFalse(videos.RefreshState.HasRefreshed);
            Assert.IsFalse(screen.IsRefreshingAny);
        }

        [STATestMethod]
        public void Reactivation_OldRemoteCompletionCannotStopOrOverwriteNewRefresh()
        {
            using var context = CreateContext();
            var options = new Options();
            var oldNews = Completion<FeedRefreshResult>();
            var oldVideos = Completion<List<YouTubeVideo>>();
            var newNews = Completion<FeedRefreshResult>();
            var newVideos = Completion<List<YouTubeVideo>>();
            var oldNewsStarted = Completion<bool>();
            var oldVideosStarted = Completion<bool>();
            var newNewsStarted = Completion<bool>();
            var newVideosStarted = Completion<bool>();
            var newsRequests = 0;
            var videoRequests = 0;
            using var news = new NewsSectionViewModel(getTaskFactory: () => context.Factory,
                downloadAsync: (_, _) =>
                {
                    var first = Interlocked.Increment(ref newsRequests) == 1;
                    (first ? oldNewsStarted : newNewsStarted).SetResult(true);
                    return first ? oldNews.Task : newNews.Task;
                }, getOptionsAsync: () => Task.FromResult(options));
            using var videos = new VideoSectionViewModel(getTaskFactory: () => context.Factory,
                downloadAsync: _ =>
                {
                    var first = Interlocked.Increment(ref videoRequests) == 1;
                    (first ? oldVideosStarted : newVideosStarted).SetResult(true);
                    return first ? oldVideos.Task : newVideos.Task;
                }, loadThumbnailsAsync: (_, _) => Task.CompletedTask, getOptionsAsync: () => Task.FromResult(options));
            using var screen = new StartScreenViewModel(CancellationToken.None, newsSection: news, videoSection: videos);

            context.Factory.Run(async () =>
            {
                var firstNews = context.Factory.RunAsync(() => news.RefreshAsync(false));
                var firstVideos = context.Factory.RunAsync(() => videos.RefreshAsync());
                await Task.WhenAll(oldNewsStarted.Task, oldVideosStarted.Task);
                screen.CancelBackgroundWork();
                screen.ActivateBackgroundWork();
                var secondNews = context.Factory.RunAsync(() => news.RefreshAsync(true));
                var secondVideos = context.Factory.RunAsync(() => videos.RefreshAsync());
                await Task.WhenAll(newNewsStarted.Task, newVideosStarted.Task);
                oldNews.SetResult(new FeedRefreshResult([Post("old")], 1, [], []));
                oldVideos.SetResult([Video("old")]);
                await firstNews;
                await firstVideos;

                Assert.IsTrue(screen.IsRefreshingNews);
                Assert.IsTrue(screen.IsRefreshingYouTube);
                Assert.IsEmpty(screen.NewsPosts);
                Assert.IsEmpty(screen.YouTubeVideos);
                newNews.SetResult(new FeedRefreshResult([Post("new")], 1, [], []));
                newVideos.SetResult([Video("new")]);
                await secondNews;
                await secondVideos;
            });

            Assert.AreEqual("new", screen.NewsPosts.Single().Title);
            Assert.AreEqual("new", screen.YouTubeVideos.Single().Title);
            Assert.IsFalse(screen.IsRefreshingAny);
            Assert.IsNotNull(news.RefreshState.LastSuccessfulRefreshUtc);
            Assert.IsNotNull(videos.RefreshState.LastSuccessfulRefreshUtc);
        }

        [TestMethod]
        public void Dispose_DetachesScreenForwardingAndCannotBeReactivated()
        {
            var news = new NewsSectionViewModel();
            var videos = new VideoSectionViewModel();
            using var screen = new StartScreenViewModel(CancellationToken.None, newsSection: news, videoSection: videos);
            screen.Dispose();
            var changed = new List<string?>();
            screen.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            screen.ActivateBackgroundWork();
            news.Activate();
            videos.Activate();
            news.IsRefreshingNews = true;
            videos.IsRefreshingYouTube = true;
            screen.ForceRefreshNews();
            screen.ForceRefreshYouTube();
            news.UpdateNewsPosts([Post("after dispose")], new Options());
            videos.UpdateYouTubeVideos([Video("after dispose")], new Options());

            Assert.IsEmpty(changed);
            Assert.IsFalse(screen.IsRefreshingAny);
            Assert.IsEmpty(news.NewsPosts);
            Assert.IsEmpty(videos.YouTubeVideos);
        }

        private static NewsPost Post(string id) => new NewsPost
        {
            Url = "https://example.com/" + id,
            Title = id,
            PublishDate = DateTime.UtcNow.AddHours(-1),
        };

        private static YouTubeVideo Video(string id) => new YouTubeVideo
        {
            Url = "https://example.com/" + id,
            Title = id,
            PublishDate = DateTime.UtcNow.AddHours(-1),
        };

        private static TaskCompletionSource<T> Completion<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static JoinableTaskContext CreateContext() => new JoinableTaskContext(Thread.CurrentThread,
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
    }
}