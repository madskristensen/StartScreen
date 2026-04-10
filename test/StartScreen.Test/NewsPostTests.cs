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
    }
}
