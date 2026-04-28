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
        private int _stashCount;
        private string _currentOperation;
        private bool? _exists;
        private string _remoteUrl;
        private bool _isSelected;

        /// <summary>
        /// The raw MRU entry strings from IVsMRUItemsStore, used for deletion.
        /// Multiple entries may exist when deduplication collapses .sln and .slnx.
        /// </summary>
        internal System.Collections.Generic.List<string> RawMruEntries { get; } = [];

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
        /// Number of stashed changes in the repository.
        /// </summary>
        public int StashCount
        {
            get => _stashCount;
            set
            {
                if (_stashCount != value)
                {
                    _stashCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasStashes));
                    OnPropertyChanged(nameof(StashText));
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

        /// <summary>
        /// The current git operation in progress (e.g., "Merge", "Rebase").
        /// Null if no operation is active.
        /// </summary>
        public string CurrentOperation
        {
            get => _currentOperation;
            set
            {
                if (_currentOperation != value)
                {
                    _currentOperation = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCurrentOperation));
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

        /// <summary>
        /// Whether ahead/behind information is available for display.
        /// </summary>
        public bool HasAheadBehind => _commitsAhead.HasValue || _commitsBehind.HasValue;

        /// <summary>
        /// Whether stash information is available for display.
        /// </summary>
        public bool HasStashes => _stashCount > 0;

        /// <summary>
        /// Whether a git operation is currently in progress.
        /// </summary>
        public bool HasCurrentOperation => !string.IsNullOrEmpty(_currentOperation);

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
        /// Formatted stash text (e.g., "2 stashes").
        /// </summary>
        public string StashText
        {
            get
            {
                if (_stashCount == 0)
                    return string.Empty;

                return _stashCount == 1 ? "1 stash" : $"{_stashCount} stashes";
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

                TimeSpan span = DateTime.Now - _lastCommitTime.Value;

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

                if (HasStashes)
                    lines.Add($"Stashes: {StashCount}");

                if (HasCurrentOperation)
                    lines.Add($"Operation: {CurrentOperation} in progress");

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
                DateTime today = DateTime.Today;
                DateTime date = _lastAccessed.Date;

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

        /// <summary>
        /// Re-checks file existence on disk and raises PropertyChanged if the value changed.
        /// Safe to call from a background thread.
        /// </summary>
        public void RefreshExists()
        {
            var newValue = CheckExists();
            if (_exists != newValue)
            {
                _exists = newValue;
                OnPropertyChanged(nameof(Exists));
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

        /// <summary>
        /// The git remote origin URL for this repository, or null if not a git repo.
        /// Used to identify the hosting provider (GitHub, ADO, etc.).
        /// </summary>
        public string RemoteUrl
        {
            get => _remoteUrl;
            set
            {
                if (_remoteUrl != value)
                {
                    _remoteUrl = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasRemoteUrl));
                }
            }
        }

        /// <summary>
        /// Whether a remote URL is available for this item.
        /// </summary>
        public bool HasRemoteUrl => !string.IsNullOrEmpty(_remoteUrl);

        /// <summary>
        /// Updates all Git-related properties from a status snapshot.
        /// </summary>
        internal void ApplyGitStatus(GitStatus status)
        {
            if (status == null)
                return;

            GitBranch = status.BranchName;
            CommitsAhead = status.CommitsAhead;
            CommitsBehind = status.CommitsBehind;
            HasUncommittedChanges = status.HasUncommittedChanges;
            LastCommitTime = status.LastCommitTime;
            StashCount = status.StashCount;
            CurrentOperation = status.CurrentOperation;
            RemoteUrl = status.RemoteUrl;
        }

        /// <summary>
        /// Whether this MRU item is currently selected in the list.
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
