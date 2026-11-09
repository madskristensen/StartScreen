using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models;
using StartScreen.Services;

namespace StartScreen.ToolWindows
{
    public partial class StartScreenViewModel
    {
        private WorkspaceTagsService _workspaceTags = new();
        private string _tagFilter = "";
        private string _tagStatus;
        private bool _tagsAvailable = true;
        private readonly SemaphoreSlim _tagsSaveGate = new(1, 1);

        public IEnumerable<string> AvailableWorkspaceTags => _workspaceTags.AvailableTags;
        public ObservableCollection<TagFilterOption> TagFilters { get; } = new();
        public bool HasTagFilter => WorkspaceTagsService.ParseTags(TagFilter).Length > 0;
        public string TagStatus
        {
            get => _tagStatus;
            private set { _tagStatus = value; OnPropertyChanged(); }
        }
        public string TagFilter
        {
            get => _tagFilter;
            set
            {
                if (_tagFilter == (value ?? ""))
                    return;
                _tagFilter = value ?? "";
                OnPropertyChanged();
                RefreshTagFilters();
                OnPropertyChanged(nameof(HasTagFilter));
                RefreshMruCollections();
            }
        }
        internal void BeginTagEdit()
        {
            if (_tagsAvailable)
                TagStatus = null;
        }

        internal async Task LoadWorkspaceTagsAsync(string json)
        {
            try
            {
                ApplyWorkspaceTags(WorkspaceTagsService.Deserialize(json));
                _tagsAvailable = true;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                _tagsAvailable = false;
                TagStatus = "Saved tags could not be loaded. Existing settings have not been changed.";
            }
        }

        internal void ApplyWorkspaceTags(WorkspaceTagsService tags)
        {
            _workspaceTags = tags;
            ApplyMruTags();
            OnPropertyChanged(nameof(AvailableWorkspaceTags));
            var available = AvailableWorkspaceTags.ToArray();
            var filter = string.Join(", ", WorkspaceTagsService.ParseTags(TagFilter)
                .Where(tag => available.Contains(tag, StringComparer.OrdinalIgnoreCase)));
            if (TagFilter != filter)
                TagFilter = filter;
            else
            {
                RefreshTagFilters();
                RefreshMruCollections();
            }
        }

        private void RefreshTagFilters()
        {
            var selected = WorkspaceTagsService.ParseTags(TagFilter);
            var names = selected.Concat(_workspaceTags.AvailableTags).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!TagFilters.Select(option => option.Name).SequenceEqual(names, StringComparer.Ordinal))
            {
                TagFilters.Clear();
                foreach (var name in names)
                    TagFilters.Add(new TagFilterOption(name));
            }
            foreach (var option in TagFilters)
                option.IsSelected = selected.Contains(option.Name, StringComparer.OrdinalIgnoreCase);
        }

        internal void ToggleTagFilter(string tag)
        {
            var selected = WorkspaceTagsService.ParseTags(TagFilter);
            TagFilter = string.Join(", ", selected.Contains(tag, StringComparer.OrdinalIgnoreCase)
                ? selected.Where(value => !value.Equals(tag, StringComparison.OrdinalIgnoreCase))
                : selected.Append(tag));
        }

        public sealed class TagFilterOption(string name) : INotifyPropertyChanged
        {
            private bool _isSelected;
            public string Name { get; } = name;
            public bool IsSelected
            {
                get => _isSelected;
                internal set
                {
                    if (_isSelected == value)
                        return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private void ApplyMruTags()
        {
            foreach (var item in _allMruItems)
                item.TagsText = string.Join(", ", _workspaceTags.GetTags(item.Path));
        }

        internal Task<bool> ChangeWorkspaceTagsAsync(IReadOnlyList<MruItem> items, string selection, bool remove)
        {
            var paths = items.Select(item => item.Path).ToArray();
            return SaveTagsChangeAsync(tags => tags.ChangeTags(paths, selection, remove),
                $"Tags {(remove ? "removed from" : "added to")} {paths.Length} project(s).");
        }

        private async Task<bool> SaveTagsChangeAsync(Action<WorkspaceTagsService> change, string success)
        {
            if (!_tagsAvailable)
            {
                TagStatus = "Saved tag settings are unavailable. Changes cannot be saved until they can be loaded. Existing settings have not been changed.";
                return false;
            }
            await _tagsSaveGate.WaitAsync();
            try
            {
                // Stage changes so a failed settings write does not appear to have succeeded.
                var next = WorkspaceTagsService.Deserialize(_workspaceTags.Serialize());
                change(next);
                Options options = await Options.GetLiveInstanceAsync();
                string previous = options.WorkspaceTagsJson;
                options.WorkspaceTagsJson = next.Serialize();
                try
                {
                    await options.SaveAsync();
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    options.WorkspaceTagsJson = previous;
                    TagStatus = "Tags could not be saved. Please try again.";
                    return false;
                }
                ApplyWorkspaceTags(next);
                TagStatus = success;
                return true;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                TagStatus = "Tags could not be saved: " + ex.Message;
                return false;
            }
            finally
            {
                _tagsSaveGate.Release();
            }
        }
    }
}
