using System.ComponentModel;
using StartScreen.Models.DevHub;

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

        [Browsable(false)]
        [DefaultValue(false)]
        public bool ShowSolutionsOnly { get; set; }

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
        /// Timestamp of the last time the user viewed the Dev Hub Issues tab.
        /// Items with UpdatedAt newer than this are flagged as "new".
        /// </summary>
        [Browsable(false)]
        public DateTime LastDevHubIssuesSeen { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Timestamp of the last time the user viewed the Dev Hub Pull Requests tab.
        /// </summary>
        [Browsable(false)]
        public DateTime LastDevHubPrsSeen { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Timestamp of the last time the user viewed the Dev Hub CI tab.
        /// </summary>
        [Browsable(false)]
        public DateTime LastDevHubCiSeen { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Timestamp of the last time the user viewed the News Blogs tab.
        /// Used to decide whether to show the "new content" indicator dot on the tab.
        /// </summary>
        [Browsable(false)]
        public DateTime LastNewsBlogsSeen { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Timestamp of the last time the user viewed the News Videos tab.
        /// Used to decide whether to show the "new content" indicator dot on the tab.
        /// </summary>
        [Browsable(false)]
        public DateTime LastNewsVideosSeen { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Width of the left (MRU) pane, persisted when the user drags the splitter.
        /// </summary>
        [Browsable(false)]
        [DefaultValue(360d)]
        public double SplitterPosition { get; set; } = 360d;

        /// <summary>
        /// Whether the Dev Hub feature is enabled.
        /// </summary>
        [Browsable(false)]
        [DefaultValue(true)]
        public bool DevHubEnabled { get; set; } = true;

        /// <summary>
        /// Custom GitHub search query for incoming Dev Hub activity.
        /// When set, this replaces the default "involves:{login}" query.
        /// The extension prepends "is:issue" or "is:pr" automatically.
        /// Use {login} as a placeholder for the authenticated username.
        /// Personal authored/assigned activity is fetched independently.
        /// </summary>
        [Browsable(false)]
        [DefaultValue("")]
        public string DevHubSearchQuery { get; set; } = "";

        /// <summary>
        /// The Dev Hub sub-tab selected by default when the Start Screen opens.
        /// </summary>
        [Browsable(false)]
        [DefaultValue(DevHubDefaultTab.MyWork)]
        public DevHubDefaultTab DevHubDefaultTab { get; set; } = DevHubDefaultTab.MyWork;

        [Browsable(false)]
        [DefaultValue(false)]
        public bool HasAppliedOverviewDefault { get; set; }

        internal bool ApplyWorkViewsDefault()
        {
            if (DevHubDefaultTab == DevHubDefaultTab.MyWork || DevHubDefaultTab == DevHubDefaultTab.Incoming)
                return false;
            DevHubDefaultTab = DevHubDefaultTab == DevHubDefaultTab.Issues
                || DevHubDefaultTab == DevHubDefaultTab.PullRequests || DevHubDefaultTab == DevHubDefaultTab.Builds
                ? DevHubDefaultTab.Incoming : DevHubDefaultTab.MyWork;
            return true;
        }

        [Browsable(false)]
        public DateTime LastOverviewSeen { get; set; } = DateTime.MinValue;

        /// <summary>
        /// How issues and pull requests from multiple providers are ordered in the
        /// Dev Hub lists.
        /// </summary>
        [Browsable(false)]
        [DefaultValue(DevHubSortOrder.MostRecent)]
        public DevHubSortOrder DevHubSortOrder { get; set; } = DevHubSortOrder.MostRecent;

        /// <summary>
        /// Preferred GitHub account login for Dev Hub credential lookup.
        /// When set, Dev Hub asks Git Credential Manager for this specific account.
        /// </summary>
        [Browsable(false)]
        [DefaultValue("")]
        public string DevHubGitHubAccount { get; set; } = "";

        [Browsable(false)]
        [DefaultValue(AzureDevOpsAuthenticationMode.Automatic)]
        public AzureDevOpsAuthenticationMode DevHubAdoAuthentication { get; set; } = AzureDevOpsAuthenticationMode.Automatic;

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

        /// <summary>
        /// When true (default), the Tip of the Day and Suggested Extension cards
        /// are shown on the Start Screen. When false, they are hidden so the
        /// Dev Hub and News sections get more vertical space.
        /// </summary>
        [Browsable(false)]
        [DefaultValue(true)]
        public bool ShowTipsAndExtensions { get; set; } = true;

        /// <summary>
        /// JSON-serialized array of extended MRU entries (path, name, type, lastAccessed).
        /// Unlike VS's own MRU store this list has no upper bound and persists every
        /// solution or folder ever opened while the extension is active.
        /// </summary>
        [Browsable(false)]
        [DefaultValue("")]
        public string ExtendedMruItems { get; set; } = "";
    }

    internal enum AzureDevOpsAuthenticationMode
    {
        Automatic,
        VisualStudio,
        PersonalAccessToken,
        Disconnected,
    }
}
