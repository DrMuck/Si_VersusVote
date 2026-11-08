using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MelonLoader;
using SilicaAdminMod;

namespace Si_VersusVote
{
    /// <summary>Everything that touches the Admin Mod assembly, so the mod still loads (filter only) when it is missing.</summary>
    internal static class AdminBridge
    {
        public static void Register()
        {
            HelperMethods.RegisterPlayerCommand("layout", Commands.Command_Versus, true);
            HelperMethods.RegisterPlayerCommand("versus", Commands.Command_Versus, true);
        }

        public static void Say(Player p, string msg) => HelperMethods.SendChatMessageToPlayer(p, msg);

        /// <summary>Admin with the Generic power (any configured Admin Mod admin); the console always is.</summary>
        public static bool IsAdmin(Player? p)
        {
            if (p == null) return true;
            try { return p.CanAdminExecute(Power.Generic); } catch { return false; }
        }
    }

    /// <summary>
    /// !layout / !versus [status|stats|help] for everyone; set / block / mapblock / 2v1 / save / reload / reset need
    /// Power.Generic. Both commands are registered as PLAYER commands, so the Admin Mod does not gate them: every edit
    /// sub-command checks the caller's power here, on the server, whether it was typed or sent by the Si_ModUI page.
    /// </summary>
    internal static class Commands
    {
        static void Reply(Player? p, string msg) => VersusVoteMod.Reply(p, msg);

        public static void Command_Versus(Player? caller, string args)
        {
            try { Handle(caller, args ?? ""); }
            catch (Exception ex)
            {
                MelonLogger.Error("[VERSUS] !versus: " + ex);
                Reply(caller, "Error: " + ex.GetBaseException().Message);
            }
        }

        static readonly HashSet<string> AdminSubs = new HashSet<string>
        {
            "set", "ceiling", "block", "mapblock", "2v1", "twovone", "save", "reload", "reset",
        };

        static void Handle(Player? caller, string args)
        {
            var tokens = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (tokens.Count > 0 && (tokens[0].StartsWith("!") || tokens[0].StartsWith("/") || tokens[0].StartsWith(".")))
                tokens.RemoveAt(0);   // the Admin Mod passes the whole line, command word included
            string sub = tokens.Count > 0 ? tokens[0].ToLowerInvariant() : "";
            string[] rest = tokens.Skip(1).ToArray();

            if (AdminSubs.Contains(sub) && !AdminBridge.IsAdmin(caller))
            {
                MelonLogger.Warning("[VERSUS] refused !versus " + sub + " from non-admin " + VersusVoteMod.SafeName(caller));
                Reply(caller, "Admin only: !versus " + sub + " needs the Admin Mod Generic power.");
                return;
            }
            string who = Who(caller);
            switch (sub)
            {
                case "": case "status": Status(caller); return;
                case "stats": Stats(caller); return;
                case "help": case "?":
                    Reply(caller, "!layout | !versus [status] - !versus stats");
                    if (AdminBridge.IsAdmin(caller))
                        Reply(caller, "Admin: !versus set [<key> <value>] | block <mode> [on|off] | mapblock [add|remove <map> <mode>] | 2v1 [add|remove <map>] | save | reload | reset");
                    return;
                case "set": foreach (string line in Edit.Set(who, rest)) Reply(caller, line); return;
                case "ceiling": foreach (string line in Edit.Set(who, new[] { "ceiling" }.Concat(rest).ToArray())) Reply(caller, line); return;
                case "block": Reply(caller, Edit.Block(who, rest)); return;
                case "mapblock": Reply(caller, Edit.MapBlock(who, rest)); return;
                case "2v1": case "twovone": Reply(caller, Edit.TwoVsOne(who, rest)); return;
                case "save": Reply(caller, Edit.Save(who)); return;
                case "reload": Reply(caller, Edit.Reload(who)); Status(caller); return;
                case "reset": Reply(caller, Edit.ResetHistory(who)); return;
            }
            Reply(caller, "Unknown option '" + sub + "'. !versus help");
        }

        static string Who(Player? p)
        {
            if (p == null) return "console";
            try { return p.PlayerName + " (" + p.PlayerID + ")"; } catch { return VersusVoteMod.SafeName(p); }
        }

        static void Status(Player? caller)
        {
            foreach (string line in VersusVoteMod.StatusLines()) Reply(caller, line);
        }

        static void Stats(Player? caller)
        {
            var rounds = History.Rounds;
            if (rounds.Count == 0) { Reply(caller, "No rounds recorded yet."); return; }
            Reply(caller, "All " + rounds.Count + " rounds since " + rounds[0].Utc.Replace("T", " ").TrimEnd('Z') + " UTC: " + Shares(rounds));
            var byMap = rounds.GroupBy(r => string.IsNullOrEmpty(r.Map) ? "?" : r.Map, StringComparer.OrdinalIgnoreCase)
                              .OrderByDescending(g => g.Count()).Take(12);
            foreach (var g in byMap) Reply(caller, g.Key + " (" + g.Count() + "): " + Shares(g.ToList()));
        }

        /// <summary>"HvH 5 (25%), HvA 8 (40%), HvHvA 7 (35%, 2v1 2)".</summary>
        internal static string Shares(IReadOnlyCollection<RoundEntry> list)
        {
            var parts = new List<string>();
            foreach (var m in Modes.All)
            {
                int c = list.Count(r => r.Parsed == m);
                if (c == 0 && m == GameModeExt.ETeamsVersus.HUMANS2_VS_ALIENS) continue;
                string pct = (100.0 * c / Math.Max(1, list.Count)).ToString("0", CultureInfo.InvariantCulture) + "%";
                int two = m == GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS ? list.Count(r => r.TwoVsOne) : 0;
                parts.Add(Modes.Short(m) + " " + c + " (" + pct + (two > 0 ? ", 2v1 " + two : "") + ")");
            }
            return string.Join(", ", parts);
        }
    }
}
