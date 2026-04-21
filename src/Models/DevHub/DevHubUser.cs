namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// Represents an authenticated user on a remote hosting provider.
    /// </summary>
    public sealed class DevHubUser
    {
        /// <summary>
        /// The username or login (e.g., "madskristensen").
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// The display name (e.g., "Mads Kristensen").
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// URL to the user's avatar image, or null if unavailable.
        /// </summary>
        public string AvatarUrl { get; set; }

        /// <summary>
        /// The provider display name (e.g., "GitHub", "Azure DevOps").
        /// </summary>
        public string ProviderName { get; set; }

        /// <summary>
        /// The host this user is authenticated against (e.g., "github.com").
        /// </summary>
        public string Host { get; set; }
    }
}
