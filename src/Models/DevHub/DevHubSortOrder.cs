namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// Controls how issues and pull requests from multiple providers are ordered
    /// in the Dev Hub lists.
    /// </summary>
    public enum DevHubSortOrder
    {
        /// <summary>Most recently updated items first, regardless of provider.</summary>
        MostRecent = 0,

        /// <summary>GitHub items first, then everything else, each group most recent first.</summary>
        GitHubFirst = 1,

        /// <summary>Azure DevOps items first, then everything else, each group most recent first.</summary>
        AzureDevOpsFirst = 2
    }
}
