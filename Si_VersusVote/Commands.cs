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

    /// <summary>!layout / !versus [status|stats|reload|reset|help]. Read-only for everyone; reload/reset need Power.Generic.</summary>
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

        static void Handle(Player? caller, string args)
        {
            string[] tokens = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "": case "status": Status(caller); return;
                case "stats": Stats(caller); return;
                case "help": case "?":
                    Reply(caller, "!layout | !versus [status] - !versus stats" + (AdminBridge.IsAdmin(caller) ? " - admin: !versus reload | reset" : ""));
                    return;
                case "reload":
                    if (!AdminBridge.IsAdmin(caller)) { Reply(caller, "Admin only."); return; }
                    VersusVoteMod.Cfg = VersusConfig.Load();
                    History.Load(VersusVoteMod.Cfg.HistoryPath);
                    MelonLogger.Msg("[VERSUS] reloaded by " + VersusVoteMod.SafeName(caller) + ": " + VersusVoteMod.Cfg.Summary());
                    Reply(caller, "Config reloaded (" + History.Rounds.Count + " rounds in history).");
                    Status(caller);
                    return;
                case "reset":
                    if (!AdminBridge.IsAdmin(caller)) { Reply(caller, "Admin only."); return; }
                    int n = History.Reset();
                    MelonLogger.Msg("[VERSUS] history reset by " + VersusVoteMod.SafeName(caller) + " (" + n + " rounds, old file kept as .bak)");
                    Reply(caller, "History cleared (" + n + " rounds; backup history.json.bak).");
                    return;
            }
            Reply(caller, "Unknown option '" + sub + "'. !versus help");
        }

        static void Status(Player? caller)
        {
            string map = VersusVoteMod.CurrentMap();
            var ev = Rules.Evaluate(VersusVoteMod.Cfg, map);
            string head = "Map " + (map.Length > 0 ? map : "?") + " | ";
            if (!VersusVoteMod.Cfg.Enabled) head += "filter OFF (enabled=false) | ";
            else if (ev.FilterOff) head += "blocklists exclude every layout: nothing filtered | ";

            var blocked = ev.BlockedVotable;
            string line = head + "counted: " + Modes.JoinDisplay(ev.AllowedVotable);
            if (blocked.Count > 0)
                line += " | not counted: " + string.Join(", ", blocked.Select(m => Modes.Display(m) + " (" + ev.Reason(m) + ")"));
            Reply(caller, line);

            string w = ev.WindowText();
            if (ev.CeilingIgnored) w += " (ceilings ignored this round: they would leave no layout)";
            Reply(caller, w);
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

        static string Shares(IReadOnlyCollection<RoundEntry> list)
        {
            var parts = new List<string>();
            foreach (var m in Modes.All)
            {
                int c = list.Count(r => r.Parsed == m);
                if (c == 0 && m == GameModeExt.ETeamsVersus.HUMANS2_VS_ALIENS) continue;
                parts.Add(Modes.Short(m) + " " + c + " (" + (100.0 * c / Math.Max(1, list.Count)).ToString("0", CultureInfo.InvariantCulture) + "%)");
            }
            return string.Join(", ", parts);
        }
    }
}
