using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Helpers;
using StartScreen.Models;

namespace StartScreen.Test
{
    [TestClass]
    public class CopilotChatHelperTests
    {
        private string _tempRoot;

        [TestInitialize]
        public void Initialize()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "StartScreenCopilotTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup; leftover temp dirs are harmless.
            }
        }

        [TestMethod]
        public void HasSessions_WithNullOrEmptyPath_ReturnsFalse()
        {
            Assert.IsFalse(CopilotChatHelper.HasSessions(null, MruItemType.Solution));
            Assert.IsFalse(CopilotChatHelper.HasSessions(string.Empty, MruItemType.Folder));
            Assert.IsFalse(CopilotChatHelper.HasSessions("   ", MruItemType.Project));
        }

        [TestMethod]
        public void HasSessions_WithNoVsFolder_ReturnsFalse()
        {
            var slnPath = Path.Combine(_tempRoot, "Demo.sln");
            File.WriteAllText(slnPath, "");

            Assert.IsFalse(CopilotChatHelper.HasSessions(slnPath, MruItemType.Solution));
        }

        [TestMethod]
        public void HasSessions_WithEmptySessionsFolder_ReturnsFalse()
        {
            var slnPath = Path.Combine(_tempRoot, "Demo.sln");
            File.WriteAllText(slnPath, "");
            Directory.CreateDirectory(Path.Combine(_tempRoot, ".vs", "Demo.sln", "copilot-chat", "894b4662", "sessions"));

            Assert.IsFalse(CopilotChatHelper.HasSessions(slnPath, MruItemType.Solution));
        }

        [TestMethod]
        public void HasSessions_WithSessionFileForSolution_ReturnsTrue()
        {
            var slnPath = Path.Combine(_tempRoot, "Demo.slnx");
            File.WriteAllText(slnPath, "");
            var sessionsDir = Path.Combine(_tempRoot, ".vs", "Demo.slnx", "copilot-chat", "894b4662", "sessions");
            Directory.CreateDirectory(sessionsDir);
            File.WriteAllText(Path.Combine(sessionsDir, Guid.NewGuid().ToString("D")), "");

            Assert.IsTrue(CopilotChatHelper.HasSessions(slnPath, MruItemType.Solution));
        }

        [TestMethod]
        public void HasSessions_WithSessionFileForFolder_ReturnsTrue()
        {
            var sessionsDir = Path.Combine(_tempRoot, ".vs", "MyFolder", "copilot-chat", "abc123", "sessions");
            Directory.CreateDirectory(sessionsDir);
            File.WriteAllText(Path.Combine(sessionsDir, Guid.NewGuid().ToString("D")), "");

            Assert.IsTrue(CopilotChatHelper.HasSessions(_tempRoot, MruItemType.Folder));
        }

        [TestMethod]
        public void HasSessions_IgnoresUnrelatedVsSubfolders()
        {
            var slnPath = Path.Combine(_tempRoot, "Demo.sln");
            File.WriteAllText(slnPath, "");
            // .vs\Demo.sln\v17\... is unrelated to copilot-chat
            Directory.CreateDirectory(Path.Combine(_tempRoot, ".vs", "Demo.sln", "v17", "Server"));

            Assert.IsFalse(CopilotChatHelper.HasSessions(slnPath, MruItemType.Solution));
        }

        [TestMethod]
        public void CountSessions_WithNoSessions_ReturnsZero()
        {
            var slnPath = Path.Combine(_tempRoot, "Demo.sln");
            File.WriteAllText(slnPath, "");

            Assert.AreEqual(0, CopilotChatHelper.CountSessions(slnPath, MruItemType.Solution));
            Assert.AreEqual(0, CopilotChatHelper.CountSessions(null, MruItemType.Solution));
        }

        [TestMethod]
        public void CountSessions_SumsAcrossAllChatFolders()
        {
            var slnPath = Path.Combine(_tempRoot, "Demo.sln");
            File.WriteAllText(slnPath, "");

            var firstSessions = Path.Combine(_tempRoot, ".vs", "Demo.sln", "copilot-chat", "abc", "sessions");
            var secondSessions = Path.Combine(_tempRoot, ".vs", "Demo.sln", "copilot-chat", "def", "sessions");
            Directory.CreateDirectory(firstSessions);
            Directory.CreateDirectory(secondSessions);

            File.WriteAllText(Path.Combine(firstSessions, Guid.NewGuid().ToString("D")), "");
            File.WriteAllText(Path.Combine(firstSessions, Guid.NewGuid().ToString("D")), "");
            File.WriteAllText(Path.Combine(secondSessions, Guid.NewGuid().ToString("D")), "");

            Assert.AreEqual(3, CopilotChatHelper.CountSessions(slnPath, MruItemType.Solution));
        }

        [TestMethod]
        public void DeleteAllSessions_WithNoSessions_ReturnsZero()
        {
            var slnPath = Path.Combine(_tempRoot, "Demo.sln");
            File.WriteAllText(slnPath, "");

            Assert.AreEqual(0, CopilotChatHelper.DeleteAllSessions(slnPath, MruItemType.Solution));
            Assert.AreEqual(0, CopilotChatHelper.DeleteAllSessions(null, MruItemType.Solution));
        }

        [TestMethod]
        public void DeleteAllSessions_RemovesSessionFilesAcrossAllChatFolders()
        {
            var slnPath = Path.Combine(_tempRoot, "Demo.sln");
            File.WriteAllText(slnPath, "");

            var firstSessions = Path.Combine(_tempRoot, ".vs", "Demo.sln", "copilot-chat", "abc", "sessions");
            var secondSessions = Path.Combine(_tempRoot, ".vs", "Demo.sln", "copilot-chat", "def", "sessions");
            Directory.CreateDirectory(firstSessions);
            Directory.CreateDirectory(secondSessions);

            File.WriteAllText(Path.Combine(firstSessions, Guid.NewGuid().ToString("D")), "");
            File.WriteAllText(Path.Combine(firstSessions, Guid.NewGuid().ToString("D")), "");
            File.WriteAllText(Path.Combine(secondSessions, Guid.NewGuid().ToString("D")), "");

            var deleted = CopilotChatHelper.DeleteAllSessions(slnPath, MruItemType.Solution);

            Assert.AreEqual(3, deleted);
            Assert.IsFalse(CopilotChatHelper.HasSessions(slnPath, MruItemType.Solution));
            // Folder structure itself is preserved
            Assert.IsTrue(Directory.Exists(firstSessions));
            Assert.IsTrue(Directory.Exists(secondSessions));
        }

        [TestMethod]
        public void DeleteAllSessions_LeavesUnrelatedVsFilesAlone()
        {
            var slnPath = Path.Combine(_tempRoot, "Demo.sln");
            File.WriteAllText(slnPath, "");

            var sessionsDir = Path.Combine(_tempRoot, ".vs", "Demo.sln", "copilot-chat", "abc", "sessions");
            Directory.CreateDirectory(sessionsDir);
            var sessionFile = Path.Combine(sessionsDir, Guid.NewGuid().ToString("D"));
            File.WriteAllText(sessionFile, "");

            var unrelatedDir = Path.Combine(_tempRoot, ".vs", "Demo.sln", "v17");
            Directory.CreateDirectory(unrelatedDir);
            var unrelatedFile = Path.Combine(unrelatedDir, "settings.json");
            File.WriteAllText(unrelatedFile, "{}");

            CopilotChatHelper.DeleteAllSessions(slnPath, MruItemType.Solution);

            Assert.IsFalse(File.Exists(sessionFile));
            Assert.IsTrue(File.Exists(unrelatedFile));
        }
    }
}
