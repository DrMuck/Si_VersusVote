using System;
using System.Collections.Generic;
using System.Linq;

namespace Si_VersusVote
{
    internal enum BlockKind { None, Server, Map, Ceiling }

    internal sealed class ModeStatus
    {
        public GameModeExt.ETeamsVersus Mode;
        public BlockKind Block;   // the reason that actually applies this round (None = counted)
        public int Count;         // rounds of this mode in the window
        public int CatCount;      // rounds of its ceiling category in the window (Sol vs Aliens + Centauri vs Aliens for both HvA layouts)
        public int Max;           // ceiling of the category; -1 = unlimited
        public BlockKind Waived;  // a blocklist entry that would block it but does not apply (HvHvA on a 2v1 map): Server / Map, else None
    }

    /// <summary>Which layouts count this round, and why the others do not.</summary>
    internal sealed class Evaluation
    {
        public string Map = "";
        public int Window;
        public int Seen;
        public bool CeilingIgnored;   // the ceilings would have left no layout: ceiling layer dropped for this round
        public bool FilterOff;        // the blocklists alone leave no layout (or the mod is disabled): nothing is filtered
        public bool TwoVsOneMap;      // the map is in map_two_vs_one
        public bool AllianceMissing;  // ... but Si_Alliance's AllianceApi is not loaded: the 3-way button plays a normal 3-way
        public bool TwoVsOne => TwoVsOneMap && !AllianceMissing;   // the 3-way button means 2v1 this round
        public int TwoVsOneInWindow;  // 2v1 rounds in the window (also counted as HvHvA)
        public readonly Dictionary<GameModeExt.ETeamsVersus, ModeStatus> Status = new Dictionary<GameModeExt.ETeamsVersus, ModeStatus>();

        public bool IsAllowed(GameModeExt.ETeamsVersus m)
            => FilterOff || !Status.TryGetValue(m, out var s) || s.Block == BlockKind.None;

        /// <summary>Allowed vote-screen layouts (HvH, SvA, CvA, HvHvA), in button order.</summary>
        public List<GameModeExt.ETeamsVersus> AllowedVotable => Modes.Votable.Where(IsAllowed).ToList();
        public List<GameModeExt.ETeamsVersus> BlockedVotable => Modes.Votable.Where(m => !IsAllowed(m)).ToList();

        /// <summary>Modes.Display, except the 3-way button on a 2v1 map is "2v1".</summary>
        public string Display(GameModeExt.ETeamsVersus m)
            => m == GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS && TwoVsOne ? "2v1" : Modes.Display(m);

        public string JoinDisplay(IEnumerable<GameModeExt.ETeamsVersus> modes)
        {
            var parts = modes.Select(Display).ToList();
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        /// <summary>
        /// The 3-way button plays 2v1 and an HvHvA blocklist entry was not applied to it: "map exclusion HvHvA not applied
        /// (2v1 map)" / "server block HvHvA not applied (2v1 map)"; "" otherwise.
        /// </summary>
        public string WaivedText(GameModeExt.ETeamsVersus m)
        {
            if (!Status.TryGetValue(m, out var s) || s.Waived == BlockKind.None) return "";
            return (s.Waived == BlockKind.Map ? "map exclusion" : "server block") + " HvHvA not applied (2v1 map)";
        }

        /// <summary>
        /// "HvHvA is excluded on Citadel, but Citadel is a 2v1 map: the 3-way button plays 2v1 and stays votable. Remove
        /// Citadel from the 2v1 list to block it." when an HvHvA blocklist entry is waived this round; "" otherwise.
        /// </summary>
        public string WaivedNote()
        {
            if (!Status.TryGetValue(Modes.ThreeWay, out var s) || s.Waived == BlockKind.None) return "";
            return Rules.TwoVsOneBlockNote(Map, s.Waived == BlockKind.Server);
        }

        public string Reason(GameModeExt.ETeamsVersus m)
        {
            if (!Status.TryGetValue(m, out var s)) return "";
            switch (s.Block)
            {
                case BlockKind.Server: return "disabled on this server";
                case BlockKind.Map: return "excluded on this map";
                case BlockKind.Ceiling:
                    if (s.Max == 0) return "ceiling 0/" + Window + ", never counted";
                    return "ceiling " + s.Max + "/" + Window + " reached" + (Modes.IsHvA(m) ? ", Sol + Centauri vs Aliens together" : "");
                default: return "";
            }
        }

        /// <summary>
        /// "Sol vs Aliens (excluded on this map), 3-way (ceiling 4/20 reached)". Sol vs Aliens and Centauri vs Aliens
        /// blocked for the same reason are one entry: "Sol + Centauri vs Aliens (ceiling 11/20 reached together)".
        /// </summary>
        public string NotCountedText(IList<GameModeExt.ETeamsVersus> blocked)
        {
            var parts = new List<string>();
            bool both = blocked.Contains(Modes.SvA) && blocked.Contains(Modes.CvA) && Reason(Modes.SvA) == Reason(Modes.CvA);
            foreach (var m in blocked)
            {
                if (both && m == Modes.CvA) continue;
                if (both && m == Modes.SvA)
                {
                    var s = Status[m];
                    string why = s.Block == BlockKind.Ceiling && s.Max > 0 ? "ceiling " + s.Max + "/" + Window + " reached together" : Reason(m);
                    parts.Add("Sol + Centauri vs Aliens (" + why + ")");
                    continue;
                }
                parts.Add(Display(m) + " (" + Reason(m) + ")");
            }
            return string.Join(", ", parts);
        }

        /// <summary>"last 20: HvHvA 4/6 (2v1 1), HvA 10/11 (Sol 8, Cent 2), HvH 6/7" (count/max, "-" = unlimited).</summary>
        public string WindowText()
        {
            var parts = new List<string>();
            foreach (var cat in Modes.Categories)
            {
                var s = Status[cat];
                string part = Modes.CategoryShort(cat) + " " + s.CatCount + "/" + (s.Max < 0 ? "-" : s.Max.ToString());
                if (cat == Modes.ThreeWay && TwoVsOneInWindow > 0) part += " (2v1 " + TwoVsOneInWindow + ")";
                if (cat == Modes.SvA && s.CatCount > 0) part += " (Sol " + Status[Modes.SvA].Count + ", Cent " + Status[Modes.CvA].Count + ")";
                parts.Add(part);
            }
            string head = Seen >= Window ? "last " + Window : "last " + Seen + " (window " + Window + ")";
            return head + ": " + string.Join(", ", parts);
        }
    }

    internal static class Rules
    {
        /// <summary>
        /// Shared wording for status, ModUI and the admin edit replies: an HvHvA block that a 2v1 map overrides.
        /// </summary>
        public static string TwoVsOneBlockNote(string map, bool serverWide)
        {
            string m = string.IsNullOrEmpty(map) ? "this map" : map;
            return "HvHvA is " + (serverWide ? "disabled on this server" : "excluded on " + m) + ", but " + m +
                   " is a 2v1 map: the 3-way button plays 2v1 and stays votable. Remove " + m + " from the 2v1 list to block it.";
        }

        public static Evaluation Evaluate(VersusConfig cfg, string map) => Evaluate(cfg, map, null);

        /// <summary>
        /// <paramref name="allianceAvailable"/>: null = ask AllianceBridge (Si_Alliance loaded?); a value overrides it
        /// (offline checks, tools/RulesCheck).
        ///
        /// 2v1 rule: when the round is 2v1 (2v1 map and Si_Alliance available) the HvHvA entries of blocked_modes and
        /// map_blocked_modes do not apply to the 3-way button - it plays Sol + Centauri vs Aliens, which the admin chose
        /// by listing the map in map_two_vs_one. Only the HvHvA ceiling still applies. Without Si_Alliance the button is
        /// a normal 3-way and the blocklists apply as usual.
        /// </summary>
        internal static Evaluation Evaluate(VersusConfig cfg, string map, bool? allianceAvailable)
        {
            var ev = new Evaluation { Map = map ?? "", Window = cfg.WindowRounds };
            var counts = History.WindowCounts(cfg.WindowRounds, out ev.Seen, out ev.TwoVsOneInWindow);
            ev.TwoVsOneMap = cfg.IsTwoVsOneMap(map);
            if (ev.TwoVsOneMap) ev.AllianceMissing = !(allianceAvailable ?? AllianceBridge.Available);
            cfg.MapBlocked.TryGetValue(Modes.MapKey(map), out var mapSet);

            var ceilingHit = new HashSet<GameModeExt.ETeamsVersus>();
            foreach (var m in Modes.All)
            {
                // HvA ceiling: Sol vs Aliens and Centauri vs Aliens are counted together against one max
                int cat = 0;
                foreach (var cm in Modes.InCategory(m)) cat += counts[cm];
                var s = new ModeStatus { Mode = m, Count = counts[m], CatCount = cat, Max = cfg.MaxFor(m) };
                bool server = cfg.Blocked.Contains(m), onMap = mapSet != null && mapSet.Contains(m);
                if (m == Modes.ThreeWay && ev.TwoVsOne && (server || onMap))
                {
                    // 2v1 round: the HvHvA blocklists do not apply to the 3-way button (see summary)
                    s.Waived = onMap ? BlockKind.Map : BlockKind.Server;
                    server = onMap = false;
                }
                if (server) s.Block = BlockKind.Server;
                else if (onMap) s.Block = BlockKind.Map;
                else if (s.Max >= 0 && s.CatCount >= s.Max) { s.Block = BlockKind.Ceiling; ceilingHit.Add(m); }
                ev.Status[m] = s;
            }

            if (!cfg.Enabled) { ev.FilterOff = true; return ev; }

            if (!Modes.Votable.Any(m => ev.Status[m].Block == BlockKind.None) && ceilingHit.Count > 0)
            {
                // The ceilings (together with the blocklists) would leave no layout: drop the ceiling layer this round.
                ev.CeilingIgnored = true;
                foreach (var m in ceilingHit) ev.Status[m].Block = BlockKind.None;
            }
            if (!Modes.Votable.Any(m => ev.Status[m].Block == BlockKind.None))
                ev.FilterOff = true;   // blocklists alone exclude everything: never strand the round, filter nothing
            return ev;
        }

        /// <summary>
        /// Re-tally of the vote counting only allowed layouts: most votes wins, ties random; no allowed vote = random allowed
        /// vote-screen layout, Centauri vs Aliens included (vanilla's own zero-vote pick is Random.Range(1,4) = 1..3).
        /// TeamsVersusVotes is only read, never modified.
        /// </summary>
        public static GameModeExt.ETeamsVersus Retally(Evaluation ev, Dictionary<Player, GameModeExt.ETeamsVersus>? votes, out int winnerVotes)
        {
            winnerVotes = 0;
            var tally = new Dictionary<GameModeExt.ETeamsVersus, int>();
            if (votes != null)
            {
                foreach (var kv in votes)
                {
                    var m = kv.Value;
                    if (m == GameModeExt.ETeamsVersus.NONE || !ev.IsAllowed(m)) continue;
                    tally.TryGetValue(m, out int n);
                    tally[m] = n + 1;
                }
            }
            if (tally.Count > 0)
            {
                int best = tally.Values.Max();
                var tied = tally.Where(kv => kv.Value == best).Select(kv => kv.Key).ToList();
                winnerVotes = best;
                return tied[UnityEngine.Random.Range(0, tied.Count)];
            }
            var pool = ev.AllowedVotable;
            if (pool.Count == 0) pool = Modes.Votable.ToList();
            return pool[UnityEngine.Random.Range(0, pool.Count)];
        }

        public static int VotesFor(Dictionary<Player, GameModeExt.ETeamsVersus>? votes, GameModeExt.ETeamsVersus m)
        {
            if (votes == null) return 0;
            int n = 0;
            foreach (var kv in votes) if (kv.Value == m) n++;
            return n;
        }
    }
}
