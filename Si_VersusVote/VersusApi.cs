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
    /// </summary>
    public static class VersusApi
    {
        public const int ApiVersion = 1;

        static bool _twoVsOne;
        static GameModeExt.ETeamsVersus _decided = GameModeExt.ETeamsVersus.NONE;

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

        // ------------------------------------------------------------------ internal state changes

        /// <summary>The prefix decided the round's layout (after the filter).</summary>
        internal static void Decide(GameModeExt.ETeamsVersus mode, bool twoVsOne)
        {
            _decided = mode;
            _twoVsOne = twoVsOne;
            Fire(twoVsOne);
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
            _twoVsOne = false;
            Fire(false);
        }

        /// <summary>MP_Strategy.Restart (and its SetTeamVersusMode(NONE)): no layout yet.</summary>
        internal static void ClearRound()
        {
            _twoVsOne = false;
            _decided = GameModeExt.ETeamsVersus.NONE;
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
