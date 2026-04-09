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
        /// Composite tooltip text showing path and optionally branch info.
        /// </summary>
        public string ToolTipText
        {
            get
            {
                if (HasGitBranch)
                    return $"{Path}\nBranch: {GitBranch}";

                return Path;
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
