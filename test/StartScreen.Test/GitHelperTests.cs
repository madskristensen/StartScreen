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
        public async Task FetchUpstream_WithMissingDirectory_ReturnsFailureAsync()
        {
            var result = await GitHelper.FetchUpstreamAsync(@"C:\this\path\does\not\exist\for\sure", "origin", "refs/heads/main");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("The Git repository could not be found.", result.ErrorMessage);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow(null)]
        public async Task FetchUpstream_WithEmptyPath_ReturnsFailureAsync(string path)
        {
            var result = await GitHelper.FetchUpstreamAsync(path, "origin", "refs/heads/main");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("The Git repository could not be found.", result.ErrorMessage);
        }

        [TestMethod]
        [DataRow(null, "refs/heads/main")]
        [DataRow("origin", null)]
        [DataRow("", "")]
        public async Task FetchUpstream_WithMissingUpstream_ReturnsFailureAsync(string remote, string branch)
        {
            var result = await GitHelper.FetchUpstreamAsync(Path.GetTempPath(), remote, branch);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("The Git branch does not have an upstream.", result.ErrorMessage);
        }

        [TestMethod]
        public async Task FetchUpstream_WithCancellation_DoesNotStartGitAsync()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => GitHelper.FetchUpstreamAsync(
                Path.GetTempPath(), "origin", "refs/heads/main", cancellation.Token));
        }

        [TestMethod]
        public async Task FetchUpstream_WithLocalRemote_UpdatesTrackingBranchAsync()
        {
            using var directory = new TemporaryDirectory();
            var remotePath = directory.GetPath("remote.git");
            var clonePath = directory.GetPath("clone");
            Repository.Init(remotePath, isBare: true);
            using Repository seed = CreateRepositoryWithCommit(directory.GetPath("seed"));
            Remote remote = seed.Network.Remotes.Add("origin", remotePath);
            seed.Network.Push(remote, seed.Head.CanonicalName, seed.Head.CanonicalName, new PushOptions());
            Repository.Clone(remotePath, clonePath);
            File.AppendAllText(Path.Combine(seed.Info.WorkingDirectory, "file.txt"), "changed");
            LibGit2Sharp.Commands.Stage(seed, "file.txt");
            seed.Commit("Next commit", CreateSignature(), CreateSignature());
            seed.Network.Push(remote, seed.Head.CanonicalName, seed.Head.CanonicalName, new PushOptions());

            var result = await GitHelper.FetchUpstreamAsync(clonePath, "origin", seed.Head.CanonicalName);
            var counts = GitHelper.GetUpdatedAheadBehind(clonePath);

            Assert.IsTrue(result.Succeeded, result.ErrorMessage);
            Assert.AreEqual(0, counts.ahead);
            Assert.AreEqual(1, counts.behind);
        }

        [TestMethod]
        public async Task FetchUpstream_WithInvalidRemote_ReturnsGitErrorAsync()
        {
            using var directory = new TemporaryDirectory();
            using Repository repository = CreateRepositoryWithCommit(directory.Path);

            var result = await GitHelper.FetchUpstreamAsync(directory.Path, "missing-remote", repository.Head.CanonicalName);

            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains(result.ErrorMessage, "missing-remote");
        }

        [TestMethod]
        public async Task RunFetchProcess_CancellationTerminatesRunningProcessAsync()
        {
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<GitCommandResult> fetch = GitHelper.RunFetchProcessAsync(CreateSleepingProcess(), cancellation.Token, pid => started.SetResult(pid));

            try
            {
                Task first = await Task.WhenAny(started.Task, fetch);
                Assert.AreSame(started.Task, first, "The process must start before the fetch completes.");
                int pid = await started.Task;

                var stopwatch = Stopwatch.StartNew();
                cancellation.Cancel();
                Task completed = await Task.WhenAny(fetch, Task.Delay(3000));
                Assert.AreSame(fetch, completed);
                Assert.IsTrue(fetch.IsCanceled);
                Assert.IsLessThan(3000, stopwatch.ElapsedMilliseconds, "Cancellation should not wait for the five-second timeout.");
                AssertProcessExited(pid);
            }
            finally
            {
                cancellation.Cancel();
                if (started.Task.Status == TaskStatus.RanToCompletion)
                    StopTestProcess(await started.Task);
            }
        }

        [TestMethod]
        public async Task RunFetchProcess_TimeoutTerminatesRunningProcessAsync()
        {
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopwatch = Stopwatch.StartNew();
            Task<GitCommandResult> fetch = GitHelper.RunFetchProcessAsync(CreateSleepingProcess(), cancellation.Token, pid => started.SetResult(pid));

            try
            {
                GitCommandResult result = await fetch;
                Assert.IsFalse(result.Succeeded);
                Assert.AreEqual("The background Git fetch timed out.", result.ErrorMessage);
                Assert.IsLessThan(10000, stopwatch.ElapsedMilliseconds);
                Assert.IsTrue(started.Task.IsCompleted, "The process must start before the fetch times out.");
                int pid = await started.Task;
                AssertProcessExited(pid);
            }
            finally
            {
                cancellation.Cancel();
                if (started.Task.Status == TaskStatus.RanToCompletion)
                    StopTestProcess(await started.Task);
            }
        }

        private static ProcessStartInfo CreateSleepingProcess()
        {
            return new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }

        private static void AssertProcessExited(int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                Assert.IsTrue(process.WaitForExit(1000), "The background process must terminate after cancellation or timeout.");
            }
            catch (ArgumentException)
            {
                // The process has already exited and its ID is no longer registered.
            }
        }

        private static void StopTestProcess(int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch (ArgumentException)
            {
                // The process has already exited and its ID is no longer registered.
            }
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
