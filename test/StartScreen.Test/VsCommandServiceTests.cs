using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class VsCommandServiceTests
    {
        [TestMethod]
        [DataRow(@"C:\Code\App.sln")]
        [DataRow(@"C:\Code\App.slnx")]
        [DataRow(@"C:\Code\App.slnf")]
        [DataRow(@"C:\Code\App.SLNF")]
        public void IsSolutionPath_WithSupportedSolutionExtension_ReturnsTrue(string path)
        {
            Assert.IsTrue(VsCommandService.IsSolutionPath(path));
        }

        [TestMethod]
        [DataRow(@"C:\Code\App.csproj")]
        [DataRow(@"C:\Code\App.txt")]
        [DataRow(@"C:\Code\App")]
        public void IsSolutionPath_WithNonSolutionExtension_ReturnsFalse(string path)
        {
            Assert.IsFalse(VsCommandService.IsSolutionPath(path));
        }
    }
}
