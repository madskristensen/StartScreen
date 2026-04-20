using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models;

namespace StartScreen.Test
{
    [TestClass]
    public class GitStatusTests
    {
        [TestMethod]
        public void IsGitRepository_WhenBranchNameIsNull_ReturnsFalse()
        {
            var status = new GitStatus();

            Assert.IsFalse(status.IsGitRepository);
        }

        [TestMethod]
        public void IsGitRepository_WhenBranchNameIsEmpty_ReturnsFalse()
        {
            var status = new GitStatus { BranchName = string.Empty };

            Assert.IsFalse(status.IsGitRepository);
        }

        [TestMethod]
        public void IsGitRepository_WhenBranchNameIsSet_ReturnsTrue()
        {
            var status = new GitStatus { BranchName = "main" };

            Assert.IsTrue(status.IsGitRepository);
        }

        [TestMethod]
        public void IsGitRepository_WhenDetachedHead_ReturnsTrue()
        {
            var status = new GitStatus { BranchName = "abc1234" };

            Assert.IsTrue(status.IsGitRepository);
        }

        [TestMethod]
        public void StashCount_DefaultsToZero()
        {
            var status = new GitStatus();

            Assert.AreEqual(0, status.StashCount);
        }

        [TestMethod]
        public void CurrentOperation_DefaultsToNull()
        {
            var status = new GitStatus();

            Assert.IsNull(status.CurrentOperation);
        }

        [TestMethod]
        public void IsGitRepository_WithStashCount_ReturnsTrue()
        {
            var status = new GitStatus
            {
                BranchName = "main",
                StashCount = 2
            };

            Assert.IsTrue(status.IsGitRepository);
            Assert.AreEqual(2, status.StashCount);
        }

        [TestMethod]
        public void IsGitRepository_WithCurrentOperation_ReturnsTrue()
        {
            var status = new GitStatus
            {
                BranchName = "main",
                CurrentOperation = "Merge"
            };

            Assert.IsTrue(status.IsGitRepository);
            Assert.AreEqual("Merge", status.CurrentOperation);
        }

        [TestMethod]
        public void GitStatus_CanHaveAllPropertiesSet()
        {
            var status = new GitStatus
            {
                BranchName = "feature/test",
                CommitsAhead = 2,
                CommitsBehind = 3,
                HasUncommittedChanges = true,
                StashCount = 1,
                CurrentOperation = "Rebase"
            };

            Assert.IsTrue(status.IsGitRepository);
            Assert.AreEqual("feature/test", status.BranchName);
            Assert.AreEqual(2, status.CommitsAhead);
            Assert.AreEqual(3, status.CommitsBehind);
            Assert.IsTrue(status.HasUncommittedChanges);
            Assert.AreEqual(1, status.StashCount);
            Assert.AreEqual("Rebase", status.CurrentOperation);
        }
    }
}
