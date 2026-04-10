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
    }
}
