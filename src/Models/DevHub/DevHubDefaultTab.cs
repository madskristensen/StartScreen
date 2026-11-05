namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// The Dev Hub sub-tab that is selected by default when the Start Screen opens.
    /// Numeric values are persisted independently of the visual tab order.
    /// </summary>
    public enum DevHubDefaultTab
    {
        /// <summary>Show the Issues tab first.</summary>
        Issues = 0,

        /// <summary>Show the Pull requests tab first.</summary>
        PullRequests = 1,

        /// <summary>Show the Builds tab first.</summary>
        Builds = 2,

        Overview = 3,

        NeedsYou = 4,

        MyWork = 5,

        Incoming = 6
    }
}
