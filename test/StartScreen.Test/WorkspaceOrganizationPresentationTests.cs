using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using StartScreen.Models;
using StartScreen.Services;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class WorkspaceOrganizationPresentationTests
    {
        [TestMethod]
        public void TagChips_ToggleMatchAllAndShowActiveSelections()
        {
            var tags = new WorkspaceTagsService();
            tags.SetTags(@"C:\Work.sln", "work, urgent");
            tags.SetTags(@"C:\Home.sln", "personal");
            using var vm = new StartScreenViewModel([
                new MruItem { Path = @"C:\Work.sln", Name = "Work" },
                new MruItem { Path = @"C:\Home.sln", Name = "Home" },
            ]);
            vm.ApplyWorkspaceTags(tags);
            Assert.IsFalse(vm.HasTagFilter);
            Assert.HasCount(3, vm.TagFilters);
            Assert.IsTrue(vm.TagFilters.All(option => !option.IsSelected));

            vm.TagFilter = "work, urgent";
            Assert.IsTrue(vm.HasTagFilter);
            Assert.AreEqual(@"C:\Work.sln", vm.VisibleMruItems.Single().Path);
            Assert.IsTrue(vm.TagFilters.Single(option => option.Name == "work").IsSelected);
            Assert.IsTrue(vm.TagFilters.Single(option => option.Name == "urgent").IsSelected);
            var workChip = vm.TagFilters.Single(option => option.Name == "work");
            vm.ToggleTagFilter("urgent");
            Assert.AreEqual("work", vm.TagFilter);
            Assert.IsFalse(vm.TagFilters.Single(option => option.Name == "urgent").IsSelected);
            Assert.AreSame(workChip, vm.TagFilters.Single(option => option.Name == "work"));

            vm.ToggleTagFilter("work");
            Assert.IsFalse(vm.HasTagFilter);
            Assert.HasCount(2, vm.VisibleMruItems);
            vm.ToggleTagFilter("personal");
            vm.ToggleTagFilter("work");
            Assert.IsEmpty(vm.VisibleMruItems);
            Assert.IsTrue(vm.HasNoSearchResults);
        }

        [TestMethod]
        public void EmptyAndCustomFilters_HaveSelectableChips()
        {
            using var vm = new StartScreenViewModel([]);
            Assert.IsEmpty(vm.TagFilters);
            vm.TagFilter = "new-tag";
            Assert.IsTrue(vm.TagFilters.Single().IsSelected);
            Assert.AreEqual("new-tag", vm.TagFilters.Single().Name);
            vm.ToggleTagFilter("new-tag");
            Assert.IsEmpty(vm.TagFilters);
            Assert.IsFalse(vm.HasTagFilter);
        }

        [TestMethod]
        public void TagChanges_RetireOnlyUnusedTagsAndClearTheirActiveFilters()
        {
            var first = new MruItem { Name = "App one", Path = @"C:\First.sln" };
            var second = new MruItem { Name = "App two", Path = @"C:\Second.sln" };
            var tags = new WorkspaceTagsService();
            tags.SetTags(first.Path, "urgent, work");
            tags.SetTags(second.Path, "work, personal");
            using var vm = new StartScreenViewModel([first, second]);
            vm.ApplyWorkspaceTags(tags);
            vm.SelectedMruItem = first;
            vm.SearchFilter = "App";
            vm.TagFilter = "urgent, work";
            Assert.AreSame(first, vm.VisibleMruItems.Single());

            tags.ChangeTags([first.Path], "urgent", true);
            vm.ApplyWorkspaceTags(tags);
            Assert.AreEqual("work", vm.TagFilter);
            Assert.IsFalse(vm.TagFilters.Any(option => option.Name == "urgent"));
            Assert.HasCount(2, vm.VisibleMruItems);

            tags.ChangeTags([first.Path], "work", true);
            vm.ApplyWorkspaceTags(tags);
            Assert.AreEqual("work", vm.TagFilter, "A tag used by another project must not be retired.");
            Assert.AreSame(second, vm.VisibleMruItems.Single());
            tags.ChangeTags([second.Path], "WORK", true);
            vm.ApplyWorkspaceTags(tags);
            Assert.IsFalse(vm.HasTagFilter);
            Assert.AreEqual("personal", vm.AvailableWorkspaceTags.Single());
            Assert.AreEqual("personal", vm.TagFilters.Single().Name);
            Assert.AreEqual("App", vm.SearchFilter);
            Assert.HasCount(2, vm.VisibleMruItems);
            Assert.AreSame(first, vm.SelectedMruItem);
            Assert.IsFalse(first.HasTags);
            Assert.AreEqual("personal", second.TagsText);
        }

        [TestMethod]
        public void TagSuggestions_IncludeSavedWorkspacesOutsideTheCurrentMru()
        {
            var tags = new WorkspaceTagsService();
            tags.SetTags(@"C:\Earlier.sln", "client");
            using var vm = new StartScreenViewModel([]);
            vm.ApplyWorkspaceTags(tags);
            Assert.AreEqual("client", vm.AvailableWorkspaceTags.Single());
        }

        [STATestMethod]
        [DataRow(false, 0, 12d)]
        [DataRow(false, 1, 12d)]
        [DataRow(true, 1, 12d)]
        [DataRow(false, 1, 20d)]
        [DataRow(true, 60, 20d)]
        public void TagEditor_SizesToContentAndKeepsActionsVisible(bool remove, int count, double fontSize)
        {
            using var vm = new StartScreenViewModel([]);
            var dialog = ParseDialogContent();
            dialog.DataContext = vm;
            var choices = (ItemsControl)dialog.FindName("TagChoices");
            choices.ItemsSource = Enumerable.Range(0, count)
                .Select(index => new WorkspaceOrganizationDialog.TagChoice("Project tag " + index)).ToArray();
            ((StackPanel)dialog.FindName("NewTagsPanel")).Visibility = remove ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)dialog.FindName("BulkTagSummary")).Text = "Add tags to 2 projects. Existing tags are kept.";
            var definition = ReadDialog().Root!;
            var host = new Window
            {
                Content = dialog, Width = double.Parse(definition.Attribute("Width")!.Value),
                MaxHeight = double.Parse(definition.Attribute("MaxHeight")!.Value),
                SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None,
                FontSize = fontSize, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false,
            };
            try
            {
                host.Show();
                host.UpdateLayout();
                var apply = (Button)dialog.FindName("ApplyTagsButton");
                Assert.IsTrue(apply.IsEnabled);
                Assert.IsTrue(apply.IsDefault);
                Assert.IsLessThanOrEqualTo(host.ActualHeight, apply.TranslatePoint(new Point(0, apply.ActualHeight), host).Y);
                Assert.AreEqual(new Thickness(0), ((TextBox)dialog.FindName("BulkTagText")).BorderThickness);
                if (count < 2 && fontSize == 12)
                    Assert.IsLessThanOrEqualTo(260d, host.ActualHeight, "A short tag list must not leave a large empty dialog.");
                if (count > 1)
                {
                    var scroll = (ScrollViewer)dialog.FindName("TagEditorScroll");
                    Assert.IsGreaterThan(0d, scroll.ScrollableHeight);
                }
                if (count > 0)
                {
                    var presenter = (ContentPresenter)choices.ItemContainerGenerator.ContainerFromIndex(0);
                    var check = (CheckBox)VisualTreeHelper.GetChild(presenter, 0);
                    check.IsChecked = true;
                    Assert.IsTrue(((WorkspaceOrganizationDialog.TagChoice)choices.Items[0]).IsSelected);
                }
            }
            finally
            {
                host.Close();
            }
        }

        [STATestMethod]
        public void Organization_TitleBarTracksThemeResourcesAndUsesCustomDraggableChrome()
        {
            var definition = ReadDialog();
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            Assert.AreEqual("None", definition.Root!.Attribute("WindowStyle")!.Value);
            Assert.AreEqual("False", definition.Root.Attribute("HasDialogFrame")!.Value);
            StringAssert.Contains(definition.Root.Attribute("Foreground")!.Value, "EnvironmentColors.ToolWindowTextBrushKey");
            var chrome = definition.Descendants(p + "WindowChrome").Single();
            Assert.AreEqual("32", chrome.Attribute("CaptionHeight")!.Value);
            Assert.AreEqual("False", chrome.Attribute("UseAeroCaptionButtons")!.Value);
            var close = definition.Descendants(p + "Button").Single(button => button.Attribute("Click")?.Value == "CloseDialog_Click");
            Assert.AreEqual("True", close.Attribute("WindowChrome.IsHitTestVisibleInChrome")!.Value);
            Assert.IsTrue(definition.Descendants(p + "Button").Any(button => button.Attribute("IsCancel")?.Value == "True"));

            var dialog = ParseDialogContent();
            var title = (DockPanel)dialog.FindName("TitleBar");
            foreach (var brush in new[] { new SolidColorBrush(Color.FromRgb(37, 37, 38)), SystemColors.ControlBrush, SystemColors.WindowBrush })
            {
                dialog.Resources["TestThemeBrush"] = brush;
                Layout(dialog);
                Assert.AreSame(brush, title.Background);
            }
        }

        [STATestMethod]
        public void RecentToolbar_LeavesRoomForProjectsAtNarrowWidths()
        {
            using var vm = new StartScreenViewModel([]);
            var toolbar = ParseToolbar();
            toolbar.DataContext = vm;
            Layout(toolbar);
            Assert.IsLessThanOrEqualTo(4d, toolbar.DesiredSize.Height, "No tags should leave no empty heading row.");
            var row = toolbar.Children.OfType<DockPanel>().Single();
            Assert.HasCount(2, row.Children, "Only tag chips and Clear belong in this row.");
            var clear = row.Children.OfType<TextBlock>().Single();
            Assert.AreEqual(Visibility.Collapsed, clear.Visibility);

            var tags = new WorkspaceTagsService();
            tags.SetTags(@"C:\App.sln", "work, urgent, client");
            vm.ApplyWorkspaceTags(tags);
            Layout(toolbar);
            Assert.IsLessThanOrEqualTo(120d, toolbar.DesiredSize.Height);
            Assert.IsLessThanOrEqualTo(280d, toolbar.DesiredSize.Width);
            vm.ToggleTagFilter("work");
            Layout(toolbar);
            Assert.AreEqual(Visibility.Visible, clear.Visibility);
            Assert.IsTrue(vm.TagFilters.Single(tag => tag.Name == "work").IsSelected);
            vm.ToggleTagFilter("work");
            Layout(toolbar);
            Assert.AreEqual(Visibility.Collapsed, clear.Visibility);
        }

        private static void Layout(FrameworkElement toolbar)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                toolbar.Measure(new Size(280, double.PositiveInfinity));
                toolbar.Arrange(new Rect(0, 0, 280, toolbar.DesiredSize.Height));
                toolbar.UpdateLayout();
            }
        }

        private static StackPanel ParseToolbar()
        {
            using var stream = typeof(WorkspaceOrganizationPresentationTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var toolbar = new XElement(document.Descendants(p + "StackPanel").Single(panel =>
                panel.Attribute(x + "Name")?.Value == "ProjectTagFilters"));
            var resources = new XElement(p + "ResourceDictionary");
            foreach (var key in new[] { "ProjectTagFilterStyle" })
            {
                var style = new XElement(document.Descendants(p + "Style").Single(element => element.Attribute(x + "Key")?.Value == key));
                resources.Add(style);
            }
            resources.Add(new XElement(p + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "BoolToVisibilityConverter")));
            toolbar.AddFirst(new XElement(p + "StackPanel.Resources", resources));
            foreach (var attribute in toolbar.Descendants().Attributes("Click").ToArray())
                attribute.Remove();
            return (StackPanel)SharedXamlReader.Parse(toolbar.ToString());
        }

        private static XDocument ReadDialog()
        {
            using var stream = typeof(WorkspaceOrganizationPresentationTests).Assembly.GetManifestResourceStream("WorkspaceOrganizationDialog.xaml");
            return XDocument.Load(stream!);
        }

        private static Border ParseDialogContent()
        {
            var document = ReadDialog();
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var content = new XElement(document.Root!.Element(p + "Border")!);
            var resources = new XElement(p + "ResourceDictionary");
            var dialogResources = document.Root.Elements().Single(element => element.Name.LocalName == "DialogWindow.Resources")
                .Element(p + "ResourceDictionary")!;
            foreach (var resource in dialogResources.Elements()
                .Where(element => element.Name != p + "ResourceDictionary.MergedDictionaries"))
            {
                var copy = new XElement(resource);
                copy.Attribute("BasedOn")?.Remove(); // Native base styles are supplied by the VS host.
                resources.Add(copy);
            }
            content.AddFirst(new XElement(p + "Border.Resources", resources));
            content.Descendants().Attributes("Click").Remove();
            return (Border)SharedXamlReader.Parse(content.ToString());
        }
    }
}
