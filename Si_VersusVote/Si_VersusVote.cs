/*
 Si_VersusVote - server-only filter for the vanilla pre-round "Battle Layout" vote of MP_Strategy.

 Vanilla tallies GameModeExt.TeamsVersusVotes when the vote timer runs out and calls SetTeamVersusMode(result). This mod
 decides per round which layouts COUNT (global blocklist, per-map blocklist, share ceilings over the last N rounds) and,
 when the result is not allowed, re-tallies the same votes over the allowed layouts only and swaps the argument.
 The layout buttons are baked into the client prefab, so a blocked layout stays clickable; players are told in chat.

 2v1 (0.2.0): on maps listed in map_two_vs_one the 3-way button (HvHvA) is played as Sol + Centauri allied against the
 Aliens: the vanilla HvHvA layout is applied and Si_Alliance (AllianceApi.ForceAlliance, by reflection) locks the two
 human teams together for the round. Other mods read VersusApi.TwoVsOneActive (VersusApi.cs).

 Lone-team compensation (0.3.0, config section two_vs_one): the lone team's starting resources x lone_start_money_mult
 (LoneBonus.cs, after the vanilla grant in MP_Strategy.SpawnStructuresAndStartingResources, only once the alliance is
 confirmed), and VersusApi v2 (LoneTeam, LoneStarterUnitsMult, IsLoneTeam) for Si_StarterUnits.

 Four buttons (0.3.1): the vote screen offers HvH (1, Sol vs Centauri), Sol vs Aliens (2), Centauri vs Aliens (4) and
 3-way (3). Sol vs Aliens and Centauri vs Aliens form the category "HvA": one ceiling for both, "HvA" in a blocklist
 blocks both, "SvA" / "CvA" block one. History rows written as "H2vA" by older versions are Centauri vs Aliens.

 2v1 overrides HvHvA blocklists (0.3.2): in a 2v1 round (2v1 map + Si_Alliance loaded) the HvHvA entries of
 blocked_modes / map_blocked_modes do not apply to the 3-way button; only the HvHvA ceiling does (Rules.Evaluate).

   !layout | !versus            status: counted / not counted this round, counts in the window, 2v1 state
   !versus stats                shares from the whole history (all maps + per map, 2v1 separately)
   !versus set | block | mapblock | 2v1 | save | reload | reset    admin (Power.Generic), see Commands.cs / Edit.cs

 Si_ModUI page: VersusModUI.ModUIData() (ModUI.cs). Config + history: UserData/cfg/VersusVote/. Chat commands need the
 Admin Mod; the filter itself does not.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_VersusVote.VersusVoteMod), "Si_VersusVote", Si_VersusVote.VersusVoteMod.Version, "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]

namespace Si_VersusVote
{
    public class VersusVoteMod : MelonMod
    {
        public const string Version = "0.3.2";
        internal const string Tag = "<color=#7FD4FF>[LAYOUT]</color> ";
        internal static VersusConfig Cfg = new VersusConfig();

        static string _sceneName = "";
        static bool _wasInVote;
        static float _announceAt = -1f, _repeatAt = -1f;
        static float _nextPoll;
        static readonly HashSet<string> _warned = new HashSet<string>();   // "<player>|<mode>" per vote phase

        public override void OnInitializeMelon()
        {
            Cfg = VersusConfig.Load();
            History.Load(Cfg.HistoryPath);
            Patches.Apply(HarmonyInstance);
            MelonLogger.Msg("[VERSUS] Si_VersusVote v" + Version + " loaded: " + Cfg.Summary());
        }

        public override void OnLateInitializeMelon()
        {
            if (!RegisteredMelons.Any(m => m.Info.Name == "Admin Mod"))
            {
                MelonLogger.Warning("[VERSUS] Admin Mod not found: no !layout / !versus commands and no private vote notices (the filter still works)");
                return;
            }
            try { AdminBridge.Register(); MelonLogger.Msg("[VERSUS] !layout / !versus registered"); }
            catch (Exception ex) { MelonLogger.Error("[VERSUS] command registration failed: " + ex); }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName == "Intro" || sceneName == "MainMenu" || sceneName == "Loading" || sceneName.Length < 2) return;
            _sceneName = sceneName;
            VersusModUI.MarkDirty();
        }

        /// <summary>Current map in mapcycle.txt spelling: NetworkGameServer's map, else the last loaded scene.</summary>
        internal static string CurrentMap()
        {
            string m = "";
            try { m = NetworkGameServer.GetServerMap() ?? ""; } catch { }
            return string.IsNullOrEmpty(m) ? _sceneName : m;
        }

        internal static void OnRestart()
        {
            _wasInVote = false;   // a restart opens a fresh vote phase even if the state looked unchanged
            _announceAt = _repeatAt = -1f;
            _warned.Clear();
            VersusModUI.MarkDirty();
        }

        internal static bool MarkWarned(Player p, GameModeExt.ETeamsVersus m)
        {
            string key;
            try { key = p.PlayerID.ToString() + "|" + p.PlayerChannel + "|" + (int)m; } catch { key = p.GetHashCode() + "|" + (int)m; }
            return _warned.Add(key);
        }

        // ------------------------------------------------------------------ vote-open announcement

        public override void OnUpdate()
        {
            AllianceBridge.Tick();   // 2v1 alliance request after the layout postfix (one bool check otherwise)
            float now = Time.time;
            if (now < _nextPoll) return;
            _nextPoll = now + 0.5f;
            try
            {
                var mode = GameMode.CurrentGameMode as MP_Strategy;
                bool inVote = Patches.InScope(mode) && Patches.InVotePhase(mode!);
                if (!inVote)
                {
                    if (_wasInVote) { _announceAt = _repeatAt = -1f; }
                    _wasInVote = false;
                    return;
                }
                if (!_wasInVote)
                {
                    _wasInVote = true;
                    _warned.Clear();
                    _announceAt = now + Cfg.AnnounceDelayS;
                    _repeatAt = Cfg.AnnounceRepeatS > 0f ? _announceAt + Cfg.AnnounceRepeatS : -1f;
                    Debug("layout vote phase opened on " + CurrentMap());
                }
                if (_announceAt >= 0f && now >= _announceAt)
                {
                    _announceAt = -1f;
                    AnnounceVoteOpen(false);
                }
                else if (_announceAt < 0f && _repeatAt >= 0f && now >= _repeatAt)
                {
                    _repeatAt = -1f;
                    int votes = 0; try { votes = mode!.TeamsVersusVotes.Count; } catch { }
                    if (votes == 0) AnnounceVoteOpen(true);
                }
            }
            catch (Exception ex) { MelonLogger.Error("[VERSUS] OnUpdate: " + ex); _announceAt = _repeatAt = -1f; }
        }

        static void AnnounceVoteOpen(bool repeat)
        {
            string map = CurrentMap();
            var ev = Rules.Evaluate(Cfg, map);
            string line = VoteOpenLine(ev);
            MelonLogger.Msg("[VERSUS] vote " + (repeat ? "reminder" : "open") + " on " + map + ": " + (line.Length > 0 ? line : "all layouts count") + " | " + ev.WindowText());
            if (line.Length > 0) ToAll(line);   // nothing blocked and no 2v1 = no chat line
        }

        internal const string TwoVsOneHint = "3-way button = Sol+Centauri allied vs Aliens";

        /// <summary>
        /// "Layout vote - counted this round: Humans vs Humans, Sol vs Aliens, Centauri vs Aliens. Not counted: 3-way
        /// (ceiling 4/20 reached)". Empty when every layout counts and the 3-way button means a normal 3-way.
        /// </summary>
        internal static string VoteOpenLine(Evaluation ev)
        {
            var blocked = ev.BlockedVotable;   // empty when the filter is off
            bool twoCounted = ev.TwoVsOne && ev.IsAllowed(GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS);
            if (blocked.Count == 0 && !twoCounted) return "";
            var counted = ev.AllowedVotable.Select(m => m == GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS && ev.TwoVsOne
                                                        ? "2v1 (" + TwoVsOneHint + ")" : ev.Display(m)).ToList();
            string line = "Layout vote - counted this round: " + (counted.Count == 0 ? "none" : string.Join(", ", counted));
            if (blocked.Count > 0)
                line += ". Not counted: " + ev.NotCountedText(blocked);
            return line;
        }

        /// <summary>!versus status and the first lines of the ModUI page.</summary>
        internal static List<string> StatusLines()
        {
            var lines = new List<string>();
            string map = CurrentMap();
            var ev = Rules.Evaluate(Cfg, map);
            string head = "Map " + (map.Length > 0 ? map : "?") + " | ";
            if (!Cfg.Enabled) head += "filter OFF (enabled=false) | ";
            else if (ev.FilterOff) head += "blocklists exclude every layout: nothing filtered | ";
            var blocked = ev.BlockedVotable;
            string line = head + "counted: " + ev.JoinDisplay(ev.AllowedVotable);
            if (blocked.Count > 0)
                line += " | not counted: " + ev.NotCountedText(blocked);
            lines.Add(line);
            string w = ev.WindowText();
            if (ev.CeilingIgnored) w += " (ceilings ignored this round: they would leave no layout)";
            lines.Add(w);
            string waived = ev.WaivedNote();
            if (waived.Length > 0) lines.Add(waived);
            lines.Add(TwoVsOneStatus(ev));
            string comp = CompensationStatus(ev);
            if (comp.Length > 0) lines.Add(comp);
            return lines;
        }

        /// <summary>"2v1 ACTIVE this round ..." / "2v1 map: 3-way button = ..." / "2v1 maps: IndustrialQuarter (not this map)".</summary>
        internal static string TwoVsOneStatus(Evaluation ev)
        {
            string layout = VersusApi.LayoutShort;
            if (VersusApi.TwoVsOneActive)
                return "2v1 ACTIVE this round on " + ev.Map + ": Sol and Centauri allied against the Aliens" + (AllianceBridge.Pending ? " (alliance being set up)" : "");
            string maps = Cfg.MapTwoVsOne.Count == 0 ? "none" : string.Join(", ", Cfg.MapTwoVsOne);
            if (!ev.TwoVsOneMap)
                return "2v1 maps: " + maps + " (not this map)" + (layout.Length > 0 ? " | layout this round: " + layout : "");
            if (ev.AllianceMissing)
                return "2v1 map, but Si_Alliance (AllianceApi) is not loaded: the 3-way button plays a normal 3-way" + (layout.Length > 0 ? " | layout this round: " + layout : "");
            if (layout.Length > 0)
                return "2v1 map | layout this round: " + layout + (layout == "HvHvA" ? " (2v1 withdrawn, normal 3-way)" : "");
            return "2v1 map: " + TwoVsOneHint + (ev.IsAllowed(GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS) ? "" : " - but 3-way/2v1 is not counted this round (" + ev.Reason(GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS) + ")");
        }

        /// <summary>
        /// "2v1 lone team Alien: start money x2, starter units x2" while 2v1 is active (plus the grant once done);
        /// "2v1 lone team compensation: ..." on a 2v1 map before the layout is decided; "" elsewhere.
        /// </summary>
        internal static string CompensationStatus(Evaluation ev)
        {
            var t = Cfg.TwoVsOne;
            if (VersusApi.TwoVsOneActive)
            {
                string lone = VersusApi.LoneTeam;
                if (lone.Length == 0) return "2v1 lone team unknown (no single team outside Sol + Centauri): no compensation this round";
                string grant = LoneBonus.StatusText();
                return "2v1 lone team " + lone + ": " + t.Summary() + (grant.Length > 0 ? " (" + grant + ")" : "");
            }
            if (ev.TwoVsOneMap) return "2v1 lone team compensation: " + t.Summary();
            return "";
        }

        // ------------------------------------------------------------------ 2v1 outcome (AllianceBridge)

        internal static void TwoVsOneApplied(string map)
        {
            ToAll("3-way on " + map + " is played as 2v1: Sol and Centauri are allied against the Aliens.");
            LoneBonus.OnAllianceOk();
            VersusModUI.MarkDirty();
        }

        internal static void TwoVsOneFailed(string shortReason, string detail)
        {
            MelonLogger.Warning("[VERSUS] 2v1 withdrawn: " + detail);
            VersusApi.Withdraw();
            History.ClearLastTwoVsOne();
            ToAll("2v1 unavailable (" + shortReason + ") - playing normal 3-way");
            VersusModUI.MarkDirty();
        }

        // ------------------------------------------------------------------ chat

        /// <summary>Vanilla server chat broadcast (Player.SendServerChatMessage, like Si_Alliance 0.2.2).</summary>
        internal static void ToAll(string msg)
        {
            int n = 0; try { n = Player.Players.Count; } catch { }
            MelonLogger.Msg("[VERSUS] announce to " + n + " players: " + msg);
            if (!Cfg.Announce || n == 0) return;
            try { Player.SendServerChatMessage(Tag + msg); }
            catch (Exception ex) { MelonLogger.Warning("[VERSUS] SendServerChatMessage failed: " + ex.GetBaseException().Message); }
        }

        /// <summary>Private line to one player (Admin Mod helper); console when p is null.</summary>
        internal static void ToPlayer(Player? p, string msg)
        {
            if (p == null) { MelonLogger.Msg("[VERSUS] " + msg); return; }
            Debug("to " + SafeName(p) + ": " + msg);
            if (!Cfg.Announce) return;
            try { AdminBridge.Say(p, Tag + msg); }
            catch (Exception ex) { MelonLogger.Warning("[VERSUS] private message failed: " + ex.GetBaseException().Message); }
        }

        /// <summary>Command replies: always shown, even with announce off (the caller asked).</summary>
        internal static void Reply(Player? p, string msg)
        {
            if (p == null) { MelonLogger.Msg("[VERSUS] " + msg); return; }
            try { AdminBridge.Say(p, Tag + msg); }
            catch (Exception ex) { MelonLogger.Warning("[VERSUS] reply failed: " + ex.GetBaseException().Message); }
        }

        internal static string SafeName(Player? p)
        {
            try { return p != null ? p.PlayerName : "console"; } catch { return "?"; }
        }

        internal static void Debug(string msg)
        {
            if (Cfg.Debug) MelonLogger.Msg("[VERSUS] " + msg);
        }
    }
}
