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
    ///                   (prefix with Priority.First: VersusApi.TwoVsOneActive is set before other mods' prefixes read it)
    ///   prefix          protected void MP_Strategy.RPC_VoteTeamVersus(Player, GameModeExt.ETeamsVersus)
    ///   postfix         public override void MP_Strategy.Restart()
    ///   postfix         protected virtual void MP_Strategy.SpawnStructuresAndStartingResources()  (Priority.Last: after
    ///                   Si_Resources' / Si_RTS_AI's postfixes that overwrite Team.StartingResources) - 2v1 lone-team money
    /// </summary>
    internal static class Patches
    {
        static bool _recordedThisRound;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            var tv = typeof(GameModeExt.ETeamsVersus);
            Patch(harmony, AccessTools.DeclaredMethod(typeof(MP_Strategy), "SetTeamVersusMode", new[] { tv, typeof(bool) }),
                  nameof(Prefix_SetTeamVersusMode), nameof(Postfix_SetTeamVersusMode), "MP_Strategy.SetTeamVersusMode(ETeamsVersus, bool)", Priority.First);
            Patch(harmony, AccessTools.DeclaredMethod(typeof(MP_Strategy), "RPC_VoteTeamVersus", new[] { typeof(Player), tv }),
                  nameof(Prefix_RPC_VoteTeamVersus), null, "MP_Strategy.RPC_VoteTeamVersus(Player, ETeamsVersus)");
            Patch(harmony, AccessTools.DeclaredMethod(typeof(MP_Strategy), "Restart", Type.EmptyTypes),
                  null, nameof(Postfix_Restart), "MP_Strategy.Restart()");
            Patch(harmony, AccessTools.DeclaredMethod(typeof(MP_Strategy), "SpawnStructuresAndStartingResources", Type.EmptyTypes),
                  null, nameof(Postfix_SpawnStructuresAndStartingResources), "MP_Strategy.SpawnStructuresAndStartingResources()", -1, Priority.Last);
        }

        static void Patch(HarmonyLib.Harmony harmony, MethodInfo? target, string? prefix, string? postfix, string label, int prefixPriority = -1, int postfixPriority = -1)
        {
            if (target == null) { MelonLogger.Error("[VERSUS] " + label + " not found: that part of Si_VersusVote is inactive"); return; }
            try
            {
                HarmonyMethod? pre = null;
                if (prefix != null)
                {
                    pre = new HarmonyMethod(typeof(Patches), prefix);
                    if (prefixPriority >= 0) pre.priority = prefixPriority;
                }
                HarmonyMethod? post = null;
                if (postfix != null)
                {
                    post = new HarmonyMethod(typeof(Patches), postfix);
                    if (postfixPriority >= 0) post.priority = postfixPriority;
                }
                harmony.Patch(target, prefix: pre, postfix: post);
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

        /// <summary>
        /// The round's layout call (vote result or vanilla's random pick). Filters it, then decides 2v1: on a map in
        /// map_two_vs_one an (allowed) HvHvA is played as Sol + Centauri allied vs Aliens. VersusApi.TwoVsOneActive is set
        /// here, before the original runs (Priority.First: before other mods' prefixes), so spawn code can read it.
        /// </summary>
        public static void Prefix_SetTeamVersusMode(MP_Strategy __instance, ref GameModeExt.ETeamsVersus __0, bool __1, out bool __state)
        {
            __state = false;
            try
            {
                if (__0 == GameModeExt.ETeamsVersus.NONE) { VersusApi.ClearRound(); return; }   // Restart's NONE
                if (__1) return;                                                               // sandbox / save load (preventSpawning)
                if (!InScope(__instance) || !InVotePhase(__instance)) return;
                __state = true;   // this call picks the round's layout: the postfix records it

                string map = VersusVoteMod.CurrentMap();
                var ev = Rules.Evaluate(VersusVoteMod.Cfg, map);
                Filter(__instance, ref __0, map, ev);

                // ---- 2v1 decision (also when the filter is off: the map list is the switch) ----
                bool twoVsOne = false;
                _twoVsOneUnavailable = false;
                if (__0 == GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS && VersusVoteMod.Cfg.IsTwoVsOneMap(map))
                {
                    if (AllianceBridge.CheckNow()) twoVsOne = true;
                    else
                    {
                        _twoVsOneUnavailable = true;
                        MelonLogger.Warning("[VERSUS] " + map + " is a 2v1 map but Si_Alliance (AllianceApi.ForceAlliance) is not loaded: normal 3-way");
                    }
                }
                Team? lone = twoVsOne ? FindLoneTeam(__instance, __0) : null;
                VersusApi.Decide(__0, twoVsOne, lone);
                MelonLogger.Msg("[VERSUS] layout decided on " + map + ": " + (twoVsOne ? "2v1 (HvHvA, Sol + Centauri allied" +
                                (lone != null ? ", lone team " + VersusApi.LoneTeam + ": " + VersusVoteMod.Cfg.TwoVsOne.Summary() : ", lone team unknown: no compensation") + ")"
                                : Modes.Short(__0)));
            }
            catch (Exception ex) { MelonLogger.Error("[VERSUS] SetTeamVersusMode prefix: " + ex); }
        }

        static bool _twoVsOneUnavailable;

        /// <summary>
        /// The lone team of a 2v1: among the TeamSetups present in <paramref name="layout"/> (Enabled, ForTeamVersusModes
        /// contains it, Team set, not IsSpecial), the one that is not one of the allied pair (AllianceBridge.TeamA / TeamB,
        /// matched by TeamShortName / TeamName, case-insensitive, prefix like Si_Alliance's lookup). Read from the setups
        /// because GetTeamSetupActive needs TeamsVersus, which the original has not set yet. Null (warning) unless exactly
        /// one team is left.
        /// </summary>
        internal static Team? FindLoneTeam(MP_Strategy mode, GameModeExt.ETeamsVersus layout)
        {
            var left = new List<Team>();
            try
            {
                if (mode.TeamSetups != null)
                    foreach (var ts in mode.TeamSetups)
                    {
                        if (ts == null || !ts.Enabled || ts.Team == null || ts.ForTeamVersusModes == null || !ts.ForTeamVersusModes.Contains(layout)) continue;
                        var t = ts.Team;
                        bool special = false; try { special = t.IsSpecial; } catch { }
                        if (special || IsAllyName(t, AllianceBridge.TeamA) || IsAllyName(t, AllianceBridge.TeamB) || left.Contains(t)) continue;
                        left.Add(t);
                    }
            }
            catch (Exception ex) { MelonLogger.Warning("[VERSUS] lone team lookup failed: " + ex.GetBaseException().Message); }
            if (left.Count == 1) return left[0];
            var names = new List<string>();
            foreach (var t in left) { try { names.Add(t.TeamShortName); } catch { names.Add("?"); } }
            MelonLogger.Warning("[VERSUS] 2v1: expected exactly one team outside " + AllianceBridge.TeamA + " + " + AllianceBridge.TeamB + " in HvHvA, found " + left.Count +
                                " [" + string.Join(", ", names) + "]: no lone-team compensation this round");
            return null;
        }

        static bool IsAllyName(Team t, string ally)
        {
            string a = ally.ToLowerInvariant(), sn = "", ln = "";
            try { sn = (t.TeamShortName ?? "").ToLowerInvariant(); } catch { }
            try { ln = (t.TeamName ?? "").ToLowerInvariant(); } catch { }
            return (sn.Length > 0 && sn.StartsWith(a)) || (ln.Length > 0 && ln.StartsWith(a));
        }

        // ------------------------------------------------------------------ SpawnStructuresAndStartingResources

        /// <summary>
        /// Vanilla has just set every active team's Team.StartingResources (MP_Strategy.cs:2750; runs once per round, on
        /// the first team join after SetTeamVersusMode queued it). Priority.Last, so the amount read here is the one other
        /// mods' postfixes (Si_Resources, Si_RTS_AI's guard) wrote. LoneBonus grants (mult - 1) x that amount once the
        /// alliance is confirmed.
        /// </summary>
        public static void Postfix_SpawnStructuresAndStartingResources(MP_Strategy __instance)
        {
            try
            {
                if (!InScope(__instance) || __instance.TeamsVersus == GameModeExt.ETeamsVersus.NONE) return;
                LoneBonus.OnStartingResourcesSet();
            }
            catch (Exception ex) { MelonLogger.Error("[VERSUS] SpawnStructuresAndStartingResources postfix: " + ex); }
        }

        /// <summary>If the layout does not count this round: re-tally the votes over the allowed layouts and replace the argument.</summary>
        static void Filter(MP_Strategy instance, ref GameModeExt.ETeamsVersus mode, string map, Evaluation ev)
        {
            var cfg = VersusVoteMod.Cfg;
            if (ev.FilterOff)
            {
                if (cfg.Enabled) MelonLogger.Warning("[VERSUS] blocklists exclude every layout on " + map + ": not filtering, " + Modes.Short(mode) + " stands");
                return;
            }
            if (ev.IsAllowed(mode))
            {
                VersusVoteMod.Debug("layout " + Modes.Short(mode) + " allowed on " + map + (ev.CeilingIgnored ? " (ceiling layer ignored)" : ""));
                return;
            }

            var original = mode;
            var votes = instance.TeamsVersusVotes;
            int originalVotes = Rules.VotesFor(votes, original);
            var pick = Rules.Retally(ev, votes, out int pickVotes);
            mode = pick;

            string why = ev.Reason(original);
            MelonLogger.Msg("[VERSUS] " + map + ": " + Modes.Short(original) + " (" + originalVotes + " votes, " + why + ") replaced by " + Modes.Short(pick) +
                            " (" + pickVotes + " votes" + (pickVotes == 0 ? ", random among allowed" : "") + "); " + ev.WindowText());
            string lead = originalVotes > 0
                ? ev.Display(original) + " was voted but is not counted this round (" + why + ")"
                : "The random layout " + ev.Display(original) + " is not counted this round (" + why + ")";
            VersusVoteMod.ToAll(lead + " - playing " + ev.Display(pick) + ".");
        }

        public static void Postfix_SetTeamVersusMode(MP_Strategy __instance, GameModeExt.ETeamsVersus __0, bool __1, bool __state)
        {
            try
            {
                if (__0 == GameModeExt.ETeamsVersus.NONE) { _recordedThisRound = false; return; }
                if (!__state || __1 || _recordedThisRound) return;
                var applied = __instance.TeamsVersus;
                VersusApi.Reconcile(applied);
                if (applied == GameModeExt.ETeamsVersus.NONE) return;
                _recordedThisRound = true;
                string map = VersusVoteMod.CurrentMap();
                bool two = VersusApi.TwoVsOneActive;
                History.Add(map, applied, two);
                MelonLogger.Msg("[VERSUS] round recorded: " + map + " " + Modes.Short(applied) + (two ? " (2v1)" : "") + " (" + History.Rounds.Count + " in history)");
                if (two) AllianceBridge.Schedule(map);
                else if (_twoVsOneUnavailable && applied == GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS)
                    VersusVoteMod.ToAll("2v1 unavailable (Si_Alliance missing) - playing normal 3-way");
                _twoVsOneUnavailable = false;
                VersusModUI.MarkDirty();
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
                string map = VersusVoteMod.CurrentMap();
                var ev = Rules.Evaluate(VersusVoteMod.Cfg, map);
                bool threeWay = __1 == GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS;
                if (ev.IsAllowed(__1))
                {
                    // a counted 3-way vote on a 2v1 map: tell the voter what the button means here
                    if (!threeWay || !ev.TwoVsOne || !VersusVoteMod.MarkWarned(__0, __1)) return;
                    VersusVoteMod.ToPlayer(__0, "Your 3-way vote counts as 2v1 on " + map + ": Sol and Centauri allied against the Aliens.");
                    return;
                }
                if (!VersusVoteMod.MarkWarned(__0, __1)) return;   // once per player and layout per vote phase
                string what = threeWay && ev.TwoVsOne ? "3-way (2v1 here)" : Modes.Display(__1);
                VersusVoteMod.ToPlayer(__0, "Your " + what + " vote will not be counted this round (" + ev.Reason(__1) + "). Counted: " +
                                            ev.JoinDisplay(ev.AllowedVotable) + ".");
            }
            catch (Exception ex) { MelonLogger.Error("[VERSUS] RPC_VoteTeamVersus prefix: " + ex); }
        }

        // ------------------------------------------------------------------ Restart

        public static void Postfix_Restart(MP_Strategy __instance)
        {
            try
            {
                _recordedThisRound = false;
                _twoVsOneUnavailable = false;
                VersusApi.ClearRound();
                AllianceBridge.Cancel();
                VersusVoteMod.OnRestart();
            }
            catch (Exception ex) { MelonLogger.Error("[VERSUS] Restart postfix: " + ex); }
        }
    }
}
