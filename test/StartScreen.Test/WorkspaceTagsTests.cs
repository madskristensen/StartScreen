using System.Text.Json;
using System.Xml.Linq;
using StartScreen.Models;
using StartScreen.Services;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class WorkspaceTagsTests
    {
        [TestMethod]
        public void Tags_NormalizeDeduplicateAndPersistWithoutChangingOtherWorkspaces()
        {
            var tags = new WorkspaceTagsService();
            tags.SetTags(@"C:\Code\App\", " work, Personal, WORK, , urgent ");
            tags.SetTags(@"C:\Code\Other", "other");

            var restored = WorkspaceTagsService.Deserialize(tags.Serialize());

            CollectionAssert.AreEqual(new[] { "Personal", "urgent", "work" }, restored.GetTags(@"c:\code\app"));
            CollectionAssert.AreEqual(new[] { "other" }, restored.GetTags(@"C:\Code\Other"));
            Assert.IsTrue(restored.Matches(@"C:\Code\App", "WORK, urgent"));
            Assert.IsFalse(restored.Matches(@"C:\Code\Other", "WORK, urgent"));
            Assert.IsTrue(restored.Matches(@"C:\Code\Unknown", ""));
        }

        [TestMethod]
        public void Tags_RemovingTheLastUseRetiresTheTagAndPersistsRemoval()
        {
            var tags = new WorkspaceTagsService();
            tags.SetTags(@"C:\App", "work");
            tags.SetTags(@"C:\Other", "work");
            tags.SetTags(@"C:\App", " , ");
            Assert.AreEqual("work", tags.AvailableTags.Single());
            tags.ChangeTags([@"C:\Other"], "WORK", true);

            var restored = WorkspaceTagsService.Deserialize(tags.Serialize());

            Assert.IsEmpty(restored.GetTags(@"C:\App"));
            Assert.IsEmpty(restored.AvailableTags.ToArray());
        }

        [TestMethod]
        public void Tags_ReturnDefensiveSnapshotsAndRejectCorruptJson()
        {
            var tags = new WorkspaceTagsService();
            tags.SetTags(@"C:\App", "work");
            string[] snapshot = tags.GetTags(@"C:\App");
            snapshot[0] = "changed";
            Assert.AreEqual("work", tags.GetTags(@"C:\App")[0]);
            Assert.Throws<JsonException>(() => WorkspaceTagsService.Deserialize("{broken"));
            Assert.Throws<ArgumentException>(() => tags.SetTags("", "work"));
        }

        [TestMethod]
        [DataRow("null")]
        [DataRow("[]")]
        [DataRow("{}")]
        [DataRow("{\"Workspaces\":null,\"Collections\":{}}")]
        [DataRow("{\"Workspaces\":[]}")]
        [DataRow("{\"Workspaces\":{\"App\":null},\"Collections\":{}}")]
        [DataRow("{\"Workspaces\":{\"App\":[null]},\"Collections\":{}}")]
        [DataRow("{\"Workspaces\":{\"App\":[1]},\"Collections\":{}}")]
        [DataRow("{\"Workspaces\":{\" \":[\"work\"]}}")]
        public void Tags_RejectNullAndMalformedSavedSettings(string json)
        {
            Assert.Throws<JsonException>(() => WorkspaceTagsService.Deserialize(json));
        }

        [TestMethod]
        public void Tags_EmptyDefaultsAndExplicitEmptySettingsRemainSupported()
        {
            Assert.IsEmpty(WorkspaceTagsService.Deserialize("").AvailableTags.ToArray());
            var tags = WorkspaceTagsService.Deserialize("{\"Workspaces\":{},\"Collections\":{}}");
            Assert.IsEmpty(tags.AvailableTags.ToArray());
            Assert.IsEmpty(WorkspaceTagsService.Deserialize("{\"Workspaces\":{}}").AvailableTags.ToArray());
        }

        [TestMethod]
        [DataRow("{\"Daily\":[\"work\"]}")]
        [DataRow("null")]
        [DataRow("[]")]
        public void Tags_LegacyCollectionsAreIgnoredWithoutLosingWorkspaceTags(string collections)
        {
            var tags = WorkspaceTagsService.Deserialize("{\"Workspaces\":{\"App.sln\":[\"work\"]},\"Collections\":" + collections + "}");
            Assert.AreEqual("work", tags.GetTags("App.sln").Single());
            using var saved = JsonDocument.Parse(tags.Serialize());
            Assert.IsFalse(saved.RootElement.TryGetProperty("Collections", out _));
            Assert.AreEqual("work", WorkspaceTagsService.Deserialize(tags.Serialize()).GetTags("App.sln").Single());
        }

        [TestMethod]
        public async Task Tags_InvalidLoadPreservesTagsAndBlocksSavingWithVisibleStatus()
        {
            var tags = new WorkspaceTagsService();
            tags.SetTags(@"C:\App.sln", "work");
            var item = new MruItem { Path = @"C:\App.sln" };
            using var viewModel = new StartScreenViewModel([item]);
            viewModel.ApplyWorkspaceTags(tags);
            viewModel.SelectedMruItem = item;

            await viewModel.LoadWorkspaceTagsAsync("null");

            Assert.AreEqual("work", item.TagsText);
            Assert.IsFalse(await viewModel.ChangeWorkspaceTagsAsync([item], "new", false));
            StringAssert.Contains(viewModel.TagStatus, "unavailable");
            StringAssert.Contains(viewModel.TagStatus, "Existing settings have not been changed");
            viewModel.BeginTagEdit();
            StringAssert.Contains(viewModel.TagStatus, "unavailable");
            Assert.AreEqual("work", item.TagsText);
        }

        [TestMethod]
        public void Tags_SurviveSolutionFormatMigration()
        {
            var tags = new WorkspaceTagsService();
            tags.SetTags(@"C:\App.sln", "work");
            Assert.AreEqual("work", tags.GetTags(@"c:\app.slnx").Single());
            tags.SetTags(@"C:\App.slnx", "personal");
            Assert.AreEqual("personal", tags.GetTags(@"C:\App.sln").Single());
        }

        [TestMethod]
        public void TagFilters_ComposeWithSearchAndPreservePinnedOrderingAndSelection()
        {
            var pinned = new MruItem { Name = "Pinned", Path = @"C:\Pinned.sln", IsPinned = true };
            var match = new MruItem { Name = "Match", Path = @"C:\Match.sln" };
            var other = new MruItem { Name = "Other", Path = @"C:\Other.sln" };
            var tags = new WorkspaceTagsService();
            tags.SetTags(pinned.Path, "work");
            tags.SetTags(match.Path, "work, urgent");
            using var viewModel = new StartScreenViewModel([match, other, pinned]);
            viewModel.ApplyWorkspaceTags(tags);
            viewModel.SelectedMruItem = match;
            Assert.AreEqual("urgent, work", match.TagsText);

            viewModel.TagFilter = "work";
            CollectionAssert.AreEqual(new[] { pinned, match }, viewModel.VisibleMruItems.ToArray());
            viewModel.TagFilter = "urgent, work";
            CollectionAssert.AreEqual(new[] { match }, viewModel.VisibleMruItems.ToArray());
            viewModel.SearchFilter = "Other";
            Assert.IsEmpty(viewModel.VisibleMruItems);
            Assert.IsTrue(viewModel.HasNoSearchResults);
            viewModel.SearchFilter = "";
            Assert.AreSame(match, viewModel.SelectedMruItem);
            CollectionAssert.AreEqual(new[] { match }, viewModel.VisibleMruItems.ToArray());
            viewModel.TagFilter = "unknown";
            Assert.IsTrue(viewModel.HasNoSearchResults);
            viewModel.TagFilter = "";
            CollectionAssert.AreEqual(new[] { pinned, match, other }, viewModel.VisibleMruItems.ToArray());
        }

        [TestMethod]
        public void Tags_AreReappliedWhenMruEntriesAreReplaced()
        {
            var tags = new WorkspaceTagsService();
            tags.SetTags(@"C:\App.sln", "work");
            using var viewModel = new StartScreenViewModel([]);
            viewModel.ApplyWorkspaceTags(tags);
            viewModel.TagFilter = "work";
            var replacement = new MruItem { Path = @"c:\app.sln" };
            viewModel.SetMruItems([replacement]);
            Assert.AreEqual("work", replacement.TagsText);
            CollectionAssert.AreEqual(new[] { replacement }, viewModel.VisibleMruItems.ToArray());
        }

        [TestMethod]
        [DataRow("client", true)]
        [DataRow("CLIENT", true)]
        [DataRow("lien", true)]
        [DataRow("client work", true)]
        [DataRow("personal", false)]
        public void Search_MatchesTagsCaseInsensitivelyIncludingPartialTags(string query, bool expected)
        {
            var item = new MruItem { Name = "App", Path = @"C:\App.sln", TagsText = "client work, urgent" };

            Assert.AreEqual(expected, StartScreenViewModel.MatchesMruFilter(item, query, false));
            item.TagsText = null;
            Assert.IsFalse(StartScreenViewModel.MatchesMruFilter(item, query, false));
        }

        [TestMethod]
        public void Search_ComposesTagNameAndPathMatchesWithExistingFiltersAndOrdering()
        {
            var pinned = new MruItem { Name = "Pinned", Path = @"C:\Pinned", Type = MruItemType.Folder, IsPinned = true };
            var byName = new MruItem { Name = "Review", Path = @"C:\First.sln", LastAccessed = DateTime.Today };
            var byPath = new MruItem { Name = "Second", Path = @"C:\Review\Second.sln", LastAccessed = DateTime.Today.AddDays(-1) };
            var byTag = new MruItem { Name = "Third", Path = @"C:\Third", Type = MruItemType.Folder, IsFromExtendedList = true };
            var other = new MruItem { Name = "Other", Path = @"C:\Other.sln" };
            var tags = new WorkspaceTagsService();
            tags.SetTags(pinned.Path, "review");
            tags.SetTags(byTag.Path, "client, review");
            using var vm = new StartScreenViewModel([other, byTag, byPath, byName, pinned]);
            vm.ApplyWorkspaceTags(tags);
            vm.SelectedMruItem = byTag;

            vm.SearchFilter = "REvI";

            CollectionAssert.AreEqual(new[] { pinned, byName, byPath, byTag }, vm.VisibleMruItems.ToArray());
            Assert.AreSame(byTag, vm.SelectedMruItem);
            Assert.IsFalse(vm.HasNoSearchResults);
            vm.TagFilter = "client";
            CollectionAssert.AreEqual(new[] { byTag }, vm.VisibleMruItems.ToArray());
            vm.ShowSolutionsOnly = true;
            Assert.IsEmpty(vm.VisibleMruItems);
            Assert.IsTrue(vm.HasNoSearchResults);
            vm.TagFilter = "";
            CollectionAssert.AreEqual(new[] { pinned, byName, byPath }, vm.VisibleMruItems.ToArray());
            vm.SearchFilter = "";
            CollectionAssert.AreEqual(new[] { pinned, byName, byPath, other }, vm.VisibleMruItems.ToArray());
        }

        [TestMethod]
        public void Search_RefreshesWhenTagsChangeOrWorkspaceEntriesAreReplaced()
        {
            var item = new MruItem { Name = "App", Path = @"C:\App.sln" };
            using var vm = new StartScreenViewModel([item]);
            vm.SearchFilter = "client";
            Assert.IsEmpty(vm.VisibleMruItems);
            var tags = new WorkspaceTagsService();
            tags.SetTags(item.Path, "client");

            vm.ApplyWorkspaceTags(tags);

            CollectionAssert.AreEqual(new[] { item }, vm.VisibleMruItems.ToArray());
            Assert.IsFalse(vm.HasNoSearchResults);
            var replacement = new MruItem { Name = "App", Path = @"C:\App.slnx" };
            vm.SetMruItems([replacement]);
            CollectionAssert.AreEqual(new[] { replacement }, vm.VisibleMruItems.ToArray());
            tags.SetTags(replacement.Path, "personal");
            vm.ApplyWorkspaceTags(tags);
            Assert.IsEmpty(vm.VisibleMruItems);
            Assert.IsTrue(vm.HasNoSearchResults);
        }

        [TestMethod]
        public void WorktreeRows_ExposeStateAndDisableBareAndPrunableEntries()
        {
            var info = new GitWorktreeInfo { Path = @"C:\Worktree", Branch = "topic", IsCurrent = true, IsLocked = true };
            var row = info;
            Assert.AreEqual(info.Path, row.Path);
            Assert.AreEqual("topic", row.Branch);
            Assert.AreEqual("current, locked", row.Status);
            Assert.IsTrue(row.CanOpen);
            info.IsBare = true;
            Assert.IsFalse(row.CanOpen);
            info.IsBare = false;
            info.IsPrunable = true;
            Assert.IsFalse(row.CanOpen);
            StringAssert.Contains(row.Status, "prunable");
        }

        [TestMethod]
        public void OrganizationUi_UsesNativeThemeStylesAndBoundsTagContent()
        {
            using var stream = typeof(WorkspaceTagsTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            Assert.IsFalse(document.Descendants(p + "Expander").Any(element => element.Attribute("Header")?.Value == "Tags and collections"));
            Assert.IsFalse(document.Descendants(p + "Hyperlink").Any(element => element.Attribute("Click")?.Value == "OrganizeProjects_Click"));
            Assert.IsFalse(document.Descendants(p + "Button").Any(element => element.Attribute("Click")?.Value == "OrganizeProjects_Click"));
            Assert.IsTrue(document.Descendants(p + "ToggleButton").Any(element => element.Attribute("Click")?.Value == "TagFilter_Click"));
            Assert.IsFalse(document.Descendants(p + "TextBox").Any(element => element.Attribute("Text")?.Value.Contains("SelectedWorkspaceTags") == true));

            using var dialogStream = typeof(WorkspaceTagsTests).Assembly.GetManifestResourceStream("WorkspaceOrganizationDialog.xaml");
            var dialog = XDocument.Load(dialogStream!);
            Assert.AreEqual("DialogWindow", dialog.Root!.Name.LocalName);
            StringAssert.Contains(dialog.Root.Attribute("Background")!.Value, "EnvironmentColors.ToolWindowBackgroundBrushKey");
            Assert.IsTrue(dialog.Descendants(p + "ScrollViewer").Any(element => element.Attribute("VerticalScrollBarVisibility")?.Value == "Auto"));
            Assert.IsTrue(dialog.Descendants(p + "Style").Any(element => element.Attribute("BasedOn")?.Value.Contains("ThemedDialogButtonStyleKey") == true));
            Assert.AreEqual("Height", dialog.Root.Attribute("SizeToContent")!.Value);
            Assert.IsNull(dialog.Root.Attribute("Height"));
            Assert.IsNull(dialog.Root.Attribute("MinHeight"));
            Assert.IsFalse(dialog.Descendants(p + "ComboBox").Any());
            Assert.IsFalse(document.Descendants(p + "ComboBox").Any(element => element.Attribute("ItemsSource")?.Value == "{Binding TagCollections}"));
        }

        [TestMethod]
        public void TagUi_UsesContextMenuEditingWithoutAWorktreeDiscoveryPanel()
        {
            using var stream = typeof(WorkspaceTagsTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            using var dialogStream = typeof(WorkspaceTagsTests).Assembly.GetManifestResourceStream("WorkspaceOrganizationDialog.xaml");
            var dialog = XDocument.Load(dialogStream!);
            Assert.IsTrue(dialog.Descendants(p + "Button").Any(element => element.Attribute("Click")?.Value == "ApplyTags_Click"));
            Assert.IsFalse(dialog.Descendants(p + "Button").Any(element => element.Attribute("Click")?.Value == "SaveWorkspaceTags_Click"));
            Assert.IsFalse(document.Descendants(p + "Button").Any(element =>
                element.Attribute("Click")?.Value == "DiscoverWorktrees_Click"));
            Assert.IsFalse(document.Descendants(p + "Button").Any(element =>
                element.Attribute("Click")?.Value == "CancelWorktrees_Click"));
            Assert.IsFalse(document.Descendants(p + "Expander").Any(element => element.Attribute("Header")?.Value == "Related worktrees"));
            Assert.IsFalse(document.Descendants(p + "MenuItem").Any(element =>
                element.Attribute("Click")?.Value == "SaveWorkspaceTags_Click"));
        }
    }
}
