/*
 Si_VersusVote - server-only filter for the vanilla pre-round "Battle Layout" vote of MP_Strategy.

 Vanilla tallies GameModeExt.TeamsVersusVotes when the vote timer runs out and calls SetTeamVersusMode(result). This mod
 decides per round which layouts COUNT (global blocklist, per-map blocklist, share ceilings over the last N rounds) and,
 when the result is not allowed, re-tallies the same votes over the allowed layouts only and swaps the argument.
 The layout buttons are baked into the client prefab, so a blocked layout stays clickable; players are told in chat.

   !layout | !versus            status: counted / not counted this round, counts in the window
   !versus stats                shares from the whole history (all maps + per map)
   !versus reload | reset       admin (Power.Generic): reload config, clear history (old file kept as .bak)

 Config + history: UserData/cfg/VersusVote/. Chat commands need the Admin Mod; the filter itself does not.
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
        public const string Version = "0.1.0";
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
            if (line.Length > 0) ToAll(line);   // nothing blocked = no chat line
        }

        /// <summary>"Layout vote - counted this round: A, B. Not counted: C (reason)"; empty when every layout counts.</summary>
        internal static string VoteOpenLine(Evaluation ev)
        {
            if (ev.FilterOff) return "";
            var blocked = ev.BlockedVotable;
            if (blocked.Count == 0) return "";
            var why = blocked.Select(m => Modes.Display(m) + " (" + ev.Reason(m) + ")");
            return "Layout vote - counted this round: " + Modes.JoinDisplay(ev.AllowedVotable) + ". Not counted: " + string.Join(", ", why);
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
