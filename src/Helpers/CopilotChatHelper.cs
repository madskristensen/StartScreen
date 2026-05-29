using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StartScreen.Models;

namespace StartScreen.Helpers
{
    /// <summary>
    /// Detects whether a repository or solution root has any Copilot Chat sessions
    /// stored on disk under its <c>.vs\&lt;solution&gt;\copilot-chat\*\sessions</c> folders.
    /// </summary>
    internal static class CopilotChatHelper
    {
        /// <summary>
        /// Returns the total number of Copilot Chat session files for the given
        /// MRU item. Looks under the item's own root and the enclosing git
        /// repository root (if any), in <c>.vs\*\copilot-chat\*\sessions\*</c>.
        /// </summary>
        public static int CountSessions(string itemPath, MruItemType type)
        {
            if (string.IsNullOrWhiteSpace(itemPath))
            {
                return 0;
            }

            var itemRoot = type == MruItemType.Folder
                ? itemPath
                : Path.GetDirectoryName(itemPath);

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return CountSessionsForRoot(itemRoot, visited)
                 + CountSessionsForRoot(GitHelper.FindRepositoryPath(itemRoot), visited);
        }

        /// <summary>
        /// Returns true if any Copilot Chat session file exists for the given MRU
        /// item. Convenience wrapper around <see cref="CountSessions"/>.
        /// </summary>
        public static bool HasSessions(string itemPath, MruItemType type)
            => CountSessions(itemPath, type) > 0;

        /// <summary>
        /// Deletes every Copilot Chat session file found under the item's own root
        /// and the enclosing git repository root. Returns the number of session
        /// files that were deleted. Best-effort: individual delete failures are
        /// logged and skipped rather than thrown.
        /// </summary>
        public static int DeleteAllSessions(string itemPath, MruItemType type)
        {
            if (string.IsNullOrWhiteSpace(itemPath))
            {
                return 0;
            }

            var itemRoot = type == MruItemType.Folder
                ? itemPath
                : Path.GetDirectoryName(itemPath);

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deleted = DeleteSessionsForRoot(itemRoot, visited);
            deleted += DeleteSessionsForRoot(GitHelper.FindRepositoryPath(itemRoot), visited);
            return deleted;
        }

        private static int CountSessionsForRoot(string root, HashSet<string> visited)
        {
            if (string.IsNullOrEmpty(root) || !visited.Add(root))
            {
                return 0;
            }

            var count = 0;

            try
            {
                var vsDir = Path.Combine(root, ".vs");
                if (!Directory.Exists(vsDir))
                {
                    return 0;
                }

                // Layout: <root>\.vs\<solution-name>\copilot-chat\<id>\sessions\<guid>
                foreach (var slnDir in Directory.EnumerateDirectories(vsDir))
                {
                    var copilotChatDir = Path.Combine(slnDir, "copilot-chat");
                    if (!Directory.Exists(copilotChatDir))
                    {
                        continue;
                    }

                    foreach (var idDir in Directory.EnumerateDirectories(copilotChatDir))
                    {
                        var sessionsDir = Path.Combine(idDir, "sessions");
                        if (!Directory.Exists(sessionsDir))
                        {
                            continue;
                        }

                        count += Directory.EnumerateFiles(sessionsDir).Count();
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            return count;
        }

        private static int DeleteSessionsForRoot(string root, HashSet<string> visited)
        {
            if (string.IsNullOrEmpty(root) || !visited.Add(root))
            {
                return 0;
            }

            var deleted = 0;

            try
            {
                var vsDir = Path.Combine(root, ".vs");
                if (!Directory.Exists(vsDir))
                {
                    return 0;
                }

                foreach (var slnDir in Directory.EnumerateDirectories(vsDir))
                {
                    var copilotChatDir = Path.Combine(slnDir, "copilot-chat");
                    if (!Directory.Exists(copilotChatDir))
                    {
                        continue;
                    }

                    foreach (var idDir in Directory.EnumerateDirectories(copilotChatDir))
                    {
                        var sessionsDir = Path.Combine(idDir, "sessions");
                        if (!Directory.Exists(sessionsDir))
                        {
                            continue;
                        }

                        foreach (var file in Directory.EnumerateFiles(sessionsDir))
                        {
                            try
                            {
                                File.Delete(file);
                                deleted++;
                            }
                            catch (Exception ex)
                            {
                                ex.Log();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            return deleted;
        }
    }
}
