using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class FileDropTests
    {
        [TestMethod]
        public void StartScreen_HandlesFileDropsBeforeChildControls()
        {
            using var stream = typeof(FileDropTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var root = XDocument.Load(stream!).Root!;

            Assert.AreEqual("True", root.Attribute("AllowDrop")?.Value);
            Assert.AreEqual("UserControl_DragOver", root.Attribute("PreviewDragEnter")?.Value);
            Assert.AreEqual("UserControl_DragOver", root.Attribute("PreviewDragOver")?.Value);
            Assert.AreEqual("UserControl_Drop", root.Attribute("PreviewDrop")?.Value);
            Assert.IsNull(root.Attribute("DragOver"));
            Assert.IsNull(root.Attribute("Drop"));
        }

        [STATestMethod]
        [DataRow("TextBox")]
        [DataRow("Button")]
        [DataRow("ListBox")]
        [DataRow("ScrollViewer")]
        [DataRow("Border")]
        public void FileDrop_InterceptsEveryChildAndOpensAllPathsOnce(string controlType)
        {
            FrameworkElement child = controlType switch
            {
                "TextBox" => new TextBox(),
                "Button" => new Button(),
                "ListBox" => new ListBox(),
                "ScrollViewer" => new ScrollViewer(),
                _ => new Border()
            };
            var panel = new Grid();
            panel.Children.Add(child);
            var root = new UserControl { AllowDrop = true, Content = panel };
            var paths = new[] { @"C:\Code\one.cs", @"C:\Code\two.txt" };
            var data = new DataObject(DataFormats.FileDrop, paths);
            var opened = new List<string>();
            var childEvents = 0;
            foreach (var routedEvent in new[] { DragDrop.PreviewDragEnterEvent, DragDrop.PreviewDragOverEvent, DragDrop.PreviewDropEvent })
            {
                root.AddHandler(routedEvent, new DragEventHandler((_, e) =>
                {
                    if (e.RoutedEvent == DragDrop.PreviewDropEvent)
                        StartScreenControl.HandleFileDrop(e, opened.Add);
                    else
                        StartScreenControl.HandleFileDragOver(e);
                }));
                child.AddHandler(routedEvent, new DragEventHandler((_, _) => childEvents++));
                var args = CreateArgs(data, child, routedEvent);

                child.RaiseEvent(args);

                Assert.IsTrue(args.Handled);
                Assert.AreEqual(DragDropEffects.Copy, args.Effects);
            }

            Assert.AreEqual(0, childEvents, "Child controls must not consume Explorer drops or insert file paths into search.");
            CollectionAssert.AreEqual(paths, opened);
        }

        [STATestMethod]
        [DataRow("PinnedMruItem")]
        [DataRow("UnicodeText")]
        public void OtherFormats_RemainAvailableToChildControls(string format)
        {
            var child = new Border();
            var root = new UserControl { AllowDrop = true, Content = child };
            root.PreviewDragOver += (_, e) => StartScreenControl.HandleFileDragOver(e);
            root.PreviewDrop += (_, e) => StartScreenControl.HandleFileDrop(e,
                _ => Assert.Fail("Non-file data must not open a file."));
            var data = new DataObject(format, "item");
            var childEvents = 0;
            child.PreviewDragOver += (_, e) => { childEvents++; e.Effects = DragDropEffects.Move; };
            child.PreviewDrop += (_, _) => childEvents++;

            var drag = CreateArgs(data, child, DragDrop.PreviewDragOverEvent);
            child.RaiseEvent(drag);
            var drop = CreateArgs(data, child, DragDrop.PreviewDropEvent);
            child.RaiseEvent(drop);

            Assert.IsFalse(drag.Handled);
            Assert.IsFalse(drop.Handled);
            Assert.AreEqual(DragDropEffects.Move, drag.Effects);
            Assert.AreEqual(2, childEvents);
        }

        [STATestMethod]
        public void EmptyFileDrop_IsRejectedWithoutOpening()
        {
            var target = new Border();
            var data = new DataObject(DataFormats.FileDrop, Array.Empty<string>());
            var drag = CreateArgs(data, target, DragDrop.PreviewDragOverEvent);
            StartScreenControl.HandleFileDragOver(drag);
            StartScreenControl.HandleFileDrop(CreateArgs(data, target, DragDrop.PreviewDropEvent),
                _ => Assert.Fail("Empty file drops must not open anything."));

            Assert.IsTrue(drag.Handled);
            Assert.AreEqual(DragDropEffects.None, drag.Effects);
        }

        [TestMethod]
        [DataRow("Code.cs")]
        [DataRow("Notes.txt")]
        [DataRow("settings.json")]
        [DataRow("Image.png")]
        [DataRow("README")]
        public async Task OrdinaryFiles_OpenAsDocumentsAsync(string name)
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath(name);
            File.WriteAllText(path, string.Empty);
            var opened = new List<string>();

            await StartScreenControl.OpenDroppedPathCoreAsync(path,
                _ => throw new AssertFailedException("Ordinary files must not be added as projects."),
                file => { opened.Add(file); return Task.CompletedTask; });

            CollectionAssert.AreEqual(new[] { path }, opened);
        }

        [TestMethod]
        [DataRow("App.sln")]
        [DataRow("App.slnx")]
        [DataRow("App.slnf")]
        [DataRow("App.CSPROJ")]
        [DataRow("App.vbproj")]
        [DataRow("App.fsproj")]
        [DataRow("App.vcxproj")]
        [DataRow("Folder.txt")]
        public async Task Workspaces_KeepTheirExistingOpenBehaviorAsync(string name)
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath(name);
            if (name == "Folder.txt")
                Directory.CreateDirectory(path);
            else
                File.WriteAllText(path, string.Empty);
            var opened = new List<string>();

            await StartScreenControl.OpenDroppedPathCoreAsync(path,
                workspace => { opened.Add(workspace); return Task.CompletedTask; },
                _ => throw new AssertFailedException("Workspaces must not be opened as ordinary documents."));

            CollectionAssert.AreEqual(new[] { path }, opened);
        }

        [TestMethod]
        public async Task MissingFile_ReportsFailureInsteadOfOpeningAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("Missing.txt");
            var error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                StartScreenControl.OpenDroppedPathCoreAsync(path,
                    _ => throw new AssertFailedException("Missing file must not open as a workspace."),
                    _ => throw new AssertFailedException("Missing file must not open as a document.")));

            Assert.AreEqual(path, error.FileName);
        }

        [TestMethod]
        public async Task OpenFailure_ReachesTheNotificationHandlerAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("File.txt");
            File.WriteAllText(path, string.Empty);
            var failure = new InvalidOperationException("Editor unavailable.");

            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                StartScreenControl.OpenDroppedPathCoreAsync(path, _ => Task.CompletedTask,
                    _ => Task.FromException(failure)));

            Assert.AreSame(failure, error);
        }

        private static DragEventArgs CreateArgs(IDataObject data, DependencyObject target, RoutedEvent routedEvent)
        {
            var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy | DragDropEffects.Move, target, new Point()], null)!;
            args.RoutedEvent = routedEvent;
            return args;
        }
    }
}
