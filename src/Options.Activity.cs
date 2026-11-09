using System.ComponentModel;

namespace StartScreen
{
    internal partial class Options
    {
        [Browsable(false)]
        [DefaultValue("")]
        public string DevHubItemPreferences { get; set; } = "";
    }
}
