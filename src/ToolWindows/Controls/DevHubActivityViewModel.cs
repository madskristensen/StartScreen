using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using StartScreen.Models;
using StartScreen.Models.DevHub;

namespace StartScreen.ToolWindows.Controls
{
    internal sealed class DevHubActivityViewModel : INotifyPropertyChanged
    {
        private readonly ObservableCollection<DevHubWorkItem> _visibleMyWork = new();
        private readonly ObservableCollection<DevHubWorkItem> _visibleIncoming = new();
        private IReadOnlyList<DevHubPullRequest> _pullRequests = Array.Empty<DevHubPullRequest>();
        private IReadOnlyList<DevHubIssue> _issues = Array.Empty<DevHubIssue>();
        private IReadOnlyList<DevHubCiRun> _builds = Array.Empty<DevHubCiRun>();
        private IReadOnlyList<DevHubWorkItem> _myWork = Array.Empty<DevHubWorkItem>();
        private IReadOnlyList<DevHubWorkItem> _incoming = Array.Empty<DevHubWorkItem>();
        private Dictionary<string, ActivityItemState> _itemStates = new();
        private bool _initialFilterPending = true;

        public DevHubActivityViewModel()
        {
            MyWorkItems = new ReadOnlyObservableCollection<DevHubWorkItem>(_visibleMyWork);
            IncomingItems = new ReadOnlyObservableCollection<DevHubWorkItem>(_visibleIncoming);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ReadOnlyObservableCollection<DevHubWorkItem> MyWorkItems { get; }
        public ReadOnlyObservableCollection<DevHubWorkItem> IncomingItems { get; }
        public DevHubWorkItemKind MyWorkKind { get; private set; } = DevHubWorkItemKind.All;
        public DevHubWorkItemKind IncomingKind { get; private set; } = DevHubWorkItemKind.PullRequest;
        public bool MyWorkAttention { get; private set; } = true;
        public int MyWorkCount => _myWork.Count;
        public int IncomingCount => _incoming.Count;
        public int AttentionCount => _myWork.Count(item => item.NeedsAttention);
        public int LoadedActivityCount => _pullRequests.Count + _issues.Count + _builds.Count;
        public string MyWorkCountText => MyWorkCount == 0 ? string.Empty : $" {MyWorkCount}";
        public string IncomingCountText => IncomingCount == 0 ? string.Empty : $" {IncomingCount}";
        public string AttentionFilterText => $"Attention ({AttentionCount})";
        public bool IsMyWorkEmpty => MyWorkItems.Count == 0;
        public bool IsIncomingEmpty => IncomingItems.Count == 0;
        public string MyWorkEmptyText => MyWorkAttention
            ? "No items need your attention in this view." : "No matching personal work in the loaded activity.";
        public string IncomingEmptyText => "No matching team activity.";

        public void UpdateRepositoryActivity(IReadOnlyList<DevHubPullRequest> pullRequests,
            IReadOnlyList<DevHubIssue> issues, IReadOnlyList<DevHubCiRun> builds,
            IEnumerable<CopilotActivitySession> sessions, DevHubSortOrder sortOrder, bool includeCompleted)
        {
            var previousLoadedCount = LoadedActivityCount;
            _pullRequests = pullRequests;
            _issues = issues;
            _builds = builds;
            Refresh(sessions, sortOrder, includeCompleted);
            if (previousLoadedCount != LoadedActivityCount)
                OnPropertyChanged(nameof(LoadedActivityCount));
        }

        public void Refresh(IEnumerable<CopilotActivitySession> sessions, DevHubSortOrder sortOrder, bool includeCompleted)
        {
            var previousMyWorkCount = MyWorkCount;
            var previousIncomingCount = IncomingCount;
            var previousAttentionCount = AttentionCount;
            var nextStates = new Dictionary<string, ActivityItemState>();
            _myWork = ReuseItems(DevHubWorkItem.GetMyWork(_pullRequests, _issues, sessions,
                sortOrder, includeCompleted, _builds), nextStates);
            _incoming = ReuseItems(DevHubWorkItem.GetIncoming(_pullRequests, _issues, _builds, sortOrder), nextStates);
            _itemStates = nextStates;
            UpdateProjections();
            if (previousMyWorkCount != MyWorkCount)
            {
                OnPropertyChanged(nameof(MyWorkCount));
                OnPropertyChanged(nameof(MyWorkCountText));
            }
            if (previousIncomingCount != IncomingCount)
            {
                OnPropertyChanged(nameof(IncomingCount));
                OnPropertyChanged(nameof(IncomingCountText));
            }
            if (previousAttentionCount != AttentionCount)
            {
                OnPropertyChanged(nameof(AttentionCount));
                OnPropertyChanged(nameof(AttentionFilterText));
            }
        }

        public void SetMyWorkFilter(DevHubWorkItemKind kind, bool attention)
        {
            CancelInitialFilterSelection();
            if (MyWorkKind == kind && MyWorkAttention == attention)
                return;
            MyWorkKind = kind;
            MyWorkAttention = attention;
            OnPropertyChanged(nameof(MyWorkKind));
            OnPropertyChanged(nameof(MyWorkAttention));
            OnPropertyChanged(nameof(MyWorkEmptyText));
            UpdateProjections();
        }

        public void SetIncomingFilter(DevHubWorkItemKind kind)
        {
            if (IncomingKind == kind)
                return;
            IncomingKind = kind;
            OnPropertyChanged(nameof(IncomingKind));
            UpdateProjections();
        }

        public void BeginVisit()
        {
            _initialFilterPending = true;
        }

        public void CancelInitialFilterSelection()
        {
            _initialFilterPending = false;
        }

        public bool TrySelectInitialFilter(bool loading, bool loadingCompleted = false)
        {
            if (!_initialFilterPending || loading || (MyWorkCount == 0 && !loadingCompleted))
                return false;
            _initialFilterPending = false;
            if (!MyWorkAttention)
                return false;
            var kind = GetInitialMyWorkKind(_myWork);
            SetMyWorkFilter(kind, kind == DevHubWorkItemKind.All);
            return true;
        }

        internal static DevHubWorkItemKind GetInitialMyWorkKind(IReadOnlyList<DevHubWorkItem> items)
        {
            if (items.Any(item => item.NeedsAttention))
                return DevHubWorkItemKind.All;
            return new[] { DevHubWorkItemKind.Copilot, DevHubWorkItemKind.PullRequest, DevHubWorkItemKind.Issue, DevHubWorkItemKind.Build }
                .FirstOrDefault(kind => items.Any(item => item.Kind == kind));
        }

        private void UpdateProjections()
        {
            var wasMyWorkEmpty = IsMyWorkEmpty;
            var wasIncomingEmpty = IsIncomingEmpty;
            SynchronizeItems(_visibleMyWork, DevHubWorkItem.Filter(_myWork, MyWorkKind, MyWorkAttention));
            SynchronizeItems(_visibleIncoming, DevHubWorkItem.Filter(_incoming, IncomingKind, false));
            if (wasMyWorkEmpty != IsMyWorkEmpty)
                OnPropertyChanged(nameof(IsMyWorkEmpty));
            if (wasIncomingEmpty != IsIncomingEmpty)
                OnPropertyChanged(nameof(IsIncomingEmpty));
        }

        private IReadOnlyList<DevHubWorkItem> ReuseItems(IReadOnlyList<DevHubWorkItem> items,
            Dictionary<string, ActivityItemState> nextStates)
        {
            return items.Select(item =>
            {
                var key = $"{item.Kind}:{item.CopilotSessionId ?? item.WebUrl}";
                var state = _itemStates.TryGetValue(key, out var previous) && previous.Matches(item)
                    ? previous : new ActivityItemState(item);
                nextStates[key] = state;
                return state.Item;
            }).ToArray();
        }

        internal static void SynchronizeItems(ObservableCollection<DevHubWorkItem> collection, IReadOnlyList<DevHubWorkItem> items)
        {
            var wanted = new HashSet<DevHubWorkItem>(items);
            for (var index = collection.Count - 1; index >= 0; index--)
            {
                if (!wanted.Contains(collection[index]))
                    collection.RemoveAt(index);
            }
            for (var index = 0; index < items.Count; index++)
            {
                if (index < collection.Count && ReferenceEquals(collection[index], items[index]))
                    continue;
                var existing = index + 1;
                while (existing < collection.Count && !ReferenceEquals(collection[existing], items[index]))
                    existing++;
                if (existing < collection.Count)
                    collection.Move(existing, index);
                else
                    collection.Insert(index, items[index]);
            }
            while (collection.Count > items.Count)
                collection.RemoveAt(collection.Count - 1);
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private sealed class ActivityItemState
        {
            private readonly DevHubLabel[] _labels;

            public ActivityItemState(DevHubWorkItem item)
            {
                Item = item;
                // Providers may mutate label collections between refreshes.
                _labels = item.Labels.Select(label => new DevHubLabel { Name = label.Name, Color = label.Color }).ToArray();
            }

            public DevHubWorkItem Item { get; }

            public bool Matches(DevHubWorkItem item)
            {
                return Item.Kind == item.Kind && Item.Title == item.Title && Item.Detail == item.Detail
                    && Item.StatusText == item.StatusText && Item.DurationText == item.DurationText
                    && Item.AttentionReason == item.AttentionReason && Item.NextActionText == item.NextActionText
                    && Item.AuthorText == item.AuthorText && Item.IsAssignedToCurrentUser == item.IsAssignedToCurrentUser
                    && Item.StatusDetails == item.StatusDetails && Item.CopilotStatus == item.CopilotStatus
                    && Item.CopilotStatusDetails == item.CopilotStatusDetails && Item.UpdatedAt == item.UpdatedAt
                    && Item.BranchDisplayText == item.BranchDisplayText && Item.IsCompleted == item.IsCompleted
                    && Item.Section == item.Section && Equals(Item.RepoIdentifier, item.RepoIdentifier)
                    && _labels.Length == item.Labels.Count
                    && _labels.Zip(item.Labels, (left, right) => left.Name == right.Name && left.Color == right.Color).All(equal => equal);
            }
        }
    }
}