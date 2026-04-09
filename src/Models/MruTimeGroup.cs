using System.Collections.ObjectModel;

namespace StartScreen.Models
{
    /// <summary>
    /// Represents a time-based group of MRU items (e.g. "Today", "Yesterday", "This week").
    /// </summary>
    public class MruTimeGroup
    {
        public string GroupName { get; set; }

        public ObservableCollection<MruItem> Items { get; set; } = new ObservableCollection<MruItem>();
    }
}
