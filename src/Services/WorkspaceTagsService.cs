using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace StartScreen.Services
{
    /// <summary>Workspace labels stored in VS user settings.</summary>
    internal sealed class WorkspaceTagsService
    {
        private readonly Dictionary<string, string[]> _workspaces = new(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<string> AvailableTags => _workspaces.Values.SelectMany(tags => tags)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase);

        public static string[] ParseTags(string text)
        {
            return (text ?? "").Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string Key(string path)
        {
            var key = (path ?? "").Trim().Replace('/', '\\').TrimEnd('\\');
            // VS treats .sln and .slnx versions of a solution as one recent workspace.
            return key.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? key.Substring(0, key.Length - 1) : key;
        }

        public string[] GetTags(string path) => _workspaces.TryGetValue(Key(path), out var tags) ? tags.ToArray() : [];

        public void SetTags(string path, string text)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Select a workspace first.", nameof(path));
            string[] tags = ParseTags(text);
            if (tags.Length == 0)
                _workspaces.Remove(Key(path));
            else
                _workspaces[Key(path)] = tags;
        }

        public bool Matches(string path, string selection)
        {
            string[] required = ParseTags(selection);
            string[] tags = GetTags(path);
            return required.All(tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }

        public void ChangeTags(IEnumerable<string> paths, string selection, bool remove)
        {
            var targets = paths?.ToArray() ?? throw new ArgumentNullException(nameof(paths));
            var selected = ParseTags(selection);
            if (targets.Length == 0 || targets.Any(string.IsNullOrWhiteSpace) || selected.Length == 0)
                throw new ArgumentException("Select projects and at least one tag.");
            foreach (var path in targets)
            {
                var existing = GetTags(path);
                SetTags(path, string.Join(", ", remove ? existing.Except(selected, StringComparer.OrdinalIgnoreCase)
                    : existing.Concat(selected)));
            }
        }

        public string Serialize() => JsonSerializer.Serialize(new TagData { Workspaces = _workspaces });

        public static WorkspaceTagsService Deserialize(string json)
        {
            var service = new WorkspaceTagsService();
            if (string.IsNullOrWhiteSpace(json))
                return service;
            var data = JsonSerializer.Deserialize<TagData>(json);
            if (data?.Workspaces == null)
                throw new JsonException("Saved tags must contain a workspace tags object.");
            foreach (var entry in data.Workspaces)
            {
                ValidateEntry(entry);
                service.SetTags(entry.Key, string.Join(",", entry.Value));
            }
            return service;
        }

        private static void ValidateEntry(KeyValuePair<string, string[]> entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value == null || entry.Value.Any(tag => tag == null))
                throw new JsonException("Saved workspace tags must have names and arrays of tag strings.");
        }

        internal sealed class TagData
        {
            public Dictionary<string, string[]> Workspaces { get; set; }
        }
    }
}
