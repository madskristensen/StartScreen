using System;
using System.Collections.Generic;
using StartScreen.Models.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubNewFlagCalculatorTests
    {
        [TestMethod]
        public void Apply_FirstLoadWithMinValueLastSeen_FlagsNothing()
        {
            List<Item> items = new()
            {
                new Item("a", new DateTime(2024, 1, 1)),
                new Item("b", new DateTime(2024, 1, 2)),
            };

            ApplyToItems(items, previous: null, lastSeen: DateTime.MinValue);

            Assert.IsFalse(items[0].IsNew);
            Assert.IsFalse(items[1].IsNew);
        }

        [TestMethod]
        public void Apply_FirstLoadWithLastSeen_FlagsOnlyItemsNewerThanLastSeen()
        {
            DateTime lastSeen = new(2024, 6, 1);
            List<Item> items = new()
            {
                new Item("old", new DateTime(2024, 5, 1)),
                new Item("new", new DateTime(2024, 7, 1)),
            };

            ApplyToItems(items, previous: null, lastSeen: lastSeen);

            Assert.IsFalse(items[0].IsNew, "Items older than lastSeen should not be flagged.");
            Assert.IsTrue(items[1].IsNew, "Items newer than lastSeen should be flagged.");
        }

        [TestMethod]
        public void Apply_WithPreviousList_FlagsOnlyItemsNotInPreviousList()
        {
            List<Item> previous = new()
            {
                new Item("a", new DateTime(2024, 1, 1)) { IsNew = false },
                new Item("b", new DateTime(2024, 1, 2)) { IsNew = false },
            };
            List<Item> current = new()
            {
                new Item("a", new DateTime(2024, 1, 1)),
                new Item("b", new DateTime(2024, 1, 2)),
                new Item("c", new DateTime(2024, 1, 3)),
            };

            ApplyToItems(current, previous, lastSeen: DateTime.UtcNow);

            Assert.IsFalse(current[0].IsNew);
            Assert.IsFalse(current[1].IsNew);
            Assert.IsTrue(current[2].IsNew, "Item not present in previous list should be flagged as new.");
        }

        [TestMethod]
        public void Apply_WithPreviousList_PreservesPriorIsNewState()
        {
            List<Item> previous = new()
            {
                new Item("a", new DateTime(2024, 1, 1)) { IsNew = true },
                new Item("b", new DateTime(2024, 1, 2)) { IsNew = false },
            };
            List<Item> current = new()
            {
                new Item("a", new DateTime(2024, 1, 1)),
                new Item("b", new DateTime(2024, 1, 2)),
            };

            ApplyToItems(current, previous, lastSeen: DateTime.UtcNow);

            Assert.IsTrue(current[0].IsNew, "Items still in previous list should keep their prior IsNew=true.");
            Assert.IsFalse(current[1].IsNew, "Items still in previous list should keep their prior IsNew=false.");
        }

        [TestMethod]
        public void Apply_NewItemArrivingAfterTabViewed_StillFlaggedAsNew()
        {
            // Regression test for the case where the user is currently viewing the tab
            // (LastDevHubIssuesSeen was just set to UtcNow) and a refresh brings new items
            // with an UpdatedAt timestamp older than UtcNow. The old timestamp-only path
            // would silently drop the NEW flag for those items.
            DateTime tabViewedAt = DateTime.UtcNow;
            List<Item> previous = new()
            {
                new Item("existing", tabViewedAt.AddHours(-12)) { IsNew = false },
            };
            List<Item> current = new()
            {
                new Item("existing", tabViewedAt.AddHours(-12)),
                new Item("arrived-during-refresh", tabViewedAt.AddHours(-8)),
            };

            ApplyToItems(current, previous, lastSeen: tabViewedAt);

            Assert.IsFalse(current[0].IsNew);
            Assert.IsTrue(current[1].IsNew, "Newly arrived item should be flagged even when its timestamp is older than lastSeen.");
        }

        [TestMethod]
        public void Apply_WithNullOrEmptyItems_DoesNotThrow()
        {
            ApplyToItems(items: null, previous: null, lastSeen: DateTime.UtcNow);
            ApplyToItems(items: new List<Item>(), previous: null, lastSeen: DateTime.UtcNow);
        }

        [TestMethod]
        public void Apply_WithMissingKey_TreatsItemAsNew()
        {
            List<Item> previous = new()
            {
                new Item("a", new DateTime(2024, 1, 1)) { IsNew = false },
            };
            List<Item> current = new()
            {
                new Item(key: null, new DateTime(2024, 1, 2)),
            };

            ApplyToItems(current, previous, lastSeen: DateTime.UtcNow);

            Assert.IsTrue(current[0].IsNew);
        }

        private static void ApplyToItems(IReadOnlyList<Item> items, IReadOnlyList<Item> previous, DateTime lastSeen)
        {
            DevHubNewFlagCalculator.Apply(
                items,
                previous,
                lastSeen,
                i => i.Timestamp,
                i => i.Key,
                i => i.IsNew,
                (i, value) => i.IsNew = value);
        }

        private sealed class Item
        {
            public Item(string key, DateTime timestamp)
            {
                Key = key;
                Timestamp = timestamp;
            }

            public string Key { get; }
            public DateTime Timestamp { get; }
            public bool IsNew { get; set; }
        }
    }
}
