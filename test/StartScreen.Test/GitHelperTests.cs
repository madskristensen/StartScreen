using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Helpers;

namespace StartScreen.Test
{
    [TestClass]
    public class GitHelperTests
    {
        [TestMethod]
        public void SuppressCredentialPrompts_DisablesGitTerminalPrompt()
        {
            var psi = new ProcessStartInfo("git");

            GitHelper.SuppressCredentialPrompts(psi);

            Assert.AreEqual("0", psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"]);
        }

        [TestMethod]
        public void SuppressCredentialPrompts_DisablesGitCredentialManagerUi()
        {
            var psi = new ProcessStartInfo("git");

            GitHelper.SuppressCredentialPrompts(psi);

            Assert.AreEqual("Never", psi.EnvironmentVariables["GCM_INTERACTIVE"]);
        }

        [TestMethod]
        public void SuppressCredentialPrompts_OverridesAskPassHelpers()
        {
            var psi = new ProcessStartInfo("git");

            GitHelper.SuppressCredentialPrompts(psi);

            // GIT_ASKPASS and SSH_ASKPASS are set so any GUI askpass helper that
            // git would otherwise spawn is replaced with a no-op (echo).
            Assert.AreEqual("echo", psi.EnvironmentVariables["GIT_ASKPASS"]);
            Assert.AreEqual("echo", psi.EnvironmentVariables["SSH_ASKPASS"]);
            Assert.AreEqual("never", psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"]);
        }

        [TestMethod]
        public void SuppressCredentialPrompts_ClearsDisplayVariable()
        {
            var psi = new ProcessStartInfo("git");
            psi.EnvironmentVariables["DISPLAY"] = ":0";

            GitHelper.SuppressCredentialPrompts(psi);

            Assert.AreEqual(string.Empty, psi.EnvironmentVariables["DISPLAY"]);
        }

        [TestMethod]
        public void FetchUpstream_WithMissingDirectory_ReturnsSilently()
        {
            // Best-effort: must never throw, even when the path is invalid.
            GitHelper.FetchUpstream(@"C:\this\path\does\not\exist\for\sure", "origin", "refs/heads/main");
        }

        [TestMethod]
        public void FetchUpstream_WithEmptyPath_ReturnsSilently()
        {
            GitHelper.FetchUpstream(string.Empty, "origin", "refs/heads/main");
            GitHelper.FetchUpstream(null, "origin", "refs/heads/main");
        }

        [TestMethod]
        public void FetchUpstream_WithMissingUpstream_ReturnsSilently()
        {
            // No upstream configured (no remote or no branch ref) means ahead/behind
            // is undefined - the helper must skip the fetch instead of running it.
            GitHelper.FetchUpstream(System.IO.Path.GetTempPath(), null, "refs/heads/main");
            GitHelper.FetchUpstream(System.IO.Path.GetTempPath(), "origin", null);
            GitHelper.FetchUpstream(System.IO.Path.GetTempPath(), string.Empty, string.Empty);
        }
    }
}
