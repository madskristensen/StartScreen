using System;
using System.ServiceModel.Syndication;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models;

namespace StartScreen.Test
{
    [TestClass]
    public class NewsPostTests
    {
        [TestMethod]
        public void IsNew_WhenPublishedToday_ReturnsTrue()
        {
            var post = new NewsPost { PublishDate = DateTime.Now };

            Assert.IsTrue(post.IsNew);
        }

        [TestMethod]
        public void IsNew_WhenPublishedYesterday_ReturnsTrue()
        {
            var post = new NewsPost { PublishDate = DateTime.Now.AddDays(-1) };

            Assert.IsTrue(post.IsNew);
        }

        [TestMethod]
        public void IsNew_WhenPublishedFourDaysAgo_ReturnsFalse()
        {
            var post = new NewsPost { PublishDate = DateTime.Now.AddDays(-4) };

            Assert.IsFalse(post.IsNew);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenNull_ReturnsNull()
        {
            NewsPost result = NewsPost.FromSyndicationItem(null);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenMinimalItem_SetsTitle()
        {
            var item = new SyndicationItem("Test Title", "Test content", null);

            NewsPost result = NewsPost.FromSyndicationItem(item);

            Assert.AreEqual("Test Title", result.Title);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenHasUrl_AppendsTrackingParameter()
        {
            var uri = new Uri("https://devblogs.microsoft.com/post1");
            var item = new SyndicationItem("Title", "Content", uri);

            NewsPost result = NewsPost.FromSyndicationItem(item);

            StringAssert.EndsWith(result.Url, "?cid=vs_start_screen");
        }

        [TestMethod]
        public void FromSyndicationItem_WhenUrlHasQueryString_DoesNotAppendTracking()
        {
            var uri = new Uri("https://devblogs.microsoft.com/post1?existing=param");
            var item = new SyndicationItem("Title", "Content", uri);

            NewsPost result = NewsPost.FromSyndicationItem(item);

            Assert.DoesNotContain(result.Url, "cid=vs_start_screen");
        }

        [TestMethod]
        public void FromSyndicationItem_WhenNoSummary_SetsHasDescriptionFalse()
        {
            var item = new SyndicationItem("Title", (string)null!, null);

            NewsPost result = NewsPost.FromSyndicationItem(item);

            Assert.IsFalse(result.HasDescription);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenHasSummary_SetsHasDescriptionTrue()
        {
            var item = new SyndicationItem
            {
                Title = new TextSyndicationContent("Title"),
                Summary = new TextSyndicationContent("Some content here")
            };

            NewsPost result = NewsPost.FromSyndicationItem(item);

            Assert.IsTrue(result.HasDescription);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenSummaryHasHtml_StripsHtmlTags()
        {
            var item = new SyndicationItem
            {
                Title = new TextSyndicationContent("Title"),
                Summary = new TextSyndicationContent("<p>Hello <b>world</b></p>", TextSyndicationContentKind.Html)
            };

            NewsPost result = NewsPost.FromSyndicationItem(item);

            Assert.DoesNotContain(result.Summary, "<");
            Assert.DoesNotContain(result.Summary, ">");
        }

        [TestMethod]
        public void FromSyndicationItem_WhenTitleHasHtmlEntities_DecodesEntities()
        {
            var item = new SyndicationItem
            {
                Title = new TextSyndicationContent("C# &amp; .NET"),
            };

            NewsPost result = NewsPost.FromSyndicationItem(item);

            Assert.AreEqual("C# & .NET", result.Title);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenNoLinks_SetsUrlToEmpty()
        {
            var item = new SyndicationItem
            {
                Title = new TextSyndicationContent("No Link Post"),
            };

            NewsPost result = NewsPost.FromSyndicationItem(item);

            Assert.AreEqual(string.Empty, result.Url);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenSummaryExceedsMaxLength_Truncates()
        {
            var longContent = new string('A', 2000);
            var item = new SyndicationItem
            {
                Title = new TextSyndicationContent("Title"),
                Summary = new TextSyndicationContent(longContent),
            };

            NewsPost result = NewsPost.FromSyndicationItem(item);

            Assert.AreEqual(1000, result.Summary.Length);
        }

        [TestMethod]
        public void FromSyndicationItem_WhenSummaryUnderMaxLength_IsNotTruncated()
        {
            var shortContent = "This is a short summary.";
            var item = new SyndicationItem
            {
                Title = new TextSyndicationContent("Title"),
                Summary = new TextSyndicationContent(shortContent),
            };

            NewsPost result = NewsPost.FromSyndicationItem(item);

            Assert.AreEqual(shortContent, result.Summary);
        }

        [TestMethod]
        public void FromSyndicationItem_SetsPublishDate()
        {
            var publishDate = new DateTimeOffset(2025, 5, 20, 14, 0, 0, TimeSpan.Zero);
            var item = new SyndicationItem("Title", "Content", null)
            {
                PublishDate = publishDate,
            };

            NewsPost result = NewsPost.FromSyndicationItem(item);

            Assert.AreEqual(publishDate.DateTime, result.PublishDate);
        }

        [TestMethod]
        public void IsNew_WhenPublishedExactlyThreeDaysAgo_ReturnsFalse()
        {
            var post = new NewsPost { PublishDate = DateTime.Now.AddDays(-3) };

            Assert.IsFalse(post.IsNew);
        }

        [TestMethod]
        public void IsNew_WhenPublishedJustUnderThreeDaysAgo_ReturnsTrue()
        {
            var post = new NewsPost { PublishDate = DateTime.Now.AddDays(-2.99) };

            Assert.IsTrue(post.IsNew);
        }

        [TestMethod]
        public void ToCacheEntry_PreservesAllFields()
        {
            var original = new NewsPost
            {
                Title = "Test Title",
                Summary = "Test Summary",
                Url = "https://example.com/post",
                PublishDate = new DateTime(2025, 6, 15, 10, 30, 0),
                Source = "Jun 15 in Test Feed",
                HasDescription = true,
            };

            FeedCacheEntry entry = original.ToCacheEntry();

            Assert.AreEqual("Test Title", entry.Title);
            Assert.AreEqual("Test Summary", entry.Summary);
            Assert.AreEqual("https://example.com/post", entry.Url);
            Assert.AreEqual(new DateTime(2025, 6, 15, 10, 30, 0), entry.PublishDate);
            Assert.AreEqual("Jun 15 in Test Feed", entry.Source);
            Assert.IsTrue(entry.HasDescription);
        }

        [TestMethod]
        public void FromCacheEntry_PreservesAllFields()
        {
            var entry = new FeedCacheEntry
            {
                Title = "Cached Title",
                Summary = "Cached Summary",
                Url = "https://example.com/cached",
                PublishDate = new DateTime(2025, 3, 10),
                Source = "Mar 10 in Blog",
                HasDescription = true,
            };

            NewsPost post = NewsPost.FromCacheEntry(entry);

            Assert.AreEqual("Cached Title", post.Title);
            Assert.AreEqual("Cached Summary", post.Summary);
            Assert.AreEqual("https://example.com/cached", post.Url);
            Assert.AreEqual(new DateTime(2025, 3, 10), post.PublishDate);
            Assert.AreEqual("Mar 10 in Blog", post.Source);
            Assert.IsTrue(post.HasDescription);
        }

        [TestMethod]
        public void FromCacheEntry_WhenNull_ReturnsNull()
        {
            NewsPost result = NewsPost.FromCacheEntry(null);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void FromCacheEntry_WhenFieldsNull_DefaultsToEmptyStrings()
        {
            var entry = new FeedCacheEntry();

            NewsPost post = NewsPost.FromCacheEntry(entry);

            Assert.AreEqual(string.Empty, post.Title);
            Assert.AreEqual(string.Empty, post.Summary);
            Assert.AreEqual(string.Empty, post.Url);
            Assert.AreEqual(string.Empty, post.Source);
        }

        [TestMethod]
        public void CacheRoundTrip_PreservesData()
        {
            var original = new NewsPost
            {
                Title = "Round Trip",
                Summary = "Summary text",
                Url = "https://example.com/rt",
                PublishDate = new DateTime(2025, 1, 20, 8, 0, 0),
                Source = "Jan 20 in Blog",
                HasDescription = true,
            };

            FeedCacheEntry entry = original.ToCacheEntry();
            NewsPost restored = NewsPost.FromCacheEntry(entry);

            Assert.AreEqual(original.Title, restored.Title);
            Assert.AreEqual(original.Summary, restored.Summary);
            Assert.AreEqual(original.Url, restored.Url);
            Assert.AreEqual(original.PublishDate, restored.PublishDate);
            Assert.AreEqual(original.Source, restored.Source);
            Assert.AreEqual(original.HasDescription, restored.HasDescription);
        }
    }
}
