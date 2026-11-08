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
        public int Max;           // -1 = unlimited
    }

    /// <summary>Which layouts count this round, and why the others do not.</summary>
    internal sealed class Evaluation
    {
        public string Map = "";
        public int Window;
        public int Seen;
        public bool CeilingIgnored;   // the ceilings would have left no layout: ceiling layer dropped for this round
        public bool FilterOff;        // the blocklists alone leave no layout (or the mod is disabled): nothing is filtered
        public readonly Dictionary<GameModeExt.ETeamsVersus, ModeStatus> Status = new Dictionary<GameModeExt.ETeamsVersus, ModeStatus>();

        public bool IsAllowed(GameModeExt.ETeamsVersus m)
            => FilterOff || !Status.TryGetValue(m, out var s) || s.Block == BlockKind.None;

        /// <summary>Allowed votable layouts (HvH, HvA, HvHvA), in enum order.</summary>
        public List<GameModeExt.ETeamsVersus> AllowedVotable => Modes.Votable.Where(IsAllowed).ToList();
        public List<GameModeExt.ETeamsVersus> BlockedVotable => Modes.Votable.Where(m => !IsAllowed(m)).ToList();

        public string Reason(GameModeExt.ETeamsVersus m)
        {
            if (!Status.TryGetValue(m, out var s)) return "";
            switch (s.Block)
            {
                case BlockKind.Server: return "disabled on this server";
                case BlockKind.Map: return "excluded on this map";
                case BlockKind.Ceiling: return s.Max == 0 ? "ceiling 0/" + Window + ", never counted" : "ceiling " + s.Max + "/" + Window + " reached";
                default: return "";
            }
        }

        /// <summary>"last 20: HvHvA 4/4, HvA 7/10, HvH 5/6" (count/max, "-" = unlimited).</summary>
        public string WindowText()
        {
            var parts = new List<string>();
            foreach (var m in new[] { GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS, GameModeExt.ETeamsVersus.HUMANS_VS_ALIENS,
                                      GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS, GameModeExt.ETeamsVersus.HUMANS2_VS_ALIENS })
            {
                var s = Status[m];
                if (m == GameModeExt.ETeamsVersus.HUMANS2_VS_ALIENS && s.Count == 0 && s.Max < 0) continue;
                parts.Add(Modes.Short(m) + " " + s.Count + "/" + (s.Max < 0 ? "-" : s.Max.ToString()));
            }
            string head = Seen >= Window ? "last " + Window : "last " + Seen + " (window " + Window + ")";
            return head + ": " + string.Join(", ", parts);
        }
    }

    internal static class Rules
    {
        public static Evaluation Evaluate(VersusConfig cfg, string map)
        {
            var ev = new Evaluation { Map = map ?? "", Window = cfg.WindowRounds };
            var counts = History.WindowCounts(cfg.WindowRounds, out ev.Seen);
            cfg.MapBlocked.TryGetValue(Modes.MapKey(map), out var mapSet);

            var ceilingHit = new HashSet<GameModeExt.ETeamsVersus>();
            foreach (var m in Modes.All)
            {
                var s = new ModeStatus { Mode = m, Count = counts[m], Max = cfg.MaxFor(m) };
                if (cfg.Blocked.Contains(m)) s.Block = BlockKind.Server;
                else if (mapSet != null && mapSet.Contains(m)) s.Block = BlockKind.Map;
                else if (s.Max >= 0 && s.Count >= s.Max) { s.Block = BlockKind.Ceiling; ceilingHit.Add(m); }
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
        /// votable layout (vanilla: Random.Range(1,4)). TeamsVersusVotes is only read, never modified.
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
