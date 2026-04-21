namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// Represents a label with a name and optional hex color.
    /// </summary>
    public sealed class DevHubLabel
    {
        /// <summary>
        /// The label display name (e.g., "bug", "enhancement").
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The hex color string without '#' prefix (e.g., "d73a4a").
        /// Null or empty if no color is available.
        /// </summary>
        public string Color { get; set; }

        public override string ToString() => Name ?? string.Empty;
    }
}
