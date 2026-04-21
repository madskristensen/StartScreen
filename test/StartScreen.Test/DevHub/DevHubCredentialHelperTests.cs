using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubCredentialHelperTests
    {
        [TestMethod]
        public void ParseGitCredentialOutput_ValidOutput_ParsesCredential()
        {
            var output = "protocol=https\nhost=github.com\nusername=madskristensen\npassword=ghp_abc123\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNotNull(result);
            Assert.AreEqual("madskristensen", result.Username);
            Assert.AreEqual("ghp_abc123", result.Token);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_WindowsLineEndings_ParsesCorrectly()
        {
            var output = "protocol=https\r\nhost=github.com\r\nusername=user\r\npassword=token123\r\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNotNull(result);
            Assert.AreEqual("user", result.Username);
            Assert.AreEqual("token123", result.Token);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_NoPassword_ReturnsNull()
        {
            var output = "protocol=https\nhost=github.com\nusername=user\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_EmptyPassword_ReturnsNull()
        {
            var output = "protocol=https\nhost=github.com\nusername=user\npassword=\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_NullInput_ReturnsNull()
        {
            Assert.IsNull(DevHubCredentialHelper.ParseGitCredentialOutput(null));
        }

        [TestMethod]
        public void ParseGitCredentialOutput_EmptyInput_ReturnsNull()
        {
            Assert.IsNull(DevHubCredentialHelper.ParseGitCredentialOutput(""));
        }

        [TestMethod]
        public void ParseGitCredentialOutput_WhitespaceOnly_ReturnsNull()
        {
            Assert.IsNull(DevHubCredentialHelper.ParseGitCredentialOutput("   \n  \n  "));
        }

        [TestMethod]
        public void ParseGitCredentialOutput_NoUsername_StillReturnsCredentialWithNullUsername()
        {
            var output = "protocol=https\nhost=github.com\npassword=my_token\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNotNull(result);
            Assert.IsNull(result.Username);
            Assert.AreEqual("my_token", result.Token);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_ExtraFields_IgnoresThem()
        {
            var output = "protocol=https\nhost=github.com\nusername=user\npassword=token\npath=/some/path\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNotNull(result);
            Assert.AreEqual("user", result.Username);
            Assert.AreEqual("token", result.Token);
        }

        [TestMethod]
        public void ParseGitCredentialOutput_PasswordWithEquals_ParsesCorrectly()
        {
            // Tokens can contain equals signs
            var output = "protocol=https\nhost=github.com\nusername=user\npassword=abc=def=ghi\n";

            var result = DevHubCredentialHelper.ParseGitCredentialOutput(output);

            Assert.IsNotNull(result);
            Assert.AreEqual("abc=def=ghi", result.Token);
        }
    }
}
