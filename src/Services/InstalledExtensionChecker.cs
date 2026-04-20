using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;
using System;
using System.Collections.Generic;

namespace StartScreen.Services
{
    /// <summary>
    /// Checks the Visual Studio private registry hive to determine which extensions are currently installed.
    /// Reads value names from User\ExtensionManager\EnabledExtensions where each name contains the extension ID.
    /// </summary>
    public static class InstalledExtensionChecker
    {
        private static HashSet<string> _installedExtensionIds;
        private static readonly object _lock = new object();

        /// <summary>
        /// Checks if an extension with the specified ID is currently installed.
        /// </summary>
        public static bool IsInstalled(string extensionId)
        {
            if (string.IsNullOrWhiteSpace(extensionId))
                return false;

            EnsureExtensionsLoaded();

            return _installedExtensionIds.Contains(extensionId);
        }

        private static void EnsureExtensionsLoaded()
        {
            if (_installedExtensionIds != null)
                return;

            lock (_lock)
            {
                if (_installedExtensionIds != null)
                    return;

                _installedExtensionIds = LoadInstalledExtensionIds();
            }
        }

        private static HashSet<string> LoadInstalledExtensionIds()
        {
            var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (RegistryKey root = VSRegistry.RegistryRoot(__VsLocalRegistryType.RegType_UserSettings))
                {
                    if (root == null)
                        return installedIds;

                    using (RegistryKey extensionsKey = root.OpenSubKey(@"ExtensionManager\EnabledExtensions"))
                    {
                        if (extensionsKey == null)
                            return installedIds;

                        foreach (string valueName in extensionsKey.GetValueNames())
                        {
                            if (string.IsNullOrWhiteSpace(valueName))
                                continue;

                            // Value names are in the format "VsixIdentityId,Version"
                            var commaIndex = valueName.IndexOf(',');
                            var extensionId = commaIndex >= 0
                                ? valueName.Substring(0, commaIndex).Trim()
                                : valueName.Trim();

                            if (!string.IsNullOrEmpty(extensionId))
                            {
                                installedIds.Add(extensionId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            return installedIds;
        }

        /// <summary>
        /// Gets all installed extension IDs (for diagnostics).
        /// </summary>
        public static IReadOnlyCollection<string> GetInstalledIds()
        {
            EnsureExtensionsLoaded();
            return _installedExtensionIds;
        }

        /// <summary>
        /// Clears the cached extension list, forcing a reload on the next check.
        /// </summary>
        public static void ClearCache()
        {
            lock (_lock)
            {
                _installedExtensionIds = null;
            }
        }
    }
}
