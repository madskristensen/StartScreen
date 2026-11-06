using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class VsCommandServiceTests
    {
        [TestMethod]
        public async Task AccountSettings_ExecutesTheNativeCommandAsync()
        {
            string? executed = null;
            await VsCommandService.OpenAccountSettingsCoreAsync(command =>
            {
                executed = command;
                return Task.FromResult(true);
            });

            Assert.AreEqual("File.AccountSettings", executed);
        }

        [TestMethod]
        public async Task AccountSettings_UnavailableCommandIsNotReportedAsSuccessAsync()
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                VsCommandService.OpenAccountSettingsCoreAsync(_ => Task.FromResult(false)));
        }

        [TestMethod]
        public async Task AccountSettings_CommandExceptionsReachTheNotificationHandlerAsync()
        {
            var failure = new InvalidOperationException("Test command failure");
            var result = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                VsCommandService.OpenAccountSettingsCoreAsync(_ => Task.FromException<bool>(failure)));

            Assert.AreSame(failure, result);
        }

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
