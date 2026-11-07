using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace StartScreen.Models
{
    public sealed class WhatsNewItem
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public BitmapSource Thumbnail { get; set; }
    }

    public sealed class WhatsNewArea
    {
        public string Area { get; set; }
        public IReadOnlyList<WhatsNewItem> Items { get; set; }
    }

    internal sealed class WhatsNewContent
    {
        internal IReadOnlyList<WhatsNewArea> Areas { get; set; }
        internal string Status { get; set; }
    }
}
