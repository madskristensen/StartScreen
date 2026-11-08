using System.IO;

namespace StartScreen.Models
{
    internal enum CopilotSessionOrigin
    {
        // Keep existing origin IDs; 1 was VS Code.
        VisualStudio = 0,
        Cli = 2,
        GitHub = 3
    }

    internal sealed class CopilotActivitySession
    {
        public string Id { get; set; }
        public CopilotSessionOrigin Origin { get; set; }
        public string SessionHome { get; set; }
        public string WebUrl { get; set; }
        public string Repository { get; set; }
        public string RemoteState { get; set; }
        public string Title { get; set; }
        public string WorkingDirectory { get; set; }
        public DateTimeOffset LastActivity { get; set; }
        public bool? IsResponding { get; set; }
        public bool IsTranscript { get; set; }
        public bool IsCached { get; set; }
        public int PendingPermissions { get; set; }
        public int PendingQuestions { get; set; }

        public string Key => Origin + ":" + Id;
        public string OriginName => Origin switch
        {
            CopilotSessionOrigin.VisualStudio => "Visual Studio",
            CopilotSessionOrigin.Cli => "CLI",
            CopilotSessionOrigin.GitHub => "GitHub",
            _ => throw new InvalidOperationException("Unknown Copilot session origin."),
        };
        public bool NeedsAttention => !IsCached && !IsTranscript && (PendingPermissions > 0 || PendingQuestions > 0 || RemoteState == "waiting_for_user");
        public bool IsRunning => !IsCached && !IsTranscript && (IsResponding == true || RemoteState == "in_progress" || RemoteState == "queued");
        public string StatusBadge => IsCached ? string.Empty : NeedsAttention ? "Waiting" : IsRunning ? "Running"
            : IsTranscript ? "Saved" : RemoteState != null ? RemoteState.Replace('_', ' ') : IsResponding == false ? "Idle" : string.Empty;
        public string Status => IsCached ? "Cached conversation. Reconnecting to check live activity."
            : IsTranscript ? "Saved conversation"
            : RemoteState != null ? "GitHub: " + RemoteState.Replace('_', ' ')
            : PendingPermissions > 0 && PendingQuestions > 0 ? "Waiting for permission and an answer"
            : PendingPermissions > 0 ? "Waiting for permission"
            : PendingQuestions > 0 ? "Waiting for your answer"
            : IsRunning ? "Running"
            : IsResponding == false ? "Idle" : "Live activity is not available for this conversation.";

        internal CopilotActivitySession Clone() => (CopilotActivitySession)MemberwiseClone();

        internal CopilotActivitySession AsCached() => new()
        {
            Id = Id,
            Origin = Origin,
            SessionHome = SessionHome,
            WebUrl = WebUrl,
            Repository = Repository,
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