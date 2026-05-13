using System;
using System.ServiceModel.Syndication;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models;

namespace StartScreen.Test
{
    [TestClass]
    public class YouTubeVideoTests
    {
        [TestMethod]
        public void FromSyndicationItem_WhenNull_ReturnsNull()
        {
            YouTubeVideo result = YouTubeVideo.FromSyndicationItem(null);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenMinimalItem_SetsTitle()
        {
            var item = new SyndicationItem("Test Video Title", "Description", null);

            YouTubeVideo result = YouTubeVideo.FromSyndicationItem(item);

            Assert.AreEqual("Test Video Title", result.Title);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenTitleHasHtmlEntities_DecodesEntities()
        {
            var item = new SyndicationItem
            {
                Title = new TextSyndicationContent("C# &amp; .NET"),
            };

            YouTubeVideo result = YouTubeVideo.FromSyndicationItem(item);

            Assert.AreEqual("C# & .NET", result.Title);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenHasVideoUrl_SetsUrl()
        {
            var uri = new Uri("https://www.youtube.com/watch?v=abc123");
            var item = new SyndicationItem("Title", "Content", uri);

            YouTubeVideo result = YouTubeVideo.FromSyndicationItem(item);

            Assert.AreEqual("https://www.youtube.com/watch?v=abc123", result.Url);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenHasVideoUrl_SetsThumbnailUrl()
        {
            var uri = new Uri("https://www.youtube.com/watch?v=abc123");
            var item = new SyndicationItem("Title", "Content", uri);

            YouTubeVideo result = YouTubeVideo.FromSyndicationItem(item);

            Assert.AreEqual("https://i.ytimg.com/vi/abc123/mqdefault.jpg", result.ThumbnailUrl);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenNoLinks_SetsUrlToEmpty()
        {
            var item = new SyndicationItem
            {
                Title = new TextSyndicationContent("No Link Video"),
            };

            YouTubeVideo result = YouTubeVideo.FromSyndicationItem(item);

            Assert.AreEqual(string.Empty, result.Url);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenNoLinks_SetsThumbnailUrlToEmpty()
        {
            var item = new SyndicationItem
            {
                Title = new TextSyndicationContent("No Link Video"),
            };

            YouTubeVideo result = YouTubeVideo.FromSyndicationItem(item);

            Assert.AreEqual(string.Empty, result.ThumbnailUrl);
        }

        [TestMethod]
        public void FromSyndicationItem_SetsPublishDate()
        {
            var publishDate = new DateTimeOffset(2026, 4, 20, 14, 0, 0, TimeSpan.Zero);
            var item = new SyndicationItem("Title", "Content", null)
            {
                PublishDate = publishDate,
            };

            YouTubeVideo result = YouTubeVideo.FromSyndicationItem(item);

            Assert.AreEqual(publishDate.DateTime, result.PublishDate);
        }

        [TestMethod]
        public void ExtractVideoIdFromUrl_WhenValidWatchUrl_ReturnsVideoId()
        {
            var url = "https://www.youtube.com/watch?v=NrMKyJVeUVA";

            var result = YouTubeVideo.ExtractVideoIdFromUrl(url);

            Assert.AreEqual("NrMKyJVeUVA", result);
        }

        [TestMethod]
        public void ExtractVideoIdFromUrl_WhenShortsUrl_ReturnsVideoId()
        {
            var url = "https://www.youtube.com/shorts/NrMKyJVeUVA";

            var result = YouTubeVideo.ExtractVideoIdFromUrl(url);

            Assert.AreEqual("NrMKyJVeUVA", result);
        }

        [TestMethod]
        public void ExtractVideoIdFromUrl_WhenEmpty_ReturnsNull()
        {
            var result = YouTubeVideo.ExtractVideoIdFromUrl(string.Empty);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void ExtractVideoIdFromUrl_WhenNull_ReturnsNull()
        {
            var result = YouTubeVideo.ExtractVideoIdFromUrl(null);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void ExtractVideoIdFromUrl_WhenNoVideoId_ReturnsNull()
        {
            var url = "https://www.youtube.com/channel/UChqrDOwARrxdJF-ykAptc7w";

            var result = YouTubeVideo.ExtractVideoIdFromUrl(url);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void ToCacheEntry_PreservesAllFields()
        {
            var original = new YouTubeVideo
            {
                Title = "Test Video",
                Url = "https://www.youtube.com/watch?v=abc123",
                ThumbnailUrl = "https://i.ytimg.com/vi/abc123/mqdefault.jpg",
                PublishDate = new DateTime(2026, 4, 20, 14, 0, 0),
            };

            YouTubeCacheEntry entry = original.ToCacheEntry();

            Assert.AreEqual("Test Video", entry.Title);
            Assert.AreEqual("https://www.youtube.com/watch?v=abc123", entry.Url);
            Assert.AreEqual("https://i.ytimg.com/vi/abc123/mqdefault.jpg", entry.ThumbnailUrl);
            Assert.AreEqual(new DateTime(2026, 4, 20, 14, 0, 0), entry.PublishDate);
        }

        [TestMethod]
        public void FromCacheEntry_PreservesAllFields()
        {
            var entry = new YouTubeCacheEntry
            {
                Title = "Cached Video",
                Url = "https://www.youtube.com/watch?v=xyz789",
                ThumbnailUrl = "https://i.ytimg.com/vi/xyz789/mqdefault.jpg",
                PublishDate = new DateTime(2026, 3, 10),
            };

            YouTubeVideo video = YouTubeVideo.FromCacheEntry(entry);

            Assert.AreEqual("Cached Video", video.Title);
            Assert.AreEqual("https://www.youtube.com/watch?v=xyz789", video.Url);
            Assert.AreEqual("https://i.ytimg.com/vi/xyz789/mqdefault.jpg", video.ThumbnailUrl);
            Assert.AreEqual(new DateTime(2026, 3, 10), video.PublishDate);
        }

        [TestMethod]
        public void FromCacheEntry_WhenNull_ReturnsNull()
        {
            YouTubeVideo result = YouTubeVideo.FromCacheEntry(null);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void FromCacheEntry_WhenFieldsNull_DefaultsToEmptyStrings()
        {
            var entry = new YouTubeCacheEntry();

            YouTubeVideo video = YouTubeVideo.FromCacheEntry(entry);

            Assert.AreEqual(string.Empty, video.Title);
            Assert.AreEqual(string.Empty, video.Url);
            Assert.AreEqual(string.Empty, video.ThumbnailUrl);
        }

        [TestMethod]
        public void CacheRoundTrip_PreservesData()
        {
            var original = new YouTubeVideo
            {
                Title = "Round Trip Video",
                Url = "https://www.youtube.com/watch?v=roundtrip",
                ThumbnailUrl = "https://i.ytimg.com/vi/roundtrip/mqdefault.jpg",
                PublishDate = new DateTime(2026, 1, 15, 8, 0, 0),
            };

            YouTubeCacheEntry entry = original.ToCacheEntry();
            YouTubeVideo restored = YouTubeVideo.FromCacheEntry(entry);

            Assert.AreEqual(original.Title, restored.Title);
            Assert.AreEqual(original.Url, restored.Url);
            Assert.AreEqual(original.ThumbnailUrl, restored.ThumbnailUrl);
            Assert.AreEqual(original.PublishDate, restored.PublishDate);
        }

        [TestMethod]
        public void IsNew_WhenPublishedToday_ReturnsTrue()
        {
            var video = new YouTubeVideo { PublishDate = DateTime.Now };

            Assert.IsTrue(video.IsNew);
        }

        [TestMethod]
        public void IsNew_WhenPublishedYesterday_ReturnsTrue()
        {
            var video = new YouTubeVideo { PublishDate = DateTime.Now.AddDays(-1) };

            Assert.IsTrue(video.IsNew);
        }

        [TestMethod]
        public void IsNew_WhenPublishedFourDaysAgo_ReturnsFalse()
        {
            var video = new YouTubeVideo { PublishDate = DateTime.Now.AddDays(-4) };

            Assert.IsFalse(video.IsNew);
        }

        [TestMethod]
        public void IsNew_WhenPublishedExactlyThreeDaysAgo_ReturnsFalse()
        {
            var video = new YouTubeVideo { PublishDate = DateTime.Now.AddDays(-3) };

            Assert.IsFalse(video.IsNew);
        }

        [TestMethod]
        public void IsNew_WhenPublishedJustUnderThreeDaysAgo_ReturnsTrue()
        {
            var video = new YouTubeVideo { PublishDate = DateTime.Now.AddDays(-2.99) };

            Assert.IsTrue(video.IsNew);
        }
    }
}
