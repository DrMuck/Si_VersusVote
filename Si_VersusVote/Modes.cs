using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Si_VersusVote
{
    /// <summary>
    /// Names and parsing for GameModeExt.ETeamsVersus. The vanilla Battle Layout screen has FOUR buttons; the map team
    /// setups (BaseTeamSetup.ForTeamVersusModes: Sol {1,2,3}, Centauri {1,3,4}, Alien {2,3,4}) give them their teams:
    ///   1 HUMANS_VS_HUMANS            "HvH"    Sol vs Centauri
    ///   2 HUMANS_VS_ALIENS            "SvA"    Sol vs Aliens
    ///   4 HUMANS2_VS_ALIENS           "CvA"    Centauri vs Aliens   (written as "H2vA" by 0.1.0 - 0.3.0, still parsed)
    ///   3 HUMANS_VS_HUMANS_VS_ALIENS  "HvHvA"  3-way (on a map_two_vs_one map: 2v1, Sol + Centauri allied vs Aliens)
    /// Category "HvA" = 2 and 4 together: one ceiling for both, "HvA" in a blocklist blocks both. The category is
    /// represented by enum 2 wherever a dictionary is keyed by category (ceilings).
    /// </summary>
    internal static class Modes
    {
        public const GameModeExt.ETeamsVersus HvH = GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS;
        public const GameModeExt.ETeamsVersus SvA = GameModeExt.ETeamsVersus.HUMANS_VS_ALIENS;
        public const GameModeExt.ETeamsVersus CvA = GameModeExt.ETeamsVersus.HUMANS2_VS_ALIENS;
        public const GameModeExt.ETeamsVersus ThreeWay = GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS;

        /// <summary>The four Battle Layout buttons, in screen order. Vanilla's zero-vote fallback is Random.Range(1,4) (1..3); 4 only comes from votes.</summary>
        public static readonly GameModeExt.ETeamsVersus[] Votable = { HvH, SvA, CvA, ThreeWay };

        public static readonly GameModeExt.ETeamsVersus[] All = { HvH, SvA, CvA, ThreeWay };

        /// <summary>The ceiling categories (representative enum: HvH 1, HvA 2, HvHvA 3), in the order status lines show them.</summary>
        public static readonly GameModeExt.ETeamsVersus[] Categories = { ThreeWay, SvA, HvH };

        /// <summary>Sol vs Aliens or Centauri vs Aliens (category HvA).</summary>
        public static bool IsHvA(GameModeExt.ETeamsVersus m) => m == SvA || m == CvA;

        /// <summary>The category key of a layout: 4 maps to 2 (HvA), everything else to itself.</summary>
        public static GameModeExt.ETeamsVersus Category(GameModeExt.ETeamsVersus m) => m == CvA ? SvA : m;

        /// <summary>The layouts of a category (HvA = 2 and 4).</summary>
        public static GameModeExt.ETeamsVersus[] InCategory(GameModeExt.ETeamsVersus cat)
            => Category(cat) == SvA ? new[] { SvA, CvA } : new[] { cat };

        /// <summary>"HvH", "HvA", "HvHvA" (ceiling keys, window text).</summary>
        public static string CategoryShort(GameModeExt.ETeamsVersus m)
            => Category(m) == SvA ? "HvA" : Short(m);

        public static string Short(GameModeExt.ETeamsVersus m)
        {
            switch (m)
            {
                case HvH: return "HvH";
                case SvA: return "SvA";
                case CvA: return "CvA";
                case ThreeWay: return "HvHvA";
                default: return "NONE";
            }
        }

        public static string Display(GameModeExt.ETeamsVersus m)
        {
            switch (m)
            {
                case HvH: return "Humans vs Humans";
                case SvA: return "Sol vs Aliens";
                case CvA: return "Centauri vs Aliens";
                case ThreeWay: return "3-way";
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

        /// <summary>
        /// A layout name as used in blocklists and commands: HvH, HvA (= Sol vs Aliens AND Centauri vs Aliens), SvA, CvA,
        /// HvHvA (3way / 3-way), H2vA (= CvA, old history), the enum names and 1..4. HUMANS_VS_ALIENS / "Humans vs Aliens"
        /// mean the HvA category (what they meant before 0.3.1). The array is in enum order.
        /// </summary>
        public static bool TryParseSet(string? s, out GameModeExt.ETeamsVersus[] modes)
        {
            modes = new GameModeExt.ETeamsVersus[0];
            if (string.IsNullOrWhiteSpace(s)) return false;
            switch (Norm(s!))
            {
                case "hvh": case "humansvshumans": case "solvscentauri": case "centvssol": case "centaurivssol": case "1":
                    modes = new[] { HvH }; return true;
                case "hva": case "humansvsaliens":
                    modes = new[] { SvA, CvA }; return true;
                case "sva": case "solvsaliens": case "alienvssol": case "aliensvssol": case "2":
                    modes = new[] { SvA }; return true;
                case "cva": case "centaurivsaliens": case "centvsaliens": case "alienvscent": case "alienvscentauri":
                case "h2va": case "humans2vsaliens": case "4":
                    modes = new[] { CvA }; return true;
                case "hvhva": case "3way": case "threeway": case "humansvshumansvsaliens": case "solvscentvsalien": case "3":
                    modes = new[] { ThreeWay }; return true;
            }
            return false;
        }

        /// <summary>One layout (history rows): like TryParseSet, but "HvA" (rows written before 0.3.1) is Sol vs Aliens (enum 2).</summary>
        public static bool TryParse(string? s, out GameModeExt.ETeamsVersus mode)
        {
            mode = GameModeExt.ETeamsVersus.NONE;
            if (!TryParseSet(s, out var set)) return false;
            mode = set[0];   // HvA -> 2 (the only HvA button 0.1.0 - 0.3.0 knew about)
            return true;
        }

        /// <summary>A ceiling category: HvH, HvA (Humans vs Aliens), HvHvA (3way ...) and 1 / 2 / 3. SvA / CvA / H2vA / 4 have no ceiling of their own.</summary>
        public static bool TryParseCategory(string? s, out GameModeExt.ETeamsVersus cat)
        {
            cat = GameModeExt.ETeamsVersus.NONE;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string n = Norm(s!);
            if (n == "2") { cat = SvA; return true; }   // the old numeric HvA key
            if (!TryParseSet(s, out var set)) return false;
            if (set.Length == 2) { cat = SvA; return true; }
            if (set[0] == HvH || set[0] == ThreeWay) { cat = set[0]; return true; }
            return false;   // SvA / CvA alone
        }

        /// <summary>Canonical blocklist token of a single layout set: HvH, HvA (both), SvA, CvA, HvHvA.</summary>
        public static string Token(IEnumerable<GameModeExt.ETeamsVersus> set)
        {
            var s = new HashSet<GameModeExt.ETeamsVersus>(set);
            if (s.Count == 2 && s.Contains(SvA) && s.Contains(CvA)) return "HvA";
            return s.Count == 1 ? Short(s.First()) : string.Join("+", s.Select(Short));
        }

        /// <summary>A blocked set as stored tokens: HvH, HvA (or SvA / CvA when only one of them), HvHvA.</summary>
        public static List<string> Tokens(ICollection<GameModeExt.ETeamsVersus> set)
        {
            var list = new List<string>();
            if (set.Contains(HvH)) list.Add("HvH");
            if (set.Contains(SvA) && set.Contains(CvA)) list.Add("HvA");
            else if (set.Contains(SvA)) list.Add("SvA");
            else if (set.Contains(CvA)) list.Add("CvA");
            if (set.Contains(ThreeWay)) list.Add("HvHvA");
            return list;
        }

        /// <summary>The tokens offered where a layout is chosen (block toggles, map exclusions).</summary>
        public static readonly string[] BlockTokens = { "HvH", "HvA", "SvA", "CvA", "HvHvA" };

        public const string NamesHelp = "HvH, HvA, SvA, CvA, HvHvA";

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
