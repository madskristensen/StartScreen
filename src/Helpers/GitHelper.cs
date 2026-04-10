using System;
using System.IO;
using LibGit2Sharp;
using StartScreen.Models;

namespace StartScreen.Helpers
{
    /// <summary>
    /// Git repository status detection using LibGit2Sharp.
    /// </summary>
    internal static class GitHelper
    {
        private const int MaxParentWalk = 10;

        /// <summary>
        /// Gets comprehensive Git status for a given solution, project, or folder path.
        /// Returns a GitStatus object with all available information.
        /// </summary>
        public static GitStatus GetGitStatus(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new GitStatus();

            try
            {
                var startDir = Directory.Exists(path)
                    ? path
                    : Path.GetDirectoryName(path);

                var repoPath = FindRepositoryPath(startDir);
                if (repoPath == null)
                    return new GitStatus();

                using (var repo = new Repository(repoPath))
                {
                    var status = new GitStatus();

                    // Branch name (or detached HEAD SHA)
                    if (repo.Head.FriendlyName == "(no branch)")
                    {
                        // Detached HEAD - show abbreviated commit SHA
                        status.BranchName = repo.Head.Tip?.Sha?.Substring(0, 7);
                    }
                    else
                    {
                        status.BranchName = repo.Head.FriendlyName;
                    }

                    // Ahead/behind tracking branch
                    if (repo.Head.IsTracking && repo.Head.TrackedBranch != null)
                    {
                        var tracking = repo.Head.TrackingDetails;
                        status.CommitsAhead = tracking.AheadBy;
                        status.CommitsBehind = tracking.BehindBy;
                    }

                    // Uncommitted changes (staged or unstaged)
                    var repoStatus = repo.RetrieveStatus(new StatusOptions
                    {
                        IncludeUntracked = false,
                        RecurseUntrackedDirs = false
                    });
                    status.HasUncommittedChanges = repoStatus.IsDirty;

                    // Last commit time
                    if (repo.Head.Tip != null)
                    {
                        status.LastCommitTime = repo.Head.Tip.Author.When.LocalDateTime;
                    }

                    return status;
                }
            }
            catch (RepositoryNotFoundException)
            {
                // Not a git repository - return empty status
                return new GitStatus();
            }
            catch (Exception ex)
            {
                ex.Log();
                return new GitStatus();
            }
        }

        /// <summary>
        /// Walks up from the given directory looking for a Git repository root.
        /// </summary>
        private static string FindRepositoryPath(string startDir)
        {
            if (string.IsNullOrEmpty(startDir))
                return null;

            var current = startDir;

            for (int i = 0; i < MaxParentWalk && current != null; i++)
            {
                var gitPath = Path.Combine(current, ".git");

                // Standard repository
                if (Directory.Exists(gitPath))
                    return current;

                // Submodule/worktree: .git is a file
                if (File.Exists(gitPath))
                    return current;

                current = Directory.GetParent(current)?.FullName;
            }

            return null;
        }
    }
}
