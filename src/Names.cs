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
        private static readonly Dictionary<string, string> IconWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex IconAndWord = new Regex(@"^\[([A-Za-z][A-Za-z0-9_]*)\]\s*(\S.*)$", RegexOptions.Compiled);
        private static readonly string[] IconWordKeys =
        {
            "%FoodProduced", "%IndustryProduced", "%MoneyProduced", "%ScienceProduced", "%InfluenceProduced", "%ApprovalProduced", "%Population",
        };

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

        // A job strategy, e.g. PopulationStrategy_Food (Agrarian in English).
        public static string Strategy(string name) => Pop(name);

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

        // The game's word for an icon of its text, in its language: FoodColored -> "Food", PopulationCategory_02 ->
        // "Artisans". Null when the game has none (BonusText then falls back to English).
        public static string IconWord(string icon)
        {
            if (icon.StartsWith("PopulationCategory_", StringComparison.Ordinal))
            {
                string job = Job(icon);
                return job.StartsWith("Job ", StringComparison.Ordinal) ? null : job;
            }
            if (IconWords.Count == 0)
            {
                ReadIconWords();
            }
            string core = BonusText.WithoutColored(icon);
            return IconWords.TryGetValue(core, out string word) ? word : null;
        }

        // A population's name as written in the game data, made readable: "Population_Minor_DaughterOfBor" ->
        // "Daughter Of Bor". Tells apart populations the game gives the same name.
        public static string Internal(string name) => Fallback(name);

        public static void ClearCache()
        {
            PopCache.Clear();
            IconWords.Clear();
        }

        // The game names each yield with its icon first ("%FoodProduced" = "[FoodColored] Food"): that gives the
        // icon's word in the game's language.
        private static void ReadIconWords()
        {
            try
            {
                var localization = UIServiceAccessManager.LocalizationService;
                if (localization == null)
                {
                    return;
                }
                foreach (string key in IconWordKeys)
                {
                    Match match = IconAndWord.Match(Clean(localization.Localize(key)) ?? string.Empty);
                    if (match.Success)
                    {
                        IconWords[BonusText.WithoutColored(match.Groups[1].Value)] = match.Groups[2].Value.Trim();
                    }
                }
                if (IconWords.TryGetValue("Dust", out string dust) && !IconWords.ContainsKey("Money"))
                {
                    IconWords["Money"] = dust;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"No icon words: {e.Message}");
            }
        }

        private static string Clean(string text) => string.IsNullOrEmpty(text) ? text : Markup.Replace(text, string.Empty).Trim();

        // "Population_Minor_DaughterOfBor" -> "Daughter Of Bor"
        private static string Fallback(string name)
        {
            string core = name;
            foreach (string prefix in new[] { "Population_Minor_", "Population_", "PopulationStrategy_" })
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
