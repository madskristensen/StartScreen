using System.IO;

namespace StartScreen.Models
{
    internal sealed class CopilotActivitySession
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string WorkingDirectory { get; set; }
        public DateTimeOffset LastActivity { get; set; }
        public bool? IsResponding { get; set; }
        public bool IsTranscript { get; set; }
        public bool IsCached { get; set; }
        public int PendingPermissions { get; set; }
        public int PendingQuestions { get; set; }

        public bool NeedsAttention => !IsCached && !IsTranscript && (PendingPermissions > 0 || PendingQuestions > 0);
        public bool IsRunning => !IsCached && !IsTranscript && IsResponding == true;
        public string StatusBadge => IsCached ? string.Empty : NeedsAttention ? "Waiting" : IsRunning ? "Running"
            : IsTranscript ? "Saved" : IsResponding == false ? "Idle" : string.Empty;
        public string Status => IsCached ? "Cached conversation. Reconnecting to check live activity."
            : IsTranscript ? "Saved conversation"
            : PendingPermissions > 0 && PendingQuestions > 0 ? "Waiting for permission and an answer"
            : PendingPermissions > 0 ? "Waiting for permission"
            : PendingQuestions > 0 ? "Waiting for your answer"
            : IsRunning ? "Running"
            : IsResponding == false ? "Idle" : "Live activity is not available for this conversation.";

        internal CopilotActivitySession AsCached() => new()
        {
            Id = Id,
            Title = Title,
            WorkingDirectory = WorkingDirectory,
            LastActivity = LastActivity,
            IsTranscript = IsTranscript,
            IsCached = true,
        };

        internal bool MatchesWorkspace(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(WorkingDirectory))
                return false;

            var directory = WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar);
            root = root.TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}