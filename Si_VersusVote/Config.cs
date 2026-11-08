using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;

namespace Si_VersusVote
{
    /// <summary>UserData/cfg/VersusVote/Si_VersusVote_Config.json. Mode names: HvH, HvA, HvHvA (alias 3way / 3-way), H2vA or the enum names.</summary>
    public class VersusConfig
    {
        /// <summary>Master switch: false = the vote result is never touched (history is still recorded).</summary>
        [JsonProperty("enabled")] public bool Enabled = true;
        /// <summary>false = no chat output at all (console log only).</summary>
        [JsonProperty("announce")] public bool Announce = true;
        /// <summary>Layouts that never count on this server.</summary>
        [JsonProperty("blocked_modes")] public List<string> BlockedModes = new List<string>();
        /// <summary>Per map (mapcycle.txt spelling, case-insensitive): layouts that never count on that map.</summary>
        [JsonProperty("map_blocked_modes")] public Dictionary<string, List<string>> MapBlockedModes = new Dictionary<string, List<string>>();
        /// <summary>Maps where the 3-way button (HvHvA) is played as 2v1: Sol + Centauri allied (Si_Alliance) vs Aliens.
        /// HvHvA rules (blocklists, ceiling) still apply to it: blocked HvHvA = no 2v1 either.</summary>
        [JsonProperty("map_two_vs_one")] public List<string> MapTwoVsOne = new List<string> { "IndustrialQuarter" };
        /// <summary>Max share of the last window_rounds rounds per layout (0 = never, absent = unlimited).</summary>
        [JsonProperty("ceilings")] public Dictionary<string, double> Ceilings = new Dictionary<string, double>
        {
            { "HvHvA", 0.20 },
            { "HvA", 0.50 },
            { "HvH", 0.30 },
        };
        /// <summary>Number of most recent rounds (all maps) the ceilings look at.</summary>
        [JsonProperty("window_rounds")] public int WindowRounds = 20;
        /// <summary>Played-rounds history (relative = next to this config).</summary>
        [JsonProperty("history_file")] public string HistoryFile = "history.json";
        /// <summary>Seconds after the layout vote opens before the "counted this round" line (loading clients miss earlier lines).</summary>
        [JsonProperty("announce_delay_s")] public float AnnounceDelayS = 3f;
        /// <summary>Repeat the line this many seconds later if nobody has voted yet (0 = no repeat).</summary>
        [JsonProperty("announce_repeat_s")] public float AnnounceRepeatS = 10f;
        [JsonProperty("debug")] public bool Debug = false;

        // ------------------------------------------------------------------ parsed (not serialized)
        [JsonIgnore] public HashSet<GameModeExt.ETeamsVersus> Blocked = new HashSet<GameModeExt.ETeamsVersus>();
        [JsonIgnore] public Dictionary<string, HashSet<GameModeExt.ETeamsVersus>> MapBlocked = new Dictionary<string, HashSet<GameModeExt.ETeamsVersus>>();
        [JsonIgnore] public Dictionary<GameModeExt.ETeamsVersus, double> CeilingOf = new Dictionary<GameModeExt.ETeamsVersus, double>();
        [JsonIgnore] public HashSet<string> TwoVsOneMaps = new HashSet<string>();

        /// <summary>The map is listed in map_two_vs_one (case/space-insensitive, see Modes.MapKey).</summary>
        public bool IsTwoVsOneMap(string? map) => !string.IsNullOrEmpty(map) && TwoVsOneMaps.Contains(Modes.MapKey(map));

        public static string Dir => System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "cfg", "VersusVote");
        public static string Path => System.IO.Path.Combine(Dir, "Si_VersusVote_Config.json");

        [JsonIgnore] public string HistoryPath
        {
            get
            {
                string f = string.IsNullOrWhiteSpace(HistoryFile) ? "history.json" : HistoryFile.Trim();
                return System.IO.Path.IsPathRooted(f) ? f : System.IO.Path.Combine(Dir, f);
            }
        }

        static readonly JsonSerializerSettings ReadSettings = new JsonSerializerSettings
        {
            // replace (not merge into) the default lists/dictionaries, so a ceiling removed from the file is really gone
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };

        public static VersusConfig Load()
        {
            VersusConfig cfg = new VersusConfig();
            try
            {
                if (File.Exists(Path))
                {
                    string text = File.ReadAllText(Path);
                    cfg = JsonConvert.DeserializeObject<VersusConfig>(text, ReadSettings) ?? new VersusConfig();
                    MelonLogger.Msg("[VERSUS] Config loaded from " + Path);
                    if (!text.Contains("\"map_two_vs_one\""))
                    {
                        cfg.Save();   // 0.1.0 file: write the new key (default IndustrialQuarter) so it is visible and editable
                        MelonLogger.Msg("[VERSUS] Config upgraded: map_two_vs_one added (" + string.Join(", ", cfg.MapTwoVsOne ?? new List<string>()) + ")");
                    }
                }
                else
                {
                    cfg.Save();
                    MelonLogger.Msg("[VERSUS] Default config written to " + Path);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[VERSUS] Config load failed (" + ex.Message + "), using defaults");
                cfg = new VersusConfig();
            }
            cfg.Sanitize();
            return cfg;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception ex) { MelonLogger.Warning("[VERSUS] Config save failed: " + ex.Message); }
        }

        public void Sanitize()
        {
            if (BlockedModes == null) BlockedModes = new List<string>();
            if (MapBlockedModes == null) MapBlockedModes = new Dictionary<string, List<string>>();
            if (Ceilings == null) Ceilings = new Dictionary<string, double>();
            if (MapTwoVsOne == null) MapTwoVsOne = new List<string>();
            if (WindowRounds < 1) WindowRounds = 1;
            if (WindowRounds > 1000) WindowRounds = 1000;
            if (float.IsNaN(AnnounceDelayS) || AnnounceDelayS < 0f) AnnounceDelayS = 0f;
            if (AnnounceDelayS > 60f) AnnounceDelayS = 60f;
            if (float.IsNaN(AnnounceRepeatS) || AnnounceRepeatS < 0f) AnnounceRepeatS = 0f;
            if (AnnounceRepeatS > 120f) AnnounceRepeatS = 120f;

            Blocked = new HashSet<GameModeExt.ETeamsVersus>();
            foreach (string s in BlockedModes)
            {
                if (Modes.TryParse(s, out var m)) Blocked.Add(m);
                else MelonLogger.Warning("[VERSUS] blocked_modes: unknown layout '" + s + "' ignored (use HvH, HvA, HvHvA, H2vA)");
            }

            MapBlocked = new Dictionary<string, HashSet<GameModeExt.ETeamsVersus>>();
            foreach (var kv in MapBlockedModes)
            {
                string key = Modes.MapKey(kv.Key);
                if (key.Length == 0 || kv.Value == null) continue;
                if (!MapBlocked.TryGetValue(key, out var set)) { set = new HashSet<GameModeExt.ETeamsVersus>(); MapBlocked[key] = set; }
                foreach (string s in kv.Value)
                {
                    if (Modes.TryParse(s, out var m)) set.Add(m);
                    else MelonLogger.Warning("[VERSUS] map_blocked_modes[" + kv.Key + "]: unknown layout '" + s + "' ignored");
                }
            }

            TwoVsOneMaps = new HashSet<string>();
            MapTwoVsOne.RemoveAll(string.IsNullOrWhiteSpace);
            foreach (string m in MapTwoVsOne) TwoVsOneMaps.Add(Modes.MapKey(m));

            CeilingOf = new Dictionary<GameModeExt.ETeamsVersus, double>();
            foreach (var kv in Ceilings)
            {
                if (!Modes.TryParse(kv.Key, out var m)) { MelonLogger.Warning("[VERSUS] ceilings: unknown layout '" + kv.Key + "' ignored"); continue; }
                double c = kv.Value;
                if (double.IsNaN(c) || c < 0) c = 0;
                if (c >= 1) continue;   // 1.0 or more = no limit
                CeilingOf[m] = c;
            }
        }

        /// <summary>Max rounds of mode m in the window; -1 = unlimited. ceiling 0 = 0, else max(1, round(ceiling * window)).</summary>
        public int MaxFor(GameModeExt.ETeamsVersus m)
        {
            if (!CeilingOf.TryGetValue(m, out double c)) return -1;
            if (c <= 0) return 0;
            return Math.Max(1, (int)Math.Round(c * WindowRounds, MidpointRounding.AwayFromZero));
        }

        public string Summary()
        {
            var parts = new List<string>();
            foreach (var m in Modes.All)
            {
                int max = MaxFor(m);
                if (max >= 0) parts.Add(Modes.Short(m) + " " + max + "/" + WindowRounds);
            }
            var blocked = new List<string>();
            foreach (var m in Blocked) blocked.Add(Modes.Short(m));
            return "enabled " + Enabled + ", announce " + Announce + ", ceilings [" + string.Join(", ", parts) + "], blocked [" + string.Join(", ", blocked) +
                   "], map rules " + MapBlocked.Count.ToString(CultureInfo.InvariantCulture) + ", 2v1 maps [" + string.Join(", ", MapTwoVsOne) + "]";
        }
    }
}
