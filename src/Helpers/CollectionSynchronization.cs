using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace StartScreen.Helpers
{
    internal static class CollectionSynchronization
    {
        internal static void Synchronize<T>(ObservableCollection<T> collection, IReadOnlyList<T> items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (i < collection.Count && ReferenceEquals(collection[i], items[i]))
                    continue;
                var existingIndex = collection.IndexOf(items[i]);
                if (existingIndex >= 0)
                    collection.Move(existingIndex, i);
                else
                    collection.Insert(i, items[i]);
            }
            for (var i = collection.Count - 1; i >= items.Count; i--)
                collection.RemoveAt(i);
        }
    }
}