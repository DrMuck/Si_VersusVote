// Offline check of Si_VersusVote's layout rules (HvA category = Sol vs Aliens + Centauri vs Aliens) and of the
// history compatibility ("H2vA" rows = enum 4). Exit code 0 = all checks passed.
using System.Reflection;
using Newtonsoft.Json;

const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
var asm = typeof(Si_VersusVote.VersusConfig).Assembly;
Type T(string n) => asm.GetType("Si_VersusVote." + n, true)!;
int failed = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failed++; }

var SvA = GameModeExt.ETeamsVersus.HUMANS_VS_ALIENS;
var CvA = GameModeExt.ETeamsVersus.HUMANS2_VS_ALIENS;
var HvH = GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS;
var Three = GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS;

// ---- optional: a real history.json (dotnet run -c Release -- <path>): every row must parse
if (args.Length > 0 && File.Exists(args[0]))
{
    var real = JsonConvert.DeserializeObject<Si_VersusVote.HistoryFile>(File.ReadAllText(args[0]))!;
    Console.WriteLine("     " + args[0] + ": " + string.Join(", ", real.Rounds.GroupBy(r => r.Mode + " -> " + r.Parsed).Select(g => g.Key + " x" + g.Count())));
    Check(real.Rounds.All(r => r.Parsed != GameModeExt.ETeamsVersus.NONE), "every row of the real history parses");
}

// ---- history: old "H2vA" / "HvA" rows and new "SvA" / "CvA" rows
string json = "{\"rounds\":[" +
    string.Join(",", Enumerable.Repeat("{\"utc\":\"2026-11-09T10:00:00Z\",\"map\":\"Badlands\",\"mode\":\"SvA\"}", 7)) +
    ",{\"utc\":\"2026-11-09T10:00:00Z\",\"map\":\"Badlands\",\"mode\":\"HvA\"}" +
    ",{\"utc\":\"2026-11-09T10:00:00Z\",\"map\":\"WhisperingPlains\",\"mode\":\"H2vA\"}" +
    ",{\"utc\":\"2026-11-09T10:00:00Z\",\"map\":\"WhisperingPlains\",\"mode\":\"CvA\"},{\"utc\":\"2026-11-09T10:00:00Z\",\"map\":\"GreatErg\",\"mode\":\"CvA\"}" +
    string.Join("", Enumerable.Repeat(",{\"utc\":\"2026-11-09T10:00:00Z\",\"map\":\"NarakaCity\",\"mode\":\"HvH\"}", 5)) +
    string.Join("", Enumerable.Repeat(",{\"utc\":\"2026-11-09T10:00:00Z\",\"map\":\"CrimsonPeak\",\"mode\":\"HvHvA\"}", 4)) + "]}";
var hf = JsonConvert.DeserializeObject<Si_VersusVote.HistoryFile>(json)!;
var h2va = hf.Rounds.First(r => r.Mode == "H2vA");
Check(h2va.Parsed == CvA, "history row mode \"H2vA\" parses to enum 4 (HUMANS2_VS_ALIENS): " + h2va.Parsed);
Check(hf.Rounds.First(r => r.Mode == "HvA").Parsed == SvA, "history row mode \"HvA\" parses to enum 2");
Check(hf.Rounds.First(r => r.Mode == "CvA").Parsed == CvA, "history row mode \"CvA\" parses to enum 4");
Check(T("Modes").GetMethod("Short", Any)!.Invoke(null, new object[] { CvA }) as string == "CvA", "Modes.Short(4) = CvA (written to new rows)");
T("History").GetField("_data", Any)!.SetValue(null, hf);   // 8 SvA + 3 CvA + 5 HvH + 4 HvHvA = 20 rounds

// ---- config: HvA ceiling 0.55 of 20 = max 11
var cfg = new Si_VersusVote.VersusConfig();
cfg.Ceilings = new Dictionary<string, double> { { "HvHvA", 0.3 }, { "HvA", 0.55 }, { "HvH", 0.4 } };
cfg.Sanitize();
var evaluate = T("Rules").GetMethod("Evaluate", Any)!;
object ev = evaluate.Invoke(null, new object[] { cfg, "NarakaCity" })!;
var evT = ev.GetType();
bool Allowed(GameModeExt.ETeamsVersus m) => (bool)evT.GetMethod("IsAllowed", Any)!.Invoke(ev, new object[] { m })!;
string Reason(GameModeExt.ETeamsVersus m) => (string)evT.GetMethod("Reason", Any)!.Invoke(ev, new object[] { m })!;
Check(!Allowed(SvA) && !Allowed(CvA), "8 SvA + 3 CvA with HvA ceiling 0.55/20 (max 11): Sol vs Aliens AND Centauri vs Aliens blocked");
Check(Allowed(HvH) && Allowed(Three), "HvH 5/8 and HvHvA 4/6 still counted");
Console.WriteLine("     Reason(SvA): " + Reason(SvA));
Console.WriteLine("     WindowText:  " + evT.GetMethod("WindowText", Any)!.Invoke(ev, null));
string line = (string)T("VersusVoteMod").GetMethod("VoteOpenLine", Any)!.Invoke(null, new[] { ev })!;
Console.WriteLine("     Vote line:   " + line);
Check(line.Contains("Sol + Centauri vs Aliens (ceiling 11/20 reached together)"), "vote-open line groups both alien layouts");

// ---- ceiling 0.6 (max 12): both counted again
cfg.Ceilings["HvA"] = 0.6; cfg.Sanitize();
ev = evaluate.Invoke(null, new object[] { cfg, "NarakaCity" })!;
Check(Allowed(SvA) && Allowed(CvA), "HvA ceiling 0.6 (max 12): both alien layouts counted");

// ---- blocklists: "CvA" alone, "HvA" both, old "H2vA" = CvA
cfg.BlockedModes = new List<string> { "CvA" }; cfg.Sanitize();
ev = evaluate.Invoke(null, new object[] { cfg, "NarakaCity" })!;
Check(Allowed(SvA) && !Allowed(CvA), "blocked_modes [CvA]: only Centauri vs Aliens blocked");
Console.WriteLine("     Vote line:   " + T("VersusVoteMod").GetMethod("VoteOpenLine", Any)!.Invoke(null, new[] { ev }));
cfg.BlockedModes = new List<string>(); cfg.MapBlockedModes = new Dictionary<string, List<string>> { { "NarakaCity", new List<string> { "HvA" } } }; cfg.Sanitize();
ev = evaluate.Invoke(null, new object[] { cfg, "narakacity" })!;
Check(!Allowed(SvA) && !Allowed(CvA) && Allowed(HvH), "map_blocked_modes NarakaCity [HvA]: both alien layouts blocked on that map");
Console.WriteLine("     Vote line:   " + T("VersusVoteMod").GetMethod("VoteOpenLine", Any)!.Invoke(null, new[] { ev }));
cfg.MapBlockedModes = new Dictionary<string, List<string>>();

// ---- 3-way over its ceiling, everything else counted (the common case)
cfg.Ceilings = new Dictionary<string, double> { { "HvHvA", 0.2 }, { "HvA", 0.6 }, { "HvH", 0.4 } }; cfg.Sanitize();
ev = evaluate.Invoke(null, new object[] { cfg, "NarakaCity" })!;
Check(!Allowed(Three) && Allowed(SvA) && Allowed(CvA) && Allowed(HvH), "HvHvA 4/4 blocked, the other three counted");
Console.WriteLine("     Vote line:   " + T("VersusVoteMod").GetMethod("VoteOpenLine", Any)!.Invoke(null, new[] { ev }));

// ---- never strand the round: every layout over its ceiling -> ceiling layer dropped
cfg.Ceilings = new Dictionary<string, double> { { "HvHvA", 0.2 }, { "HvA", 0.5 }, { "HvH", 0.25 } }; cfg.Sanitize();
ev = evaluate.Invoke(null, new object[] { cfg, "NarakaCity" })!;
Check((bool)evT.GetField("CeilingIgnored", Any)!.GetValue(ev)! && Allowed(SvA) && Allowed(CvA) && Allowed(HvH) && Allowed(Three),
      "all four over their ceilings: ceiling layer dropped, all counted");

Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
return failed == 0 ? 0 : 1;
