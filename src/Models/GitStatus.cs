using System;

namespace StartScreen.Models
{
    /// <summary>
    /// Contains Git repository status information for an MRU item.
    /// </summary>
    internal sealed class GitStatus
    {
        /// <summary>
        /// The current branch name, or abbreviated commit SHA if in detached HEAD state.
        /// Null if not a Git repository.
        /// </summary>
        public string BranchName { get; set; }

        /// <summary>
        /// Number of commits ahead of the upstream tracking branch.
        /// Null if no upstream is configured or not a Git repository.
        /// </summary>
        public int? CommitsAhead { get; set; }

        /// <summary>
        /// Number of commits behind the upstream tracking branch.
        /// Null if no upstream is configured or not a Git repository.
        /// </summary>
        public int? CommitsBehind { get; set; }

        /// <summary>
        /// Whether there are uncommitted changes (staged or unstaged).
        /// </summary>
        public bool HasUncommittedChanges { get; set; }

        /// <summary>
        /// The timestamp of the last commit on the current branch.
        /// Null if no commits exist or not a Git repository.
        /// </summary>
        public DateTime? LastCommitTime { get; set; }

        /// <summary>
        /// Number of stashed changes in the repository.
        /// Default is 0 if not a Git repository.
        /// </summary>
        public int StashCount { get; set; }

        /// <summary>
        /// The current git operation in progress (e.g., "Merge", "Rebase", "CherryPick").
        /// Null if no operation is in progress or not a Git repository.
        /// </summary>
        public string CurrentOperation { get; set; }

        /// <summary>
        /// Whether this represents a valid Git repository.
        /// </summary>
        public bool IsGitRepository => !string.IsNullOrEmpty(BranchName);
    }
}
