using System.Linq;

namespace StartScreen.Models
{
    internal sealed class GitWorktreeInfo
    {
        public string Path { get; set; }
        public string Branch { get; set; }
        public bool IsCurrent { get; set; }
        public bool IsBare { get; set; }
        public bool IsLocked { get; set; }
        public bool IsPrunable { get; set; }
        public bool CanOpen => !IsBare && !IsPrunable;
        public string BranchLabel => IsBare ? "Bare repository" : string.IsNullOrWhiteSpace(Branch) ? "Detached HEAD" : Branch;
        public string Status => string.Join(", ", new[]
        {
            IsCurrent ? "current" : null, IsBare ? "bare" : null,
            IsLocked ? "locked" : null, IsPrunable ? "prunable" : null,
        }.Where(value => value != null));
        public string MenuText => Clean(BranchLabel) + " - "
            + Clean(System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')))
            + (Status.Length == 0 ? "" : " (" + Status + ")");

        private static string Clean(string text) => text.Replace("&", "&&").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }
}
