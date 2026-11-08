using System;
using MelonLoader;

namespace Si_VersusVote
{
    /// <summary>
    /// Public, stable surface for other mods (read by reflection, no compile-time reference needed).
    ///
    ///   TwoVsOneActive  true from the moment this round's layout is decided as 2v1 (set INSIDE the SetTeamVersusMode
    ///                   prefix, which runs with Priority.First, before the original and before vanilla queues the HQ/Nest
    ///                   spawn) until MP_Strategy.Restart. Withdrawn (false) when Si_Alliance refuses the alliance, or when
    ///                   another mod changes the layout away from HvHvA before the original runs. Never set when
    ///                   Si_Alliance's AllianceApi is not loaded.
    ///   CurrentMap      the server map (NetworkGameServer.GetServerMap(), fallback: last loaded scene).
    ///   LayoutShort     "HvH", "HvA", "HvHvA", "2v1", "" (NONE); "H2vA" only if something else applies that stub.
    ///   IsTwoVsOneMap   the map is listed in map_two_vs_one (case/space-insensitive).
    ///   LayoutDecided   fired right after the flag is set (argument = twoVsOne); fired again with false if 2v1 is
    ///                   withdrawn later in the same round. Handlers run inside the Harmony prefix: keep them cheap.
    ///
    /// ApiVersion 2 (0.3.0), lone-team compensation. Set in the same prefix as TwoVsOneActive (before LayoutDecided
    /// fires), cleared with it (Restart, withdrawal):
    ///   LoneTeam              TeamShortName of the team outside the Sol + Centauri alliance ("Alien"), derived from the
    ///                         MP_Strategy.TeamSetups active in HvHvA; "" when 2v1 is not active (or no single lone team).
    ///   LoneStartMoneyMult    config two_vs_one.lone_start_money_mult while 2v1 is active with a lone team, else 1.
    ///                         Applied by this mod (starting resources, see LoneBonus.cs); informational for others.
    ///   LoneStarterUnitsMult  config two_vs_one.lone_starter_units_mult (integer >= 1) under the same condition, else 1.
    ///                         Applied by Si_StarterUnits 1.2.0+ to every starter unit count of the lone team.
    ///   IsLoneTeam(team)      2v1 active and team is the lone team (same Team object, or TeamShortName / TeamName equal,
    ///                         case-insensitive). False for null and whenever LoneTeam is "".
    /// The multipliers read the live config, so an admin edit applies to whatever is granted after it.
    /// </summary>
    public static class VersusApi
    {
        public const int ApiVersion = 2;

        static bool _twoVsOne;
        static GameModeExt.ETeamsVersus _decided = GameModeExt.ETeamsVersus.NONE;
        static Team? _lone;
        static string _loneShort = "", _loneLong = "";

        public static bool TwoVsOneActive => _twoVsOne;

        public static string CurrentMap => VersusVoteMod.CurrentMap();

        public static string LayoutShort
        {
            get
            {
                if (_twoVsOne) return "2v1";
                var m = _decided;
                if (m == GameModeExt.ETeamsVersus.NONE)
                {
                    try { if (GameMode.CurrentGameMode is GameModeExt gm) m = gm.TeamsVersus; } catch { }
                }
                return m == GameModeExt.ETeamsVersus.NONE ? "" : Modes.Short(m);
            }
        }

        public static bool IsTwoVsOneMap(string map) => VersusVoteMod.Cfg.IsTwoVsOneMap(map);

        public static event Action<bool>? LayoutDecided;

        // ------------------------------------------------------------------ v2: lone team

        public static string LoneTeam => _twoVsOne ? _loneShort : "";

        public static float LoneStartMoneyMult => LoneTeam.Length > 0 ? VersusVoteMod.Cfg.TwoVsOne.LoneStartMoneyMult : 1f;

        public static int LoneStarterUnitsMult => LoneTeam.Length > 0 ? VersusVoteMod.Cfg.TwoVsOne.LoneStarterUnitsMult : 1;

        public static bool IsLoneTeam(Team team)
        {
            if (team == null || LoneTeam.Length == 0) return false;
            try
            {
                if (_lone != null && ReferenceEquals(team, _lone)) return true;
                string sn = team.TeamShortName ?? "", ln = team.TeamName ?? "";
                if (sn.Length > 0 && (string.Equals(sn, _loneShort, StringComparison.OrdinalIgnoreCase) || string.Equals(sn, _loneLong, StringComparison.OrdinalIgnoreCase))) return true;
                if (ln.Length > 0 && (string.Equals(ln, _loneLong, StringComparison.OrdinalIgnoreCase) || string.Equals(ln, _loneShort, StringComparison.OrdinalIgnoreCase))) return true;
            }
            catch { }
            return false;
        }

        /// <summary>The lone Team object while 2v1 is active (null otherwise).</summary>
        internal static Team? LoneTeamRef => _twoVsOne && _loneShort.Length > 0 ? _lone : null;

        // ------------------------------------------------------------------ internal state changes

        /// <summary>The prefix decided the round's layout (after the filter).</summary>
        internal static void Decide(GameModeExt.ETeamsVersus mode, bool twoVsOne, Team? lone)
        {
            _decided = mode;
            SetLone(twoVsOne ? lone : null);
            _twoVsOne = twoVsOne;
            Fire(twoVsOne);
        }

        static void SetLone(Team? t)
        {
            _lone = t;
            _loneShort = _loneLong = "";
            if (t == null) return;
            try { _loneShort = t.TeamShortName ?? ""; } catch { }
            try { _loneLong = t.TeamName ?? ""; } catch { }
            if (_loneShort.Length == 0) _loneShort = _loneLong;
        }

        /// <summary>The postfix saw the layout actually applied (another mod may have changed it after our prefix).</summary>
        internal static void Reconcile(GameModeExt.ETeamsVersus applied)
        {
            _decided = applied;
            if (_twoVsOne && applied != GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS)
            {
                MelonLogger.Warning("[VERSUS] layout changed to " + Modes.Short(applied) + " after the 2v1 decision (another mod's prefix?): 2v1 withdrawn");
                Withdraw();
            }
        }

        /// <summary>2v1 is off for the rest of this round (alliance refused / unavailable).</summary>
        internal static void Withdraw()
        {
            if (!_twoVsOne) return;
            LoneBonus.OnWithdraw();
            _twoVsOne = false;
            SetLone(null);
            Fire(false);
        }

        /// <summary>MP_Strategy.Restart (and its SetTeamVersusMode(NONE)): no layout yet.</summary>
        internal static void ClearRound()
        {
            _twoVsOne = false;
            _decided = GameModeExt.ETeamsVersus.NONE;
            SetLone(null);
            LoneBonus.ResetRound();
        }

        static void Fire(bool twoVsOne)
        {
            var h = LayoutDecided;
            if (h == null) return;
            foreach (var d in h.GetInvocationList())
            {
                try { ((Action<bool>)d)(twoVsOne); }
                catch (Exception ex) { MelonLogger.Warning("[VERSUS] LayoutDecided handler " + d.Method.DeclaringType?.FullName + " threw: " + ex.GetBaseException().Message); }
            }
        }
    }
}
