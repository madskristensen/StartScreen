using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models;

namespace StartScreen.Test
{
    [TestClass]
    public class MruItemTests
    {
        [TestMethod]
        public void AheadBehindText_WhenNoAheadBehind_ReturnsEmpty()
        {
            var item = new MruItem();

            Assert.AreEqual(string.Empty, item.AheadBehindText);
        }

        [TestMethod]
        public void AheadBehindText_WhenOnlyAhead_ReturnsUpArrowCount()
        {
            var item = new MruItem { CommitsAhead = 3 };

            Assert.AreEqual("\u21913", item.AheadBehindText);
        }

        [TestMethod]
        public void AheadBehindText_WhenOnlyBehind_ReturnsDownArrowCount()
        {
            var item = new MruItem { CommitsBehind = 5 };

            Assert.AreEqual("\u21935", item.AheadBehindText);
        }

        [TestMethod]
        public void AheadBehindText_WhenBothAheadAndBehind_ReturnsBothArrows()
        {
            var item = new MruItem { CommitsAhead = 2, CommitsBehind = 3 };

            Assert.AreEqual("\u21912 \u21933", item.AheadBehindText);
        }

        [TestMethod]
        public void AheadBehindText_WhenZeroAhead_DoesNotIncludeUpArrow()
        {
            var item = new MruItem { CommitsAhead = 0, CommitsBehind = 1 };

            Assert.AreEqual("\u21931", item.AheadBehindText);
        }

        [TestMethod]
        public void HasAheadBehind_WhenBothNull_ReturnsFalse()
        {
            var item = new MruItem();

            Assert.IsFalse(item.HasAheadBehind);
        }

        [TestMethod]
        public void HasAheadBehind_WhenAheadHasValue_ReturnsTrue()
        {
            var item = new MruItem { CommitsAhead = 0 };

            Assert.IsTrue(item.HasAheadBehind);
        }

        [TestMethod]
        public void HasGitBranch_WhenNull_ReturnsFalse()
        {
            var item = new MruItem();

            Assert.IsFalse(item.HasGitBranch);
        }

        [TestMethod]
        public void HasGitBranch_WhenSet_ReturnsTrue()
        {
            var item = new MruItem { GitBranch = "main" };

            Assert.IsTrue(item.HasGitBranch);
        }

        [TestMethod]
        public void LastCommitTimeText_WhenNull_ReturnsEmpty()
        {
            var item = new MruItem();

            Assert.AreEqual(string.Empty, item.LastCommitTimeText);
        }

        [TestMethod]
        public void LastCommitTimeText_WhenJustNow_ReturnsJustNow()
        {
            var item = new MruItem { LastCommitTime = DateTime.Now.AddSeconds(-10) };

            Assert.AreEqual("just now", item.LastCommitTimeText);
        }

        [TestMethod]
        public void LastCommitTimeText_WhenMinutesAgo_ReturnsMinutesFormat()
        {
            var item = new MruItem { LastCommitTime = DateTime.Now.AddMinutes(-15) };

            Assert.AreEqual("15m ago", item.LastCommitTimeText);
        }

        [TestMethod]
        public void LastCommitTimeText_WhenHoursAgo_ReturnsHoursFormat()
        {
            var item = new MruItem { LastCommitTime = DateTime.Now.AddHours(-3) };

            Assert.AreEqual("3h ago", item.LastCommitTimeText);
        }

        [TestMethod]
        public void LastCommitTimeText_WhenDaysAgo_ReturnsDaysFormat()
        {
            var item = new MruItem { LastCommitTime = DateTime.Now.AddDays(-2) };

            Assert.AreEqual("2d ago", item.LastCommitTimeText);
        }

        [TestMethod]
        public void LastCommitTimeText_WhenWeeksAgo_ReturnsWeeksFormat()
        {
            var item = new MruItem { LastCommitTime = DateTime.Now.AddDays(-14) };

            Assert.AreEqual("2w ago", item.LastCommitTimeText);
        }

        [TestMethod]
        public void LastCommitTimeText_WhenOverMonth_ReturnsDateFormat()
        {
            var commitTime = DateTime.Now.AddDays(-45);
            var item = new MruItem { LastCommitTime = commitTime };

            Assert.AreEqual(commitTime.ToString("MMM d"), item.LastCommitTimeText);
        }

        [TestMethod]
        public void TimeGroup_WhenToday_ReturnsToday()
        {
            var item = new MruItem { LastAccessed = DateTime.Today.AddHours(10) };

            Assert.AreEqual("Today", item.TimeGroup);
        }

        [TestMethod]
        public void TimeGroup_WhenYesterday_ReturnsYesterday()
        {
            var item = new MruItem { LastAccessed = DateTime.Today.AddDays(-1).AddHours(10) };

            Assert.AreEqual("Yesterday", item.TimeGroup);
        }

        [TestMethod]
        public void TimeGroup_WhenThisWeek_ReturnsThisWeek()
        {
            var item = new MruItem { LastAccessed = DateTime.Today.AddDays(-3) };

            Assert.AreEqual("This week", item.TimeGroup);
        }

        [TestMethod]
        public void TimeGroup_WhenThisMonth_ReturnsThisMonth()
        {
            var item = new MruItem { LastAccessed = DateTime.Today.AddDays(-15) };

            Assert.AreEqual("This month", item.TimeGroup);
        }

        [TestMethod]
        public void TimeGroup_WhenOlder_ReturnsOlder()
        {
            var item = new MruItem { LastAccessed = DateTime.Today.AddDays(-60) };

            Assert.AreEqual("Older", item.TimeGroup);
        }

        [TestMethod]
        public void ToolTipText_WhenPathOnly_ReturnsPath()
        {
            var item = new MruItem { Path = @"C:\Projects\Test.sln" };

            Assert.AreEqual(@"C:\Projects\Test.sln", item.ToolTipText);
        }

        [TestMethod]
        public void ToolTipText_WhenGitBranch_IncludesBranchInfo()
        {
            var item = new MruItem
            {
                Path = @"C:\Projects\Test.sln",
                GitBranch = "main"
            };

            string tooltip = item.ToolTipText;

            StringAssert.Contains(tooltip, @"C:\Projects\Test.sln");
            StringAssert.Contains(tooltip, "Branch: main");
        }

        [TestMethod]
        public void ToolTipText_WhenUncommittedChanges_IncludesAsterisk()
        {
            var item = new MruItem
            {
                Path = @"C:\Projects\Test.sln",
                GitBranch = "main",
                HasUncommittedChanges = true
            };

            StringAssert.Contains(item.ToolTipText, "*");
        }

        [TestMethod]
        public void ToolTipText_WhenAheadBehind_IncludesAheadBehindText()
        {
            var item = new MruItem
            {
                Path = @"C:\Projects\Test.sln",
                GitBranch = "main",
                CommitsAhead = 2,
                CommitsBehind = 1
            };

            string tooltip = item.ToolTipText;

            StringAssert.Contains(tooltip, "\u21912");
            StringAssert.Contains(tooltip, "\u21931");
        }

        [TestMethod]
        public void PropertyChanged_WhenNameChanges_RaisesEvent()
        {
            var item = new MruItem();
            string? changedProperty = null;
            item.PropertyChanged += (s, e) => changedProperty = e.PropertyName;

            item.Name = "Test";

            Assert.AreEqual(nameof(MruItem.Name), changedProperty);
        }

        [TestMethod]
        public void PropertyChanged_WhenIsPinnedChanges_RaisesEvent()
        {
            var item = new MruItem();
            string? changedProperty = null;
            item.PropertyChanged += (s, e) => changedProperty = e.PropertyName;

            item.IsPinned = true;

            Assert.AreEqual(nameof(MruItem.IsPinned), changedProperty);
        }

        [TestMethod]
        public void PropertyChanged_WhenSameValue_DoesNotRaise()
        {
            var item = new MruItem { Name = "Test" };
            bool raised = false;
            item.PropertyChanged += (s, e) => raised = true;

            item.Name = "Test";

            Assert.IsFalse(raised);
        }

        [TestMethod]
        public void PropertyChanged_WhenGitBranchChanges_RaisesHasGitBranch()
        {
            var item = new MruItem();
            var changedProperties = new List<string>();
            item.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName);

            item.GitBranch = "main";

            CollectionAssert.Contains(changedProperties, nameof(MruItem.HasGitBranch));
        }

        [TestMethod]
        public void PropertyChanged_WhenGitBranchChanges_RaisesToolTipText()
        {
            var item = new MruItem();
            var changedProperties = new List<string>();
            item.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName);

            item.GitBranch = "main";

            CollectionAssert.Contains(changedProperties, nameof(MruItem.ToolTipText));
        }

        [TestMethod]
        public void PropertyChanged_WhenCommitsAheadChanges_RaisesHasAheadBehind()
        {
            var item = new MruItem();
            var changedProperties = new List<string>();
            item.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName);

            item.CommitsAhead = 3;

            CollectionAssert.Contains(changedProperties, nameof(MruItem.HasAheadBehind));
        }

        [TestMethod]
        public void PropertyChanged_WhenCommitsAheadChanges_RaisesAheadBehindText()
        {
            var item = new MruItem();
            var changedProperties = new List<string>();
            item.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName);

            item.CommitsAhead = 3;

            CollectionAssert.Contains(changedProperties, nameof(MruItem.AheadBehindText));
        }

        [TestMethod]
        public void PropertyChanged_WhenCommitsBehindChanges_RaisesToolTipText()
        {
            var item = new MruItem();
            var changedProperties = new List<string>();
            item.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName);

            item.CommitsBehind = 2;

            CollectionAssert.Contains(changedProperties, nameof(MruItem.ToolTipText));
        }

        [TestMethod]
        public void PropertyChanged_WhenHasUncommittedChangesChanges_RaisesToolTipText()
        {
            var item = new MruItem();
            var changedProperties = new List<string>();
            item.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName);

            item.HasUncommittedChanges = true;

            CollectionAssert.Contains(changedProperties, nameof(MruItem.ToolTipText));
        }

        [TestMethod]
        public void PropertyChanged_WhenLastCommitTimeChanges_RaisesLastCommitTimeText()
        {
            var item = new MruItem();
            var changedProperties = new List<string>();
            item.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName);

            item.LastCommitTime = DateTime.Now.AddHours(-1);

            CollectionAssert.Contains(changedProperties, nameof(MruItem.LastCommitTimeText));
        }

        [TestMethod]
        public void PropertyChanged_WhenPathChanges_RaisesExists()
        {
            var item = new MruItem();
            var changedProperties = new List<string>();
            item.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName);

            item.Path = @"C:\nonexistent\path.sln";

            CollectionAssert.Contains(changedProperties, nameof(MruItem.Exists));
        }

        [TestMethod]
        public void AheadBehindText_WhenBothZero_ReturnsEmpty()
        {
            var item = new MruItem { CommitsAhead = 0, CommitsBehind = 0 };

            Assert.AreEqual(string.Empty, item.AheadBehindText);
        }

        [TestMethod]
        public void ToolTipText_WhenLastCommitTime_IncludesLastCommitLine()
        {
            var item = new MruItem
            {
                Path = @"C:\Projects\Test.sln",
                LastCommitTime = DateTime.Now.AddHours(-2),
            };

            StringAssert.Contains(item.ToolTipText, "Last commit:");
        }

        [TestMethod]
        public void FormattedDate_ReturnsShortDateString()
        {
            var date = new DateTime(2025, 6, 15, 14, 30, 0);
            var item = new MruItem { LastAccessed = date };

            Assert.AreEqual(date.ToString("d"), item.FormattedDate);
        }

        [TestMethod]
        public void Exists_WhenPathIsNull_ReturnsFalse()
        {
            var item = new MruItem();

            Assert.IsFalse(item.Exists);
        }

        [TestMethod]
        public void Exists_WhenPathIsWhitespace_ReturnsFalse()
        {
            var item = new MruItem { Path = "   " };

            Assert.IsFalse(item.Exists);
        }
    }
}
