using System.ComponentModel.Design;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.Shell;
using StartScreen.Commands;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubItemPreferencesTests
    {
        private static readonly DateTime _now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void PinsAndSnoozes_RoundTripAndRetainSeparateProviderIdentities()
        {
            var github = Item("https://github.com/o/r/issues/1");
            var ado = Item("https://dev.azure.com/o/p/_workitems/edit/1");
            var preferences = new DevHubItemPreferences();
            preferences.SetPinned(github, true, _now);
            preferences.Snooze(ado, _now.AddHours(1), _now);

            var restored = DevHubItemPreferences.FromJson(preferences.ToJson(_now));

            Assert.IsTrue(restored.IsPinned(github));
            Assert.IsFalse(restored.IsPinned(ado));
            Assert.IsNull(restored.GetSnoozedUntil(github));
            Assert.AreEqual(_now.AddHours(1), restored.GetSnoozedUntil(ado));
            Assert.AreEqual(_now.AddHours(1), restored.NextExpiry(_now));
        }

        [TestMethod]
        public void PinAndSnooze_AreIndependentAndCanBothBeRemoved()
        {
            var item = Item("https://github.com/o/r/issues/1");
            var preferences = new DevHubItemPreferences();
            preferences.SetPinned(item, true, _now);
            preferences.Snooze(item, _now.AddDays(1), _now);
            preferences.Snooze(item, null, _now);
            Assert.IsTrue(preferences.IsPinned(item));
            Assert.IsNull(preferences.GetSnoozedUntil(item));
            preferences.SetPinned(item, false, _now);
            Assert.AreEqual("{}", preferences.ToJson(_now));
        }

        [TestMethod]
        public void ExpiredSnoozes_ArePrunedButPinsArePreserved()
        {
            var pinned = Item("https://github.com/o/r/issues/1");
            var snoozed = Item("https://github.com/o/r/issues/2");
            var preferences = new DevHubItemPreferences();
            preferences.SetPinned(pinned, true, _now);
            preferences.Snooze(pinned, _now.AddHours(1), _now);
            preferences.Snooze(snoozed, _now.AddHours(1), _now);

            var restored = DevHubItemPreferences.FromJson(preferences.ToJson(_now.AddHours(1)));

            Assert.IsTrue(restored.IsPinned(pinned));
            Assert.IsFalse(restored.IsPinned(snoozed));
            Assert.IsNull(restored.GetSnoozedUntil(snoozed));
            Assert.IsNull(restored.NextExpiry(_now.AddHours(1)));
        }

        [TestMethod]
        public void InvalidSettings_AreRejectedRatherThanOverwritingSavedPreferences()
        {
            Assert.ThrowsExactly<JsonException>(() => DevHubItemPreferences.FromJson("not json"));
            Assert.ThrowsExactly<InvalidDataException>(() => DevHubItemPreferences.FromJson("null"));
            Assert.ThrowsExactly<InvalidDataException>(() => DevHubItemPreferences.FromJson("{\"key\":null}"));
            Assert.ThrowsExactly<InvalidDataException>(() => DevHubItemPreferences.FromJson("{\"key\":{\"SnoozedUntilUtc\":\"2026-11-09T12:00:00\"}}"));
            var preferences = new DevHubItemPreferences();
            var item = Item("https://github.com/o/r/issues/1");
            Assert.ThrowsExactly<ArgumentException>(() => preferences.Snooze(item, _now, _now));
            Assert.ThrowsExactly<ArgumentException>(() => preferences.Snooze(item, DateTime.SpecifyKind(_now.AddHours(1), DateTimeKind.Unspecified), _now));
            var session = DevHubWorkItem.GetCopilotItems([new CopilotActivitySession { Id = "session" }], false).Single();
            Assert.ThrowsExactly<ArgumentException>(() => preferences.SetPinned(session, true, _now));
        }

        [TestMethod]
        public void Pins_MoveToTopInBothFeedsAndAddToAttentionWithoutChangingOwnership()
        {
            var preferences = new DevHubItemPreferences();
            var own = Pr("own", true, _now);
            var pinned = Pr("pinned", true, _now.AddDays(-1));
            var team = Pr("team", false, _now);
            var pinnedTeam = Pr("pinned-team", false, _now.AddDays(-1));
            preferences.SetPinned(DevHubWorkItem.GetMyWork([pinned], [], []).Single(), true, _now);
            preferences.SetPinned(DevHubWorkItem.GetIncoming([pinnedTeam], [], []).Single(), true, _now);
            var activity = new DevHubActivityViewModel(() => _now);
            activity.SetPreferences(preferences, false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            activity.UpdateRepositoryActivity([own, pinned, team, pinnedTeam], [], [], [], DevHubSortOrder.MostRecent, false);

            CollectionAssert.AreEqual(new[] { "pinned", "own" }, activity.MyWorkItems.Select(item => item.Title).ToArray());
            CollectionAssert.AreEqual(new[] { "pinned-team", "team" }, activity.IncomingItems.Select(item => item.Title).ToArray());
            Assert.AreEqual(2, activity.MyWorkCount);
            Assert.AreEqual(2, activity.IncomingCount);
            Assert.AreEqual(2, activity.AttentionCount);
            Assert.IsTrue(activity.MyWorkItems[0].IsPinned);
            Assert.AreEqual("Pinned to Attention", activity.MyWorkItems[0].PreferenceText);
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, true);
            CollectionAssert.AreEquivalent(new[] { "pinned", "pinned-team" }, activity.MyWorkItems.Select(item => item.Title).ToArray());
        }

        [TestMethod]
        public void Snooze_HidesAttentionAndTeamActivityUntilExpiryOrExplicitRecovery()
        {
            var now = _now;
            var own = Pr("own", true, now);
            own.CiStatus = "failure";
            var team = Pr("team", false, now);
            var preferences = new DevHubItemPreferences();
            preferences.Snooze(DevHubWorkItem.GetMyWork([own], [], []).Single(), now.AddHours(1), now);
            preferences.Snooze(DevHubWorkItem.GetIncoming([team], [], []).Single(), now.AddHours(1), now);
            var activity = new DevHubActivityViewModel(() => now);
            activity.SetPreferences(preferences, false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, true);
            activity.UpdateRepositoryActivity([own, team], [], [], [], DevHubSortOrder.MostRecent, false);
            Assert.IsEmpty(activity.MyWorkItems);
            Assert.IsEmpty(activity.IncomingItems);
            Assert.AreEqual(0, activity.MyWorkCount);
            Assert.AreEqual(0, activity.AttentionCount);
            Assert.AreEqual(2, activity.LoadedActivityCount);
            Assert.IsFalse(activity.TrySelectInitialFilter());

            activity.SetPreferences(preferences, true);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            Assert.IsTrue(activity.MyWorkItems.Single().IsSnoozed);
            Assert.IsTrue(activity.IncomingItems.Single().IsSnoozed);
            Assert.AreEqual(1, activity.AttentionCount);
            StringAssert.Contains(activity.MyWorkItems.Single().PreferenceText, "Snoozed until");

            activity.SetPreferences(preferences, false);
            now = now.AddHours(1);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            Assert.IsFalse(activity.MyWorkItems.Single().IsSnoozed);
            Assert.IsFalse(activity.IncomingItems.Single().IsSnoozed);
            Assert.AreEqual(1, activity.AttentionCount);
        }

        [TestMethod]
        public void PreferenceChanges_RefreshCardsAndSurviveWorkspaceReplacement()
        {
            var preferences = new DevHubItemPreferences();
            var own = Pr("own", true, _now);
            var activity = new DevHubActivityViewModel(() => _now);
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            activity.UpdateRepositoryActivity([own], [], [], [], DevHubSortOrder.MostRecent, false);
            var original = activity.MyWorkItems.Single();
            preferences.SetPinned(original, true, _now);
            activity.SetPreferences(preferences, false);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            Assert.AreNotSame(original, activity.MyWorkItems.Single());
            Assert.IsTrue(activity.MyWorkItems.Single().IsPinned);
            activity.UpdateRepositoryActivity([], [], [], [], DevHubSortOrder.MostRecent, false);
            Assert.IsEmpty(activity.MyWorkItems);
            activity.UpdateRepositoryActivity([own], [], [], [], DevHubSortOrder.MostRecent, false);
            Assert.IsTrue(activity.MyWorkItems.Single().IsPinned);
            preferences.SetPinned(activity.MyWorkItems.Single(), false, _now);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            Assert.IsFalse(activity.MyWorkItems.Single().IsPinned);
        }

        [TestMethod]
        [DoNotParallelize]
        public void NativeMenu_TracksPinAndSnoozeStateAndClearsAfterClosing()
        {
            var item = Item("https://github.com/o/r/issues/1");
            var preferences = new DevHubItemPreferences();
            preferences.SetPinned(item, true, _now);
            preferences.Snooze(item, _now.AddDays(1), _now);
            item.ApplyPreferences(preferences, _now);
            DevHubWorkItemMenu.Current = new DevHubWorkItemMenu { Item = item };
            try
            {
                var pin = Command(PackageIds.DevHubPinItem);
                var snooze = Command(PackageIds.DevHubSnoozeItem);
                Assert.IsTrue(pin.Enabled);
                Assert.AreEqual("Unpin from Attention", pin.Text);
                Assert.AreEqual("Unsnooze item", snooze.Text);
                Assert.IsFalse(Command(PackageIds.DevHubSnoozeItemDay).Visible);
                DevHubWorkItemMenu.Current = null;
                DevHubWorkItemMenu.Update(pin, PackageIds.DevHubPinItem);
                Assert.IsFalse(pin.Visible);
                Assert.IsFalse(pin.Enabled);
            }
            finally
            {
                DevHubWorkItemMenu.Current = null;
            }
        }

        [TestMethod]
        public void NativeCommandsAndRecoveryControl_AreWired()
        {
            using var stream = typeof(DevHubItemPreferencesTests).Assembly.GetManifestResourceStream("VSCommandTable.vsct");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace vsct = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
            foreach (var name in new[] { "DevHubPinItem", "DevHubSnoozeItem", "DevHubSnoozeItemDay" })
            {
                Assert.IsTrue(document.Descendants(vsct + "Button").Any(button => button.Attribute("id")?.Value == name));
                Assert.IsTrue(document.Descendants(vsct + "IDSymbol").Any(symbol => symbol.Attribute("name")?.Value == name));
            }
            using var panelStream = typeof(DevHubItemPreferencesTests).Assembly.GetManifestResourceStream("DevHubPanel.xaml");
            Assert.IsNotNull(panelStream);
            var panel = XDocument.Load(panelStream);
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            Assert.IsFalse(panel.Descendants().Any(element => element.Attribute(xaml + "Name")?.Value == "ShowSnoozedItems"));
            using var screenStream = typeof(DevHubItemPreferencesTests).Assembly.GetManifestResourceStream("StartScreenControl.xaml");
            var screen = XDocument.Load(screenStream!);
            var recovery = screen.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == "ShowSnoozedItems");
            Assert.AreEqual("{Binding ShowSnoozedItems, ElementName=DevHubPanelControl, Mode=TwoWay}", recovery.Attribute("IsChecked")?.Value);
            Assert.AreEqual("ViewFilters", recovery.Parent!.Attribute(xaml + "Name")?.Value);
            Assert.IsTrue(recovery.ElementsBeforeSelf().Any(element => element.Attribute("Content")?.Value == "Solutions only"));
            Assert.AreEqual("Bottom", recovery.Parent.Attribute("DockPanel.Dock")?.Value);
        }

        private static DevHubWorkItem Item(string url) => DevHubWorkItem.GetMyWork([], [new DevHubIssue
        {
            WebUrl = url, Title = "Task", IsAssignedToCurrentUser = true,
        }], []).Single();

        private static DevHubPullRequest Pr(string name, bool own, DateTime updated) => new()
        {
            Title = name, WebUrl = "https://github.com/o/r/pull/" + name, Status = "open",
            IsAuthoredByCurrentUser = own, UpdatedAt = updated,
        };

        private static OleMenuCommand Command(int id)
        {
            var command = new OleMenuCommand(null, new CommandID(PackageGuids.StartScreen, id));
            DevHubWorkItemMenu.Update(command, id);
            return command;
        }
    }
}
