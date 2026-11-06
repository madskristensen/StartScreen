using System;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using StartScreen.Models;

namespace StartScreen.Helpers
{
    /// <summary>
    /// ImageMonikers for icons that aren't reliably exposed by
    /// <see cref="Microsoft.VisualStudio.Imaging.KnownMonikers"/> in the package
    /// version referenced by this extension.
    /// </summary>
    internal static class CopilotMonikers
    {
        internal static ImageMoniker ForOrigin(CopilotSessionOrigin origin) => origin switch
        {
            CopilotSessionOrigin.VisualStudio => _agent,
            CopilotSessionOrigin.VisualStudioCode => _visualStudioCode,
            CopilotSessionOrigin.Cli => KnownMonikers.Console,
            CopilotSessionOrigin.GitHub => KnownMonikers.GitHub,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        /// <summary>
        /// ImageMoniker for the GitHub Copilot glyph, addressed by its
        /// well-known guid/id pair so it resolves on any VS that ships the icon.
        /// </summary>
        public static readonly ImageMoniker GitHubCopilot = new ImageMoniker
        {
            Guid = new Guid("ae27a6b0-e345-4288-96df-5eaf394ee369"),
            Id = 4045
        };

        private static readonly ImageMoniker _agent = Resolve("Agent", GitHubCopilot);
        private static readonly ImageMoniker _visualStudioCode = Resolve("VisualStudioCode", KnownMonikers.VisualStudioOnline);

        private static ImageMoniker Resolve(string name, ImageMoniker fallback) => typeof(KnownMonikers).GetProperty(name)?.GetValue(null)
            is ImageMoniker moniker ? moniker : fallback;
    }
}
