using System;
using System.IO;

namespace StartScreen.Helpers
{
    /// <summary>
    /// Lightweight Git branch detection by reading .git/HEAD directly (no git CLI dependency).
    /// </summary>
    internal static class GitHelper
    {
        private const int MaxParentWalk = 10;
        private const string RefPrefix = "ref: refs/heads/";

        /// <summary>
        /// Gets the current Git branch name for a given solution, project, or folder path.
        /// Returns null if the path is not inside a Git repository.
        /// </summary>
        public static string GetCurrentBranch(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                var startDir = Directory.Exists(path)
                    ? path
                    : Path.GetDirectoryName(path);

                var gitDir = FindGitDirectory(startDir);
                if (gitDir == null)
                    return null;

                var headFile = Path.Combine(gitDir, "HEAD");
                if (!File.Exists(headFile))
                    return null;

                var headContent = File.ReadAllText(headFile).Trim();

                // Symbolic ref: "ref: refs/heads/main"
                if (headContent.StartsWith(RefPrefix, StringComparison.Ordinal))
                {
                    return headContent.Substring(RefPrefix.Length);
                }

                // Detached HEAD: raw commit SHA — show abbreviated hash
                if (headContent.Length >= 7)
                {
                    return headContent.Substring(0, 7);
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            return null;
        }

        /// <summary>
        /// Walks up from the given directory looking for a .git directory or file (submodule worktree).
        /// </summary>
        private static string FindGitDirectory(string startDir)
        {
            if (string.IsNullOrEmpty(startDir))
                return null;

            var current = startDir;

            for (int i = 0; i < MaxParentWalk && current != null; i++)
            {
                var gitPath = Path.Combine(current, ".git");

                if (Directory.Exists(gitPath))
                {
                    return gitPath;
                }

                // Submodule/worktree: .git is a file containing "gitdir: <path>"
                if (File.Exists(gitPath))
                {
                    var content = File.ReadAllText(gitPath).Trim();
                    if (content.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    {
                        var resolved = content.Substring("gitdir:".Length).Trim();
                        if (!Path.IsPathRooted(resolved))
                        {
                            resolved = Path.GetFullPath(Path.Combine(current, resolved));
                        }

                        if (Directory.Exists(resolved))
                        {
                            return resolved;
                        }
                    }
                }

                current = Directory.GetParent(current)?.FullName;
            }

            return null;
        }
    }
}
