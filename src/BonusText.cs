using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PopulationPlanner
{
    // Turns the game's effect text into plain lines for the window, which can show neither the game's formatting tags
    // (<b>, <c=...>, links) nor its [Symbol] icons. An icon becomes its word, unless the text names it right after
    // anyway, as in "[FoodColored] Food". No game types in here, so it can be tested without the game.
    internal static class BonusText
    {
        private static readonly Regex Tag = new Regex("<[^<>]*>", RegexOptions.Compiled);
        private static readonly Regex Icon = new Regex(@"\[([A-Za-z][A-Za-z0-9_]*)\]", RegexOptions.Compiled);
        private static readonly Regex Word = new Regex(@"\p{L}+", RegexOptions.Compiled);
        private static readonly Regex Spaces = new Regex(@"[ \t ​]+", RegexOptions.Compiled);
        private static readonly Regex SpaceAfterOpening = new Regex(@"\( ", RegexOptions.Compiled);
        private static readonly Regex SpaceBeforeClosing = new Regex(@" ([),.:;!?%])", RegexOptions.Compiled);

        // English words for the icons, when the game has none (localWord): e.g. Culture is shown as Influence.
        private static readonly Dictionary<string, string> Words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Food", "Food" },
            { "Industry", "Industry" },
            { "Dust", "Dust" },
            { "Money", "Dust" },
            { "Science", "Science" },
            { "Culture", "Influence" },
            { "Influence", "Influence" },
            { "PublicOrder", "Approval" },
            { "Population", "Population" },
            { "Turn", "turns" },
            { "Cadavers", "Corpses" },
            { "Fortification", "Fortification" },
            { "Military", "Military" },
            { "Fame", "Fame" },
            { "Health", "Health" },
            { "Damage", "Damage" },
            { "Defense", "Defense" },
            { "Experience", "Experience" },
            { "MovementPoints", "Movement" },
            { "VisionRange", "Vision" },
            { "City", "Cities" },
            { "Unit", "Units" },
            { "Territory", "Territories" },
            { "Dweller", "Dwellers" },
            { "ResourceStrategic", "Strategic resources" },
            { "ResourceLuxury", "Luxury resources" },
        };

        // Icons that only decorate (bullets, arrows) or mark unfinished text.
        private static readonly HashSet<string> Dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DoubleArrow", "Bulletpoint", "Separator", "TBD", "DEPRECATED", "Debug", "Locked", "CheckmarkIcon",
        };

        // The game's text as plain lines. localWord gives the game's own word for an icon (e.g. FoodColored -> Food,
        // PopulationCategory_02 -> Artisans), in its language; null when it has none.
        public static List<string> Lines(string text, Func<string, string> localWord = null)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return lines;
            }
            string plain = Tag.Replace(text.Replace("\r", string.Empty), string.Empty);
            foreach (string raw in plain.Split('\n'))
            {
                string line = Spaces.Replace(Spell(raw, localWord), " ").Trim();
                line = SpaceAfterOpening.Replace(line, "(");
                line = SpaceBeforeClosing.Replace(line, "$1");
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }
            return lines;
        }

        // "[FoodColored]" -> "Food"
        public static string WithoutColored(string icon)
        {
            return icon.EndsWith("Colored", StringComparison.OrdinalIgnoreCase) ? icon.Substring(0, icon.Length - "Colored".Length) : icon;
        }

        private static string Spell(string line, Func<string, string> localWord)
        {
            MatchCollection icons = Icon.Matches(line);
            if (icons.Count == 0)
            {
                return line;
            }
            var builder = new StringBuilder(line.Length + 16);
            int position = 0;
            // The previous icon was spelled out, with nothing but spaces since: icons in a row become a list,
            // as in "+1 [FoodColored][IndustryColored] on ..." -> "+1 Food, Industry on ...".
            bool listing = false;
            foreach (Match icon in icons)
            {
                string between = line.Substring(position, icon.Index - position);
                builder.Append(between);
                position = icon.Index + icon.Length;
                if (between.Trim().Length > 0)
                {
                    listing = false;
                }
                string word = IconWord(icon.Groups[1].Value, localWord, out bool known);
                string following = Icon.Replace(line.Substring(position), " ");
                // A known word is left out when the text names it next ("[FoodColored] Food"); an unknown icon when
                // any word follows, which is then its name ("[Strategic01Colored] Titanium").
                if (word.Length == 0 || (known ? NamedSoon(word, following) : StartsWithWord(following)))
                {
                    listing = false;
                    continue;
                }
                if (listing)
                {
                    builder.Append(',');
                }
                builder.Append(' ').Append(word).Append(' ');
                listing = true;
            }
            builder.Append(line.Substring(position));
            return builder.ToString();
        }

        private static string IconWord(string icon, Func<string, string> localWord, out bool known)
        {
            known = true;
            if (Dropped.Contains(icon))
            {
                return string.Empty;
            }
            string local = localWord?.Invoke(icon);
            if (!string.IsNullOrEmpty(local))
            {
                return local.Trim();
            }
            if (icon.StartsWith("PopulationCategory_", StringComparison.OrdinalIgnoreCase))
            {
                return DefaultJobName(icon);
            }
            string core = WithoutColored(icon);
            if (Words.TryGetValue(core, out string word))
            {
                return word;
            }
            known = false;
            return Readable(core);
        }

        private static string DefaultJobName(string icon)
        {
            string job = icon.Substring("PopulationCategory_".Length);
            switch (job.ToLowerInvariant())
            {
                case "01": return "Citizens";
                case "02": return "Artisans";
                case "03": return "Scribes";
                case "homeless": return "Destitute";
                default: return "Job " + job.TrimStart('0');
            }
        }

        // Whether one of the next two words names the icon's word (singular or plural).
        private static bool NamedSoon(string word, string following)
        {
            Match wordMatch = Word.Match(word);
            string first = wordMatch.Success ? wordMatch.Value : word;
            Match next = Word.Match(following);
            for (int i = 0; i < 2 && next.Success; i++, next = next.NextMatch())
            {
                if (SameWord(first, next.Value))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool StartsWithWord(string following)
        {
            string rest = following.TrimStart(' ', '\t', ' ', '​');
            return rest.Length > 0 && char.IsLetter(rest[0]);
        }

        // "Turn" ~ "Turns", "Artisans" ~ "Artisan", "Dweller" ~ "Dwellers".
        private static bool SameWord(string a, string b)
        {
            string shorter = a.Length <= b.Length ? a : b;
            string longer = a.Length <= b.Length ? b : a;
            return shorter.Length > 0 && longer.Length - shorter.Length <= 2 && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
        }

        // "Strategic01" -> "Strategic", "SubjugationPoints" -> "Subjugation Points".
        private static string Readable(string icon)
        {
            var builder = new StringBuilder(icon.Length + 4);
            for (int i = 0; i < icon.Length; i++)
            {
                char c = icon[i];
                if (char.IsDigit(c))
                {
                    continue;
                }
                if (c == '_')
                {
                    builder.Append(' ');
                    continue;
                }
                if (i > 0 && char.IsUpper(c) && char.IsLower(icon[i - 1]))
                {
                    builder.Append(' ');
                }
                builder.Append(c);
            }
            return builder.ToString().Trim();
        }
    }
}
