using System.Text.Json;
using System.Xml.Linq;
using LibGit2Sharp;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test
{
    [TestClass]
    public class WorkspaceActivityTests
    {
        [TestMethod]
        public void BranchMatching_RequiresSourceRepositoryExactBranchAndOpenPr()
        {
            var item = Item();
            var matching = Pr();
            matching.RepoIdentifier = RemoteRepoIdentifier.TryParse("https://github.com/upstream/repo");
            var wrongFork = Pr();
            wrongFork.SourceRepoIdentifier = matching.RepoIdentifier;
            var wrongCase = Pr();
            wrongCase.SourceBranch = "Fix";
            var unknown = Pr();
            unknown.SourceRepoIdentifier = null;
            var closed = Pr();
            closed.Status = "closed";
            CollectionAssert.AreEqual(new[] { matching }, WorkspaceActivity.MatchBranch(item, [matching, wrongFork, wrongCase, unknown, closed]).ToArray());
            item.ApplyGitStatus(new GitStatus { BranchName = "fix", RemoteUrl = item.RemoteUrl, IsDetachedHead = true });
            Assert.IsEmpty(WorkspaceActivity.MatchBranch(item, [matching]));
        }

        [TestMethod]
        [DataRow("abc", "local HEAD")]
        [DataRow("def", "different commit")]
        [DataRow(null, "commit not verified")]
        public void Checks_OnlyValidateTheExactLocalCommit(string? checkedCommit, string expected)
        {
            var pr = Pr();
            pr.CiStatus = "success";
            pr.ChecksCommitId = checkedCommit;
            var text = WorkspaceActivity.Format(Item(), [pr], false);
            Assert.IsNotNull(text);
            StringAssert.Contains(text, "PR checks: success (" + expected + ")");
            Assert.DoesNotContain("2 open PRs", text);
        }

        [TestMethod]
        public void Tooltip_IsBoundedAndLabelsIncompleteDataWithoutInferringMissingWork()
        {
            var prs = Enumerable.Range(1, 4).Select(id =>
            {
                var pr = Pr();
                pr.Number = "#" + id;
                pr.WebUrl += id;
                pr.Title = new string('x', 300) + "\r\nInjected row";
                pr.LinkedWorkItems = Enumerable.Range(1, 4).ToDictionary(index => "https://example.com/" + index, index => "Task " + index);
                return pr;
            }).ToArray();
            var text = WorkspaceActivity.Format(Item(), prs, true);
            Assert.IsNotNull(text);
            StringAssert.Contains(text, "And 2 more branch PRs");
            StringAssert.Contains(text, "And 2 more linked items");
            StringAssert.Contains(text, "Cached or incomplete");
            Assert.DoesNotContain(new string('x', 161), text);
            Assert.DoesNotContain("Task 3", text);
            Assert.IsNull(WorkspaceActivity.Format(Item(), [], false));
        }

        [TestMethod]
        public void LocalGitChanges_ClearPreviouslyBoundBranchContextAndNotify()
        {
            var item = Item();
            var notifications = new List<string?>();
            item.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            item.ApplyBranchActivity("Branch PR #1: Fix");
            StringAssert.Contains(item.ToolTipText, "Branch PR #1");
            Assert.Contains(nameof(MruItem.BranchActivityTooltip), notifications);
            item.GitBranch = "other";
            Assert.IsNull(item.BranchActivityTooltip);
            item.ApplyBranchActivity("Old repository");
            item.RemoteUrl = "https://github.com/other/repo";
            Assert.IsNull(item.BranchActivityTooltip);
        }

        [TestMethod]
        public void GitSnapshot_CapturesTheActualHeadAndDetachedState()
        {
            using var directory = new TemporaryDirectory();
            Repository.Init(directory.Path);
            using var repo = new Repository(directory.Path);
            File.WriteAllText(directory.GetPath("file.txt"), "contents");
            LibGit2Sharp.Commands.Stage(repo, "file.txt");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            var commit = repo.Commit("Initial", signature, signature);
            Assert.AreEqual(commit.Sha, GitHelper.GetGitStatus(directory.Path).HeadCommitId);
            LibGit2Sharp.Commands.Checkout(repo, commit);
            Assert.IsTrue(GitHelper.GetGitStatus(directory.Path).IsDetachedHead);
        }

        [TestMethod]
        public void GitHubMetadata_UsesExplicitForkCommitAndClosingIssueRelationships()
        {
            var pr = Pr();
            using var metadata = JsonDocument.Parse("""
                {"headRefName":"fix","headRefOid":"abc","headRepository":{"url":"https://github.com/fork/repo"},
                 "closingIssuesReferences":{"nodes":[{"url":"https://github.com/o/r/issues/1","title":"The issue"}]},
                 "commits":{"nodes":[{"commit":{"oid":"abc","statusCheckRollup":{"state":"SUCCESS"}}}]}}
                """);
            GitHubDevHubProvider.ApplyActionMetadata(pr, metadata.RootElement);
            Assert.AreEqual("fork", pr.SourceRepoIdentifier.Owner);
            Assert.AreEqual("abc", pr.HeadCommitId);
            Assert.AreEqual("abc", pr.ChecksCommitId);
            Assert.AreEqual("The issue", pr.LinkedWorkItems["https://github.com/o/r/issues/1"]);
            Assert.IsNull(WorkspaceActivity.Format(Item(), [pr], false));
        }

        [TestMethod]
        public void AzureMetadata_PreservesSourceCommitAndExplicitWorkItemLinks()
        {
            var repo = RemoteRepoIdentifier.TryParse("https://dev.azure.com/o/p/_git/r");
            var pr = AzureDevOpsDevHubProvider.ParseAdoPullRequests("""
                {"value":[{"pullRequestId":1,"title":"Fix","status":"active","creationDate":"2026-11-06T00:00:00Z",
                "sourceRefName":"refs/heads/fix","targetRefName":"refs/heads/main","lastMergeSourceCommit":{"commitId":"abc"},
                "workItemRefs":[{"id":"42"}]}]}
                """, repo).Single();
            Assert.AreEqual(repo, pr.SourceRepoIdentifier);
            Assert.AreEqual("abc", pr.HeadCommitId);
            Assert.AreEqual("Work item 42", pr.LinkedWorkItems["https://dev.azure.com/o/p/_workitems/edit/42"]);
        }

        [TestMethod]
        public void CorrelationMetadata_ChangesInvalidatePresentationEquality()
        {
            var left = Pr();
            var right = Pr();
            Assert.IsTrue(DevHubItemComparer.SamePullRequests([left], [right]));
            right.HeadCommitId = "new";
            Assert.IsFalse(DevHubItemComparer.SamePullRequests([left], [right]));
            left.HeadCommitId = "new";
            right.LinkedWorkItems["https://example.com/1"] = "Task";
            Assert.IsFalse(DevHubItemComparer.SamePullRequests([left], [right]));
        }

        [TestMethod]
        public void MruTooltip_AddsOnlySupplementalBranchContext()
        {
            using var stream = typeof(WorkspaceActivityTests).Assembly.GetManifestResourceStream("MruItemControl.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var binding = document.Descendants(xaml + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding BranchActivityTooltip}");
            Assert.IsTrue(binding.Ancestors(xaml + "ToolTip").Any());
            Assert.AreEqual("400", binding.Attribute("MaxWidth")?.Value);
        }

        private static MruItem Item()
        {
            var item = new MruItem { Path = @"C:\Code\repo" };
            item.ApplyGitStatus(new GitStatus { BranchName = "fix", HeadCommitId = "abc", RemoteUrl = "https://github.com/o/r" });
            return item;
        }

        private static DevHubPullRequest Pr() => new()
        {
            SourceBranch = "fix", SourceRepoIdentifier = RemoteRepoIdentifier.TryParse("https://github.com/o/r"),
            RepoIdentifier = RemoteRepoIdentifier.TryParse("https://github.com/o/r"), Status = "open",
            Title = "Fix", Number = "#1", WebUrl = "https://github.com/o/r/pull/1",
        };
    }
}
