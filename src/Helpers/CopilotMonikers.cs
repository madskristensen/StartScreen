using System;
using Microsoft.VisualStudio.Imaging.Interop;

namespace StartScreen.Helpers
{
    /// <summary>
    /// ImageMonikers for icons that aren't reliably exposed by
    /// <see cref="Microsoft.VisualStudio.Imaging.KnownMonikers"/> in the package
    /// version referenced by this extension.
    /// </summary>
    internal static class CopilotMonikers
    {
        /// <summary>
        /// ImageMoniker for the GitHub Copilot glyph, addressed by its
        /// well-known guid/id pair so it resolves on any VS that ships the icon.
        /// </summary>
        public static readonly ImageMoniker GitHubCopilot = new ImageMoniker
        {
            Guid = new Guid("ae27a6b0-e345-4288-96df-5eaf394ee369"),
            Id = 4045
        };
    }
}
