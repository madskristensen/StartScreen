using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using StartScreen.Models;
using System;
using System.IO;

namespace StartScreen.Helpers
{
    /// <summary>
    /// Provides icon monikers for MRU items based on file type.
    /// </summary>
    public static class FileIconHelper
    {
        /// <summary>
        /// Gets the appropriate ImageMoniker for an MRU item.
        /// </summary>
        public static ImageMoniker GetIconForMruItem(MruItem item)
        {
            if (item == null)
                return KnownMonikers.Document;

            switch (item.Type)
            {
                case MruItemType.Folder:
                    return KnownMonikers.FolderOpened;

                case MruItemType.Solution:
                    return KnownMonikers.Solution;

                case MruItemType.Project:
                    return GetProjectIcon(item.Path);

                default:
                    return KnownMonikers.Document;
            }
        }

        /// <summary>
        /// Gets the appropriate ImageMoniker for a project file based on extension.
        /// </summary>
        private static ImageMoniker GetProjectIcon(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return KnownMonikers.Document;

            var extension = Path.GetExtension(path)?.ToLowerInvariant();

            switch (extension)
            {
                case ".csproj":
                    return KnownMonikers.CSProjectNode;

                case ".vbproj":
                    return KnownMonikers.VBProjectNode;

                case ".fsproj":
                    return KnownMonikers.FSProjectNode;

                case ".vcxproj":
                    return KnownMonikers.CPPProjectNode;

                case ".sqlproj":
                    return KnownMonikers.Database;

                case ".wapproj":
                    return KnownMonikers.ApplicationWarning;

                default:
                    return KnownMonikers.Application;
            }
        }
    }
}
