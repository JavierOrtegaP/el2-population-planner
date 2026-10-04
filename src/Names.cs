using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Amplitude;
using Amplitude.Mercury.Interop;
using Amplitude.UI;

namespace PopulationPlanner
{
    // Display names in the game's language. Main thread only: the game's localization is not thread-safe.
    internal static class Names
    {
        private static readonly Dictionary<string, string> PopCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Regex Markup = new Regex("<[^>]*>", RegexOptions.Compiled);

        public static string Pop(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "-";
            }
            if (PopCache.TryGetValue(name, out string cached))
            {
                return cached;
            }
            string result = null;
            try
            {
                var dataUtils = Amplitude.Mercury.Utils.DataUtils;
                if (dataUtils != null && dataUtils.TryGetUIMapper(new StaticString(name), out UIMapper mapper) && mapper != null)
                {
                    string key = !string.IsNullOrEmpty(mapper.RawTitle) ? mapper.RawTitle : mapper.Title;
                    var localization = UIServiceAccessManager.LocalizationService;
                    if (localization != null && !string.IsNullOrEmpty(key))
                    {
                        result = localization.Localize(key);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"No display name for {name}: {e.Message}");
            }
            result = Clean(result);
            if (string.IsNullOrEmpty(result) || result.StartsWith("%", StringComparison.Ordinal) || result.StartsWith("#", StringComparison.Ordinal))
            {
                result = Fallback(name);
            }
            else
            {
                // Only cache real translations: the localization may not be loaded yet on the first frames.
                PopCache[name] = result;
            }
            return result;
        }

        // A job (population category), e.g. PopulationCategory_02.
        public static string Job(string name)
        {
            string text = Pop(name);
            if (text.StartsWith("Category ", StringComparison.Ordinal) || text.StartsWith("Population Category", StringComparison.Ordinal))
            {
                // Untranslated: "PopulationCategory_02" -> "Job 2".
                string digits = new string(name.Where(char.IsDigit).ToArray()).TrimStart('0');
                return digits.Length > 0 ? "Job " + digits : name;
            }
            return text;
        }

        public static string City(CityState city)
        {
            if (!string.IsNullOrEmpty(city.Name))
            {
                return city.Name;
            }
            try
            {
                if (city.NameSource is EntityNameInfo info)
                {
                    city.Name = Clean(info.ToString());
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"No display name for city {city.Guid}: {e.Message}");
            }
            if (string.IsNullOrEmpty(city.Name))
            {
                city.Name = "City " + city.Guid;
            }
            return city.Name;
        }

        public static void ClearCache() => PopCache.Clear();

        private static string Clean(string text) => string.IsNullOrEmpty(text) ? text : Markup.Replace(text, string.Empty).Trim();

        // "Population_Minor_DaughterOfBor" -> "Daughter Of Bor"
        private static string Fallback(string name)
        {
            string core = name;
            foreach (string prefix in new[] { "Population_Minor_", "Population_" })
            {
                if (core.StartsWith(prefix, StringComparison.Ordinal))
                {
                    core = core.Substring(prefix.Length);
                    break;
                }
            }
            var builder = new StringBuilder(core.Length + 8);
            for (int i = 0; i < core.Length; i++)
            {
                char c = core[i];
                if (c == '_')
                {
                    builder.Append(' ');
                    continue;
                }
                if (i > 0 && char.IsUpper(c) && char.IsLower(core[i - 1]))
                {
                    builder.Append(' ');
                }
                builder.Append(c);
            }
            return builder.ToString();
        }
    }
}
