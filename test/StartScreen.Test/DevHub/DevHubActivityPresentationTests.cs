using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Xml.Linq;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using XamlReader = StartScreen.Test.SharedXamlReader;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubActivityPresentationTests
    {
        [TestMethod]
        [DataRow(null, false, "Approved")]
        [DataRow(false, false, "Approved")]
        [DataRow(true, true, "Ready to merge")]
        public void MergeReadiness_RequiresExplicitProviderConfirmation(bool? canMerge, bool attention, string status)
        {
            var pr = OwnPullRequest();
            pr.IsApproved = true;
            pr.CiStatus = "success";
            pr.CanMerge = canMerge;

            Assert.AreEqual(status, pr.StatusText);
            Assert.AreEqual(attention, pr.NeedsAttention);
            Assert.AreEqual(canMerge == true ? "Merge when ready" : "Check remaining merge requirements", pr.NextActionText);
        }

        [TestMethod]
        public void MultipleBlockers_KeepPrimaryStatusAndAllTooltipDetails()
        {
            var pr = OwnPullRequest();
            pr.Status = "conflicts";
            pr.HasChangesRequested = true;
            pr.CiStatus = "failure";
            pr.UnresolvedThreadCount = 3;
            pr.ChecksSummary = "Unit tests failed";
            pr.ChecksUrl = "https://example.com/checks/1";
            pr.Author = "me";
            var card = DevHubWorkItem.GetMyWork([pr], [], []).Single();

            Assert.AreEqual("Conflicts", card.StatusText);
            Assert.IsNull(card.NextActionText);
            foreach (var detail in new[] { "Author: me", "Changes requested", "Checks: failure", "Unresolved discussions: 3", "Unit tests failed" })
                StringAssert.Contains(card.StatusDetails, detail);
        }

        [TestMethod]
        public void DiscussionAndMentionSignals_DoNotInventUnreadOrDraftActions()
        {
            var pr = OwnPullRequest();
            pr.IsMentioned = true;
            Assert.IsFalse(pr.NeedsAttention);
            StringAssert.Contains(pr.StatusDetails, "not an unread indicator");
            pr.UnresolvedThreadCount = 1;
            Assert.AreEqual("Unresolved discussions", pr.AttentionReason);
            pr.Status = "draft";
            Assert.IsFalse(pr.NeedsAttention);
            Assert.AreEqual("Draft", pr.StatusText);
            pr.Status = "open";
            pr.IsAuthoredByCurrentUser = false;
            Assert.IsFalse(pr.NeedsAttention);
        }

        [TestMethod]
        public void AssignedTasks_PreserveWorkflowStateAndKeepTaskMetadataInTooltip()
        {
            var issue = new DevHubIssue
            {
                Title = "Fix regression", WebUrl = "https://example.com/issues/1", State = "In progress",
                IsAssignedToCurrentUser = true, AssignedTo = "Me", WorkItemType = "Bug", Priority = "P1", Iteration = "Sprint 12",
                UpdatedAt = DateTime.UtcNow,
            };
            var card = DevHubWorkItem.GetMyWork([], [issue], []).Single();

            Assert.AreEqual("In progress", card.StatusText);
            Assert.IsTrue(card.IsAssignedToCurrentUser);
            Assert.IsNull(card.NextActionText);
            Assert.IsEmpty(card.Labels);
            StringAssert.Contains(card.StatusDetails, "Bug / P1");
            StringAssert.Contains(card.StatusDetails, "Assigned to: Me");
            StringAssert.Contains(card.StatusDetails, "Iteration: Sprint 12");
            issue.IsBlocked = true;
            Assert.AreEqual("Blocked", issue.AttentionReason);
            StringAssert.Contains(issue.StatusDetails, "In progress");
            issue.IsClosed = true;
            Assert.IsFalse(issue.NeedsAttention);
        }

        [TestMethod]
        public void FailedBuilds_StayInBuildInventoriesButNeverNeedAttention()
        {
            var own = new DevHubCiRun
            {
                Name = "Build", Status = "failure", IsTriggeredByCurrentUser = true, WebUrl = "https://example.com/build/1",
                FailureSummary = "Compile failed", FailureUrl = "https://example.com/build/1/logs",
            };
            var other = new DevHubCiRun { Name = "Other build", Status = "failure", WebUrl = "https://example.com/build/2" };
            var myWork = DevHubWorkItem.GetMyWork([], [], [], builds: [own, other]);
            var incoming = DevHubWorkItem.GetIncoming([], [], [own, other]);

            Assert.AreEqual("Build", myWork.Single().Title);
            Assert.IsNull(myWork.Single().AttentionReason);
            Assert.IsFalse(myWork.Single().NeedsAttention);
            Assert.IsFalse(incoming.Single().NeedsAttention);
            Assert.IsEmpty(DevHubWorkItem.Filter(myWork, DevHubWorkItemKind.All, true));
            Assert.IsEmpty(DevHubWorkItem.Filter(incoming, DevHubWorkItemKind.All, true));
            Assert.AreSame(myWork.Single(), DevHubWorkItem.Filter(myWork, DevHubWorkItemKind.Build, false).Single());
            Assert.AreSame(incoming.Single(), DevHubWorkItem.Filter(incoming, DevHubWorkItemKind.Build, false).Single());
            Assert.AreEqual("Build failure", myWork.Single().StatusText);
            Assert.AreEqual("Compile failed", myWork.Single().StatusDetails);
            Assert.AreEqual("Compile failed", myWork.Single().NextActionText);
            Assert.AreEqual("Other build", incoming.Single().Title);
            Assert.AreEqual(DevHubWorkItemKind.Build, myWork.Single().Kind);
        }

        [TestMethod]
        [DataRow("update-ty-aabbd38e3ec2dd636e26f2f9157d1e615d578", true)]
        [DataRow("update-ty-aabbd38e3ec2dd636e26f2f9157d1e615d578", false)]
        [DataRow("main", false)]
        [DataRow(null, true)]
        public void BuildCards_KeepBranchNamesOnHoverInsteadOfInHeadings(string? branch, bool personal)
        {
            var build = new DevHubCiRun
            {
                Name = "Validate JSON Schemas", RepoDisplayName = "SchemaStore/schemastore", Branch = branch,
                Status = "failure", FailureSummary = "Compile failed", IsTriggeredByCurrentUser = personal,
                WebUrl = "https://example.com/build/1",
            };
            var card = (personal ? DevHubWorkItem.GetMyWork([], [], [], builds: [build])
                : DevHubWorkItem.GetIncoming([], [], [build])).Single();

            Assert.AreEqual("SchemaStore/schemastore", card.Detail);
            Assert.AreEqual(build.Name, card.Title);
            StringAssert.Contains(card.StatusDetails, "Compile failed");
            if (branch != null)
                StringAssert.Contains(card.StatusDetails, "Branch: " + branch);
            Assert.IsFalse(card.NeedsAttention);
        }

        [TestMethod]
        [DataRow(0d, "<1s")]
        [DataRow(0.5d, "<1s")]
        [DataRow(12d, "12s")]
        [DataRow(60d, "1m 0s")]
        [DataRow(135d, "2m 15s")]
        [DataRow(3600d, "1h 0m")]
        [DataRow(90061d, "25h 1m")]
        [DataRow(-1d, null)]
        public void BuildDuration_UsesCompactElapsedUnits(double seconds, string? expected)
        {
            Assert.AreEqual(expected, new DevHubCiRun { Duration = TimeSpan.FromSeconds(seconds) }.DurationText);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void BuildDuration_ReachesBothWorkBladesAndSurvivesCacheSerialization(bool personal)
        {
            var build = new DevHubCiRun
            {
                Name = "Build", Duration = TimeSpan.FromSeconds(135), IsTriggeredByCurrentUser = personal,
                WebUrl = "https://example.com/build/1",
            };
            var cached = JsonSerializer.Deserialize<DevHubCiRun>(JsonSerializer.Serialize(build))!;
            var item = (personal ? DevHubWorkItem.GetMyWork([], [], [], builds: [cached])
                : DevHubWorkItem.GetIncoming([], [], [cached])).Single();

            Assert.AreEqual(build.Duration, cached.Duration);
            Assert.AreEqual("2m 15s", item.DurationText);
            Assert.IsNull(new DevHubCiRun().DurationText);
        }

        [TestMethod]
        public void RecentCompletion_IsOptionalPersonalBoundedAndNeverNeedsAttention()
        {
            var merged = OwnPullRequest();
            merged.Status = "closed";
            merged.IsMerged = true;
            merged.CiStatus = "failure";
            merged.UpdatedAt = DateTime.UtcNow.AddDays(-1);
            var old = OwnPullRequest();
            old.WebUrl += "-old";
            old.Status = "closed";
            old.IsMerged = true;
            old.UpdatedAt = DateTime.UtcNow.AddDays(-15);
            var assigned = new DevHubIssue
            {
                Title = "Done", WebUrl = "https://example.com/issues/1", State = "Done", IsClosed = true,
                IsAssignedToCurrentUser = true, UpdatedAt = DateTime.UtcNow,
            };

            Assert.IsEmpty(DevHubWorkItem.GetMyWork([merged, old], [assigned], []));
            var recent = DevHubWorkItem.GetMyWork([merged, old], [assigned], [], includeCompleted: true);
            Assert.HasCount(2, recent);
            Assert.IsTrue(recent.All(item => item.Section == "Recently completed" && item.IsCompleted && !item.NeedsAttention));
            Assert.AreEqual("Merged", recent.Single(item => item.IsPullRequest).StatusText);
            Assert.IsEmpty(DevHubWorkItem.GetIncoming([merged, old], [assigned], []));
        }

        [TestMethod]
        public void MergeSupplementalMetadata_PreservesDirectRequestsAndUnknownDetailsWithoutDuplicates()
        {
            var feed = OwnPullRequest();
            feed.IsAuthoredByCurrentUser = false;
            feed.CiStatus = "failure";
            var personal = OwnPullRequest();
            personal.IsReviewRequestedForCurrentUser = true;
            personal.IsMentioned = true;
            personal.UnresolvedThreadCount = 2;
            personal.ChecksSummary = "Tests failed";
            var merged = DevHubService.MergePullRequests([personal], [feed]).Single();

            Assert.AreSame(feed, merged);
            Assert.IsTrue(merged.IsAuthoredByCurrentUser);
            Assert.IsTrue(merged.IsReviewRequestedForCurrentUser);
            Assert.IsTrue(merged.IsMentioned);
            Assert.AreEqual(2, merged.UnresolvedThreadCount);
            Assert.AreEqual("Tests failed", merged.ChecksSummary);
            Assert.AreEqual("failure", merged.CiStatus);
        }

        [TestMethod]
        public void MetadataChanges_AreNotSuppressedByComparers()
        {
            var left = OwnPullRequest();
            var right = OwnPullRequest();
            Assert.IsTrue(DevHubItemComparer.SamePullRequests([left], [right]));
            right.CanMerge = true;
            Assert.IsFalse(DevHubItemComparer.SamePullRequests([left], [right]));
            var issue = new DevHubIssue { State = "Active" };
            var changedIssue = new DevHubIssue { State = "Active", Iteration = "Sprint 1" };
            Assert.IsFalse(DevHubItemComparer.SameIssues([issue], [changedIssue]));
            var build = new DevHubCiRun { Status = "failure" };
            var changedBuild = new DevHubCiRun { Status = "failure", FailureUrl = "https://example.com/log" };
            Assert.IsFalse(DevHubItemComparer.SameCiRuns([build], [changedBuild]));
        }

        [TestMethod]
        public async Task ExpandedOptions_ReachEverySourceAndScopeAndRoundTripInCacheAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "StartScreenTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var provider = new ActivityProvider();
                var cache = new DevHubCacheService(path);
                var service = new DevHubService(cache, [provider], _ => Task.FromResult<IReadOnlyList<string>>(["https://example.com/repo"]),
                    () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);
                var options = new DevHubActivityOptions(120, true);
                var dashboard = await service.RefreshAsync(CancellationToken.None, options: options);

                Assert.HasCount(4, provider.Requests);
                Assert.IsTrue(provider.Requests.All(request => request.MaxItems == 120 && request.IncludeCompleted));
                Assert.AreEqual(120, dashboard.ActivityLimit);
                Assert.IsTrue(dashboard.IncludesCompleted);
                var cached = await cache.ReadDashboardAsync();
                Assert.IsNotNull(cached);
                Assert.AreEqual(120, cached.ActivityLimit);
                Assert.IsTrue(cached.IncludesCompleted);
                var scoped = await service.ScopeToRepoAsync(provider.Repo, CancellationToken.None, options);
                Assert.HasCount(5, provider.Requests);
                Assert.AreSame(options, provider.Requests.Last());
                Assert.AreEqual(120, scoped.ActivityLimit);
                Assert.IsTrue(scoped.IncludesCompleted);
            }
            finally
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(301)]
        public void ActivityLimits_RejectUnsupportedValues(int limit)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DevHubActivityOptions(limit));
        }

        [TestMethod]
        public void OldDashboardCache_DefaultsToConservativeActivityLimit()
        {
            var dashboard = JsonSerializer.Deserialize<DevHubDashboard>("{}");
            Assert.IsNotNull(dashboard);
            Assert.AreEqual(30, dashboard.ActivityLimit);
            Assert.IsFalse(dashboard.IncludesCompleted);
        }

        [STATestMethod]
        public void EnrichedCards_CollapseMissingDetailsAndWrapWithoutClipping()
        {
            using var stream = typeof(DevHubActivityPresentationTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var action = document.Descendants(p + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding NextActionText}");
            Assert.AreEqual("Wrap", action.Attribute("TextWrapping")?.Value);
            Assert.AreEqual("{Binding NextActionText, Converter={StaticResource NullToCollapsedConverter}}", action.Attribute("Visibility")?.Value);
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Width = 180 };
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(DevHubPullRequest.NextActionText)));
            text.DataContext = new DevHubPullRequest { Status = "open", IsAuthoredByCurrentUser = true, MergeBlockReason = "Waiting for required policy approval and additional validation" };
            text.Measure(new Size(180, double.PositiveInfinity));
            Assert.IsGreaterThan(0d, text.DesiredSize.Height);
            Assert.IsLessThanOrEqualTo(180d, text.DesiredSize.Width);
        }

        [TestMethod]
        public void ActivityControls_WireLoadMoreHistoryWithoutCheckDetailsLink()
        {
            using var stream = typeof(DevHubActivityPresentationTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var loadMore = document.Descendants(p + "Hyperlink").Single(element => element.Attribute(x + "Name")?.Value == "LoadMoreLink");
            Assert.AreEqual("LoadMoreActivity_Click", loadMore.Attribute("Click")?.Value);
            var completed = document.Descendants(p + "CheckBox").Single(element => element.Attribute(x + "Name")?.Value == "IncludeCompletedItems");
            Assert.AreEqual("IncludeCompletedItems_Changed", completed.Attribute("Checked")?.Value);
            Assert.AreEqual("IncludeCompletedItems_Changed", completed.Attribute("Unchecked")?.Value);
            Assert.AreEqual("Include completed work (14 days)", completed.Attribute("Content")?.Value);
            Assert.IsEmpty(document.Descendants(p + "Button").Where(element => element.Attribute("Content")?.Value == "View check details"));
            Assert.IsEmpty(document.Descendants(p + "TextBlock").Where(element => element.Attribute("Text")?.Value == "{Binding MetadataText}"));
            var workBuilds = document.Descendants(p + "RadioButton").Single(element => element.Attribute(x + "Name")?.Value == "MyWorkBuildFilter");
            Assert.AreEqual("MyWorkKind", workBuilds.Attribute("GroupName")?.Value);
            var incoming = document.Descendants(p + "ItemsControl").Single(element => element.Attribute(x + "Name")?.Value == "IncomingList");
            Assert.IsEmpty(incoming.Elements(p + "ItemsControl.GroupStyle"), "Team activity must not display redundant section headings.");
        }

        [TestMethod]
        public void PullRequestCards_HaveInlineAuthorsAndNoRedundantReviewerHint()
        {
            var pr = OwnPullRequest();
            pr.Author = "me";
            var own = DevHubWorkItem.GetMyWork([pr], [], []).Single();
            Assert.AreEqual("Waiting for review", own.StatusText);
            Assert.IsNull(own.NextActionText);
            Assert.AreEqual("by me", own.AuthorText);
            pr.IsAuthoredByCurrentUser = false;
            pr.IsReviewRequestedForCurrentUser = true;
            var requested = DevHubWorkItem.GetMyWork([pr], [], []).Single();
            Assert.AreEqual("by me", requested.AuthorText);
            Assert.AreEqual("Review requested", requested.StatusText);
            Assert.IsNull(requested.NextActionText);
            pr.Author = " ";
            Assert.IsNull(DevHubWorkItem.GetMyWork([pr], [], []).Single().AuthorText);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void IssueCards_KeepPriorityAndTypeTogetherAsLabels(bool personal)
        {
            var issue = new DevHubIssue
            {
                Title = "Issue", State = "open", WebUrl = "https://example.com/issues/1", Priority = "Priority:3",
                WorkItemType = "Feature", IsAssignedToCurrentUser = personal,
                Labels = [new DevHubLabel { Name = "priority:3" }, new DevHubLabel { Name = "Type:Feature" }, new DevHubLabel { Name = "Type:CustomerSupport" }],
            };
            var card = (personal ? DevHubWorkItem.GetMyWork([], [issue], []) : DevHubWorkItem.GetIncoming([], [issue], [])).Single();
            Assert.IsNull(card.StatusText);
            Assert.AreEqual("priority:3,Type:Feature,Type:CustomerSupport", string.Join(",", card.Labels.Select(label => label.Name)));
            Assert.AreSame(issue.Labels, card.Labels);
            Assert.AreEqual(personal, card.IsAssignedToCurrentUser);
            issue.IsAssignedToCurrentUser = true;
            issue.IsClosed = true;
            issue.UpdatedAt = DateTime.UtcNow;
            Assert.IsFalse(DevHubWorkItem.GetMyWork([], [issue], [], includeCompleted: true).Single().IsAssignedToCurrentUser);
        }

        [TestMethod]
        public void AssignmentPill_IsBelowTitleAndLabelsUseSolidPills()
        {
            using var stream = typeof(DevHubActivityPresentationTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var pill = document.Descendants(p + "Border").Single(element => element.Attribute(x + "Name")?.Value == "AssignmentPill");
            Assert.AreEqual("{Binding IsAssignedToCurrentUser, Converter={StaticResource BoolToVisibilityConverter}}", pill.Attribute("Visibility")?.Value);
            StringAssert.StartsWith(pill.Attribute("BorderBrush")!.Value, "{DynamicResource");
            Assert.AreEqual("{StaticResource StartScreen.StatusBadge}", pill.Attribute("Style")?.Value);
            StringAssert.StartsWith(SharedXamlReader.Setter("StartScreen.StatusBadge", "Background"), "{DynamicResource");
            Assert.AreEqual("1", SharedXamlReader.Setter("StartScreen.StatusBadge", "BorderThickness"));
            Assert.AreEqual("9", SharedXamlReader.Setter("StartScreen.StatusBadge", "CornerRadius"));
            Assert.AreEqual("Assigned to you", pill.Element(p + "TextBlock")?.Attribute("Text")?.Value);
            Assert.AreEqual("{StaticResource StartScreen.StatusText}", pill.Element(p + "TextBlock")?.Attribute("Style")?.Value);
            Assert.AreEqual("Bold", SharedXamlReader.Setter("StartScreen.StatusText", "FontWeight"));
            Assert.AreEqual("Horizontal", pill.Parent!.Attribute("Orientation")?.Value);
            Assert.IsFalse(pill.Parent.Elements(p + "TextBlock").Any(element => element.Attribute("Text")?.Value == "{Binding ActivityDateText}"));
            Assert.IsFalse(document.Descendants().Any(element => element.Attribute(x + "Name")?.Value == "AssignmentBanner"));
            var card = document.Descendants(p + "DataTemplate").Single(element => element.Attribute(x + "Key")?.Value == "WorkItemTemplate");
            var title = card.Descendants(p + "TextBlock").Single(element => element.Attribute("Text")?.Value == "{Binding DisplayTitle}");
            Assert.IsTrue(title.ElementsAfterSelf(p + "ContentControl").Any());
            var labels = document.Descendants(p + "ItemsControl").Single(element => element.Attribute("ItemsSource")?.Value == "{Binding Labels}");
            var labelPill = labels.Descendants(p + "Border").Single();
            Assert.AreEqual("{StaticResource StartScreen.StatusBadge}", labelPill.Element(p + "Border.Style")!.Element(p + "Style")!.Attribute("BasedOn")?.Value);
            Assert.AreEqual("9", SharedXamlReader.Setter("StartScreen.StatusBadge", "CornerRadius"));
            Assert.IsTrue(labelPill.Descendants(p + "Setter").Any(setter =>
                setter.Attribute("Property")?.Value == "Background"
                && setter.Attribute("Value")?.Value == "{Binding Color, Converter={StaticResource HexColorToBrushConverter}}"));
            Assert.HasCount(1, labels.Descendants(p + "TextBlock"));
            var metadata = document.Descendants(p + "DataTemplate").Single(element => element.Attribute(x + "Key")?.Value == "RepositoryItemMetadataTemplate");
            Assert.AreSame(metadata, pill.Ancestors(p + "DataTemplate").Single());
            Assert.AreEqual("{Binding StatusText, Converter={StaticResource NullToCollapsedConverter}}", metadata.Descendants(p + "Border")
                .Single(element => element.Element(p + "TextBlock")?.Attribute("Text")?.Value == "{Binding StatusText}").Attribute("Visibility")?.Value);
        }

        [STATestMethod]
        [DataRow(200d, "Short repository")]
        [DataRow(400d, "Short repository")]
        [DataRow(200d, "A very long repository or workspace name that must never push the date out of the card")]
        [DataRow(400d, "A very long repository or workspace name that must never push the date out of the card")]
        public void WorkCardDates_StayAtTheFarRightBelowTheTitle(double width, string detail)
        {
            using var stream = typeof(DevHubActivityPresentationTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            var document = XDocument.Load(stream!);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = document.Descendants(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == "WorkItemDetails");
            Assert.AreEqual("{Binding DisplayTitle}", definition.ElementsBeforeSelf(p + "TextBlock").Single().Attribute("Text")?.Value);
            Assert.IsFalse(definition.Descendants(p + "DataTrigger").Any(), "Repository/workspace details must be visible for every card kind.");
            var row = new XElement(definition);
            row.Elements().Single(element => element.Attribute(x + "Name")?.Value == "CopilotActivityIcon").Remove();
            row.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            var root = (Grid)XamlReader.Parse(row.ToString());
            root.DataContext = new { Detail = detail, DetailToolTip = detail, ActivityDateText = "today", ActivityDateToolTip = "Updated today" };
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
            root.UpdateLayout();
            var repository = (TextBlock)root.Children[0];
            var date = (TextBlock)root.Children[1];

            Assert.AreEqual(detail, repository.Text);
            Assert.AreEqual(TextTrimming.CharacterEllipsis, repository.TextTrimming);
            Assert.AreEqual("today", date.Text);
            Assert.AreEqual("Updated today", date.ToolTip);
            Assert.AreEqual(HorizontalAlignment.Right, date.HorizontalAlignment);
            Assert.AreEqual(width, date.TranslatePoint(new Point(date.ActualWidth, 0), root).X, 0.01);
            Assert.IsLessThanOrEqualTo(date.TranslatePoint(new Point(), root).X,
                repository.TranslatePoint(new Point(repository.ActualWidth, 0), root).X);
        }

        [TestMethod]
        [DataRow("Conflicts", "Error")]
        [DataRow("Checks failing", "Error")]
        [DataRow("Changes requested", "Error")]
        [DataRow("Blocked", "Error")]
        [DataRow("Build failure", "Error")]
        [DataRow("Build success", "Success")]
        [DataRow("Ready to merge", "Success")]
        [DataRow("Approved", "Success")]
        [DataRow("1 approval", "Success")]
        [DataRow("12 approvals", "Success")]
        [DataRow("Merged", "Success")]
        [DataRow("Review requested", "Warning")]
        [DataRow("Unresolved discussions", "Warning")]
        [DataRow("Checks pending", "Information")]
        [DataRow("Build running", "Information")]
        [DataRow("Waiting for approval", "Information")]
        [DataRow("Draft", "Muted")]
        [DataRow("Build cancelled", "Muted")]
        [DataRow(null, "Muted")]
        public void StatusCategories_ReflectOutcomeRatherThanAttention(string? status, string category)
        {
            Assert.AreEqual(category, DevHubPullRequest.GetStatusCategory(status));
        }

        [TestMethod]
        public void MergeReadyAction_RemainsSuccessfulRatherThanWarning()
        {
            var pr = OwnPullRequest();
            pr.CanMerge = true;
            Assert.IsTrue(pr.NeedsAttention);
            Assert.AreEqual("Success", pr.StatusCategory);
            Assert.AreEqual("Success", DevHubWorkItem.GetMyWork([pr], [], []).Single().StatusCategory);
        }

        [STATestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void AttentionItems_SelectPullRequestMetadataOnlyForPullRequests(bool isPullRequest)
        {
            var pr = OwnPullRequest();
            pr.CanMerge = true;
            var issue = new DevHubIssue
            {
                Title = "Assigned issue", State = "open", IsAssignedToCurrentUser = true,
                WebUrl = "https://example.com/issues/5",
                UpdatedAt = DateTime.UtcNow,
            };
            var items = DevHubWorkItem.GetMyWork([pr], [issue], []);
            var attention = DevHubWorkItem.Filter(items, DevHubWorkItemKind.All, true);
            var item = attention.Single(candidate => candidate.IsPullRequest == isPullRequest);
            Assert.AreSame(items.Single(candidate => candidate.IsPullRequest == isPullRequest), item);

            using var stream = typeof(DevHubActivityPresentationTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = new XElement(document.Descendants(p + "ContentControl").Single(element =>
                element.Descendants(p + "Setter").Any(setter =>
                    setter.Attribute("Value")?.Value == "{StaticResource PullRequestMetadataTemplate}")));
            definition.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            definition.Attribute("Visibility")!.Remove();
            definition.AddFirst(new XElement(p + "ContentControl.Resources",
                new XElement(p + "DataTemplate", new XAttribute(x + "Key", "PullRequestMetadataTemplate")),
                new XElement(p + "DataTemplate", new XAttribute(x + "Key", "RepositoryItemMetadataTemplate"))));
            var metadata = (ContentControl)XamlReader.Parse(definition.ToString());
            metadata.DataContext = item;
            metadata.Measure(new Size(400, double.PositiveInfinity));
            metadata.Arrange(new Rect(0, 0, 400, metadata.DesiredSize.Height));
            metadata.UpdateLayout();

            Assert.AreSame(metadata.Resources[isPullRequest ? "PullRequestMetadataTemplate" : "RepositoryItemMetadataTemplate"],
                metadata.ContentTemplate);
        }

        [TestMethod]
        [DataRow("open", false, null)]
        [DataRow("Open", false, null)]
        [DataRow("In progress", false, "In progress")]
        [DataRow("open", true, "Blocked")]
        [DataRow("closed", false, "closed")]
        public void IssuePills_OmitOpenButKeepWorkflowAndCompletedStates(string state, bool blocked, string? expected)
        {
            var issue = new DevHubIssue
            {
                State = state, IsBlocked = blocked, IsAssignedToCurrentUser = true, WebUrl = "https://example.com/issues/1",
                IsClosed = state == "closed", UpdatedAt = DateTime.UtcNow,
            };
            var item = DevHubWorkItem.GetMyWork([], [issue], [], includeCompleted: true).Single();
            Assert.AreEqual(expected, item.StatusText);
            StringAssert.Contains(item.StatusDetails, state);
        }

        [STATestMethod]
        [DataRow(220d, false)]
        [DataRow(500d, false)]
        [DataRow(220d, true)]
        [DataRow(500d, true)]
        public void IssueLabels_UseSolidProviderColorsAndWrapWithoutClipping(double width, bool highContrast)
        {
            using var stream = typeof(DevHubActivityPresentationTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var definition = new XElement(document.Descendants(p + "ItemsControl").Single(element => element.Attribute("ItemsSource")?.Value == "{Binding Labels}"));
            definition.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            XNamespace controls = "clr-namespace:StartScreen.ToolWindows.Controls;assembly=StartScreen";
            definition.SetAttributeValue(XNamespace.Xmlns + "controls", controls.NamespaceName);
            definition.AddFirst(new XElement(p + "ItemsControl.Resources",
                new XElement(controls + "HexColorToBrushConverter", new XAttribute(x + "Key", "HexColorToBrushConverter")),
                new XElement(controls + "HexColorToForegroundConverter", new XAttribute(x + "Key", "HexColorToForegroundConverter"))));
            var labels = (ItemsControl)XamlReader.Parse(definition.ToString());
            labels.Resources[SystemParameters.HighContrastKey] = highContrast;
            var issue = new DevHubIssue
            {
                WebUrl = "https://example.com/issues/1", IsAssignedToCurrentUser = true, State = "open",
                Priority = "Priority:3", Labels =
                [
                    new DevHubLabel { Name = "Priority:3", Color = "ededed" },
                    new DevHubLabel { Name = "Type:Feature", Color = "0052cc" },
                    new DevHubLabel { Name = "Type:CustomerSupport", Color = "1d76db" },
                ],
            };
            labels.DataContext = DevHubWorkItem.GetMyWork([], [issue], []).Single();
            labels.Measure(new Size(width, double.PositiveInfinity));
            labels.Arrange(new Rect(0, 0, width, labels.DesiredSize.Height));
            labels.UpdateLayout();
            Assert.HasCount(3, labels.Items);
            var first = (FrameworkElement)labels.ItemContainerGenerator.ContainerFromIndex(0);
            for (var index = 0; index < labels.Items.Count; index++)
            {
                var item = (FrameworkElement)labels.ItemContainerGenerator.ContainerFromIndex(index);
                var position = item.TranslatePoint(new Point(), labels);
                Assert.IsLessThanOrEqualTo(width + 0.1, position.X + item.ActualWidth);
                if (width == 500)
                    Assert.AreEqual(first.TranslatePoint(new Point(), labels).Y, position.Y);
                var presenter = (ContentPresenter)item;
                var pill = (Border)presenter.ContentTemplate.FindName("LabelPill", presenter);
                var text = (TextBlock)pill.Child;
                Assert.AreEqual(issue.Labels[index].Name, text.Text);
                Assert.AreEqual(new CornerRadius(9), pill.CornerRadius);
                Assert.AreEqual(1d, text.Opacity);
                if (highContrast)
                {
                    Assert.AreSame(SystemColors.WindowBrush, pill.Background);
                    Assert.AreSame(SystemColors.WindowTextBrush, text.Foreground);
                    Assert.AreSame(SystemColors.WindowTextBrush, pill.BorderBrush);
                }
                else
                {
                    Assert.AreEqual("#FF" + issue.Labels[index].Color.ToUpperInvariant(),
                        ((System.Windows.Media.SolidColorBrush)pill.Background).Color.ToString());
                    Assert.AreEqual(index == 0 ? "#FF1E1E1E" : "#FFFFFFFF",
                        ((System.Windows.Media.SolidColorBrush)text.Foreground).Color.ToString());
                    Assert.AreEqual(new Thickness(0), pill.BorderThickness);
                }
            }
        }

        [TestMethod]
        public void LoadMore_HasAnUpdatableLoadingLabelAndWrappingOutcome()
        {
            using var stream = typeof(DevHubActivityPresentationTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var link = document.Descendants(p + "Hyperlink").Single(element => element.Attribute(x + "Name")?.Value == "LoadMoreLink");
            Assert.AreEqual("LoadMoreLabel", link.Element(p + "Run")?.Attribute(x + "Name")?.Value);
            Assert.AreEqual("Load more", link.Element(p + "Run")?.Attribute("Text")?.Value);
            var status = document.Descendants(p + "TextBlock").Single(element => element.Attribute(x + "Name")?.Value == "ActivityLoadStatus");
            Assert.AreEqual("Collapsed", status.Attribute("Visibility")?.Value);
            Assert.AreEqual("Wrap", status.Attribute("TextWrapping")?.Value);
            using var startStream = typeof(DevHubActivityPresentationTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            Assert.IsNotNull(startStream);
            var host = XDocument.Load(startStream).Descendants().Single(element => element.Name.LocalName == "DevHubPanel");
            Assert.AreEqual("DevHubPanel_ActivityOptionsChanged", host.Attribute("ActivityOptionsChanged")?.Value);
        }

        [TestMethod]
        [DataRow("StartScreenControl.xaml", "NewsTabItemStyle")]
        public void TabHeaderHover_DoesNotTrackFiltersInsideTheTabContent(string resource, string styleKey)
        {
            using var stream = typeof(DevHubActivityPresentationTests).Assembly.GetManifestResourceStream(resource);
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var style = document.Descendants(p + "Style").Single(element => element.Attribute(x + "Key")?.Value == styleKey);
            var hover = style.Descendants(p + "Trigger").Single(element => element.Attribute("Property")?.Value == "IsMouseOver");
            Assert.AreEqual("TabRoot", hover.Attribute("SourceName")?.Value);
            Assert.AreEqual("TextElement.Foreground", hover.Element(p + "Setter")?.Attribute("Property")?.Value);
            Assert.AreEqual("TabContent", hover.Element(p + "Setter")?.Attribute("TargetName")?.Value);
            var header = style.Descendants(p + "Grid").Single(element => element.Attribute(x + "Name")?.Value == "TabRoot");
            Assert.AreEqual("Header", header.Element(p + "ContentPresenter")?.Attribute("ContentSource")?.Value);
            Assert.HasCount(1, style.Descendants(p + "Trigger").Where(element => element.Attribute("Property")?.Value == "IsKeyboardFocused"));
        }

        private static DevHubPullRequest OwnPullRequest() => new DevHubPullRequest
        {
            Title = "My PR", Status = "open", IsAuthoredByCurrentUser = true, WebUrl = "https://example.com/pull/1",
            UpdatedAt = DateTime.UtcNow,
        };

        private sealed class ActivityProvider : IDevHubProvider, IDevHubActivityProvider
        {
            public RemoteRepoIdentifier Repo { get; } = new RemoteRepoIdentifier("github.com", "owner", "repo");
            public List<DevHubActivityOptions> Requests { get; } = [];
            public string DisplayName => "Activity tests";
            public bool CanHandle(string remoteUrl) => true;
            public RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl) => Repo;
            public Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken) => Task.FromResult(new DevHubUser { Username = "me" });
            public Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DevHubUser>>([new DevHubUser { Username = "me" }]);
            public Task<IReadOnlyList<RemoteRepoIdentifier>> GetActivityReposAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RemoteRepoIdentifier>>([Repo]);
            public Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(DevHubActivityOptions options, CancellationToken cancellationToken) => RecordAsync<DevHubPullRequest>(options);
            public Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(DevHubActivityOptions options, CancellationToken cancellationToken) => RecordAsync<DevHubIssue>(options);
            public Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(DevHubActivityOptions options, CancellationToken cancellationToken) => RecordAsync<DevHubCiRun>(options);
            public Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, DevHubActivityOptions options, CancellationToken cancellationToken)
            {
                Requests.Add(options);
                return Task.FromResult(new DevHubRepoDetail { RepoIdentifier = repo });
            }
            private Task<IReadOnlyList<T>> RecordAsync<T>(DevHubActivityOptions options)
            {
                Requests.Add(options);
                return Task.FromResult<IReadOnlyList<T>>([]);
            }
            public Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken) => throw new NotSupportedException();
            public string GetRepoWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetIssuesWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
        }
    }
}