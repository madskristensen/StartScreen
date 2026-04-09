using System;

namespace StartScreen.Services
{
    /// <summary>
    /// Provides tips to display on the Start Screen.
    /// Swap the implementation to pull tips from a remote source or other backing store.
    /// </summary>
    public interface ITipProvider
    {
        /// <summary>
        /// Gets the tip for today. Implementations should rotate through available tips.
        /// </summary>
        string GetTipOfTheDay();
    }

    /// <summary>
    /// Default tip provider with a small set of hard-coded Visual Studio productivity tips.
    /// </summary>
    public sealed class HardCodedTipProvider : ITipProvider
    {
        private static readonly string[] Tips = new[]
        {
            "Press Ctrl+Q to quickly search menus, options, and commands in Visual Studio.",
            "Use Ctrl+T to navigate to any file, type, or member in your solution.",
            "Press Ctrl+. to trigger Quick Actions and refactorings on the current line.",
            "Hold Ctrl and click a symbol to navigate to its definition (Go To Definition).",
            "Press Ctrl+Shift+F to search across all files in your solution."
        };

        /// <inheritdoc />
        public string GetTipOfTheDay()
        {
            int index = DateTime.Now.DayOfYear % Tips.Length;
            return Tips[index];
        }
    }
}
