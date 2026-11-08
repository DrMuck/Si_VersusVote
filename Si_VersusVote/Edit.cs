using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MelonLoader;
using MelonLoader.Utils;

namespace Si_VersusVote
{
    /// <summary>
    /// Live config edits (chat !versus set / block / mapblock / 2v1 and the Si_ModUI page, which only sends those chat
    /// commands). Every successful edit writes the JSON, re-parses it and rebuilds the ModUI page; the filter evaluates
    /// the config on every call, so the change applies at once (also to a vote that is running). The admin check is done
    /// by the caller (Commands, Power.Generic) - nothing here is reachable without it.
    /// </summary>
    internal static class Edit
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal sealed class Key
        {
            public string Name = "", Label = "", Desc = "";
            public bool IsBool, IsInt;
            public double Min, Max;
            public Func<VersusConfig, double> Get = _ => 0;
            public Action<VersusConfig, double> Set = (_, __) => { };
        }

        public static readonly Key[] Keys =
        {
            new Key { Name = "enabled", Label = "Layout filter", Desc = "Master switch of the filter (off = the vote result is never replaced; history and 2v1 still work).", IsBool = true,
                      Get = c => c.Enabled ? 1 : 0, Set = (c, v) => c.Enabled = v != 0 },
            new Key { Name = "announce", Label = "Chat announcements", Desc = "Vote-open line, replacement line, private voter notes (command replies always show).", IsBool = true,
                      Get = c => c.Announce ? 1 : 0, Set = (c, v) => c.Announce = v != 0 },
            new Key { Name = "window_rounds", Label = "Ceiling window (rounds)", Desc = "How many recent rounds (all maps) the ceilings look at.", IsInt = true, Min = 1, Max = 1000,
                      Get = c => c.WindowRounds, Set = (c, v) => c.WindowRounds = (int)v },
            new Key { Name = "announce_delay_s", Label = "Vote line delay (s)", Desc = "Delay of the vote-open line after the vote phase starts.", Min = 0, Max = 60,
                      Get = c => c.AnnounceDelayS, Set = (c, v) => c.AnnounceDelayS = (float)v },
            new Key { Name = "announce_repeat_s", Label = "Vote line repeat (s)", Desc = "Repeat the line this much later if nobody voted (0 = off).", Min = 0, Max = 120,
                      Get = c => c.AnnounceRepeatS, Set = (c, v) => c.AnnounceRepeatS = (float)v },
            new Key { Name = "debug", Label = "Debug logging", Desc = "Extra console logging.", IsBool = true,
                      Get = c => c.Debug ? 1 : 0, Set = (c, v) => c.Debug = v != 0 },
        };

        public static string Show(Key k, VersusConfig c)
        {
            double v = k.Get(c);
            if (k.IsBool) return v != 0 ? "on" : "off";
            return k.IsInt ? ((long)v).ToString(Inv) : v.ToString("0.##", Inv);
        }

        public static string Range(Key k) => k.IsBool ? "on|off" : k.Min.ToString("0.##", Inv) + ".." + k.Max.ToString("0.##", Inv);

        /// <summary>"0.2" or "none" (no ceiling) for the ModUI value row and the listing.</summary>
        public static string ShowCeiling(VersusConfig c, GameModeExt.ETeamsVersus m)
            => c.CeilingOf.TryGetValue(m, out double v) ? v.ToString("0.###", Inv) : "none";

        static bool TryBool(string raw, out bool on)
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "on": case "true": case "1": case "yes": on = true; return true;
                case "off": case "false": case "0": case "no": on = false; return true;
            }
            on = false;
            return false;
        }

        // ------------------------------------------------------------------ set <key> <value>

        /// <summary>"set" with no args lists every key; "set &lt;key&gt; &lt;value&gt;" changes one. Returns the reply lines.</summary>
        public static List<string> Set(string who, string[] args)
        {
            var c = VersusVoteMod.Cfg;
            var outp = new List<string>();
            if (args.Length == 0)
            {
                outp.Add("Keys: " + string.Join(" | ", Keys.Select(k => k.Name + "=" + Show(k, c) + " (" + Range(k) + ")")));
                outp.Add("Ceilings: " + string.Join(" | ", Modes.Votable.Select(m => "ceiling_" + Modes.Short(m).ToLowerInvariant() + "=" + ShowCeiling(c, m))) +
                         " (0..1 share of the window, 'none' = unlimited, '20%' works)");
                outp.Add("Usage: !versus set <key> <value> | !versus block <mode> | !versus mapblock add|remove <map> <mode> | !versus 2v1 add|remove <map>");
                return outp;
            }
            string keyName = args[0].Trim();
            if (keyName.StartsWith("ceiling", StringComparison.OrdinalIgnoreCase))
            {
                // ceiling_hvhva 0.2 | ceiling HvHvA 0.2
                string modeText = keyName.Length > 7 ? keyName.Substring(7).TrimStart('_', '-', '.') : (args.Length > 1 ? args[1] : "");
                var rest = keyName.Length > 7 ? args.Skip(1).ToArray() : args.Skip(2).ToArray();
                outp.Add(SetCeiling(who, modeText, rest.Length > 0 ? rest[0] : ""));
                return outp;
            }
            var key = Keys.FirstOrDefault(k => string.Equals(k.Name, keyName, StringComparison.OrdinalIgnoreCase));
            if (key == null) { outp.Add("Unknown key '" + keyName + "'. !versus set lists the keys."); return outp; }
            if (args.Length < 2) { outp.Add(key.Name + " = " + Show(key, c) + " (" + Range(key) + "). !versus set " + key.Name + " <value>"); return outp; }

            string raw = args[1].Trim();
            double value;
            if (key.IsBool)
            {
                if (!TryBool(raw, out bool on)) { outp.Add(key.Name + " takes on or off."); return outp; }
                value = on ? 1 : 0;
            }
            else
            {
                string num = raw.TrimEnd('s', 'S');
                bool relative = num.StartsWith("+") || num.StartsWith("-");
                if (!double.TryParse(num, NumberStyles.Float, Inv, out double parsed) || double.IsNaN(parsed) || double.IsInfinity(parsed))
                {
                    outp.Add("'" + raw + "' is not a number. " + key.Name + ": " + Range(key));
                    return outp;
                }
                value = relative ? key.Get(c) + parsed : parsed;
                if (key.IsInt) value = Math.Round(value, MidpointRounding.AwayFromZero);
                if (value < key.Min || value > key.Max)
                {
                    outp.Add(key.Name + " must be within " + Range(key) + " (got " + value.ToString("0.##", Inv) + "). Unchanged: " + Show(key, c) + ".");
                    return outp;
                }
            }
            string before = Show(key, c);
            key.Set(c, value);
            Commit(who, key.Name + ": " + before + " -> " + Show(key, c));
            outp.Add(key.Label + ": " + before + " -> " + Show(key, c) + " (saved, live).");
            return outp;
        }

        static string SetCeiling(string who, string modeText, string raw)
        {
            var c = VersusVoteMod.Cfg;
            if (!Modes.TryParse(modeText, out var m)) return "Unknown layout '" + modeText + "'. Use ceiling_hvh, ceiling_hva or ceiling_hvhva.";
            if (raw.Length == 0) return "ceiling_" + Modes.Short(m).ToLowerInvariant() + " = " + ShowCeiling(c, m) + " (max " + MaxText(c, m) + "). Value 0..1, 'none' = unlimited.";
            double? v;
            string r = raw.Trim().ToLowerInvariant();
            if (r == "none" || r == "off" || r == "-" || r == "unlimited" || r == "no") v = null;
            else
            {
                bool pct = r.EndsWith("%");
                if (!double.TryParse(r.TrimEnd('%'), NumberStyles.Float, Inv, out double d) || double.IsNaN(d) || double.IsInfinity(d))
                    return "'" + raw + "' is not a number. Ceiling: 0..1 (share of the window), '20%' or 'none'.";
                if (pct) d /= 100.0;
                if (d < 0 || d > 1) return "A ceiling is a share 0..1 (or 0%..100%); got " + raw + ". 'none' = unlimited.";
                v = d >= 1 ? (double?)null : d;   // 1.0 = no limit
            }
            string before = ShowCeiling(c, m);
            foreach (var k in c.Ceilings.Keys.ToList())
                if (Modes.TryParse(k, out var km) && km == m) c.Ceilings.Remove(k);
            if (v != null) c.Ceilings[Modes.Short(m)] = Math.Round(v.Value, 4);
            Commit(who, "ceiling " + Modes.Short(m) + ": " + before + " -> " + ShowCeiling(c, m));
            return "Ceiling " + Modes.Short(m) + ": " + before + " -> " + ShowCeiling(c, m) + " (max " + MaxText(c, m) + "; saved, live).";
        }

        static string MaxText(VersusConfig c, GameModeExt.ETeamsVersus m)
        {
            int max = c.MaxFor(m);
            return max < 0 ? "unlimited" : max + " of " + c.WindowRounds;
        }

        // ------------------------------------------------------------------ block <mode> [on|off]

        public static string Block(string who, string[] args)
        {
            var c = VersusVoteMod.Cfg;
            if (args.Length == 0)
                return "Disabled on this server: " + (c.Blocked.Count == 0 ? "none" : string.Join(", ", c.Blocked.Select(Modes.Short))) + ". !versus block <mode> [on|off] toggles.";
            if (!Modes.TryParse(args[0], out var m)) return "Unknown layout '" + args[0] + "' (HvH, HvA, HvHvA).";
            bool now = c.Blocked.Contains(m);
            bool want = !now;
            if (args.Length > 1 && !TryBool(args[1], out want)) return "!versus block " + Modes.Short(m) + " on|off";
            if (want == now) return Modes.Short(m) + " is already " + (now ? "disabled" : "allowed") + " on this server.";
            c.BlockedModes.RemoveAll(s => Modes.TryParse(s, out var x) && x == m);
            if (want) c.BlockedModes.Add(Modes.Short(m));
            Commit(who, "blocked_modes " + Modes.Short(m) + " -> " + (want ? "blocked" : "allowed"));
            return Modes.Short(m) + (want ? " is now disabled on this server" : " is allowed again on this server") + " (saved, live)." +
                   (want && m == GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS && c.TwoVsOneMaps.Count > 0 ? " This also blocks 2v1." : "");
        }

        // ------------------------------------------------------------------ mapblock list|add|remove

        public static string MapBlock(string who, string[] args)
        {
            var c = VersusVoteMod.Cfg;
            string op = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            if (op == "list" || op == "")
                return "Per-map exclusions: " + (MapRulesText(c) is string t && t.Length > 0 ? t : "none") + ". !versus mapblock add|remove <map> <mode>";
            if (op != "add" && op != "remove" && op != "del" && op != "rm") return "!versus mapblock [list] | add <map> <mode> | remove <map> [mode]";
            bool add = op == "add";
            if (args.Length < 2) return "!versus mapblock " + (add ? "add <map> <mode>" : "remove <map> [mode]") + " (also <map>:<mode>)";
            string mapText = args[1];
            string modeText = args.Length > 2 ? args[2] : "";
            int colon = mapText.IndexOf(':');
            if (colon > 0) { modeText = mapText.Substring(colon + 1); mapText = mapText.Substring(0, colon); }

            GameModeExt.ETeamsVersus m = GameModeExt.ETeamsVersus.NONE;
            if (modeText.Length > 0 && !Modes.TryParse(modeText, out m)) return "Unknown layout '" + modeText + "' (HvH, HvA, HvHvA).";
            if (add && m == GameModeExt.ETeamsVersus.NONE) return "!versus mapblock add <map> <mode>";

            string mapKey = Modes.MapKey(mapText);
            if (mapKey.Length == 0) return "No map given.";
            string existingKey = c.MapBlockedModes.Keys.FirstOrDefault(k => Modes.MapKey(k) == mapKey) ?? "";

            if (add)
            {
                string name = existingKey.Length > 0 ? existingKey : Maps.Canonical(mapText, out _);
                if (!c.MapBlockedModes.TryGetValue(name, out var list) || list == null) { list = new List<string>(); c.MapBlockedModes[name] = list; }
                if (list.Any(s => Modes.TryParse(s, out var x) && x == m)) return Modes.Short(m) + " is already excluded on " + name + ".";
                list.Add(Modes.Short(m));
                Commit(who, "map_blocked_modes " + name + " + " + Modes.Short(m));
                Maps.Canonical(mapText, out bool known);
                return Modes.Short(m) + " is now excluded on " + name + " (saved, live)." + (known || existingKey.Length > 0 ? "" : " Note: '" + name + "' is not a map this server knows - check the spelling.");
            }
            if (existingKey.Length == 0) return "No exclusions for " + mapText + ".";
            var cur = c.MapBlockedModes[existingKey] ?? new List<string>();
            if (m == GameModeExt.ETeamsVersus.NONE) cur.Clear();
            else if (cur.RemoveAll(s => Modes.TryParse(s, out var x) && x == m) == 0) return Modes.Short(m) + " is not excluded on " + existingKey + ".";
            if (cur.Count == 0) c.MapBlockedModes.Remove(existingKey);
            Commit(who, "map_blocked_modes " + existingKey + " - " + (m == GameModeExt.ETeamsVersus.NONE ? "all" : Modes.Short(m)));
            return (m == GameModeExt.ETeamsVersus.NONE ? "All exclusions" : Modes.Short(m) + " exclusion") + " removed on " + existingKey + " (saved, live).";
        }

        /// <summary>"NarakaCity: HvHvA; Badlands: HvH" from the parsed config.</summary>
        public static string MapRulesText(VersusConfig c)
            => string.Join("; ", c.MapBlockedModes.Where(kv => kv.Value != null && kv.Value.Count > 0)
                                                 .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                                 .Select(kv => kv.Key + ": " + string.Join(", ", kv.Value)));

        // ------------------------------------------------------------------ 2v1 list|add|remove

        public static string TwoVsOne(string who, string[] args)
        {
            var c = VersusVoteMod.Cfg;
            string op = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            if (op == "list" || op == "")
                return "2v1 maps (3-way button = Sol+Centauri allied vs Aliens): " + (c.MapTwoVsOne.Count == 0 ? "none" : string.Join(", ", c.MapTwoVsOne)) +
                       ". !versus 2v1 add|remove <map>" + (AllianceBridge.Available ? "" : " | Si_Alliance AllianceApi NOT loaded: 2v1 maps play a normal 3-way");
            if (op != "add" && op != "remove" && op != "del" && op != "rm") return "!versus 2v1 [list] | add <map> | remove <map>";
            if (args.Length < 2) return "!versus 2v1 " + op + " <map>";
            string key = Modes.MapKey(args[1]);
            if (key.Length == 0) return "No map given.";
            if (op == "add")
            {
                if (c.TwoVsOneMaps.Contains(key)) return args[1] + " is already a 2v1 map.";
                string name = Maps.Canonical(args[1], out bool known);
                c.MapTwoVsOne.Add(name);
                Commit(who, "map_two_vs_one + " + name);
                return name + " is now a 2v1 map: its 3-way button plays Sol+Centauri allied vs Aliens (saved, applies from the next layout vote)." +
                       (known ? "" : " Note: '" + name + "' is not a map this server knows - check the spelling.");
            }
            int n = c.MapTwoVsOne.RemoveAll(s => Modes.MapKey(s) == key);
            if (n == 0) return args[1] + " is not a 2v1 map.";
            Commit(who, "map_two_vs_one - " + args[1]);
            return args[1] + " is no longer a 2v1 map (saved; a round already running keeps its alliance).";
        }

        // ------------------------------------------------------------------ save / reload / reset

        public static string Save(string who)
        {
            var c = VersusVoteMod.Cfg;
            c.Sanitize();
            c.Save();
            MelonLogger.Msg("[VERSUS] config saved by " + who + ": " + c.Summary());
            VersusModUI.MarkDirty();
            return "Config written to " + Path.GetFileName(VersusConfig.Path) + ".";
        }

        public static string Reload(string who)
        {
            VersusVoteMod.Cfg = VersusConfig.Load();
            History.Load(VersusVoteMod.Cfg.HistoryPath);
            MelonLogger.Msg("[VERSUS] reloaded by " + who + ": " + VersusVoteMod.Cfg.Summary());
            VersusModUI.MarkDirty();
            return "Config reloaded (" + History.Rounds.Count + " rounds in history).";
        }

        public static string ResetHistory(string who)
        {
            int n = History.Reset();
            MelonLogger.Msg("[VERSUS] history reset by " + who + " (" + n + " rounds, old file kept as .bak)");
            VersusModUI.MarkDirty();
            return "History cleared (" + n + " rounds; backup " + Path.GetFileName(VersusVoteMod.Cfg.HistoryPath) + ".bak).";
        }

        static void Commit(string who, string change)
        {
            var c = VersusVoteMod.Cfg;
            c.Sanitize();
            c.Save();
            MelonLogger.Msg("[VERSUS] " + who + " changed " + change + " | " + c.Summary());
            VersusModUI.MarkDirty();
        }
    }

    /// <summary>Map names the server knows: GameDatabase levels (multiplayer, enabled) plus the UserData/cfg/Spawns folders.</summary>
    internal static class Maps
    {
        static List<string>? _cache;
        static float _at = -1000f;

        public static List<string> Known()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_cache != null && now - _at < 60f) return _cache;
            var byKey = new Dictionary<string, string>();
            try
            {
                var levels = GameDatabase.Database?.AllLevels;
                if (levels != null)
                    foreach (var l in levels)
                    {
                        if (l == null || !l.Enabled || !l.IsMultiplayer || l.IsDeveloperOnly || string.IsNullOrEmpty(l.FileName)) continue;
                        string k = Modes.MapKey(l.FileName);
                        if (k.Length > 0 && !byKey.ContainsKey(k)) byKey[k] = l.FileName;
                    }
            }
            catch { }
            try
            {
                string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "cfg", "Spawns");
                if (Directory.Exists(dir))
                    foreach (string d in Directory.GetDirectories(dir))
                    {
                        string name = Path.GetFileName(d);
                        if (name.StartsWith("~") || name.StartsWith(".")) continue;
                        string k = Modes.MapKey(name);
                        if (k.Length > 0 && !byKey.ContainsKey(k)) byKey[k] = name;
                    }
            }
            catch { }
            _cache = byKey.Values.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            _at = now;
            return _cache;
        }

        /// <summary>The known spelling of a map name (case/space-insensitive), else the text as typed.</summary>
        public static string Canonical(string text, out bool known)
        {
            string k = Modes.MapKey(text);
            foreach (string m in Known())
                if (Modes.MapKey(m) == k) { known = true; return m; }
            known = false;
            return (text ?? "").Trim();
        }
    }
}
