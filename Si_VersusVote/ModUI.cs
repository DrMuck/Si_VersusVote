/*
 ModUI.cs - the Si_VersusVote page in Si_ModUI (optional; the mod works the same without it, nothing references
 Si_ModUI at compile time).

 Contract (Si_ModUI README, "Mod data", SiModUI/2): the ModUI server finds `public static string ModUIData()` by
 reflection on the assembly whose name is the mod id (Si_VersusVote, the assembly that registered /versus) and calls it
 on every frame build, every few seconds. The document is rebuilt when something changed (MarkDirty: config edit or
 reload, round recorded, 2v1 applied or withdrawn, restart, scene load) or after 5 s, and returned from a cache
 otherwise.

 Every widget only sends a /versus chat command. Admin gating, exactly as in Si_WaspHarvest:
   1. rows built with admin: true ("a": true) are merged by the ModUI server only into frames of players for whom
      Player.IsAdmin() is true, so other players never see them;
   2. /versus is a PLAYER command in the Admin Mod registry, so the Admin Mod does not gate it: Commands.Handle checks
      Player.CanAdminExecute(Power.Generic) for every edit sub-command on the server and answers "Admin only" otherwise.
      A modified client that sends "/versus set ..." anyway is refused there.
*/
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MelonLoader;
using ModUI;

namespace Si_VersusVote
{
    public static class VersusModUI
    {
        static string? _cache;
        static bool _dirty = true;
        static float _builtAt = -1000f;
        static bool _warned;

        internal static void MarkDirty() => _dirty = true;

        public static string? ModUIData()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!_dirty && _cache != null && now - _builtAt < 5f) return _cache;
            try
            {
                _cache = Build();
                _dirty = false;
                _builtAt = now;
            }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; MelonLogger.Warning("[VERSUS] ModUIData: " + ex); }
            }
            return _cache;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static string Build()
        {
            var c = VersusVoteMod.Cfg;
            string map = VersusVoteMod.CurrentMap();
            var ev = Rules.Evaluate(c, map);
            var x = ModUIExport.New().Enabled(c.Enabled || c.MapTwoVsOne.Count > 0);

            // ---------------- everyone
            foreach (string line in VersusVoteMod.StatusLines()) x.Info(line);

            var now = new List<string[]>();
            foreach (var m in Modes.Votable)
            {
                var s = ev.Status[m];
                string name = m == Modes.ThreeWay && ev.TwoVsOne ? "2v1 (3-way button)" : Modes.Display(m);
                string max = s.Max < 0 ? "-" : s.Max.ToString(Inv);
                // the two alien layouts share the HvA ceiling: own count plus the combined count/max ("8 (HvA 10/11)")
                string window = Modes.IsHvA(m) ? s.Count + " (HvA " + s.CatCount + "/" + max + ")" : s.Count + "/" + max;
                if (m == Modes.ThreeWay && ev.TwoVsOneInWindow > 0) window += " (2v1 " + ev.TwoVsOneInWindow + ")";
                string okNote = ev.CeilingIgnored && s.Block == BlockKind.None && s.Max >= 0 && s.CatCount >= s.Max ? "ceiling ignored" : ev.WaivedText(m);
                now.Add(new[] { name, ev.IsAllowed(m) ? "yes" : "no", ev.IsAllowed(m) ? okNote : ev.Reason(m), window });
            }
            x.Table("This round on " + (map.Length > 0 ? map : "?"), new[] { "Layout", "Counted", "Reason", "Last " + c.WindowRounds }, now.ToArray());

            var rounds = History.Rounds;
            if (rounds.Count > 0)
            {
                var rows = new List<string[]>();
                int hva = 0;
                foreach (var m in new[] { Modes.HvH, Modes.SvA, Modes.CvA })
                {
                    int n = rounds.Count(r => r.Parsed == m);
                    if (Modes.IsHvA(m)) hva += n;
                    rows.Add(new[] { m == Modes.HvH ? "HvH" : Modes.Display(m), n.ToString(Inv), Pct(n, rounds.Count) });
                }
                rows.Add(new[] { "Humans vs Aliens total", hva.ToString(Inv), Pct(hva, rounds.Count) });
                int three = rounds.Count(r => r.Parsed == Modes.ThreeWay);
                rows.Add(new[] { "HvHvA", three.ToString(Inv), Pct(three, rounds.Count) });
                int two = rounds.Count(r => r.TwoVsOne);
                if (two > 0 || c.MapTwoVsOne.Count > 0) rows.Add(new[] { "of which 2v1", two.ToString(Inv), Pct(two, rounds.Count) });
                x.Table("All " + rounds.Count + " rounds since " + rounds[0].Utc.Split('T')[0], new[] { "Layout", "Rounds", "Share" }, rows.ToArray());
            }

            var mapRules = c.MapBlockedModes.Where(kv => kv.Value != null && kv.Value.Count > 0)
                                            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                            .Select(kv => new[] { kv.Key, string.Join(", ", kv.Value) }).ToArray();
            if (mapRules.Length > 0) x.Table("Per-map exclusions", new[] { "Map", "Not counted" }, mapRules);
            x.Info("Server-wide disabled: " + (c.Blocked.Count == 0 ? "none" : string.Join(", ", Modes.Tokens(c.Blocked))) +
                   " | 2v1 maps: " + (c.MapTwoVsOne.Count == 0 ? "none" : string.Join(", ", c.MapTwoVsOne)));

            x.Report("/versus", "Status", "Which of the four layouts (Humans vs Humans, Sol vs Aliens, Centauri vs Aliens, 3-way) count this round and why, the window counts and the 2v1 state. Sol vs Aliens and Centauri vs Aliens share the HvA ceiling.");
            x.Report("/versus stats", "Statistics", "Layout shares over the whole history, all maps and per map: HvA = Sol vs Aliens + Centauri vs Aliens, 2v1 separately.");
            x.Report("/versus help", "Help", "The !versus commands.");

            // ---------------- admins (a: true; the handler checks Power.Generic again)
            x.OnOff("/versus set enabled", "Layout filter", "Master switch: off = the vote result is never replaced (history and 2v1 keep working).", true, c.Enabled);
            x.OnOff("/versus set announce", "Chat announcements", "Vote-open line, replacement line and private voter notes.", true, c.Announce);
            x.Value("/versus set window_rounds", "Ceiling window, rounds [1..1000]", "How many recent rounds (all maps) the ceilings look at.", true, c.WindowRounds.ToString(Inv));
            foreach (var m in Modes.Categories)
            {
                int max = c.MaxFor(m);
                string name = Modes.CategoryShort(m);
                string label = "Ceiling " + name + (m == Modes.ThreeWay ? " / 2v1" : m == Modes.SvA ? " (Sol vs Aliens + Centauri vs Aliens together)" : "") +
                               " [0..1 or none] (max " + (max < 0 ? "unlimited" : max + "/" + c.WindowRounds) + ")";
                x.Value("/versus set ceiling_" + name.ToLowerInvariant(), label,
                        "Max share of the last window_rounds rounds. 0 = never, none = unlimited, 20% also works." +
                        (m == Modes.ThreeWay ? " 2v1 rounds count here." : m == Modes.SvA ? " One limit for both alien layouts: Sol vs Aliens and Centauri vs Aliens rounds are added up." : ""),
                        true, Edit.ShowCeiling(c, m));
            }
            x.Multi("/versus block", "Disabled on this server",
                    "Highlighted layouts never count on this server. Click toggles. HvA = Sol vs Aliens and Centauri vs Aliens, SvA / CvA = one of them. HvHvA does not block the 3-way button on 2v1 maps (it plays 2v1 there); remove a map from the 2v1 list to block it.", true,
                    string.Join(",", BlockedTokens(c.Blocked)), ("HvH", "HvH"), ("HvA", "HvA (both)"), ("SvA", "Sol vs Aliens"), ("CvA", "Centauri vs Aliens"), ("HvHvA", "HvHvA / 2v1"));

            var known = Maps.Known();
            var addChoices = new List<(string, string)>();
            foreach (string mp in known)
            {
                c.MapBlocked.TryGetValue(Modes.MapKey(mp), out var set);
                foreach (string tok in Modes.BlockTokens)
                    if (set == null || !Covered(set, tok)) addChoices.Add((mp + ":" + tok, mp + " - " + tok));
            }
            if (addChoices.Count > 0)
                x.Picker("/versus mapblock add", "Exclude a layout on a map", "That layout never counts on that map. HvA = Sol vs Aliens and Centauri vs Aliens, SvA / CvA = one of them. HvHvA on a 2v1 map does not block its 3-way button (it plays 2v1): to block 3-way there, remove the map from the 2v1 list and keep the HvHvA exclusion.", true, "choice", addChoices.ToArray());
            var removeChoices = new List<(string, string)>();
            foreach (var kv in c.MapBlockedModes.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!c.MapBlocked.TryGetValue(Modes.MapKey(kv.Key), out var set)) continue;
                foreach (string tok in BlockedTokens(set)) removeChoices.Add((kv.Key + ":" + tok, kv.Key + " - " + tok));
            }
            if (removeChoices.Count > 0)
                x.Picker("/versus mapblock remove", "Remove a map exclusion", "The layout counts on that map again (ceilings still apply).", true, "choice", removeChoices.ToArray());

            var twoAdd = known.Where(mp => !c.IsTwoVsOneMap(mp)).Select(mp => (mp, mp)).ToArray();
            if (twoAdd.Length > 0)
                x.Picker("/versus 2v1 add", "Add a 2v1 map", "On that map the 3-way button plays Sol+Centauri allied vs Aliens (needs Si_Alliance). HvHvA exclusions do not block it there, only the HvHvA ceiling does.", true, "choice", twoAdd);
            var twoRemove = c.MapTwoVsOne.Select(mp => (mp, mp)).ToArray();
            if (twoRemove.Length > 0)
                x.Picker("/versus 2v1 remove", "Remove a 2v1 map", "That map plays a normal 3-way again (HvHvA exclusions apply to it again).", true, "choice", twoRemove);

            x.Value("/versus set lone_money", "2v1 lone team start money, x [1..10]",
                    "The lone team's (Aliens') starting resources are multiplied by this in a 2v1 round, granted once the alliance is confirmed. 1 = off.", true,
                    c.TwoVsOne.LoneStartMoneyMult.ToString("0.##", Inv));
            x.Value("/versus set lone_units", "2v1 lone team starter units, x [1..10]",
                    "Si_StarterUnits multiplies every starter unit count of the lone team by this integer in a 2v1 round (extra units only up to the unit cap). 1 = off.", true,
                    c.TwoVsOne.LoneStarterUnitsMult.ToString(Inv));
            x.Value("/versus set announce_delay_s", "Vote line delay, s [0..60]", "Delay of the vote-open line after the vote starts.", true, c.AnnounceDelayS.ToString("0.##", Inv));
            x.Value("/versus set announce_repeat_s", "Vote line repeat, s [0..120]", "Repeat the line this much later if nobody voted (0 = off).", true, c.AnnounceRepeatS.ToString("0.##", Inv));
            x.OnOff("/versus set debug", "Debug logging", "Extra console logging.", true, c.Debug);
            x.Report("/versus set", "All settings", "List every setting with its value and range in chat.", true);
            x.Action("/versus save", "Save config", "Write the current settings to Si_VersusVote_Config.json (edits already save).", true);
            x.Action("/versus reload", "Reload config", "Re-read Si_VersusVote_Config.json and history.json.", true);
            x.Action("/versus reset", "Reset history", "Clear the round history (ceiling counts and stats). The old file is kept as history.json.bak.", true);
            return x.Build();
        }

        static string Pct(int n, int total) => (100.0 * n / Math.Max(1, total)).ToString("0", Inv) + "%";

        /// <summary>Every layout of the token is in the set (HvA needs both alien layouts).</summary>
        static bool Covered(ICollection<GameModeExt.ETeamsVersus> set, string token)
            => Modes.TryParseSet(token, out var ms) && ms.All(set.Contains);

        /// <summary>The block tokens fully covered by the set (HvA blocked = HvA, SvA and CvA highlighted / removable).</summary>
        static List<string> BlockedTokens(ICollection<GameModeExt.ETeamsVersus> set)
            => Modes.BlockTokens.Where(t => Covered(set, t)).ToList();
    }
}
