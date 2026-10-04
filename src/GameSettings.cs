using System;
using System.Collections.Generic;

namespace PopulationPlanner
{
    // What the player chose for one game, saved next to the BepInEx config, one file per game.
    internal sealed class GameSettings
    {
        // Populations to aim for, first one first. Empty = automatic order (closest to its last bonus first).
        public List<string> Priority = new List<string>();
        // Never aimed for, and never spread.
        public List<string> Skipped = new List<string>();
        // Bonus unlocked, but don't put one in every city.
        public List<string> NoSpread = new List<string>();
        // Keyed by the settlement GUID.
        public Dictionary<string, CitySettings> Cities = new Dictionary<string, CitySettings>();

        public CitySettings GetCity(ulong guid)
        {
            return Cities != null && Cities.TryGetValue(Key(guid), out CitySettings city) ? city : null;
        }

        public CitySettings GetOrAddCity(ulong guid)
        {
            if (Cities == null)
            {
                Cities = new Dictionary<string, CitySettings>();
            }
            string key = Key(guid);
            if (!Cities.TryGetValue(key, out CitySettings city))
            {
                city = new CitySettings();
                Cities[key] = city;
            }
            return city;
        }

        public bool IsSkipped(string pop) => Skipped != null && Skipped.Contains(pop);

        public bool IsNoSpread(string pop) => NoSpread != null && NoSpread.Contains(pop);

        // Fills lists a hand-edited or older file may lack.
        public void Normalize()
        {
            Priority = Priority ?? new List<string>();
            Skipped = Skipped ?? new List<string>();
            NoSpread = NoSpread ?? new List<string>();
            Cities = Cities ?? new Dictionary<string, CitySettings>();
        }

        private static string Key(ulong guid) => guid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal sealed class CitySettings
    {
        // The mod leaves this city alone.
        public bool Off;
        // The mod picks growth here but leaves its jobs alone.
        public bool JobsOff;
        // Grow this population here until its last bonus is unlocked, then follow the global plan.
        public string Target;
        // Minimum approval level for this city (ApprovalRule level name); null = the global setting.
        public string MinApproval;
        // Picked by hand in the city screen; the mod waits until the city grows (or until resumed).
        public string HeldPop;
        public int HeldAtGained = -1;
        public bool HeldUntilResumed;

        public bool IsEmpty() => !Off && !JobsOff && string.IsNullOrEmpty(Target) && string.IsNullOrEmpty(HeldPop) && string.IsNullOrEmpty(MinApproval);
    }

    // The "keep cities at least Content / Happy / Jubilant" option.
    internal static class ApprovalRule
    {
        public const string Off = "Off";
        public static readonly string[] Levels = { Off, "Content", "Happy", "Jubilant" };

        // The game's level definitions, and their minimum approval should the game data not be readable.
        public static string Definition(string level)
        {
            switch (level)
            {
                case "Content": return "SettlementApproval_Neutral";
                case "Happy": return "SettlementApproval_Happy";
                case "Jubilant": return "SettlementApproval_VeryHappy";
                default: return null;
            }
        }

        public static float DefaultMinimum(string level)
        {
            switch (level)
            {
                case "Content": return 25f;
                case "Happy": return 60f;
                case "Jubilant": return 85f;
                default: return float.NaN;
            }
        }

        public static string Normalize(string level)
        {
            foreach (string known in Levels)
            {
                if (string.Equals(known, level?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }
            return Off;
        }

        // Approval the city must keep (NaN = no minimum).
        public static float Minimum(GameState state, string level)
        {
            string definition = Definition(Normalize(level));
            if (definition == null)
            {
                return float.NaN;
            }
            return state.ApprovalLevels.TryGetValue(definition, out float minimum) ? minimum : DefaultMinimum(Normalize(level));
        }

        // Below this, the mod puts approval first in the city (the minimum plus a buffer for the next growth).
        public static float Target(GameState state, GameSettings settings, CityState city, string globalLevel, float buffer)
        {
            string level = settings.GetCity(city.Guid)?.MinApproval ?? globalLevel;
            float minimum = Minimum(state, level);
            return float.IsNaN(minimum) ? float.NaN : minimum + Math.Max(0f, buffer);
        }
    }
}
