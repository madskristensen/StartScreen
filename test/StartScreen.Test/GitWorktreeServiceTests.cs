using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    [DoNotParallelize]
    public class GitWorktreeServiceTests
    {
        [TestMethod]
        public void Parse_PreservesSpacesUnicodeNewlinesAndLiteralQuotes()
        {
            var path = "C:/repos/spaces 日本語\nnext\"literal";
            var result = GitWorktreeService.Parse(Record(path, "branch refs/heads/feature/日本語"), @"C:\other");

            Assert.HasCount(1, result);
            Assert.AreEqual(path, result[0].Path);
            Assert.AreEqual("feature/日本語", result[0].Branch);
            Assert.IsFalse(result[0].IsCurrent);
            Assert.IsFalse(result[0].IsBare);
            Assert.IsFalse(result[0].IsLocked);
            Assert.IsFalse(result[0].IsPrunable);
        }

        [TestMethod]
        public void Parse_RecognizesBareDetachedLockedAndPrunableRecords()
        {
            var output = Record("C:/repo.git", "bare")
                + Record("C:/detached", "HEAD 123", "detached", "locked reason with\nnewline")
                + Record("C:/missing", "branch refs/heads/topic", "prunable gitdir file points to non-existent location")
                + Record("C:/flags", "branch refs/heads/main", "locked", "prunable", "future-field value");

            var result = GitWorktreeService.Parse(output, @"C:\detached\src");

            Assert.HasCount(4, result);
            Assert.IsTrue(result[0].IsBare);
            Assert.AreEqual(string.Empty, result[0].Branch);
            Assert.AreEqual("detached HEAD", result[1].Branch);
            Assert.IsTrue(result[1].IsLocked);
            Assert.IsTrue(result[1].IsCurrent);
            Assert.IsTrue(result[2].IsPrunable);
            Assert.IsTrue(result[3].IsLocked);
            Assert.IsTrue(result[3].IsPrunable);
            Assert.HasCount(1, result.Where(item => item.IsCurrent).ToList());
        }

        [TestMethod]
        public void Parse_MatchesNormalizedCurrentPathAndPrefersNestedWorktree()
        {
            var output = Record("C:/repo", "branch refs/heads/main")
                + Record("C:/repo/linked", "branch refs/heads/linked")
                + Record("C:/repo/linked-other", "branch refs/heads/other");

            var result = GitWorktreeService.Parse(output, @"c:\REPO\linked\src\..\");

            Assert.IsFalse(result[0].IsCurrent);
            Assert.IsTrue(result[1].IsCurrent);
            Assert.IsFalse(result[2].IsCurrent);
        }

        [TestMethod]
        public void Parse_DoesNotMatchSiblingWithSamePrefix()
        {
            var result = GitWorktreeService.Parse(Record("C:/repo", "branch refs/heads/main"), @"C:\repo-other");

            Assert.IsFalse(result[0].IsCurrent);
        }

        [TestMethod]
        public void Parse_MatchesDriveRoot()
        {
            var result = GitWorktreeService.Parse(Record("C:/", "branch refs/heads/main"), @"C:\src");

            Assert.IsTrue(result[0].IsCurrent);
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("\0\0")]
        [DataRow("worktree C:/repo\0branch refs/heads/main\0")]
        [DataRow("branch refs/heads/main\0\0")]
        [DataRow("worktree \0bare\0\0")]
        [DataRow("worktree C:/repo\0HEAD 123\0\0")]
        [DataRow("worktree C:/repo\0worktree C:/other\0bare\0\0")]
        public void Parse_RejectsMalformedOrIncompleteOutput(string output)
        {
            Assert.Throws<InvalidDataException>(() => GitWorktreeService.Parse(output, @"C:\repo"));
        }

        [TestMethod]
        public void ReadResult_RejectsTruncationRatherThanReturningPartialList()
        {
            var result = new OwnedProcessResult
            {
                Output = Record("C:/repo", "branch refs/heads/main"),
                OutputTruncated = true,
            };

            var exception = Assert.Throws<InvalidDataException>(() => GitWorktreeService.ReadResult(result, @"C:\repo"));

            StringAssert.Contains(exception.Message, "incomplete");
        }

        [TestMethod]
        public void ReadResult_ReportsTimeout()
        {
            Assert.Throws<TimeoutException>(() => GitWorktreeService.ReadResult(
                new OwnedProcessResult { TimedOut = true }, @"C:\repo"));
        }

        [TestMethod]
        public void ReadResult_PreservesGitFailureDiagnosticAndTruncationMarker()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => GitWorktreeService.ReadResult(
                new OwnedProcessResult { ExitCode = 128, Error = "fatal: actionable failure\n", ErrorTruncated = true }, @"C:\repo"));

            StringAssert.Contains(exception.Message, "actionable failure");
            StringAssert.Contains(exception.Message, "[output truncated]");
        }

        [TestMethod]
        public void ReadResult_UsesOutputWhenErrorStreamIsEmpty()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => GitWorktreeService.ReadResult(
                new OwnedProcessResult { ExitCode = 1, Output = "useful failure" }, @"C:\repo"));

            StringAssert.Contains(exception.Message, "useful failure");
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow(" ")]
        public async Task Read_RejectsEmptyWorkspaceAsync(string path)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => GitWorktreeService.ReadAsync(path, CancellationToken.None));
        }

        [TestMethod]
        public async Task Read_PreCanceled_DoesNotStartGitAsync()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => GitWorktreeService.ReadAsync(
                "missing workspace", cancellation.Token));
        }

        [TestMethod]
        public async Task Read_RejectsMissingWorkspaceAsync()
        {
            using var directory = new WorktreeDirectory();

            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => GitWorktreeService.ReadAsync(
                directory.GetPath("missing.sln"), CancellationToken.None));
        }

        [TestMethod]
        public async Task Read_LocalMainAndLinkedWorktrees_FromSolutionProjectAndNestedFolderAsync()
        {
            await RequireGitAsync();
            using var directory = new WorktreeDirectory();
            var main = directory.GetPath("main 日本語 space");
            var linked = directory.GetPath("linked 日本語 space");
            Directory.CreateDirectory(main);
            await InitializeAsync(main);
            await GitAsync(main, "worktree add -q -b feature/topic " + Quote(linked));
            var nested = Path.Combine(linked, "src", "nested");
            Directory.CreateDirectory(nested);
            var solution = Path.Combine(nested, "sample.sln");
            var project = Path.Combine(nested, "sample.csproj");
            File.WriteAllText(solution, string.Empty);
            File.WriteAllText(project, string.Empty);
            var gitBefore = File.ReadAllBytes(Path.Combine(linked, ".git"));
            var headBefore = File.ReadAllBytes(Path.Combine(main, ".git", "HEAD"));

            foreach (var input in new[] { linked, nested, solution, project })
            {
                var result = await GitWorktreeService.ReadAsync(input, CancellationToken.None);

                Assert.HasCount(2, result);
                var current = result.Single(item => item.IsCurrent);
                Assert.AreEqual(Normalize(linked), Normalize(current.Path));
                Assert.AreEqual("feature/topic", current.Branch);
                Assert.AreEqual("main", result.Single(item => !item.IsCurrent).Branch);
            }

            var fromMain = await GitWorktreeService.ReadAsync(main + Path.DirectorySeparatorChar, CancellationToken.None);
            Assert.AreEqual("main", fromMain.Single(item => item.IsCurrent).Branch);
            CollectionAssert.AreEqual(gitBefore, File.ReadAllBytes(Path.Combine(linked, ".git")));
            CollectionAssert.AreEqual(headBefore, File.ReadAllBytes(Path.Combine(main, ".git", "HEAD")));
            Assert.AreEqual(string.Empty, (await GitAsync(main, "status --porcelain")).Output);
        }

        [TestMethod]
        public async Task Read_LocalBareDetachedLockedAndPrunableWorktreesAsync()
        {
            await RequireGitAsync();
            using var directory = new WorktreeDirectory();
            var bare = directory.GetPath("bare repository.git");
            var detached = directory.GetPath("detached worktree");
            var missing = directory.GetPath("missing worktree");
            Directory.CreateDirectory(bare);
            await GitAsync(bare, "init --bare -q");
            var seed = directory.GetPath("seed");
            Directory.CreateDirectory(seed);
            await InitializeAsync(seed);
            await GitAsync(seed, "push -q " + Quote(bare) + " HEAD:refs/heads/main");
            await GitAsync(bare, "symbolic-ref HEAD refs/heads/main");
            await GitAsync(bare, "worktree add -q --detach " + Quote(detached) + " main");
            await GitAsync(bare, "worktree lock --reason " + Quote("a reason with spaces 日本語") + " " + Quote(detached));
            await GitAsync(bare, "worktree add -q -b missing " + Quote(missing) + " main");
            Directory.Delete(missing, recursive: true);
            var staleRecords = Directory.GetDirectories(Path.Combine(bare, "worktrees"));

            var result = await GitWorktreeService.ReadAsync(bare, CancellationToken.None);

            Assert.HasCount(3, result);
            var bareEntry = result.Single(item => item.IsBare);
            Assert.IsTrue(bareEntry.IsCurrent);
            Assert.AreEqual(Normalize(bare), Normalize(bareEntry.Path));
            var detachedEntry = result.Single(item => item.Branch == "detached HEAD");
            Assert.IsTrue(detachedEntry.IsLocked);
            Assert.IsFalse(detachedEntry.IsCurrent);
            Assert.IsTrue(result.Single(item => item.Branch == "missing").IsPrunable);

            var fromDetached = await GitWorktreeService.ReadAsync(detached, CancellationToken.None);
            Assert.AreEqual("detached HEAD", fromDetached.Single(item => item.IsCurrent).Branch);
            var fromBareSubdirectory = await GitWorktreeService.ReadAsync(Path.Combine(bare, "objects"), CancellationToken.None);
            Assert.IsTrue(fromBareSubdirectory.Single(item => item.IsCurrent).IsBare);
            CollectionAssert.AreEquivalent(staleRecords, Directory.GetDirectories(Path.Combine(bare, "worktrees")),
                "Discovery must not prune a stale worktree record.");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Prune_RemovesOnlyStaleUnlockedRegistrationsAndPreservesFilesAndBranchesAsync(bool bare)
        {
            await RequireGitAsync();
            using var directory = new WorktreeDirectory();
            var seed = Directory.CreateDirectory(directory.GetPath("seed")).FullName;
            await InitializeAsync(seed);
            var main = bare ? directory.GetPath("bare repository.git") : seed;
            if (bare)
                await GitAsync(seed, "clone -q --bare . " + Quote(main));
            var live = directory.GetPath("live checkout");
            var gone = directory.GetPath("deleted checkout");
            var locked = directory.GetPath("locked missing checkout");
            await GitAsync(main, "worktree add -q -b live " + Quote(live));
            await GitAsync(main, "worktree add -q -b gone " + Quote(gone));
            await GitAsync(main, "worktree add -q -b locked " + Quote(locked));
            await GitAsync(main, "worktree lock " + Quote(locked));
            var edits = Path.Combine(live, "uncommitted.txt");
            File.WriteAllText(edits, "Keep my working files");
            Directory.Delete(gone, recursive: true);
            Directory.Delete(locked, recursive: true);
            var branches = (await GitAsync(main, "for-each-ref --format=%(refname) refs/heads")).Output;
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => GitWorktreeService.PruneAsync(main, cancelled.Token));
            Assert.IsTrue((await GitWorktreeService.ReadAsync(main, CancellationToken.None)).Any(item => item.Branch == "gone"));

            await GitWorktreeService.PruneAsync(main, CancellationToken.None);

            var remaining = await GitWorktreeService.ReadAsync(main, CancellationToken.None);
            Assert.IsFalse(remaining.Any(item => item.Branch == "gone"));
            Assert.IsTrue(remaining.Single(item => item.Branch == "locked").IsLocked);
            Assert.IsTrue(remaining.Any(item => item.Branch == "live"));
            Assert.AreEqual("Keep my working files", File.ReadAllText(edits));
            Assert.AreEqual(branches, (await GitAsync(main, "for-each-ref --format=%(refname) refs/heads")).Output);
        }

        [TestMethod]
        public async Task Prune_NonRepository_PropagatesGitErrorAsync()
        {
            await RequireGitAsync();
            using var directory = new WorktreeDirectory();
            File.WriteAllText(directory.GetPath(".git"), "gitdir: " + directory.GetPath("absent") + "\n");
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => GitWorktreeService.PruneAsync(directory.Path, CancellationToken.None));
            StringAssert.Contains(error.Message, "Git worktree cleanup failed.");
        }

        [TestMethod]
        public async Task Read_NonRepository_PropagatesGitErrorAsync()
        {
            await RequireGitAsync();
            using var directory = new WorktreeDirectory();
            // A .git file with a missing target prevents discovery of this test's parent repository.
            File.WriteAllText(directory.GetPath(".git"), "gitdir: " + directory.GetPath("absent") + "\n");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => GitWorktreeService.ReadAsync(
                directory.Path, CancellationToken.None));

            StringAssert.Contains(exception.Message, "Git worktree discovery failed");
            Assert.IsGreaterThan("Git worktree discovery failed.".Length, exception.Message.Length);
        }

        [TestMethod]
        public async Task Cache_SharesMainLinkedNestedAndProjectRequestsAndRefreshesAfterInvalidationAsync()
        {
            await RequireGitAsync();
            using var directory = new WorktreeDirectory();
            var main = Directory.CreateDirectory(directory.GetPath("main")).FullName;
            var linked = directory.GetPath("linked");
            await InitializeAsync(main);
            await GitAsync(main, "worktree add -q -b topic " + Quote(linked));
            var nested = Directory.CreateDirectory(Path.Combine(linked, "src")).FullName;
            var solution = Path.Combine(main, "App.sln");
            File.WriteAllText(solution, string.Empty);
            var reads = 0;
            var cache = new GitWorktreeCache(async (path, token) =>
            {
                Interlocked.Increment(ref reads);
                return await GitWorktreeService.ReadAsync(path, token);
            });

            var results = await Task.WhenAll(new[] { main, linked, nested, solution }
                .Select(path => cache.GetAsync(path, CancellationToken.None)));

            Assert.AreEqual(1, reads);
            foreach (var result in results)
                Assert.HasCount(2, result);
            Assert.AreEqual(Normalize(main), Normalize(results[0].Single(item => item.IsCurrent).Path));
            Assert.AreEqual(Normalize(linked), Normalize(results[1].Single(item => item.IsCurrent).Path));
            Assert.AreEqual(Normalize(linked), Normalize(results[2].Single(item => item.IsCurrent).Path));
            Assert.AreEqual(Normalize(main), Normalize(results[3].Single(item => item.IsCurrent).Path));
            Assert.AreNotSame(results[0][0], results[1][0], "Current-checkout state must never be shared between cards.");

            var another = directory.GetPath("another");
            await GitAsync(main, "worktree add -q -b another " + Quote(another));
            Assert.HasCount(2, await cache.GetAsync(main, CancellationToken.None));
            var generation = cache.Generation;
            cache.Invalidate();
            Assert.AreEqual(generation + 1, cache.Generation);
            Assert.HasCount(3, await cache.GetAsync(main, CancellationToken.None));
            Assert.AreEqual(2, reads);
        }

        [TestMethod]
        public async Task Cache_LimitsDiscoveryToTwoConcurrentRepositoriesAsync()
        {
            using var directory = new WorktreeDirectory();
            var paths = Enumerable.Range(0, 4).Select(index => directory.GetPath("repo" + index)).ToArray();
            foreach (var path in paths)
                LibGit2Sharp.Repository.Init(path);
            var twoStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            var active = 0;
            var maximum = 0;
            var gate = new object();
            var cache = new GitWorktreeCache(async (path, token) =>
            {
                lock (gate)
                {
                    active++;
                    maximum = Math.Max(maximum, active);
                    if (++started == 2)
                        twoStarted.TrySetResult(true);
                }
                await release.Task.WithCancellation(token);
                lock (gate) active--;
                return new[] { new GitWorktreeInfo { Path = path, Branch = "main" } };
            });
            var requests = Task.WhenAll(paths.Select(path => cache.GetAsync(path, CancellationToken.None)));
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await twoStarted.Task.WithCancellation(timeout.Token);
                Assert.AreEqual(2, started);
            }
            finally
            {
                release.TrySetResult(true);
                await requests;
            }
            Assert.AreEqual(2, maximum);
            Assert.AreEqual(4, started);
        }

        [TestMethod]
        public async Task Cache_RetainsFailuresUntilRefreshAndThenRetriesAsync()
        {
            using var directory = new WorktreeDirectory();
            LibGit2Sharp.Repository.Init(directory.Path);
            var reads = 0;
            var failure = new InvalidOperationException("Git is unavailable.");
            var cache = new GitWorktreeCache((_, _) =>
            {
                reads++;
                return Task.FromException<IReadOnlyList<GitWorktreeInfo>>(failure);
            });

            Assert.AreSame(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(directory.Path, CancellationToken.None)));
            Assert.AreSame(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(directory.Path, CancellationToken.None)));
            Assert.AreEqual(1, reads);
            cache.Invalidate();
            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(directory.Path, CancellationToken.None));
            Assert.AreEqual(2, reads);
        }

        [TestMethod]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "The assertion observes this test's own canceled discovery task without a UI synchronization context.")]
        public async Task Cache_CanceledDiscoveryCanRestartOnReactivationAsync()
        {
            using var directory = new WorktreeDirectory();
            LibGit2Sharp.Repository.Init(directory.Path);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            var cache = new GitWorktreeCache(async (path, token) =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    started.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token);
                }
                return new[] { new GitWorktreeInfo { Path = path, Branch = "main" } };
            });
            using var cancellation = new CancellationTokenSource();
            var first = cache.GetAsync(directory.Path, cancellation.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await started.Task.WithCancellation(timeout.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => first);
            cache.Invalidate();

            Assert.HasCount(1, await cache.GetAsync(directory.Path, CancellationToken.None));
            Assert.AreEqual(2, reads);
        }

        private static string Record(string path, params string[] fields)
        {
            return "worktree " + path + "\0" + string.Join("\0", fields) + "\0\0";
        }

        private static string Normalize(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private static async Task InitializeAsync(string directory)
        {
            await GitAsync(directory, "init -q -b main");
            await GitAsync(directory, "-c user.name=WorktreeTests -c user.email=worktree-tests@example.invalid -c commit.gpgsign=false commit -q --allow-empty -m initial");
        }

        private static async Task RequireGitAsync()
        {
            try
            {
                OwnedProcessResult result = await OwnedProcess.RunAsync(new ProcessStartInfo("git", "--version"), TimeSpan.FromSeconds(5));
                Assert.IsFalse(result.TimedOut, "Git availability check timed out.");
                Assert.AreEqual(0, result.ExitCode, result.Error);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
            {
                await ex.LogAsync();
                Assert.Inconclusive("Git is not installed or is not on PATH: " + ex.Message);
            }
        }

        private static async Task<OwnedProcessResult> GitAsync(string directory, string arguments)
        {
            if (File.Exists(Path.Combine(directory, "HEAD")) && Directory.Exists(Path.Combine(directory, "objects")))
                arguments = "--git-dir=. " + arguments;
            var info = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = directory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            GitHelper.SuppressCredentialPrompts(info);
            OwnedProcessResult result = await OwnedProcess.RunAsync(info, TimeSpan.FromSeconds(10));
            Assert.IsFalse(result.TimedOut, "Git fixture command timed out: " + arguments);
            Assert.AreEqual(0, result.ExitCode, arguments + ": " + result.Error);
            return result;
        }

        private sealed class WorktreeDirectory : IDisposable
        {
            public WorktreeDirectory()
            {
                Path = System.IO.Path.Combine(Environment.CurrentDirectory, "WorktreeTests." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public string GetPath(string relativePath)
            {
                return System.IO.Path.Combine(Path, relativePath);
            }

            public void Dispose()
            {
                if (!Directory.Exists(Path))
                    return;
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
