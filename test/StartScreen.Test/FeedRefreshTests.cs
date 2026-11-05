using System.Collections.Specialized;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StartScreen.Models;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class FeedRefreshTests
    {
        [TestMethod]
        public void News_IdenticalRefreshRetainsPinnedAndUnpinnedCardsWithoutNotifications()
        {
            var model = new StartScreenViewModel();
            var options = new Options { PinnedArticles = "https://example.com/pinned" };
            var post = Post("post");
            var pinned = Post("pinned");
            model.UpdateNewsPosts([post, pinned], options);
            var news = model.NewsPosts;
            var pins = model.PinnedNewsPosts;
            var changes = new List<string?>();
            model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            post.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            pinned.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            news.CollectionChanged += (_, e) => changes.Add(e.Action.ToString());
            pins.CollectionChanged += (_, e) => changes.Add(e.Action.ToString());

            model.UpdateNewsPosts([Post("post"), Post("pinned")], options);

            Assert.AreSame(news, model.NewsPosts);
            Assert.AreSame(pins, model.PinnedNewsPosts);
            Assert.AreSame(post, model.NewsPosts.Single());
            Assert.AreSame(pinned, model.PinnedNewsPosts.Single());
            Assert.IsTrue(pinned.IsPinned);
            Assert.IsEmpty(changes);
        }

        [TestMethod]
        public void News_ChangedContentUpdatesExistingCardWithoutReplacingCollections()
        {
            var model = new StartScreenViewModel();
            var options = new Options();
            var post = Post("post");
            model.UpdateNewsPosts([post], options);
            var collectionChanges = new List<NotifyCollectionChangedAction>();
            model.NewsPosts.CollectionChanged += (_, e) => collectionChanges.Add(e.Action);
            var updated = Post("post");
            updated.Title = "Revised title";
            updated.Summary = "Revised summary";
            updated.PublishDate = updated.PublishDate.AddDays(1);
            updated.Source = "Revised source";
            updated.ToolTip = "Revised tooltip";
            updated.HasDescription = false;

            model.UpdateNewsPosts([updated], options);

            Assert.AreSame(post, model.NewsPosts.Single());
            Assert.AreEqual(updated.Title, post.Title);
            Assert.AreEqual(updated.Summary, post.Summary);
            Assert.AreEqual(updated.PublishDate, post.PublishDate);
            Assert.AreEqual(updated.Source, post.Source);
            Assert.AreEqual(updated.ToolTip, post.ToolTip);
            Assert.IsFalse(post.HasDescription);
            Assert.IsEmpty(collectionChanges);
        }

        [TestMethod]
        public void News_ChangesMembershipOrderAndPinsWithoutResettingRetainedCards()
        {
            var model = new StartScreenViewModel();
            var options = new Options();
            var a = Post("a");
            var b = Post("b");
            var c = Post("c");
            model.UpdateNewsPosts([a, b, c], options);
            var actions = new List<NotifyCollectionChangedAction>();
            model.NewsPosts.CollectionChanged += (_, e) => actions.Add(e.Action);
            model.PinnedNewsPosts.CollectionChanged += (_, e) => actions.Add(e.Action);
            var d = Post("d");

            model.UpdateNewsPosts([Post("c"), d, Post("a")], options);
            CollectionAssert.AreEqual(new[] { c, d, a }, model.NewsPosts.ToArray());

            options.PinnedArticles = a.Url;
            model.UpdateNewsPosts([Post("c"), Post("d"), Post("a")], options);
            CollectionAssert.AreEqual(new[] { c, d }, model.NewsPosts.ToArray());
            Assert.AreSame(a, model.PinnedNewsPosts.Single());

            options.PinnedArticles = string.Empty;
            model.UpdateNewsPosts([Post("c"), Post("d"), Post("a")], options);
            CollectionAssert.AreEqual(new[] { c, d, a }, model.NewsPosts.ToArray());
            Assert.IsEmpty(model.PinnedNewsPosts);
            CollectionAssert.DoesNotContain(actions, NotifyCollectionChangedAction.Reset);
            CollectionAssert.DoesNotContain(actions, NotifyCollectionChangedAction.Replace);

            model.UpdateNewsPosts([], options);
            Assert.IsEmpty(model.NewsPosts);
        }

        [TestMethod]
        public void News_ColumnChangesKeepRetainedCardsAndRespectDisplayLimits()
        {
            var model = new StartScreenViewModel();
            var posts = Enumerable.Range(0, 25).Select(index => Post(index.ToString())).ToArray();
            model.UpdateNewsPosts(posts, new Options());
            Assert.HasCount(20, model.NewsPosts);
            var collection = model.NewsPosts;

            model.UpdateColumnCounts(1, 1);
            CollectionAssert.AreEqual(posts.Take(10).ToArray(), model.NewsPosts.ToArray());
            model.UpdateColumnCounts(3, 1);

            Assert.AreSame(collection, model.NewsPosts);
            CollectionAssert.AreEqual(posts, model.NewsPosts.ToArray());
        }

        [STATestMethod]
        public void Videos_IdenticalRefreshRetainsCardAndThumbnailWithoutNotifications()
        {
            var model = new StartScreenViewModel();
            var options = new Options();
            var video = Video("video");
            model.UpdateYouTubeVideos([video], options);
            var collection = model.YouTubeVideos;
            var thumbnail = video.Thumbnail;
            var changes = new List<string?>();
            model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            video.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            collection.CollectionChanged += (_, e) => changes.Add(e.Action.ToString());

            model.UpdateYouTubeVideos([Video("video")], options);

            Assert.AreSame(collection, model.YouTubeVideos);
            Assert.AreSame(video, model.YouTubeVideos.Single());
            Assert.AreSame(thumbnail, video.Thumbnail);
            Assert.IsEmpty(changes);
        }

        [STATestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void Videos_UpdateChangedContentAndOnlyReplaceThumbnailsWhenNeeded(bool changedUrl, bool missingThumbnail)
        {
            var model = new StartScreenViewModel();
            var options = new Options();
            var video = Video("video");
            if (missingThumbnail)
                video.Thumbnail = null;
            var previousThumbnail = video.Thumbnail;
            model.UpdateYouTubeVideos([video], options);
            var updated = Video("video");
            updated.Title = "Revised title";
            updated.PublishDate = updated.PublishDate.AddDays(1);
            if (changedUrl)
                updated.ThumbnailUrl = "https://example.com/revised.jpg";
            var collectionChanges = new List<NotifyCollectionChangedAction>();
            model.YouTubeVideos.CollectionChanged += (_, e) => collectionChanges.Add(e.Action);

            model.UpdateYouTubeVideos([updated], options);

            Assert.AreSame(video, model.YouTubeVideos.Single());
            Assert.AreEqual("Revised title", video.Title);
            Assert.AreEqual(updated.PublishDate, video.PublishDate);
            Assert.AreEqual(updated.ThumbnailUrl, video.ThumbnailUrl);
            Assert.AreSame(changedUrl || missingThumbnail ? updated.Thumbnail : previousThumbnail, video.Thumbnail);
            Assert.IsEmpty(collectionChanges);
        }

        private static NewsPost Post(string id) => new NewsPost
        {
            Url = "https://example.com/" + id,
            Title = "Post " + id,
            Summary = "Summary",
            PublishDate = new DateTime(2026, 11, 1),
            Source = "Source",
            ToolTip = "Tooltip",
            HasDescription = true,
        };

        private static YouTubeVideo Video(string id)
        {
            var thumbnail = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            thumbnail.Freeze();
            return new YouTubeVideo
            {
                Url = "https://example.com/" + id,
                Title = "Video " + id,
                PublishDate = new DateTime(2026, 11, 1),
                ThumbnailUrl = "https://example.com/" + id + ".jpg",
                Thumbnail = thumbnail,
            };
        }
    }
}