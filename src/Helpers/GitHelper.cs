using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;
using StartScreen.Models;

namespace StartScreen.Helpers
{
    internal sealed class GitCommandResult
    {
        public static GitCommandResult Success { get; } = new GitCommandResult(true, null);

        public GitCommandResult(bool succeeded, string errorMessage)
        {
            Succeeded = succeeded;
            ErrorMessage = errorMessage;
        }

        public bool Succeeded { get; }

        public string ErrorMessage { get; }
    }

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

                    // Branch name (or tag/detached HEAD SHA)
                    if (repo.Head.FriendlyName == "(no branch)")
                    {
                        // Detached HEAD - prefer tag name if HEAD points to a tag
                        var tag = repo.Head.Tip != null
                            ? repo.Tags.FirstOrDefault(t => t.Target.Sha == repo.Head.Tip.Sha)
                            : null;

                        status.BranchName = tag != null
                            ? tag.FriendlyName
                            : repo.Head.Tip?.Sha?.Substring(0, 7);
                    }
                    else
                    {
                        status.BranchName = repo.Head.FriendlyName;
                    }

                    // Ahead/behind tracking branch
                    if (repo.Head.IsTracking && repo.Head.TrackedBranch != null)
                    {
                        BranchTrackingDetails tracking = repo.Head.TrackingDetails;
                        status.CommitsAhead = tracking.AheadBy;
                        status.CommitsBehind = tracking.BehindBy;
                    }

                    // Uncommitted changes (staged or unstaged)
                    RepositoryStatus repoStatus = repo.RetrieveStatus(new StatusOptions
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

                    // Stash count
                    status.StashCount = repo.Stashes.Count();

                    // Current operation (merge, rebase, cherry-pick, etc.)
                    status.CurrentOperation = GetCurrentOperationDisplayString(repo.Info.CurrentOperation);

                    // Remote URL (origin)
                    var origin = repo.Network.Remotes["origin"];
                    if (origin != null)
                    {
                        status.RemoteUrl = origin.Url;
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
        internal static string FindRepositoryPath(string startDir)
        {
            if (string.IsNullOrEmpty(startDir))
                return null;

            var current = startDir;

            for (var i = 0; i < MaxParentWalk && current != null; i++)
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

        /// <summary>
        /// Maps LibGit2Sharp CurrentOperation enum to a user-friendly display string.
        /// </summary>
        private static string GetCurrentOperationDisplayString(CurrentOperation operation)
        {
            return operation switch
            {
                CurrentOperation.Merge => "Merge",
                CurrentOperation.Rebase => "Rebase",
                CurrentOperation.RebaseInteractive => "Rebase",
                CurrentOperation.RebaseMerge => "Rebase",
                CurrentOperation.CherryPick => "Cherry-pick",
                CurrentOperation.Revert => "Revert",
                CurrentOperation.Bisect => "Bisect",
                _ => null,
            };
        }

        /// <summary>
        /// Runs git fetch --all --quiet for the specified repository.
        /// Best-effort: returns silently on any failure (offline, timeout, no remote, etc.).
        /// Credential prompts are suppressed so the background fetch never pops up
        /// login windows; if stored credentials don't work, the fetch fails silently.
        /// </summary>
        internal static void FetchAll(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath) || !Directory.Exists(repoPath))
                return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    // -c core.askPass= disables any GUI/console askpass helper for this invocation.
                    Arguments = "-c core.askPass= fetch --all --quiet",
                    WorkingDirectory = repoPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                // Suppress all interactive credential prompts from git and the
                // Git Credential Manager so a background fetch can't spam login dialogs.
                SuppressCredentialPrompts(psi);

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                        return;

                    // 5-second timeout (best-effort, silent failure on slow networks)
                    if (!process.WaitForExit(5000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // Best-effort kill
                        }
                    }
                }
            }
            catch
            {
                // Best-effort: silent failure for offline, no git, no remote, etc.
            }
        }

        /// <summary>
        /// Configures the given <see cref="ProcessStartInfo"/> so that git, the
        /// Git Credential Manager, OpenSSH and any askpass helper will never
        /// display an interactive prompt. Used for background operations that
        /// must not pop up login windows when stored credentials fail.
        /// </summary>
        internal static void SuppressCredentialPrompts(ProcessStartInfo psi)
        {
            // Prevents git itself from prompting on the terminal.
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

            // Prevents Git Credential Manager (and the older Git Credential
            // Manager for Windows) from showing its UI. Both names are set to
            // cover GCM Core and the legacy GCM4W.
            psi.EnvironmentVariables["GCM_INTERACTIVE"] = "Never";
            psi.EnvironmentVariables["GIT_ASKPASS"] = "echo";

            // Prevents OpenSSH from launching its GUI askpass dialog when
            // pulling from an SSH remote with a passphrase-protected key.
            psi.EnvironmentVariables["SSH_ASKPASS"] = "echo";
            psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "never";

            // Prevents the generic display askpass that some installers wire up.
            psi.EnvironmentVariables["DISPLAY"] = string.Empty;
        }

        /// <summary>
        /// Runs git pull --quiet for the specified repository.
        /// </summary>
        internal static async Task<GitCommandResult> PullAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath) || !Directory.Exists(repoPath))
                return new GitCommandResult(false, "The Git repository could not be found.");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "pull --quiet",
                    WorkingDirectory = repoPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                        return new GitCommandResult(false, "Git could not be started.");

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    process.WaitForExit();

                    var output = await outputTask;
                    var error = await errorTask;

                    if (process.ExitCode == 0)
                        return GitCommandResult.Success;

                    var message = string.IsNullOrWhiteSpace(error) ? output : error;

                    return new GitCommandResult(false, string.IsNullOrWhiteSpace(message) ? "Git pull failed." : message.Trim());
                }
            }
            catch (Exception ex)
            {
                ex.Log();
                return new GitCommandResult(false, ex.Message);
            }
        }

        /// <summary>
        /// Re-reads only the ahead/behind counts for a repository after fetch.
        /// Returns (ahead, behind) or (null, null) on error.
        /// </summary>
        internal static (int? ahead, int? behind) GetUpdatedAheadBehind(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return (null, null);

            try
            {
                var startDir = Directory.Exists(path)
                    ? path
                    : Path.GetDirectoryName(path);

                var repoPath = FindRepositoryPath(startDir);
                if (repoPath == null)
                    return (null, null);

                using (var repo = new Repository(repoPath))
                {
                    if (repo.Head.IsTracking && repo.Head.TrackedBranch != null)
                    {
                        BranchTrackingDetails tracking = repo.Head.TrackingDetails;
                        return (tracking.AheadBy, tracking.BehindBy);
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            return (null, null);
        }
    }
}
