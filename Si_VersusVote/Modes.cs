using System;
using System.Collections.Generic;
using System.Text;

namespace Si_VersusVote
{
    /// <summary>Names and parsing for GameModeExt.ETeamsVersus (NONE=0, HvH=1, HvA=2, HvHvA=3, H2vA=4).</summary>
    internal static class Modes
    {
        /// <summary>The three layouts the vanilla Battle Layout screen offers (vanilla's zero-vote fallback is Random.Range(1,4) = these).</summary>
        public static readonly GameModeExt.ETeamsVersus[] Votable =
        {
            GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS,
            GameModeExt.ETeamsVersus.HUMANS_VS_ALIENS,
            GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS,
        };

        public static readonly GameModeExt.ETeamsVersus[] All =
        {
            GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS,
            GameModeExt.ETeamsVersus.HUMANS_VS_ALIENS,
            GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS,
            GameModeExt.ETeamsVersus.HUMANS2_VS_ALIENS,
        };

        public static string Short(GameModeExt.ETeamsVersus m)
        {
            switch (m)
            {
                case GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS: return "HvH";
                case GameModeExt.ETeamsVersus.HUMANS_VS_ALIENS: return "HvA";
                case GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS: return "HvHvA";
                case GameModeExt.ETeamsVersus.HUMANS2_VS_ALIENS: return "H2vA";
                default: return "NONE";
            }
        }

        public static string Display(GameModeExt.ETeamsVersus m)
        {
            switch (m)
            {
                case GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS: return "Humans vs Humans";
                case GameModeExt.ETeamsVersus.HUMANS_VS_ALIENS: return "Humans vs Aliens";
                case GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS: return "3-way";
                case GameModeExt.ETeamsVersus.HUMANS2_VS_ALIENS: return "Humans x2 vs Aliens";
                default: return "none";
            }
        }

        static string Norm(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>Accepts HvH / HvA / HvHvA / H2vA, the enum names (HUMANS_VS_ALIENS ...), 3way / 3-way / threeway and 1..4.</summary>
        public static bool TryParse(string? s, out GameModeExt.ETeamsVersus mode)
        {
            mode = GameModeExt.ETeamsVersus.NONE;
            if (string.IsNullOrWhiteSpace(s)) return false;
            switch (Norm(s!))
            {
                case "hvh": case "humansvshumans": case "1":
                    mode = GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS; return true;
                case "hva": case "humansvsaliens": case "2":
                    mode = GameModeExt.ETeamsVersus.HUMANS_VS_ALIENS; return true;
                case "hvhva": case "3way": case "threeway": case "humansvshumansvsaliens": case "3":
                    mode = GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS; return true;
                case "h2va": case "humans2vsaliens": case "4":
                    mode = GameModeExt.ETeamsVersus.HUMANS2_VS_ALIENS; return true;
            }
            return false;
        }

        /// <summary>Map key for case/space-insensitive comparison ("Monumentvalley" == "MonumentValley" == "monument valley").</summary>
        public static string MapKey(string? map) => string.IsNullOrEmpty(map) ? "" : Norm(map!);

        public static string JoinDisplay(IEnumerable<GameModeExt.ETeamsVersus> modes)
        {
            var parts = new List<string>();
            foreach (var m in modes) parts.Add(Display(m));
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }
    }
}
