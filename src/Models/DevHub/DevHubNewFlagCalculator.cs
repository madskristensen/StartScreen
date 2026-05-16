using System;
using System.Collections.Generic;

namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// Computes the runtime IsNew flag for DevHub items (issues, pull requests, CI runs).
    /// </summary>
    /// <remarks>
    /// The flag is intentionally driven by two distinct strategies:
    /// <list type="bullet">
    /// <item><description>
    /// First-ever load (no previously bound list) - falls back to comparing each item's
    /// timestamp against the persisted "last seen" value. Skipped entirely when the user
    /// has never viewed the tab (lastSeen == MinValue) so the user is not greeted with
    /// every existing item flagged as new.
    /// </description></item>
    /// <item><description>
    /// Subsequent loads (a previous list exists) - identity based. Items whose key is not
    /// present in the previous list are flagged as new. Items still present keep whatever
    /// IsNew state they carried before, which the tab-view handler resets to false when
    /// the user actually looks at the tab.
    /// </description></item>
    /// </list>
    /// The identity-based path is what makes items that arrive during the current session
    /// (after the user has already viewed the tab once) show up as NEW - the persisted
    /// timestamp alone cannot tell those apart from items the user already saw.
    /// </remarks>
    internal static class DevHubNewFlagCalculator
    {
        public static void Apply<T>(
            IReadOnlyList<T> newItems,
            IReadOnlyList<T> previousItems,
            DateTime lastSeen,
            Func<T, DateTime> getTimestamp,
            Func<T, string> getKey,
            Func<T, bool> getIsNew,
            Action<T, bool> setIsNew)
        {
            if (newItems == null || newItems.Count == 0)
            {
                return;
            }

            if (previousItems == null)
            {
                bool firstLoad = lastSeen == DateTime.MinValue;
                foreach (T item in newItems)
                {
                    setIsNew(item, !firstLoad && getTimestamp(item) > lastSeen);
                }
                return;
            }

            var previousByKey = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (T prev in previousItems)
            {
                string key = getKey(prev);
                if (!string.IsNullOrEmpty(key))
                {
                    previousByKey[key] = prev;
                }
            }

            foreach (T item in newItems)
            {
                string key = getKey(item);
                if (!string.IsNullOrEmpty(key) && previousByKey.TryGetValue(key, out T prev))
                {
                    setIsNew(item, getIsNew(prev));
                }
                else
                {
                    setIsNew(item, true);
                }
            }
        }
    }
}
