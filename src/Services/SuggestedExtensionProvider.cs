using StartScreen.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace StartScreen.Services
{
    /// <summary>
    /// Provides suggested extensions loaded from the embedded <c>extensions.json</c> resource.
    /// </summary>
    public static class SuggestedExtensionProvider
    {
        private static readonly IReadOnlyList<SuggestedExtension> _extensions = LoadExtensionsFromResource();

        /// <summary>
        /// Gets the total number of available extension suggestions.
        /// </summary>
        public static int SuggestionCount => _extensions.Count;

        /// <summary>
        /// Gets all available extension suggestions.
        /// </summary>
        public static IReadOnlyList<SuggestedExtension> Extensions => _extensions;

        /// <summary>
        /// Gets the extension suggestion at the specified index, wrapping around if out of range.
        /// </summary>
        public static SuggestedExtension GetSuggestionAt(int index)
        {
            if (_extensions.Count == 0)
                return null;

            var wrapped = ((index % _extensions.Count) + _extensions.Count) % _extensions.Count;
            return _extensions[wrapped];
        }

        /// <summary>
        /// Gets an extension suggestion based on the current day of the year.
        /// </summary>
        public static SuggestedExtension GetSuggestionOfTheDay()
        {
            if (_extensions.Count == 0)
                return null;

            var index = DateTime.Now.DayOfYear % _extensions.Count;
            return _extensions[index];
        }

        private static IReadOnlyList<SuggestedExtension> LoadExtensionsFromResource()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("extensions.json", StringComparison.OrdinalIgnoreCase));

                if (resourceName != null)
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                    using (var reader = new StreamReader(stream))
                    {
                        var json = reader.ReadToEnd();
                        var extensions = JsonSerializer.Deserialize<List<SuggestedExtension>>(json);

                        if (extensions != null && extensions.Count > 0)
                        {
                            return extensions;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            return new List<SuggestedExtension>
            {
                new SuggestedExtension
                {
                    Id = "Example.Extension",
                    Name = "Example Extension",
                    Description = "No extensions configured",
                    MarketplaceUrl = "https://marketplace.visualstudio.com/"
                }
            };
        }
    }
}
