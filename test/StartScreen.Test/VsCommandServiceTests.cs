using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class VsCommandServiceTests
    {
        [TestMethod]
        public void StartScreenCommand_AlsoAppearsOnTheStandardToolbar()
        {
            using var stream = typeof(VsCommandServiceTests).Assembly.GetManifestResourceStream("VSCommandTable.vsct");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace vsct = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
            var button = document.Descendants(vsct + "Button")
                .Single(element => element.Attribute("id")?.Value == "ShowStartScreenCommand");
            var placement = document.Descendants(vsct + "CommandPlacement")
                .Single(element => element.Attribute("id")?.Value == "ShowStartScreenCommand");

            Assert.AreEqual("StartScreen", placement.Attribute("guid")?.Value);
            Assert.AreEqual("guidSHLMainMenu", placement.Element(vsct + "Parent")?.Attribute("guid")?.Value);
            Assert.AreEqual("IDG_VS_TOOLSB_SAVEOPEN", placement.Element(vsct + "Parent")?.Attribute("id")?.Value);
            Assert.AreEqual("StartScreenGroup", button.Element(vsct + "Parent")?.Attribute("id")?.Value);
            Assert.AreEqual("Home", button.Element(vsct + "Icon")?.Attribute("id")?.Value);
            Assert.IsTrue(button.Elements(vsct + "CommandFlag").Any(element => element.Value == "IconIsMoniker"));
        }

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
