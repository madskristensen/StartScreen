using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace StartScreen.Services
{
    internal static class WhatsNewService
    {
        internal static async Task<WhatsNewContent> ReadAsync(string contentDirectory, CultureInfo culture,
            CancellationToken token = default)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            token.ThrowIfCancellationRequested();
            var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().WithDuplicateKeyChecking().Build();
            var index = deserializer.Deserialize<ContentIndex>(File.ReadAllText(Path.Combine(contentDirectory, "index.yml")));
            if (index?.Files == null)
                throw new InvalidDataException("The What's New index has no file list.");

            var directories = new List<string>();
            for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
                directories.Add(Path.Combine(contentDirectory, current.Name));
            directories.Add(Path.Combine(contentDirectory, "en-us"));

            var areas = new List<WhatsNewArea>();
            var itemsByArea = new Dictionary<string, List<WhatsNewItem>>(StringComparer.Ordinal);
            var failures = 0;
            foreach (var file in index.Files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file
                        || !string.Equals(Path.GetExtension(file), ".md", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The What's New index contains an invalid file name.");
                    var path = directories.Select(directory => Path.Combine(directory, file)).FirstOrDefault(File.Exists)
                        ?? throw new FileNotFoundException("The localized What's New item is missing.", file);
                    var metadata = deserializer.Deserialize<ItemMetadata>(ReadFrontMatter(path, token));
                    if (string.IsNullOrWhiteSpace(metadata?.Area) || string.IsNullOrWhiteSpace(metadata.Title)
                        || string.IsNullOrWhiteSpace(metadata.Description))
                        throw new InvalidDataException("The What's New item requires an area, title and description.");
                    var area = metadata.Area.Trim();
                    if (!itemsByArea.TryGetValue(area, out var items))
                    {
                        items = new List<WhatsNewItem>();
                        itemsByArea.Add(area, items);
                        areas.Add(new WhatsNewArea { Area = area, Items = items });
                    }
                    items.Add(new WhatsNewItem { Title = metadata.Title.Trim(), Description = metadata.Description.Trim() });
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                    || ex is InvalidDataException || ex is YamlException)
                {
                    await ex.LogAsync();
                    failures++;
                }
            }
            token.ThrowIfCancellationRequested();
            return new WhatsNewContent
            {
                Areas = areas,
                Status = failures > 0 ? "Some What's New items could not be loaded. Reopen the Start Screen to retry."
                    : areas.Count == 0 ? "No What's New items are available for this installation." : null,
            };
        }

        private static string ReadFrontMatter(string path, CancellationToken token)
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine()?.Trim() != "---")
                throw new InvalidDataException("The What's New item has no YAML front matter.");
            var yaml = new StringBuilder();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                token.ThrowIfCancellationRequested();
                if (line.Trim() == "---")
                    return yaml.ToString();
                if (yaml.Length + line.Length > 65536)
                    throw new InvalidDataException("The What's New metadata is too large.");
                yaml.AppendLine(line);
            }
            throw new InvalidDataException("The What's New YAML front matter is incomplete.");
        }

        private sealed class ContentIndex
        {
            [YamlMember(Alias = "files")]
            public List<string> Files { get; set; }
        }

        private sealed class ItemMetadata
        {
            [YamlMember(Alias = "area")]
            public string Area { get; set; }
            [YamlMember(Alias = "title")]
            public string Title { get; set; }
            [YamlMember(Alias = "description")]
            public string Description { get; set; }
        }
    }
}
