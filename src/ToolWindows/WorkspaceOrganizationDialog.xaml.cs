using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.VisualStudio.PlatformUI;
using StartScreen.Models;

namespace StartScreen.ToolWindows
{
    public partial class WorkspaceOrganizationDialog : DialogWindow
    {
        private readonly StartScreenViewModel _viewModel;
        private readonly IReadOnlyList<MruItem> _tagTargets;
        private readonly bool _removeTags;

        internal WorkspaceOrganizationDialog(StartScreenViewModel viewModel, IReadOnlyList<MruItem> items, bool remove)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _tagTargets = items?.ToArray() ?? throw new ArgumentNullException(nameof(items));
            if (_tagTargets.Count == 0)
                throw new ArgumentException("Select at least one project to edit its tags.", nameof(items));
            _removeTags = remove;
            _viewModel.BeginTagEdit();
            InitializeComponent();
            DataContext = viewModel;
            Title = remove ? "Remove project tags" : "Add project tags";
            NewTagsPanel.Visibility = remove ? Visibility.Collapsed : Visibility.Visible;
            ApplyTagsButton.Content = remove ? "Remove tags" : "Add tags";
            var projects = items.Count == 1 ? "1 project" : $"{items.Count} projects";
            BulkTagSummary.Text = remove ? $"Remove tags from {projects}." : $"Add tags to {projects}. Existing tags are kept.";
            TagChoices.ItemsSource = (remove ? _tagTargets.SelectMany(item => item.Tags) : viewModel.AvailableWorkspaceTags)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new TagChoice(name)).ToArray();
        }

        public sealed class TagChoice(string name)
        {
            public string Name { get; } = name;
            public bool IsSelected { get; set; }
        }

        private void ApplyTags_Click(object sender, RoutedEventArgs e)
        {
            ApplyTagsAsync().FileAndForget(nameof(WorkspaceOrganizationDialog));
        }

        private async Task ApplyTagsAsync()
        {
            ApplyTagsButton.IsEnabled = false;
            try
            {
                var selection = string.Join(", ", TagChoices.Items.Cast<TagChoice>()
                    .Where(choice => choice.IsSelected).Select(choice => choice.Name).Append(_removeTags ? "" : BulkTagText.Text));
                if (await _viewModel.ChangeWorkspaceTagsAsync(_tagTargets, selection, _removeTags))
                    Close();
            }
            finally
            {
                ApplyTagsButton.IsEnabled = true;
            }
        }

        private void CloseDialog_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

    }
}
