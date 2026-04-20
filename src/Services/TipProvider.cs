using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StartScreen.Services
{
    /// <summary>
    /// Provides tips loaded from the embedded <c>tips.txt</c> resource.
    /// Each non-blank line that does not start with <c>#</c> is treated as a tip.
    /// Lines starting with <c>#</c> are category comments and are ignored.
    /// </summary>
    public static class TipProvider
    {
        private static readonly IReadOnlyList<string> _tips = LoadTipsFromResource();

        /// <summary>
        /// Gets the total number of available tips.
        /// </summary>
        public static int TipCount => _tips.Count;

        /// <summary>
        /// Gets the tip at the specified index, wrapping around if out of range.
        /// </summary>
        public static string GetTipAt(int index)
        {
            var wrapped = ((index % _tips.Count) + _tips.Count) % _tips.Count;
            return _tips[wrapped];
        }

        /// <summary>
        /// Gets a tip based on the current day of the year.
        /// </summary>
        public static string GetTipOfTheDay()
        {
            var index = DateTime.Now.DayOfYear % _tips.Count;
            return _tips[index];
        }

        private static IReadOnlyList<string> LoadTipsFromResource()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("tips.txt", StringComparison.OrdinalIgnoreCase));

                if (resourceName != null)
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                    using (var reader = new StreamReader(stream))
                    {
                        var tips = new List<string>();
                        string line;

                        while ((line = reader.ReadLine()) != null)
                        {
                            if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#"))
                            {
                                tips.Add(line);
                            }
                        }

                        if (tips.Count > 0)
                        {
                            return tips;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            return new[] { "Explore Visual Studio shortcuts to boost your productivity." };
        }
    }
}
