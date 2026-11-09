using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;

namespace StartScreen.Services
{
    internal static class GitWorktreeService
    {
        internal static async Task<IReadOnlyList<GitWorktreeInfo>> ReadAsync(string workspacePath, CancellationToken token)
        {
            var (result, directory) = await RunGitAsync(workspacePath, "list --porcelain -z", token);
            return ReadResult(result, directory);
        }

        internal static async Task PruneAsync(string workspacePath, CancellationToken token)
        {
            var (result, _) = await RunGitAsync(workspacePath, "prune --expire=now", token);
            EnsureSuccess(result, "cleanup");
        }

        private static async Task<(OwnedProcessResult Result, string Directory)> RunGitAsync(string workspacePath, string arguments, CancellationToken token)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(workspacePath))
                throw new ArgumentException("A workspace path is required.", nameof(workspacePath));

            var fullPath = Path.GetFullPath(workspacePath);
            var directory = Directory.Exists(fullPath) ? fullPath
                : File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)
                : throw new DirectoryNotFoundException("The workspace path could not be found: " + workspacePath);
            var bareRoot = FindBareRepository(directory);
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = (bareRoot == null ? string.Empty : "--git-dir=. ")
                    + "--no-optional-locks -c core.quotepath=false worktree " + arguments,
                WorkingDirectory = bareRoot ?? directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            GitHelper.SuppressCredentialPrompts(startInfo);

            // Git resolves both .git directories and linked-worktree .git files, as
            // well as bare repositories, from any directory inside the workspace.
            OwnedProcessResult result = await OwnedProcess.RunAsync(startInfo, TimeSpan.FromSeconds(5), token);
            return (result, directory);
        }

        internal static IReadOnlyList<GitWorktreeInfo> ReadResult(OwnedProcessResult result, string directory)
        {
            EnsureSuccess(result, "discovery");
            if (result.OutputTruncated)
                throw new InvalidDataException("Git worktree discovery output exceeded the supported size. The list is incomplete.");
            return Parse(result.Output, directory);
        }

        private static void EnsureSuccess(OwnedProcessResult result, string operation)
        {
            if (result.TimedOut)
                throw new TimeoutException($"Git worktree {operation} timed out.");
            if (result.ExitCode != 0)
            {
                var useError = !string.IsNullOrWhiteSpace(result.Error);
                var diagnostic = useError ? result.Error : result.Output;
                var truncated = useError ? result.ErrorTruncated : result.OutputTruncated;
                throw new InvalidOperationException($"Git worktree {operation} failed."
                    + (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : " " + diagnostic.Trim())
                    + (truncated ? " [output truncated]" : string.Empty));
            }
        }

        internal static IReadOnlyList<GitWorktreeInfo> Parse(string output, string directory)
        {
            if (string.IsNullOrEmpty(output) || !output.EndsWith("\0\0", StringComparison.Ordinal))
                throw new InvalidDataException("Git returned an incomplete worktree list.");

            var worktrees = new List<GitWorktreeInfo>();
            GitWorktreeInfo current = null;
            foreach (var field in output.Split('\0'))
            {
                if (field.Length == 0)
                {
                    if (current != null)
                    {
                        if (!current.IsBare && string.IsNullOrEmpty(current.Branch))
                            throw new InvalidDataException("Git returned a worktree without a branch or detached HEAD marker.");
                        worktrees.Add(current);
                        current = null;
                    }
                    continue;
                }

                if (field.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    if (current != null || field.Length == "worktree ".Length)
                        throw new InvalidDataException("Git returned an invalid worktree record.");
                    current = new GitWorktreeInfo { Path = field.Substring("worktree ".Length), Branch = string.Empty };
                }
                else if (current == null)
                {
                    throw new InvalidDataException("Git returned a worktree field without a path.");
                }
                else if (field.StartsWith("branch ", StringComparison.Ordinal))
                {
                    var branch = field.Substring("branch ".Length);
                    current.Branch = branch.StartsWith("refs/heads/", StringComparison.Ordinal)
                        ? branch.Substring("refs/heads/".Length) : branch;
                }
                else if (field == "detached")
                    current.Branch = "detached HEAD";
                else if (field == "bare")
                    current.IsBare = true;
                else if (field == "locked" || field.StartsWith("locked ", StringComparison.Ordinal))
                    current.IsLocked = true;
                else if (field == "prunable" || field.StartsWith("prunable ", StringComparison.Ordinal))
                    current.IsPrunable = true;
            }
            if (worktrees.Count == 0)
                throw new InvalidDataException("Git returned an empty worktree list.");

            return ForWorkspace(worktrees, directory);
        }

        internal static IReadOnlyList<GitWorktreeInfo> ForWorkspace(IEnumerable<GitWorktreeInfo> source, string directory)
        {
            var worktrees = source.Select(worktree => new GitWorktreeInfo
            {
                Path = worktree.Path, Branch = worktree.Branch, IsBare = worktree.IsBare,
                IsLocked = worktree.IsLocked, IsPrunable = worktree.IsPrunable,
            }).ToList();
            // Match on directory boundaries, not textual prefixes. The longest match
            // also handles a linked worktree nested beneath the main working tree.
            var workspace = NormalizePath(directory);
            GitWorktreeInfo selected = null;
            var selectedLength = -1;
            foreach (var worktree in worktrees)
            {
                var root = worktree.Path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    .TrimEnd(Path.DirectorySeparatorChar);
                if ((workspace.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || workspace.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    && root.Length > selectedLength)
                {
                    selected = worktree;
                    selectedLength = root.Length;
                }
            }
            if (selected != null)
                selected.IsCurrent = true;
            return worktrees.AsReadOnly();
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string FindBareRepository(string directory)
        {
            for (var current = directory; current != null; current = Path.GetDirectoryName(current))
            {
                var gitPath = Path.Combine(current, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    return null;
                if (File.Exists(Path.Combine(current, "HEAD"))
                    && Directory.Exists(Path.Combine(current, "objects"))
                    && Directory.Exists(Path.Combine(current, "refs")))
                {
                    // The user explicitly selected this repository. Name its Git
                    // directory explicitly rather than weakening safe.bareRepository.
                    return current;
                }
            }
            return null;
        }
    }
}
