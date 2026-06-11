namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// The Dev Hub sub-tab that is selected by default when the Start Screen opens.
    /// The numeric values match the tab order in the Dev Hub panel
    /// (Issues, Pull requests, Builds), so they map directly to the
    /// TabControl's SelectedIndex.
    /// </summary>
    public enum DevHubDefaultTab
    {
        /// <summary>Show the Issues tab first.</summary>
        Issues = 0,

        /// <summary>Show the Pull requests tab first.</summary>
        PullRequests = 1,

        /// <summary>Show the Builds tab first.</summary>
        Builds = 2
    }
}
