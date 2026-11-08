using System;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace Si_VersusVote
{
    /// <summary>
    /// Si_Alliance by reflection (no compile-time reference): type Si_Alliance.AllianceApi,
    /// public static bool ForceAlliance(string teamA, string teamB, bool locked, string reason), optional ApiVersion.
    /// The alliance is requested on the first OnUpdate frame after the SetTeamVersusMode postfix and retried every
    /// 0.5 s for up to 5 s while ForceAlliance returns false (or throws).
    /// </summary>
    internal static class AllianceBridge
    {
        const string TypeName = "Si_Alliance.AllianceApi";
        /// <summary>The two allied teams of a 2v1 round (names as passed to ForceAlliance); the lone team is the other one.</summary>
        internal const string TeamA = "Sol", TeamB = "Centauri";
        const float RetryEvery = 0.5f, GiveUpAfter = 5f;

        static MethodInfo? _force;
        static int _apiVersion = -1;
        static float _nextLookup;
        static bool _badSignatureWarned;

        static bool _pending;
        static int _scheduledFrame;
        static float _deadline, _nextTry;
        static int _tries;
        static string _map = "";

        public static int ApiVersion => _apiVersion;

        /// <summary>True when Si_Alliance's AllianceApi.ForceAlliance is loaded. Failed lookups are repeated at most every 10 s.</summary>
        public static bool Available => Resolve(false) != null;

        /// <summary>Prefix-time check (fresh lookup if not linked yet).</summary>
        public static bool CheckNow() => Resolve(true) != null;

        public static bool Pending => _pending;

        static MethodInfo? Resolve(bool force)
        {
            if (_force != null) return _force;
            float now = Time.realtimeSinceStartup;
            if (!force && now < _nextLookup) return null;
            _nextLookup = now + 10f;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? t;
                    try { t = asm.GetType(TypeName, false); } catch { continue; }
                    if (t == null) continue;
                    var m = t.GetMethod("ForceAlliance", BindingFlags.Public | BindingFlags.Static, null,
                                        new[] { typeof(string), typeof(string), typeof(bool), typeof(string) }, null);
                    if (m == null || m.ReturnType != typeof(bool))
                    {
                        if (_badSignatureWarned) return null;
                        _badSignatureWarned = true;
                        MelonLogger.Warning("[VERSUS] " + TypeName + " found in " + asm.GetName().Name + " but without public static bool ForceAlliance(string, string, bool, string)");
                        return null;
                    }
                    _apiVersion = ReadApiVersion(t);
                    if (_apiVersion >= 0 && _apiVersion < 1)
                        MelonLogger.Warning("[VERSUS] " + TypeName + ".ApiVersion " + _apiVersion + " (< 1): trying ForceAlliance anyway");
                    MelonLogger.Msg("[VERSUS] Si_Alliance linked: " + asm.GetName().Name + " " + asm.GetName().Version + ", AllianceApi v" + (_apiVersion >= 0 ? _apiVersion.ToString() : "?"));
                    _force = m;
                    return m;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[VERSUS] Si_Alliance lookup failed: " + ex.GetBaseException().Message); }
            return null;
        }

        static int ReadApiVersion(Type t)
        {
            try
            {
                var f = t.GetField("ApiVersion", BindingFlags.Public | BindingFlags.Static);
                if (f != null) return Convert.ToInt32(f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null));
                var p = t.GetProperty("ApiVersion", BindingFlags.Public | BindingFlags.Static);
                if (p != null) return Convert.ToInt32(p.GetValue(null));
            }
            catch { }
            return -1;
        }

        /// <summary>From the SetTeamVersusMode postfix: request the alliance from the next OnUpdate frame on.</summary>
        public static void Schedule(string map)
        {
            _pending = true;
            _map = map ?? "";
            _scheduledFrame = Time.frameCount;
            _nextTry = 0f;
            _deadline = Time.realtimeSinceStartup + GiveUpAfter;
            _tries = 0;
        }

        public static void Cancel() => _pending = false;

        /// <summary>Every OnUpdate (one bool check while nothing is pending).</summary>
        public static void Tick()
        {
            if (!_pending) return;
            if (Time.frameCount <= _scheduledFrame) return;   // at least one frame after the postfix
            float now = Time.realtimeSinceStartup;
            if (now < _nextTry) return;
            _nextTry = now + RetryEvery;
            if (!VersusApi.TwoVsOneActive) { _pending = false; return; }   // withdrawn meanwhile

            var m = Resolve(true);
            if (m == null)
            {
                _pending = false;
                VersusVoteMod.TwoVsOneFailed("Si_Alliance missing", "AllianceApi.ForceAlliance not found");
                return;
            }
            _tries++;
            bool ok = false;
            string err = "";
            try { ok = (bool)m.Invoke(null, new object[] { TeamA, TeamB, true, "2v1 " + _map }); }
            catch (Exception ex) { err = ex.GetBaseException().Message; }

            if (ok)
            {
                _pending = false;
                MelonLogger.Msg("[VERSUS] 2v1: Si_Alliance ForceAlliance(Sol, Centauri, locked) OK after " + _tries + " attempt(s) on " + _map);
                VersusVoteMod.TwoVsOneApplied(_map);
                return;
            }
            VersusVoteMod.Debug("ForceAlliance attempt " + _tries + " returned false" + (err.Length > 0 ? " (" + err + ")" : ""));
            if (now >= _deadline)
            {
                _pending = false;
                VersusVoteMod.TwoVsOneFailed("Si_Alliance refused the alliance",
                    "ForceAlliance(Sol, Centauri) returned false " + _tries + " times in " + GiveUpAfter + " s" + (err.Length > 0 ? ", last error: " + err : ""));
            }
        }
    }
}
