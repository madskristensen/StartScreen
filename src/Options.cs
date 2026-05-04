using System.ComponentModel;

namespace StartScreen
{
    /// <summary>
    /// Extension options stored in user settings.
    /// All settings are managed inline on the Start Screen UI, not via Tools > Options.
    /// </summary>
    internal class Options : BaseOptionModel<Options>
    {
        /// <summary>
        /// Semicolon-delimited list of pinned MRU item paths.
        /// </summary>
        [Browsable(false)]
        [DefaultValue("")]
        public string PinnedItems { get; set; } = "";

        /// <summary>
        /// Semicolon-delimited list of pinned news article URLs.
        /// </summary>
        [Browsable(false)]
        [DefaultValue("")]
        public string PinnedArticles { get; set; } = "";

        /// <summary>
        /// JSON string containing user-added custom feeds.
        /// </summary>
        [Browsable(false)]
        [DefaultValue("")]
        public string CustomFeeds { get; set; } = "";

        /// <summary>
        /// Serialized feed selection state (which feeds are enabled).
        /// </summary>
        [Browsable(false)]
        [DefaultValue("")]
        public string FeedSelection { get; set; } = "";

        /// <summary>
        /// Timestamp of the last time the user read the news feed.
        /// </summary>
        [Browsable(false)]
        public DateTime LastFeedRead { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Width of the left (MRU) pane, persisted when the user drags the splitter.
        /// </summary>
        [Browsable(false)]
        [DefaultValue(500d)]
        public double SplitterPosition { get; set; } = 500d;

        /// <summary>
        /// Whether the Dev Hub feature is enabled.
        /// </summary>
        [Browsable(false)]
        [DefaultValue(true)]
        public bool DevHubEnabled { get; set; } = true;

        /// <summary>
        /// Custom GitHub search query for the Dev Hub.
        /// When set, this replaces the default "involves:{login}" query.
        /// The extension prepends "is:issue" or "is:pr" automatically.
        /// Use {login} as a placeholder for the authenticated username.
        /// </summary>
        [Browsable(false)]
        [DefaultValue("")]
        public string DevHubSearchQuery { get; set; } = "";

        /// <summary>
        /// Semicolon-delimited list of Azure DevOps Server (on-premises) hosts that the
        /// user has manually added through the Dev Hub settings panel. Each entry is the
        /// host portion only (e.g., "tfs.contoso.com"); credentials are stored separately
        /// in Windows Credential Manager keyed by host.
        /// </summary>
        [Browsable(false)]
        [DefaultValue("")]
        public string DevHubAdoServers { get; set; } = "";

        /// <summary>
        /// Whether the extension has already disabled the built-in VS Start Window on first run.
        /// </summary>
        [Browsable(false)]
        [DefaultValue(false)]
        public bool HasDisabledBuiltInStartWindow { get; set; }
    }
}
