using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace Si_VersusVote
{
    /// <summary>
    /// Harmony patches on MP_Strategy (SilicaCore is Mono on the dedicated server; patched manually so one missing method
    /// never takes the others down):
    ///   prefix+postfix  public override void MP_Strategy.SetTeamVersusMode(GameModeExt.ETeamsVersus, bool preventSpawning = false)
    ///   prefix          protected void MP_Strategy.RPC_VoteTeamVersus(Player, GameModeExt.ETeamsVersus)
    ///   postfix         public override void MP_Strategy.Restart()
    /// </summary>
    internal static class Patches
    {
        static bool _recordedThisRound;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            var tv = typeof(GameModeExt.ETeamsVersus);
            Patch(harmony, AccessTools.DeclaredMethod(typeof(MP_Strategy), "SetTeamVersusMode", new[] { tv, typeof(bool) }),
                  nameof(Prefix_SetTeamVersusMode), nameof(Postfix_SetTeamVersusMode), "MP_Strategy.SetTeamVersusMode(ETeamsVersus, bool)");
            Patch(harmony, AccessTools.DeclaredMethod(typeof(MP_Strategy), "RPC_VoteTeamVersus", new[] { typeof(Player), tv }),
                  nameof(Prefix_RPC_VoteTeamVersus), null, "MP_Strategy.RPC_VoteTeamVersus(Player, ETeamsVersus)");
            Patch(harmony, AccessTools.DeclaredMethod(typeof(MP_Strategy), "Restart", Type.EmptyTypes),
                  null, nameof(Postfix_Restart), "MP_Strategy.Restart()");
        }

        static void Patch(HarmonyLib.Harmony harmony, MethodInfo? target, string? prefix, string? postfix, string label)
        {
            if (target == null) { MelonLogger.Error("[VERSUS] " + label + " not found: that part of Si_VersusVote is inactive"); return; }
            try
            {
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(typeof(Patches), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(Patches), postfix) : null);
                VersusVoteMod.Debug("patched " + label);
            }
            catch (Exception ex) { MelonLogger.Error("[VERSUS] patching " + label + " failed: " + ex.Message); }
        }

        /// <summary>The plain multiplayer Strategy mode on the server, outside the campaign (Coop/Matchmaking subclasses and the campaign are left alone).</summary>
        internal static bool InScope(MP_Strategy? mode)
        {
            if (mode == null || mode.GetType() != typeof(MP_Strategy)) return false;
            if (Game.GetIsClient()) return false;
            try { if (GlobeCampaign.IsCampaignRunning) return false; } catch { }
            return true;
        }

        /// <summary>The pre-round Battle Layout phase: mission INIT and no layout chosen yet.</summary>
        internal static bool InVotePhase(MP_Strategy mode)
            => mode.MissionState == GameModeExt.EMissionState.INIT && mode.TeamsVersus == GameModeExt.ETeamsVersus.NONE;

        // ------------------------------------------------------------------ SetTeamVersusMode

        public static void Prefix_SetTeamVersusMode(MP_Strategy __instance, ref GameModeExt.ETeamsVersus __0, bool __1, out bool __state)
        {
            __state = false;
            try
            {
                if (__0 == GameModeExt.ETeamsVersus.NONE || __1) return;   // Restart's NONE, sandbox/save load (preventSpawning)
                if (!InScope(__instance) || !InVotePhase(__instance)) return;
                __state = true;   // this call picks the round's layout: the postfix records it

                var cfg = VersusVoteMod.Cfg;
                string map = VersusVoteMod.CurrentMap();
                var ev = Rules.Evaluate(cfg, map);
                if (ev.FilterOff)
                {
                    if (cfg.Enabled) MelonLogger.Warning("[VERSUS] blocklists exclude every layout on " + map + ": not filtering, " + Modes.Short(__0) + " stands");
                    return;
                }
                if (ev.IsAllowed(__0))
                {
                    VersusVoteMod.Debug("layout " + Modes.Short(__0) + " allowed on " + map + (ev.CeilingIgnored ? " (ceiling layer ignored)" : ""));
                    return;
                }

                var original = __0;
                var votes = __instance.TeamsVersusVotes;
                int originalVotes = Rules.VotesFor(votes, original);
                var pick = Rules.Retally(ev, votes, out int pickVotes);
                __0 = pick;

                string why = ev.Reason(original);
                MelonLogger.Msg("[VERSUS] " + map + ": " + Modes.Short(original) + " (" + originalVotes + " votes, " + why + ") replaced by " + Modes.Short(pick) +
                                " (" + pickVotes + " votes" + (pickVotes == 0 ? ", random among allowed" : "") + "); " + ev.WindowText());
                string lead = originalVotes > 0
                    ? Modes.Display(original) + " was voted but is not counted this round (" + why + ")"
                    : "The random layout " + Modes.Display(original) + " is not counted this round (" + why + ")";
                VersusVoteMod.ToAll(lead + " - playing " + Modes.Display(pick) + ".");
            }
            catch (Exception ex) { MelonLogger.Error("[VERSUS] SetTeamVersusMode prefix: " + ex); }
        }

        public static void Postfix_SetTeamVersusMode(MP_Strategy __instance, GameModeExt.ETeamsVersus __0, bool __1, bool __state)
        {
            try
            {
                if (__0 == GameModeExt.ETeamsVersus.NONE) { _recordedThisRound = false; return; }
                if (!__state || __1 || _recordedThisRound) return;
                var applied = __instance.TeamsVersus;
                if (applied == GameModeExt.ETeamsVersus.NONE) return;
                _recordedThisRound = true;
                string map = VersusVoteMod.CurrentMap();
                History.Add(map, applied);
                MelonLogger.Msg("[VERSUS] round recorded: " + map + " " + Modes.Short(applied) + " (" + History.Rounds.Count + " in history)");
            }
            catch (Exception ex) { MelonLogger.Error("[VERSUS] SetTeamVersusMode postfix: " + ex); }
        }

        // ------------------------------------------------------------------ RPC_VoteTeamVersus

        /// <summary>The server has just stored the vote and rebroadcasts it: tell the voter privately if it will not count. The vote is not altered.</summary>
        public static void Prefix_RPC_VoteTeamVersus(MP_Strategy __instance, Player __0, GameModeExt.ETeamsVersus __1)
        {
            try
            {
                if (__0 == null || __1 == GameModeExt.ETeamsVersus.NONE) return;
                if (!InScope(__instance) || !InVotePhase(__instance)) return;
                var ev = Rules.Evaluate(VersusVoteMod.Cfg, VersusVoteMod.CurrentMap());
                if (ev.IsAllowed(__1)) return;
                if (!VersusVoteMod.MarkWarned(__0, __1)) return;   // once per player and layout per vote phase
                VersusVoteMod.ToPlayer(__0, "Your " + Modes.Display(__1) + " vote will not be counted this round (" + ev.Reason(__1) + "). Counted: " +
                                            Modes.JoinDisplay(ev.AllowedVotable) + ".");
            }
            catch (Exception ex) { MelonLogger.Error("[VERSUS] RPC_VoteTeamVersus prefix: " + ex); }
        }

        // ------------------------------------------------------------------ Restart

        public static void Postfix_Restart(MP_Strategy __instance)
        {
            try
            {
                _recordedThisRound = false;
                VersusVoteMod.OnRestart();
            }
            catch (Exception ex) { MelonLogger.Error("[VERSUS] Restart postfix: " + ex); }
        }
    }
}
