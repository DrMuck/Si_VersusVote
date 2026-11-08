using System;
using System.Globalization;
using MelonLoader;
using UnityEngine;

namespace Si_VersusVote
{
    /// <summary>
    /// 2v1 lone-team starting money (config two_vs_one.lone_start_money_mult).
    ///
    /// Vanilla 1.0.15 writes each team's starting money in exactly one place per round:
    /// MP_Strategy.SpawnStructuresAndStartingResources() (MP_Strategy.cs:2734-2755, assignment at :2750
    /// "teamSetup.Team.StartingResources = Max(Ceil(teamSetup.StartingResources * MultStartingRes * scale), first deposit
    /// cost)"). SetTeamVersusMode only queues it (QueueCoopSpawnBaseStructures); it runs on the first team join after the
    /// layout (MP_Strategy.cs:1954-1957). Si_Resources and Si_RTS_AI overwrite Team.StartingResources in their own postfixes
    /// on that method, so ours runs with Priority.Last and reads the final amount.
    ///
    /// The bonus (mult - 1) x that amount is added to Team.StartingResources once per round, and only when BOTH have
    /// happened, in either order: the starting money was set (postfix above) AND Si_Alliance confirmed the alliance
    /// (AllianceBridge success -> VersusVoteMod.TwoVsOneApplied). If the alliance is refused, 2v1 is withdrawn before
    /// anything was granted. Should 2v1 be withdrawn after a grant (no such path today), what is left of the bonus is taken
    /// back (OnWithdraw). Reset on Restart.
    /// </summary>
    internal static class LoneBonus
    {
        static Team? _team;
        static string _teamName = "";
        static int _base = -1;          // the lone team's starting money as vanilla + other mods set it; -1 = not set yet
        static bool _allianceOk;
        static bool _done;              // grant decided (granted or skipped) this round
        static int _granted, _after;
        static float _mult = 1f;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static void ResetRound()
        {
            _team = null;
            _teamName = "";
            _base = -1;
            _allianceOk = false;
            _done = false;
            _granted = _after = 0;
            _mult = 1f;
        }

        /// <summary>"granted 9000 -> 18000" / "waiting for the base spawn" / "waiting for the alliance" / "".</summary>
        internal static string StatusText()
        {
            if (_granted > 0) return "start money granted " + _base.ToString(Inv) + " -> " + _after.ToString(Inv);
            if (_done) return "";
            if (_base < 0) return "start money applied when the bases spawn";
            if (!_allianceOk) return "start money applied when the alliance is confirmed";
            return "";
        }

        /// <summary>Postfix of SpawnStructuresAndStartingResources (in scope, layout set).</summary>
        internal static void OnStartingResourcesSet()
        {
            if (!VersusApi.TwoVsOneActive || _base >= 0 || _done) return;
            var team = VersusApi.LoneTeamRef;
            if (team == null) { VersusVoteMod.Debug("starting resources set, but no lone team known: no 2v1 money bonus"); return; }
            _team = team;
            _teamName = VersusApi.LoneTeam;
            _base = team.StartingResources;
            VersusVoteMod.Debug("2v1: " + _teamName + " starting resources set to " + _base + (_allianceOk ? "" : ", bonus waits for the alliance"));
            TryGrant();
        }

        /// <summary>Si_Alliance accepted ForceAlliance (AllianceBridge success).</summary>
        internal static void OnAllianceOk()
        {
            _allianceOk = true;
            TryGrant();
        }

        static void TryGrant()
        {
            if (_done || _base < 0 || !_allianceOk || !VersusApi.TwoVsOneActive) return;
            var team = _team;
            if (team == null || !ReferenceEquals(team, VersusApi.LoneTeamRef)) return;
            _done = true;
            _mult = VersusVoteMod.Cfg.TwoVsOne.LoneStartMoneyMult;
            if (_mult <= 1f)
            {
                MelonLogger.Msg("[VERSUS] 2v1: " + _teamName + " starting resources " + _base + " (lone_start_money_mult 1: no bonus)");
                return;
            }
            int bonus = Mathf.CeilToInt(_base * (_mult - 1f));
            if (bonus <= 0) return;
            try
            {
                int now = team.StartingResources;
                team.StartingResources = now + bonus;   // server: setter fires OnResourcesStored and RPC_SynchStartingResources
                _granted = bonus;
                _after = _base + bonus;
                MelonLogger.Msg("[VERSUS] 2v1: " + _teamName + " starting resources " + _base + " -> " + _after + " (" + TwoVsOneConfig.Mult(_mult) + ")" +
                                (now != _base ? " [unspent before the bonus: " + now + ", now " + team.StartingResources + "]" : ""));
                VersusModUI.MarkDirty();
            }
            catch (Exception ex) { MelonLogger.Warning("[VERSUS] 2v1 starting money bonus failed: " + ex.GetBaseException().Message); }
        }

        /// <summary>2v1 withdrawn: take back what is left of a granted bonus (StartingResources is spent first, so it may be less).</summary>
        internal static void OnWithdraw()
        {
            try
            {
                if (_granted > 0 && _team != null)
                {
                    int now = _team.StartingResources;
                    int take = Math.Min(_granted, now);
                    if (take > 0) _team.StartingResources = now - take;
                    MelonLogger.Warning("[VERSUS] 2v1 withdrawn: " + _teamName + " starting money bonus revoked " + take + " of " + _granted);
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[VERSUS] revoking the 2v1 money bonus failed: " + ex.GetBaseException().Message); }
            ResetRound();
        }
    }
}
