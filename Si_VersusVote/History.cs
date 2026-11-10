using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MelonLoader;
using Newtonsoft.Json;

namespace Si_VersusVote
{
    public class RoundEntry
    {
        [JsonProperty("utc")] public string Utc = "";
        [JsonProperty("map")] public string Map = "";
        /// <summary>Modes.Short: HvH, SvA, CvA, HvHvA. Older rows: "HvA" (= SvA, enum 2) and "H2vA" (= CvA, enum 4) still parse.</summary>
        [JsonProperty("mode")] public string Mode = "";
        /// <summary>An HvHvA round played as 2v1 (Sol + Centauri allied vs Aliens); counts toward the HvHvA ceiling.</summary>
        [JsonProperty("two_vs_one", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool TwoVsOne;

        [JsonIgnore] public GameModeExt.ETeamsVersus Parsed
            => Modes.TryParse(Mode, out var m) ? m : GameModeExt.ETeamsVersus.NONE;
    }

    public class HistoryFile
    {
        [JsonProperty("rounds")] public List<RoundEntry> Rounds = new List<RoundEntry>();
    }

    /// <summary>Every played round (whole history kept for stats); only the last window_rounds count toward the ceilings.</summary>
    internal static class History
    {
        static HistoryFile _data = new HistoryFile();
        static string _path = "";

        public static IReadOnlyList<RoundEntry> Rounds => _data.Rounds;

        public static void Load(string path)
        {
            _path = path;
            _data = new HistoryFile();
            try
            {
                if (!File.Exists(path)) { MelonLogger.Msg("[VERSUS] No history yet (" + path + ")"); return; }
                _data = JsonConvert.DeserializeObject<HistoryFile>(File.ReadAllText(path)) ?? new HistoryFile();
                if (_data.Rounds == null) _data.Rounds = new List<RoundEntry>();
                _data.Rounds.RemoveAll(r => r == null || r.Parsed == GameModeExt.ETeamsVersus.NONE);
                MelonLogger.Msg("[VERSUS] History loaded: " + _data.Rounds.Count + " rounds from " + path);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[VERSUS] History unreadable (" + ex.Message + "), starting empty; old file kept as .corrupt");
                try { File.Copy(path, path + ".corrupt", true); } catch { }
                _data = new HistoryFile();
            }
        }

        public static void Add(string map, GameModeExt.ETeamsVersus mode, bool twoVsOne)
        {
            _data.Rounds.Add(new RoundEntry
            {
                Utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Map = map ?? "",
                Mode = Modes.Short(mode),
                TwoVsOne = twoVsOne && mode == GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS,
            });
            Save();
        }

        /// <summary>2v1 withdrawn after the round was recorded (Si_Alliance refused): clear the flag on the last entry.</summary>
        public static void ClearLastTwoVsOne()
        {
            if (_data.Rounds.Count == 0) return;
            var last = _data.Rounds[_data.Rounds.Count - 1];
            if (!last.TwoVsOne) return;
            last.TwoVsOne = false;
            Save();
        }

        /// <summary>Clears the history; the old file is kept as history.json.bak.</summary>
        public static int Reset()
        {
            int n = _data.Rounds.Count;
            try { if (File.Exists(_path)) File.Copy(_path, _path + ".bak", true); } catch { }
            _data = new HistoryFile();
            Save();
            return n;
        }

        static void Save()
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(_data, Formatting.Indented));
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
            }
            catch (Exception ex) { MelonLogger.Warning("[VERSUS] History save failed: " + ex.Message); }
        }

        /// <summary>Count per layout (1..4) over the last <paramref name="window"/> rounds (all maps). <paramref name="seen"/> = rounds actually in the window.</summary>
        public static Dictionary<GameModeExt.ETeamsVersus, int> WindowCounts(int window, out int seen) => WindowCounts(window, out seen, out _);

        /// <summary>Same, plus the 2v1 rounds in the window (they are also counted as HvHvA).</summary>
        public static Dictionary<GameModeExt.ETeamsVersus, int> WindowCounts(int window, out int seen, out int twoVsOne)
        {
            twoVsOne = 0;
            var counts = new Dictionary<GameModeExt.ETeamsVersus, int>();
            foreach (var m in Modes.All) counts[m] = 0;
            int start = Math.Max(0, _data.Rounds.Count - window);
            seen = _data.Rounds.Count - start;
            for (int i = start; i < _data.Rounds.Count; i++)
            {
                var m = _data.Rounds[i].Parsed;
                if (counts.ContainsKey(m)) counts[m]++;
                if (_data.Rounds[i].TwoVsOne) twoVsOne++;
            }
            return counts;
        }
    }
}
