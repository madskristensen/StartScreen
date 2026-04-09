using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace StartScreen.Models
{
    /// <summary>
    /// Type of MRU item (solution, project, or folder).
    /// </summary>
    public enum MruItemType
    {
        Solution,
        Project,
        Folder
    }

    /// <summary>
    /// Represents a Most Recently Used (MRU) item - a solution, project, or folder.
    /// </summary>
    public class MruItem : INotifyPropertyChanged
    {
        private string _name;
        private string _path;
        private MruItemType _type;
        private DateTime _lastAccessed;
        private bool _isPinned;
        private string _gitBranch;
        private int? _commitsAhead;
        private int? _commitsBehind;
        private bool _hasUncommittedChanges;
        private DateTime? _lastCommitTime;
        private bool? _exists;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Path
        {
            get => _path;
            set
            {
                if (_path != value)
                {
                    _path = value;
                    _exists = null; // Reset lazy existence check
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Exists));
                }
            }
        }

        public MruItemType Type
        {
            get => _type;
            set
            {
                if (_type != value)
                {
                    _type = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime LastAccessed
        {
            get => _lastAccessed;
            set
            {
                if (_lastAccessed != value)
                {
                    _lastAccessed = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (_isPinned != value)
                {
                    _isPinned = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// The current Git branch name, or null if the item is not in a Git repository.
        /// </summary>
        public string GitBranch
        {
            get => _gitBranch;
            set
            {
                if (_gitBranch != value)
                {
                    _gitBranch = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasGitBranch));
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

        /// <summary>
        /// Whether a Git branch is available for display.
        /// </summary>
        public bool HasGitBranch => !string.IsNullOrEmpty(_gitBranch);

        /// <summary>
        /// Number of commits ahead of the upstream tracking branch.
        /// Null if no upstream is configured.
        /// </summary>
        public int? CommitsAhead
        {
            get => _commitsAhead;
            set
            {
                if (_commitsAhead != value)
                {
                    _commitsAhead = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasAheadBehind));
                    OnPropertyChanged(nameof(AheadBehindText));
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

        /// <summary>
        /// Number of commits behind the upstream tracking branch.
        /// Null if no upstream is configured.
        /// </summary>
        public int? CommitsBehind
        {
            get => _commitsBehind;
            set
            {
                if (_commitsBehind != value)
                {
                    _commitsBehind = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasAheadBehind));
                    OnPropertyChanged(nameof(AheadBehindText));
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

        /// <summary>
        /// Whether there are uncommitted changes in the working directory.
        /// </summary>
        public bool HasUncommittedChanges
        {
            get => _hasUncommittedChanges;
            set
            {
                if (_hasUncommittedChanges != value)
                {
                    _hasUncommittedChanges = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

        /// <summary>
        /// The timestamp of the last commit on the current branch.
        /// </summary>
        public DateTime? LastCommitTime
        {
            get => _lastCommitTime;
            set
            {
                if (_lastCommitTime != value)
                {
                    _lastCommitTime = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LastCommitTimeText));
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

        /// <summary>
        /// Whether ahead/behind information is available for display.
        /// </summary>
        public bool HasAheadBehind => _commitsAhead.HasValue || _commitsBehind.HasValue;

        /// <summary>
        /// Formatted ahead/behind text (e.g., "up arrow 2 down arrow 3").
        /// </summary>
        public string AheadBehindText
        {
            get
            {
                if (!HasAheadBehind)
                    return string.Empty;

                var parts = new System.Collections.Generic.List<string>();
                if (_commitsAhead > 0)
                    parts.Add($"\u2191{_commitsAhead}");
                if (_commitsBehind > 0)
                    parts.Add($"\u2193{_commitsBehind}");

                return string.Join(" ", parts);
            }
        }

        /// <summary>
        /// Formatted relative time for last commit (e.g., "2 hours ago").
        /// </summary>
        public string LastCommitTimeText
        {
            get
            {
                if (!_lastCommitTime.HasValue)
                    return string.Empty;

                var span = DateTime.Now - _lastCommitTime.Value;

                if (span.TotalMinutes < 1)
                    return "just now";
                if (span.TotalMinutes < 60)
                    return $"{(int)span.TotalMinutes}m ago";
                if (span.TotalHours < 24)
                    return $"{(int)span.TotalHours}h ago";
                if (span.TotalDays < 7)
                    return $"{(int)span.TotalDays}d ago";
                if (span.TotalDays < 30)
                    return $"{(int)(span.TotalDays / 7)}w ago";

                return _lastCommitTime.Value.ToString("MMM d");
            }
        }

        /// <summary>
        /// Composite tooltip text showing path and Git status info.
        /// </summary>
        public string ToolTipText
        {
            get
            {
                var lines = new System.Collections.Generic.List<string> { Path };

                if (HasGitBranch)
                {
                    var branchInfo = $"Branch: {GitBranch}";
                    if (HasAheadBehind)
                        branchInfo += $" ({AheadBehindText})";
                    if (HasUncommittedChanges)
                        branchInfo += " *";
                    lines.Add(branchInfo);
                }

                if (_lastCommitTime.HasValue)
                    lines.Add($"Last commit: {LastCommitTimeText}");

                return string.Join("\n", lines);
            }
        }

        /// <summary>
        /// Returns a friendly time group label based on LastAccessed relative to today.
        /// </summary>
        public string TimeGroup
        {
            get
            {
                var today = DateTime.Today;
                var date = _lastAccessed.Date;

                if (date == today)
                    return "Today";
                if (date == today.AddDays(-1))
                    return "Yesterday";
                if (date > today.AddDays(-7))
                    return "This week";
                if (date > today.AddDays(-30))
                    return "This month";

                return "Older";
            }
        }

        /// <summary>
        /// Returns a formatted date/time string for display (e.g. "4/8/2026 1:44 PM").
        /// </summary>
        public string FormattedDate => _lastAccessed.ToString("d");

        /// <summary>
        /// Lazy-loaded file existence check. Cached after first access.
        /// </summary>
        public bool Exists
        {
            get
            {
                if (!_exists.HasValue)
                {
                    _exists = CheckExists();
                }
                return _exists.Value;
            }
        }

        private bool CheckExists()
        {
            if (string.IsNullOrWhiteSpace(_path))
                return false;

            try
            {
                if (Type == MruItemType.Folder)
                    return Directory.Exists(_path);
                else
                    return File.Exists(_path);
            }
            catch
            {
                return false;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
