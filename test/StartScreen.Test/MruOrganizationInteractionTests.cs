using System.ComponentModel.Design;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.VisualStudio.Shell;
using StartScreen.Commands;
using StartScreen.Models;
using StartScreen.Services;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test
{
    [TestClass]
    public class MruOrganizationInteractionTests
    {
        [STATestMethod]
        public void CtrlSelection_TogglesItemsAndRightClickPreservesTheGroup()
        {
            var items = Enumerable.Range(0, 3).Select(index => new MruItem { Name = "Project " + index }).ToArray();
            var list = new ListBox { ItemsSource = items, SelectionMode = SelectionMode.Extended };
            MruItemControl.SelectMruItem(list, items[0], false, false);
            MruItemControl.SelectMruItem(list, items[1], true, false);
            CollectionAssert.AreEquivalent(items.Take(2).ToArray(), list.SelectedItems.Cast<MruItem>().ToArray());
            MruItemControl.SelectMruItem(list, items[0], false, true);
            Assert.HasCount(2, list.SelectedItems);
            MruItemControl.SelectMruItem(list, items[1], true, false);
            Assert.AreSame(items[0], list.SelectedItems.Cast<MruItem>().Single());
            MruItemControl.SelectMruItem(list, items[2], false, true);
            Assert.AreSame(items[2], list.SelectedItems.Cast<MruItem>().Single());
            MruItemControl.SelectMruItem(list, items[0], true, false);
            MruItemControl.SelectMruItem(list, items[1], false, false);
            Assert.AreSame(items[1], list.SelectedItems.Cast<MruItem>().Single());
        }

        [STATestMethod]
        [DoNotParallelize]
        public void RightMouseDownAndCardFocus_DoNotDiscardMultiSelection()
        {
            var application = Application.Current ?? new Application();
            if (!application.Resources.Contains(VsResourceKeys.ThemedDialogDefaultStylesKey))
                application.Resources.Add(VsResourceKeys.ThemedDialogDefaultStylesKey, new ResourceDictionary());
            if (!application.Resources.Contains(VsResourceKeys.ThemedDialogComboBoxStyleKey))
            {
                var style = new Style(typeof(ComboBox));
                style.Seal();
                application.Resources.Add(VsResourceKeys.ThemedDialogComboBoxStyleKey, style);
            }
            var items = Enumerable.Range(0, 3).Select(index => new MruItem { Name = "Project " + index, Path = @"C:\Project" + index }).ToArray();
            var list = new ListBox
            {
                Name = "MruPanel",
                ItemsSource = items,
                SelectionMode = SelectionMode.Extended,
                ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(MruItemControl)) },
            };
            var host = new Window { Content = list, Width = 320, Height = 400, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                host.Show();
                host.UpdateLayout();
                var first = MruItemControl.GetItemControl(list, 0)!;
                var second = MruItemControl.GetItemControl(list, 1)!;
                var third = MruItemControl.GetItemControl(list, 2)!;
                Assert.IsNotNull(first);
                Assert.IsNotNull(second);
                Assert.IsNotNull(third);
                MruItemControl.SelectMruItem(list, items[0], false, false);
                MruItemControl.SelectMruItem(list, items[1], true, false);
                second.FocusItem(true);
                Assert.HasCount(2, list.SelectedItems);
                var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent };
                ((Border)first.Content).RaiseEvent(rightClick);
                Assert.IsTrue(rightClick.Handled);
                CollectionAssert.AreEquivalent(items.Take(2).ToArray(), list.SelectedItems.Cast<MruItem>().ToArray());
                var otherClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent };
                ((Border)third.Content).RaiseEvent(otherClick);
                Assert.IsTrue(otherClick.Handled);
                Assert.AreSame(items[2], list.SelectedItems.Cast<MruItem>().Single());
            }
            finally
            {
                host.Close();
            }
        }

        [TestMethod]
        public void BulkTagChanges_PreserveOtherTagsAndUnselectedWorkspaces()
        {
            var tags = new WorkspaceTagsService();
            tags.SetTags(@"C:\First.sln", "work, client");
            tags.SetTags(@"C:\Second.sln", "personal");
            tags.SetTags(@"C:\Other.sln", "other");
            var paths = new[] { @"C:\First.slnx", @"C:\Second.sln" };
            tags.ChangeTags(paths, "URGENT, work", false);
            CollectionAssert.AreEquivalent(new[] { "client", "work", "URGENT" }, tags.GetTags(paths[0]));
            CollectionAssert.AreEquivalent(new[] { "personal", "work", "URGENT" }, tags.GetTags(paths[1]));
            tags.ChangeTags(paths, "urgent, WORK", true);
            Assert.AreEqual("client", tags.GetTags(paths[0]).Single());
            Assert.AreEqual("personal", tags.GetTags(paths[1]).Single());
            Assert.AreEqual("other", tags.GetTags(@"C:\Other.sln").Single());
            string saved = tags.Serialize();
            var reloaded = WorkspaceTagsService.Deserialize(saved);
            Assert.AreEqual("client", reloaded.GetTags(paths[0]).Single());
            Assert.AreEqual("personal", reloaded.GetTags(paths[1]).Single());
            Assert.Throws<ArgumentException>(() => tags.ChangeTags(paths, " , ", false));
            Assert.Throws<ArgumentException>(() => tags.ChangeTags([paths[0], ""], "new", false));
            Assert.AreEqual(saved, tags.Serialize());
        }

        [TestMethod]
        public void TagPillNames_AreNormalizedAndNotifyWhenChangedOrCleared()
        {
            var item = new MruItem();
            var changed = new List<string?>();
            item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            item.TagsText = "work, urgent, WORK";
            CollectionAssert.AreEqual(new[] { "urgent", "work" }, item.Tags.ToArray());
            Assert.IsTrue(item.HasTags);
            CollectionAssert.Contains(changed, nameof(MruItem.Tags));
            CollectionAssert.Contains(changed, nameof(MruItem.HasTags));
            item.TagsText = " , ";
            Assert.IsEmpty(item.Tags.ToArray());
            Assert.IsFalse(item.HasTags);
        }

        [STATestMethod]
        [DataRow(220d, 12d)]
        [DataRow(220d, 20d)]
        [DataRow(320d, 12d)]
        public void TagSummary_SharesTheDetailsLineWithoutClippingReviewsOrTakingHeaderSpace(double width, double fontSize)
        {
            var card = StartScreenInlineBindingTests.ParseMruCard();
            StartScreen.ToolWindows.Typography.SetEnvironmentSize(card, fontSize);
            var item = new MruItem
            {
                Name = "hubitat-drivers", GitBranch = "feature/long-branch-name",
                TagsText = "client, urgent, work, a very long project tag that must stay inside the card",
            };
            item.ApplyRepositorySummary("Loaded", "Review details", 42, 21);
            card.DataContext = item;
            Layout(card, width);
            var pills = (Border)card.FindName("WorkspaceTagPills");
            var tags = (TextBlock)pills.Child;
            var reviews = (Grid)card.FindName("RepositoryStatusRow");
            var name = (TextBlock)card.FindName("NameText");
            Assert.AreEqual("21 reviews", ((Button)reviews.Children[0]).Content);
            Assert.IsGreaterThan(0d, name.ActualWidth);
            Assert.IsGreaterThan(0d, pills.ActualHeight);
            Assert.AreSame(reviews, pills.Parent);
            Assert.AreEqual(item.TagsText, tags.Text);
            Assert.AreEqual(item.TagsText, pills.ToolTip);
            Assert.AreEqual(TextTrimming.CharacterEllipsis, tags.TextTrimming);
            var reviewLink = (Button)reviews.Children[0];
            var untrimmed = new TextBlock { Text = "21 reviews", FontSize = reviewLink.FontSize, FontFamily = reviewLink.FontFamily };
            untrimmed.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.IsLessThanOrEqualTo(reviewLink.ActualWidth + 0.01, untrimmed.DesiredSize.Width);
            Assert.IsGreaterThan(0d, pills.CornerRadius.TopLeft);
            Assert.IsGreaterThan(0d, pills.ActualWidth);
            Assert.IsLessThanOrEqualTo(width - card.Padding.Right + 0.01,
                pills.TranslatePoint(new Point(pills.ActualWidth, 0), card).X);
            Assert.IsLessThanOrEqualTo(card.ActualHeight - card.Padding.Bottom + 0.01,
                pills.TranslatePoint(new Point(0, pills.ActualHeight), card).Y);
            item.TagsText = "";
            Layout(card, width);
            Assert.AreEqual(Visibility.Collapsed, pills.Visibility);
            Assert.AreEqual(Visibility.Visible, reviews.Visibility);
        }

        [TestMethod]
        [DoNotParallelize]
        public void NativeTagCommands_TrackSelectionAndRetainExistingProjectActions()
        {
            var first = new MruItem { IsPinned = true, TagsText = "work", GitBranch = "main" };
            var second = new MruItem();
            MruItemMenu.Current = new MruItemMenu { Item = first, Selection = [first, second] };
            try
            {
                Assert.AreEqual("Add tags (2 projects)...", Command(PackageIds.MruAddTags).Text);
                Assert.IsTrue(Command(PackageIds.MruRemoveTags).Enabled);
                Assert.AreEqual("Unpin", Command(PackageIds.MruPin).Text);
                Assert.IsTrue(Command(PackageIds.MruGitPull).Enabled);
                Assert.IsFalse(Command(PackageIds.MruDeleteChats).Visible);
                MruItemMenu.Current.Selection = [second];
                Assert.AreEqual("Add tags...", Command(PackageIds.MruAddTags).Text);
                Assert.IsFalse(Command(PackageIds.MruRemoveTags).Enabled);
                MruItemMenu.Current = null;
                Assert.IsFalse(Command(PackageIds.MruAddTags).Visible);
                Assert.IsFalse(Command(PackageIds.MruRemoveTags).Enabled);
            }
            finally
            {
                MruItemMenu.Current = null;
            }
        }

        [STATestMethod]
        [DataRow(220d, 12d)]
        [DataRow(320d, 12d)]
        [DataRow(480d, 12d)]
        [DataRow(220d, 20d)]
        [DataRow(320d, 20d)]
        [DataRow(480d, 20d)]
        [DataRow(320d, 32d)]
        public void RecentRows_KeepTheSameHeightAsReviewsTagsAndWorktreesArrive(double width, double fontSize)
        {
            var card = StartScreenInlineBindingTests.ParseMruCard();
            StartScreen.ToolWindows.Typography.SetEnvironmentSize(card, fontSize);
            var item = new MruItem { Name = "Project", Path = @"C:\main" };
            card.DataContext = item;
            Layout(card, width);
            var height = card.ActualHeight;
            if (fontSize == 12)
                Assert.AreEqual(48d, height, 0.01);
            else
                Assert.IsGreaterThan(48d, height, "Rows must grow with the font rather than clipping larger text.");

            foreach (var (reviews, worktrees, tags) in new[]
            {
                (21, false, ""),
                (0, true, ""),
                (0, false, "client, urgent, work, a very long project tag that must stay inside the card"),
                (21, true, "client, urgent, work, a very long project tag that must stay inside the card"),
                (0, false, ""),
            })
            {
                item.GitBranch = "main";
                item.ApplyRepositorySummary("Loaded", "Review details", 42, reviews);
                item.ApplyWorktrees(worktrees ? [
                    new GitWorktreeInfo { Path = item.Path, Branch = "main", IsCurrent = true },
                    new GitWorktreeInfo { Path = @"C:\other", Branch = "topic" },
                ] : [], 0);
                item.TagsText = tags;
                Layout(card, width);
                Assert.AreEqual(height, card.ActualHeight, 0.01, "Metadata must not change the row height.");
                var details = (Grid)card.FindName("RepositoryStatusRow");
                if (reviews == 0 && !worktrees && tags.Length == 0)
                    Assert.AreEqual(Visibility.Collapsed, details.Visibility);
                else
                {
                    Assert.AreEqual(Visibility.Visible, details.Visibility);
                    Assert.IsLessThanOrEqualTo(height - card.Padding.Bottom + 0.01,
                        details.TranslatePoint(new Point(0, details.ActualHeight), card).Y);
                }
            }
        }

        [TestMethod]
        [DoNotParallelize]
        [DataRow(PackageIds.MruOpen)]
        [DataRow(PackageIds.MruOpenNewInstance)]
        [DataRow(PackageIds.MruOpenFolder)]
        [DataRow(PackageIds.MruOpenTerminal)]
        [DataRow(PackageIds.MruCopyPath)]
        [DataRow(PackageIds.MruNewChat)]
        public void SingleProjectCommands_HideForMultiSelectionAndReturnForSingleSelection(int id)
        {
            var first = new MruItem { Path = Path.GetTempPath(), Type = MruItemType.Folder };
            var second = new MruItem();
            MruItemMenu.Current = new MruItemMenu { Item = first, Selection = [first] };
            try
            {
                var command = Command(id);
                Assert.IsTrue(command.Visible);
                Assert.IsTrue(command.Enabled);

                MruItemMenu.Current.Selection = [first, second];
                MruItemMenu.Update(command, id);
                Assert.IsFalse(command.Visible);
                Assert.IsFalse(command.Enabled);
                Assert.IsTrue(Command(PackageIds.MruAddTags).Visible);
                Assert.IsTrue(Command(PackageIds.MruRemoveTags).Visible);

                MruItemMenu.Current.Selection = [first];
                MruItemMenu.Update(command, id);
                Assert.IsTrue(command.Visible);
                Assert.IsTrue(command.Enabled);

                XNamespace v = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
                var name = typeof(PackageIds).GetFields().Single(field => field.IsLiteral && Equals(field.GetRawConstantValue(), id)).Name;
                var button = Read("VSCommandTable.vsct").Descendants(v + "Button").Single(element => element.Attribute("id")?.Value == name);
                var flags = button.Elements(v + "CommandFlag").Select(element => element.Value).ToArray();
                CollectionAssert.Contains(flags, "DynamicVisibility");
                CollectionAssert.Contains(flags, "DefaultInvisible");
            }
            finally
            {
                MruItemMenu.Current = null;
            }
        }

        [TestMethod]
        [DoNotParallelize]
        public void PruneCommand_ShowsOnlyForOneProjectWithStaleUnlockedCheckouts()
        {
            var item = new MruItem();
            var stale = new GitWorktreeInfo { IsPrunable = true };
            MruItemMenu.Current = new MruItemMenu { Item = item, Selection = [item], Worktrees = [stale] };
            try
            {
                Assert.IsTrue(Command(PackageIds.MruPruneWorktrees).Visible);
                stale.IsLocked = true;
                Assert.IsFalse(Command(PackageIds.MruPruneWorktrees).Visible);
                stale.IsLocked = false;
                stale.IsPrunable = false;
                Assert.IsFalse(Command(PackageIds.MruPruneWorktrees).Visible);
                stale.IsPrunable = true;
                MruItemMenu.Current.Selection = [item, new MruItem()];
                Assert.IsFalse(Command(PackageIds.MruPruneWorktrees).Visible);
                Assert.IsFalse(Command(PackageIds.MruNewChat).Visible);
            }
            finally
            {
                MruItemMenu.Current = null;
            }
        }

        [TestMethod]
        public void GitPullCommand_UsesPullMoniker()
        {
            XNamespace v = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
            var button = Read("VSCommandTable.vsct").Descendants(v + "Button")
                .Single(element => element.Attribute("id")?.Value == "MruGitPull");
            var icon = button.Element(v + "Icon")!;
            Assert.AreEqual("ImageCatalogGuid", icon.Attribute("guid")!.Value);
            Assert.AreEqual("Pull", icon.Attribute("id")!.Value);
            Assert.IsTrue(button.Elements(v + "CommandFlag").Any(element => element.Value == "IconIsMoniker"));
        }

        [STATestMethod]
        [DataRow(220d, 12d)]
        [DataRow(220d, 20d)]
        [DataRow(320d, 20d)]
        public void WorktreeIndicator_HidesWithoutOtherCheckoutsAndFitsBesideReviews(double width, double fontSize)
        {
            var card = StartScreenInlineBindingTests.ParseMruCard();
            StartScreen.ToolWindows.Typography.SetEnvironmentSize(card, fontSize);
            var item = new MruItem { Name = "Project", Path = @"C:\main", GitBranch = "main" };
            card.DataContext = item;
            var indicator = (StackPanel)card.FindName("WorktreeIndicator");
            var details = (Grid)card.FindName("RepositoryStatusRow");
            Layout(card, width);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
            Assert.AreEqual(Visibility.Collapsed, details.Visibility);
            item.ApplyWorktrees([new GitWorktreeInfo { Path = item.Path, Branch = "main", IsCurrent = true }], 0);
            Layout(card, width);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
            item.ApplyWorktrees([
                new GitWorktreeInfo { Path = item.Path, Branch = "main", IsCurrent = true },
                new GitWorktreeInfo { Path = @"C:\topic", Branch = "topic" },
                new GitWorktreeInfo { Path = @"C:\bare.git", IsBare = true },
            ], 0);
            Layout(card, width);
            Assert.AreEqual(1, item.AdditionalWorktreeCount);
            StringAssert.Contains(item.WorktreeTooltip, "Open Worktree");
            StringAssert.Contains(item.WorktreeTooltip, "Hold Shift");
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(Visibility.Visible, details.Visibility);
            StringAssert.Contains((string)indicator.ToolTip, "1 other worktree.");
            Assert.AreEqual("1", ((TextBlock)indicator.Children[1]).Text);
            item.ApplyRepositorySummary("Loaded", "Review details", 42, 21);
            Layout(card, width);
            var reviews = (Button)details.Children[0];
            Assert.AreEqual("21 reviews", reviews.Content);
            var untrimmed = new TextBlock { Text = "21 reviews", FontSize = reviews.FontSize, FontFamily = reviews.FontFamily };
            untrimmed.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.IsLessThanOrEqualTo(reviews.ActualWidth + 0.01, untrimmed.DesiredSize.Width);
            Assert.IsGreaterThan(0d, ((TextBlock)card.FindName("NameText")).ActualWidth);
            Assert.IsLessThanOrEqualTo(width - card.Padding.Right + 0.01,
                indicator.TranslatePoint(new Point(indicator.ActualWidth, 0), card).X);
            item.ApplyWorktreeFailure("Worktree discovery unavailable. Refresh to try again.", 0);
            Layout(card, width);
            StringAssert.Contains((string)indicator.ToolTip, "unavailable");
            Assert.AreEqual(1, item.AdditionalWorktreeCount, "A failure must retain the last known list.");
            item.ApplyWorktrees([], 1);
            Layout(card, width);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
            Assert.AreEqual(Visibility.Visible, details.Visibility);
            Assert.IsNull(item.WorktreeError);
        }

        [TestMethod]
        [DoNotParallelize]
        public void WorktreeMenu_UsesStableSnapshotAndHidesForMultipleProjects()
        {
            var item = new MruItem();
            var current = new GitWorktreeInfo { Path = @"C:\main", Branch = "main", IsCurrent = true };
            var other = new GitWorktreeInfo { Path = @"C:\topic", Branch = "topic" };
            item.ApplyWorktrees([current, other], 0);
            MruItemMenu.Current = new MruItemMenu { Item = item, Selection = [item], Worktrees = item.Worktrees.ToArray() };
            try
            {
                Assert.IsTrue(Command(PackageIds.MruWorktreesMenu).Visible);
                CollectionAssert.AreEqual(new[] { current, other }, WorktreeCommands.GetItems().ToArray());
                item.ApplyWorktrees([current], 1);
                Assert.HasCount(2, WorktreeCommands.GetItems(), "Background updates must not reorder an open menu.");
                MruItemMenu.Current.Selection = [item, new MruItem()];
                Assert.IsFalse(Command(PackageIds.MruWorktreesMenu).Visible);
                Assert.IsFalse(Command(PackageIds.MruWorktreesMenu).Enabled);
                Assert.IsEmpty(WorktreeCommands.GetItems());
                MruItemMenu.Current.Selection = [item];
                MruItemMenu.Current.Worktrees = item.Worktrees;
                Assert.IsFalse(Command(PackageIds.MruWorktreesMenu).Visible);
            }
            finally
            {
                MruItemMenu.Current = null;
            }
        }

        [TestMethod]
        public void WorktreeMenu_ChecksCurrentDisablesUnavailableAndEscapesUserSuppliedLabels()
        {
            var item = new GitWorktreeInfo { Path = @"C:\topic & folder", Branch = "feature&fix\nnext", IsCurrent = true };
            var command = new OleMenuCommand((_, _) => { }, new CommandID(PackageGuids.StartScreen, PackageIds.MruOpenWorktree));
            WorktreeCommands.Update(command, item);
            Assert.IsTrue(command.Checked);
            Assert.IsTrue(command.Enabled, "The checked checkout must remain selectable so Shift can open it in a new instance.");
            Assert.AreEqual("feature&&fix next - topic && folder (current)", command.Text);
            item.IsCurrent = false;
            item.IsLocked = true;
            WorktreeCommands.Update(command, item);
            Assert.IsFalse(command.Checked);
            Assert.IsTrue(command.Enabled, "A locked worktree can still be opened.");
            StringAssert.Contains(command.Text, "locked");
            item.IsBare = true;
            WorktreeCommands.Update(command, item);
            Assert.IsFalse(command.Enabled);
            item.IsBare = false;
            item.IsPrunable = true;
            WorktreeCommands.Update(command, item);
            Assert.IsFalse(command.Enabled);
            StringAssert.Contains(command.Text, "prunable");
        }

        [TestMethod]
        [DataRow(ModifierKeys.None, false)]
        [DataRow(ModifierKeys.Control, false)]
        [DataRow(ModifierKeys.Alt, false)]
        [DataRow(ModifierKeys.Windows, false)]
        [DataRow(ModifierKeys.Shift, true)]
        [DataRow(ModifierKeys.Shift | ModifierKeys.Control, true)]
        [DataRow(ModifierKeys.Shift | ModifierKeys.Alt, true)]
        [DataRow(ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Alt, true)]
        public void WorktreeSelection_OpensInNewInstanceOnlyWhenShiftIsHeld(ModifierKeys modifiers, bool expected)
        {
            Assert.AreEqual(expected, WorktreeCommands.IsNewInstanceRequested(modifiers));
        }

        [TestMethod]
        public void RecentProjectMenu_SeparatesOpeningCopilotGitTagsAndItemActions()
        {
            XNamespace v = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
            var menu = Read("VSCommandTable.vsct");
            var groups = menu.Descendants(v + "Group")
                .Where(element => element.Element(v + "Parent")?.Attribute("id")?.Value == "MruItemMenu")
                .OrderBy(element => Convert.ToInt32(element.Attribute("priority")!.Value, 16)).ToArray();
            CollectionAssert.AreEqual(new[] { "MruOpenActions", "MruCopilotActions", "MruGitActions", "MruTagActions", "MruItemActions" },
                groups.Select(element => element.Attribute("id")!.Value).ToArray());
            var expectedCommands = new[]
            {
                new[] { "MruOpen", "MruOpenNewInstance", "MruOpenFolder", "MruOpenTerminal" },
                new[] { "MruNewChat", "MruDeleteChats" },
                new[] { "MruWorktreesMenu", "MruGitPull", "MruGitFetch" },
                new[] { "MruAddTags", "MruRemoveTags" },
                new[] { "MruCopyPath", "MruPin", "MruRemove" },
            };
            for (var index = 0; index < groups.Length; index++)
            {
                var name = groups[index].Attribute("id")!.Value;
                var commands = menu.Descendants().Where(element => element.Name == v + "Button" || element.Name == v + "Menu")
                    .Where(element => element.Element(v + "Parent")?.Attribute("id")?.Value == name)
                    .OrderBy(element => Convert.ToInt32(element.Attribute("priority")!.Value, 16))
                    .Select(element => element.Attribute("id")!.Value).ToArray();
                CollectionAssert.AreEqual(expectedCommands[index], commands, name);
                var symbol = menu.Descendants(v + "IDSymbol").Single(element => element.Attribute("name")?.Value == name);
                Assert.AreEqual(typeof(PackageIds).GetField(name)!.GetRawConstantValue(),
                    Convert.ToInt32(symbol.Attribute("value")!.Value, 16));
            }
        }

        [TestMethod]
        public void OpenContainingFolder_UsesTheOpenFolderKnownMoniker()
        {
            XNamespace v = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
            var button = Read("VSCommandTable.vsct").Descendants(v + "Button")
                .Single(element => element.Attribute("id")?.Value == "MruOpenFolder");
            Assert.AreEqual("ImageCatalogGuid", button.Element(v + "Icon")!.Attribute("guid")!.Value);
            Assert.AreEqual("OpenFolder", button.Element(v + "Icon")!.Attribute("id")!.Value);
            CollectionAssert.Contains(button.Elements(v + "CommandFlag").Select(element => element.Value).ToArray(), "IconIsMoniker");
        }

        [TestMethod]
        public void OpenWorktreeFlyout_UsesOneDynamicListAndReservedIdRange()
        {
            XNamespace v = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
            var menu = Read("VSCommandTable.vsct");
            var flyout = menu.Descendants(v + "Menu").Single(element => element.Attribute("id")?.Value == "MruWorktreesMenu");
            Assert.AreEqual("Open Worktree", flyout.Element(v + "Strings")!.Element(v + "ButtonText")!.Value);
            Assert.AreEqual("Menu", flyout.Attribute("type")!.Value);
            Assert.IsTrue(flyout.Elements(v + "CommandFlag").Any(flag => flag.Value == "DynamicVisibility"));
            var menuSymbol = menu.Descendants(v + "IDSymbol").Single(element => element.Attribute("name")?.Value == "MruWorktreesMenu");
            Assert.AreEqual(PackageIds.MruWorktreesMenu, Convert.ToInt32(menuSymbol.Attribute("value")!.Value, 16));
            Assert.IsFalse(menu.Descendants(v + "Menu").Any(element => element.Attribute("id")?.Value == "MruWorktreesNewInstanceMenu"));
            Assert.IsFalse(menu.Descendants(v + "Button").Any(element => element.Attribute("id")?.Value == "MruOpenWorktreeNewInstance"));
            var button = menu.Descendants(v + "Button").Single(element => element.Attribute("id")?.Value == "MruOpenWorktree");
            Assert.AreEqual("MruWorktreeActions", button.Element(v + "Parent")!.Attribute("id")!.Value);
            Assert.IsTrue(button.Elements(v + "CommandFlag").Any(flag => flag.Value == "DynamicItemStart"));
            var id = PackageIds.MruOpenWorktree;
            var symbol = menu.Descendants(v + "IDSymbol").Single(element => element.Attribute("name")?.Value == "MruOpenWorktree");
            Assert.AreEqual(id, Convert.ToInt32(symbol.Attribute("value")!.Value, 16));
            Assert.IsFalse(menu.Descendants(v + "IDSymbol").Any(element =>
            {
                int value = Convert.ToInt32(element.Attribute("value")!.Value, 16);
                return value > id && value < id + 0x1000;
            }));
            var type = typeof(MruOpenWorktree);
            Assert.AreEqual("BaseDynamicCommand`2", type.BaseType!.Name);
            Assert.AreEqual(id, type.GetCustomAttributesData().Single(attribute => attribute.AttributeType.Name == "CommandAttribute").ConstructorArguments[0].Value);
        }

        [TestMethod]
        public void NativeMenuAndMultiSelection_AreWiredForMouseKeyboardAndTheTagPicker()
        {
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var screen = Read("StartScreenControl.xaml");
            var list = screen.Descendants(p + "ListBox").Single(element => element.Attribute(x + "Name")?.Value == "MruPanel");
            Assert.AreEqual("Extended", list.Attribute("SelectionMode")!.Value);
            Assert.AreEqual("MruPanel_SelectionChanged", list.Attribute("SelectionChanged")!.Value);
            var card = Read("MruItemControl.xaml");
            Assert.IsFalse(card.Descendants(p + "ContextMenu").Any());
            var root = card.Descendants(p + "Border").Single(element => element.Attribute(x + "Name")?.Value == "RootBorder");
            Assert.AreEqual("RootBorder_PreviewMouseRightButtonDown", root.Attribute("PreviewMouseRightButtonDown")!.Value);
            Assert.AreEqual("RootBorder_MouseRightButtonUp", root.Attribute("MouseRightButtonUp")!.Value);
            var cardControl = screen.Descendants().Single(element => element.Name.LocalName == "MruItemControl");
            Assert.AreEqual("MruItemControl_PruneWorktreesRequested", cardControl.Attribute("PruneWorktreesRequested")!.Value);
            Assert.AreEqual("MruItemControl_NewChatRequested", cardControl.Attribute("NewChatRequested")!.Value);
            var dialog = Read("WorkspaceOrganizationDialog.xaml");
            Assert.IsTrue(dialog.Descendants(p + "ItemsControl").Any(element => element.Attribute(x + "Name")?.Value == "TagChoices"));
            Assert.IsTrue(dialog.Descendants(p + "Button").Any(element => element.Attribute("Click")?.Value == "ApplyTags_Click"));
            var menu = Read("VSCommandTable.vsct");
            XNamespace v = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
            foreach (var id in new[] { "MruOpen", "MruOpenNewInstance", "MruOpenFolder", "MruOpenTerminal", "MruGitPull", "MruGitFetch", "MruDeleteChats", "MruCopyPath", "MruPin", "MruRemove", "MruAddTags", "MruRemoveTags" })
            {
                Assert.IsTrue(menu.Descendants(v + "Button").Any(element => element.Attribute("id")?.Value == id));
                var symbol = menu.Descendants(v + "IDSymbol").Single(element => element.Attribute("name")?.Value == id);
                var commandId = (int)typeof(PackageIds).GetField(id)!.GetRawConstantValue()!;
                Assert.AreEqual(commandId, Convert.ToInt32(symbol.Attribute("value")!.Value, 16));
                var handler = typeof(MruItemMenu).Assembly.GetType("StartScreen.Commands." + id)!;
                var attribute = handler.GetCustomAttributesData().Single(data => data.AttributeType.Name == "CommandAttribute");
                Assert.AreEqual(commandId, attribute.ConstructorArguments[0].Value);
                Assert.IsNotNull(handler.GetMethod("InitializeAsync", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy));
            }
        }

        [TestMethod]
        [DoNotParallelize]
        [DataRow(false, false, false, true, true)]
        [DataRow(true, false, false, true, false)]
        [DataRow(false, true, false, true, false)]
        [DataRow(false, false, true, true, false)]
        [DataRow(false, false, false, false, false)]
        public async Task GitCommands_DisableDuringEitherOperationAndRouteFetchAsync(bool pulling, bool fetching, bool merging, bool hasBranch, bool enabled)
        {
            var executed = 0;
            MruItemMenu.Current = new MruItemMenu
            {
                Item = new MruItem { GitBranch = hasBranch ? "main" : null, CurrentOperation = merging ? "Merge" : null },
                Selection = [new MruItem()],
                IsPulling = pulling,
                IsFetching = fetching,
                ExecuteActionAsync = id => { executed = id; return Task.CompletedTask; },
            };
            try
            {
                var pull = Command(PackageIds.MruGitPull);
                var fetch = Command(PackageIds.MruGitFetch);
                Assert.AreEqual(enabled, pull.Enabled);
                Assert.AreEqual(enabled, fetch.Enabled);
                Assert.AreEqual(pulling ? "Pulling..." : "Git Pull", pull.Text);
                Assert.AreEqual(fetching ? "Fetching..." : "Git Fetch", fetch.Text);
                if (enabled)
                {
                    await MruItemMenu.ExecuteAsync(PackageIds.MruGitFetch);
                    Assert.AreEqual(PackageIds.MruGitFetch, executed);
                }
                MruItemMenu.Current = null;
                MruItemMenu.Update(fetch, PackageIds.MruGitFetch);
                Assert.IsFalse(fetch.Enabled);
                Assert.IsFalse(fetch.Visible);
            }
            finally
            {
                MruItemMenu.Current = null;
            }
        }

        private static OleMenuCommand Command(int id)
        {
            var command = new OleMenuCommand((_, _) => { }, new CommandID(PackageGuids.StartScreen, id));
            MruItemMenu.Update(command, id);
            return command;
        }

        private static XDocument Read(string name)
        {
            using var stream = typeof(MruOrganizationInteractionTests).Assembly.GetManifestResourceStream(name);
            return XDocument.Load(stream!);
        }

        private static void Layout(FrameworkElement element, double width)
        {
            for (var pass = 0; pass < 3; pass++)
            {
                element.Measure(new Size(width, double.PositiveInfinity));
                element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
                element.UpdateLayout();
            }
        }
    }
}
