using System.Diagnostics;
using LibGit2Sharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Helpers;

namespace StartScreen.Test
{
    [TestClass]
    [DoNotParallelize]
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

        [TestMethod]
        public void IsRepositoryOwnershipFailure_WithOwnershipError_ReturnsTrue()
        {
            var exception = new LibGit2SharpException(
                "repository path 'C:/repos/example' is not owned by current user");

            Assert.IsTrue(GitHelper.IsRepositoryOwnershipFailure(exception));
        }

        [TestMethod]
        public void IsRepositoryOwnershipFailure_WithOtherError_ReturnsFalse()
        {
            var exception = new LibGit2SharpException("failed to read repository");

            Assert.IsFalse(GitHelper.IsRepositoryOwnershipFailure(exception));
        }

        [TestMethod]
        public void GetGitStatus_WithCommittedRepository_ReturnsCleanBranchStatus()
        {
            using var directory = new TemporaryDirectory();
            using Repository repository = CreateRepositoryWithCommit(directory.Path);

            var status = GitHelper.GetGitStatus(directory.Path);

            Assert.IsTrue(status.IsGitRepository);
            Assert.AreEqual(repository.Head.FriendlyName, status.BranchName);
            Assert.IsFalse(status.HasUncommittedChanges);
            Assert.IsNotNull(status.LastCommitTime);
        }

        [TestMethod]
        public void GetGitStatus_WithModifiedTrackedFile_ReturnsDirtyStatus()
        {
            using var directory = new TemporaryDirectory();
            using Repository repository = CreateRepositoryWithCommit(directory.Path);
            File.AppendAllText(directory.GetPath("file.txt"), "changed");

            var status = GitHelper.GetGitStatus(directory.Path);

            Assert.IsTrue(status.HasUncommittedChanges);
        }

        [TestMethod]
        public void GetGitStatus_WithDetachedHead_ReturnsShortCommitId()
        {
            using var directory = new TemporaryDirectory();
            using Repository repository = CreateRepositoryWithCommit(directory.Path);
            Commit commit = repository.Head.Tip;
            LibGit2Sharp.Commands.Checkout(repository, commit);

            var status = GitHelper.GetGitStatus(directory.Path);

            Assert.AreEqual(commit.Sha.Substring(0, 7), status.BranchName);
        }

        [TestMethod]
        public void GetGitStatus_WithStash_ReturnsStashCount()
        {
            using var directory = new TemporaryDirectory();
            using Repository repository = CreateRepositoryWithCommit(directory.Path);
            File.AppendAllText(directory.GetPath("file.txt"), "changed");
            LibGit2Sharp.Commands.Stage(repository, "file.txt");
            repository.Stashes.Add(CreateSignature(), "test stash", StashModifiers.Default);

            var status = GitHelper.GetGitStatus(directory.Path);

            Assert.AreEqual(1, status.StashCount);
        }

        [TestMethod]
        public void GetGitStatus_WithTrackingBranch_ReturnsUpstreamCoordinates()
        {
            using var directory = new TemporaryDirectory();
            var remotePath = directory.GetPath("remote.git");
            var seedPath = directory.GetPath("seed");
            var clonePath = directory.GetPath("clone");
            Repository.Init(remotePath, isBare: true);

            using (Repository seed = CreateRepositoryWithCommit(seedPath))
            {
                Remote remote = seed.Network.Remotes.Add("origin", remotePath);
                seed.Network.Push(remote, seed.Head.CanonicalName, seed.Head.CanonicalName, new PushOptions());
            }

            Repository.Clone(remotePath, clonePath);

            var status = GitHelper.GetGitStatus(clonePath);

            Assert.AreEqual("origin", status.UpstreamRemoteName);
            Assert.AreEqual($"refs/heads/{status.BranchName}", status.UpstreamBranchRef);
            Assert.AreEqual(0, status.CommitsAhead);
            Assert.AreEqual(0, status.CommitsBehind);
        }

        [TestMethod]
        public void GetGitStatus_WithNonRepository_ReturnsEmptyStatus()
        {
            using var directory = new TemporaryDirectory();

            var status = GitHelper.GetGitStatus(directory.Path);

            Assert.IsFalse(status.IsGitRepository);
            Assert.IsNull(status.BranchName);
        }

        [TestMethod]
        public void FindRepositoryPath_FromNestedDirectory_ReturnsRepositoryRoot()
        {
            using var directory = new TemporaryDirectory();
            using Repository repository = CreateRepositoryWithCommit(directory.Path);
            var nestedPath = directory.GetPath(@"src\nested");
            Directory.CreateDirectory(nestedPath);

            string result = GitHelper.FindRepositoryPath(nestedPath);

            Assert.AreEqual(directory.Path, result);
        }

        private static Repository CreateRepositoryWithCommit(string path)
        {
            Directory.CreateDirectory(path);
            Repository.Init(path);
            var repository = new Repository(path);
            File.WriteAllText(System.IO.Path.Combine(path, "file.txt"), "initial");
            LibGit2Sharp.Commands.Stage(repository, "file.txt");
            Signature signature = CreateSignature();
            repository.Commit("Initial commit", signature, signature);
            return repository;
        }

        private static Signature CreateSignature()
        {
            return new Signature("StartScreen Tests", "tests@example.com", DateTimeOffset.UtcNow);
        }
    }
}
