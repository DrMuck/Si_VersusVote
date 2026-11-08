# Si_VersusVote

Server-only MelonLoader mod for Silica (dedicated server, 1.0.15). It filters the result of the vanilla
pre-round **Battle Layout** vote of `MP_Strategy`: per round it works out which layouts **count**, and when the vote
picks a layout that does not count, it re-tallies the same votes over the allowed layouts only. Nothing is installed on
the clients.

Reasons a layout may not count: it is disabled on the whole server, it is excluded on the current map, or it has
reached its share ceiling over the last N rounds (for example "at most 4 of the last 20 rounds are 3-way").

Since 0.2.0:

- **2v1 maps** (`map_two_vs_one`, default IndustrialQuarter): the 3-way button is played as Sol + Centauri allied
  against the Aliens, through Si_Alliance. Other mods can read the state through `VersusApi` (see below).
- **Si_ModUI page**: status for everyone, live settings for admins. Every setting is also a chat command.

Optional companions: **Admin Mod** (chat commands, private voter notes; the filter works without it), **Si_Alliance**
with `AllianceApi` (needed for 2v1), **Si_ModUI_Server** 1.6.x (the page).

## How the vote works in vanilla

Decompile of SilicaCore 1.0.15 (`MP_Strategy.cs`, `GameModeExt.cs`):

- After `Restart()` the mission is in `INIT` and `TeamsVersus == NONE`; every client shows the Battle Layout screen
  (`HandleUI`). The buttons come from the client prefab (`BattleLayoutsScreen`), so the server cannot hide one.
- A click sends RPC `VOTE_TEAMVERSUS`. The server stores it in `GameModeExt.TeamsVersusVotes`
  (`Dictionary<Player, ETeamsVersus>`, one entry per player, last vote wins) and rebroadcasts it with
  `RPC_VoteTeamVersus(Player, ETeamsVersus)`.
- The vote countdown only runs while `TeamsVersusVotes.Count > 0`. When it reaches 0 the server calls
  `SetTeamVersusMode(GetTeamsVersusVoteResult())`: most votes wins, ties by coin flip, no votes = `Random.Range(1,4)`
  (HvH, HvA or HvHvA).
- `SetTeamVersusMode` only stores the layout, sets `QueueCoopSpawnBaseStructures` and syncs; the HQs / Nests are
  spawned later from that queue.
- `ETeamsVersus`: NONE=0, HUMANS_VS_HUMANS=1 (HvH), HUMANS_VS_ALIENS=2 (HvA), HUMANS_VS_HUMANS_VS_ALIENS=3 (HvHvA,
  "3-way"), HUMANS2_VS_ALIENS=4 (H2vA, a stub: not on the vote screen, no UI). The mod never applies H2vA.

## What the mod does

| Hook | Kind | Purpose |
|---|---|---|
| `MP_Strategy.SetTeamVersusMode(ETeamsVersus, bool preventSpawning = false)` | prefix, `Priority.First` | if the layout does not count: re-tally, replace the argument, announce; then decide 2v1 and set `VersusApi.TwoVsOneActive` |
| same | postfix | record the applied layout in the history (once per round), schedule the 2v1 alliance |
| `MP_Strategy.RPC_VoteTeamVersus(Player, ETeamsVersus)` (protected) | prefix | private note to a player whose vote will not count, or whose 3-way vote counts as 2v1 (vote untouched) |
| `MP_Strategy.Restart()` | postfix | new round: reset the per-round flags, `TwoVsOneActive = false` |
| `OnUpdate` | - | 2v1 alliance request (from the frame after the postfix, retried up to 5 s); "counted this round" line ~3 s after the vote opens, repeated ~10 s later if nobody voted (0.5 s poll) |

The filter only acts on the plain `MP_Strategy` mode on the server, outside the campaign, and only on the call that picks
the round's layout (mission `INIT`, `TeamsVersus == NONE`). It lets `NONE` (Restart) and `preventSpawning == true`
(sandbox / save load) pass untouched. Coop and Matchmaking (subclasses) and the campaign's forced 3-way are left alone.
`TeamsVersusVotes` is only read, never modified (removing entries would stall the countdown).

### Filter rules (per round)

1. `blocked_modes` - never counts on this server ("disabled on this server").
2. `map_blocked_modes[<map>]` - never counts on that map ("excluded on this map"). Map names as in `mapcycle.txt` /
   the folders in `UserData/cfg/Spawns` (NarakaCity, CrimsonPeak, GreatErg, RiftBasin, NorthPolarCap, MonumentValley,
   CrystalChasm, WhisperingPlains, TheMaw, Badlands, IndustrialQuarter, BlackIsle, Citadel); matched case- and
   space-insensitively against `NetworkGameServer.GetServerMap()` (fallback: the loaded scene name).
3. `ceilings` - layout `m` is blocked when `count_m(last window_rounds rounds) >= max_m`, where
   `max_m = ceiling == 0 ? 0 : max(1, round(ceiling * window_rounds))`. A layout without a ceiling is unlimited; a
   ceiling of 1.0 or more means no limit. The window covers the last `window_rounds` rounds on any map.
   Defaults with `window_rounds` 20: HvHvA 4, HvA 10, HvH 6.
4. If the ceilings (on top of the blocklists) would leave no layout, the ceiling layer is ignored for that round; the
   blocklists still apply.
5. If the blocklists alone leave no layout, nothing is filtered that round (a warning is logged).

When the vote result does not count: votes for allowed layouts are tallied, most votes wins, ties are random. With no
vote for an allowed layout the mod picks at random among the allowed vote-screen layouts (mirrors vanilla's random
pick). Chat example: `3-way was voted but is not counted this round (ceiling 4/20 reached) - playing Humans vs Aliens.`

## 2v1 maps

On a map listed in `map_two_vs_one` (default `["IndustrialQuarter"]`, matched like `map_blocked_modes`):

- A plain HvHvA 3-way is not played. A 3-way vote, or vanilla's random 3-way pick, is a **2v1** request: the vanilla
  layout HUMANS_VS_HUMANS_VS_ALIENS is applied unchanged (so the vote works as usual and layout-dependent code sees 3
  teams), and the two human teams are allied for the whole round via Si_Alliance: Humans + Humans vs Aliens.
- **Same button, same rules.** 2v1 is the HvHvA button. If HvHvA is blocked for that map by `blocked_modes`,
  `map_blocked_modes` or its ceiling, 2v1 is blocked too and the votes are re-tallied as usual. When the filter lets
  HvHvA stand (allowed, `enabled: false`, ceilings ignored, or blocklists excluding everything), 2v1 applies. The map
  list itself is the 2v1 switch; `enabled` does not turn 2v1 off.
- **Ceilings and history.** A 2v1 round is recorded as mode `HvHvA` with `"two_vs_one": true`, so it counts toward the
  HvHvA ceiling. Status, stats and the ModUI page show the 2v1 rounds separately (`HvHvA 4/4 (2v1 1)`).
- **Chat.** Vote-open line on such a map (sent even when nothing is blocked):
  `Layout vote - counted this round: Humans vs Humans, Humans vs Aliens, 2v1 (3-way button = Sol+Centauri allied vs Aliens)`.
  A 3-way voter gets `Your 3-way vote counts as 2v1 on IndustrialQuarter: Sol and Centauri allied against the Aliens.`
  When the alliance is in place: `3-way on IndustrialQuarter is played as 2v1: Sol and Centauri are allied against the Aliens.`
- **Alliance.** The SetTeamVersusMode postfix schedules the request; from the next `OnUpdate` frame on the mod calls
  `Si_Alliance.AllianceApi.ForceAlliance("Sol", "Centauri", locked: true, "2v1 <map>")` by reflection (no compile-time
  reference) and retries every 0.5 s for up to 5 s while it returns false. The result is logged.
- **Fallbacks.** Si_Alliance or its `AllianceApi.ForceAlliance(string, string, bool, string)` not loaded: checked in the
  prefix, so `TwoVsOneActive` is never set; warning in the log and chat
  `2v1 unavailable (Si_Alliance missing) - playing normal 3-way`. ForceAlliance still false after 5 s (or throwing):
  `TwoVsOneActive` goes back to false, `LayoutDecided(false)` fires, the history entry loses its 2v1 flag, chat
  `2v1 unavailable (Si_Alliance refused the alliance) - playing normal 3-way`.
- Ending the alliance at round end is Si_Alliance's job (its per-round reset).

### API for other mods (`VersusApi.cs`)

```csharp
namespace Si_VersusVote {
  public static class VersusApi {
    public const int ApiVersion = 1;
    public static bool TwoVsOneActive { get; }   // see below
    public static string CurrentMap { get; }     // NetworkGameServer.GetServerMap(), fallback last loaded scene
    public static string LayoutShort { get; }    // "HvH", "HvA", "HvHvA", "2v1", "" (NONE)
    public static bool IsTwoVsOneMap(string map);// listed in map_two_vs_one (case/space-insensitive)
    public static event Action<bool> LayoutDecided; // fired right after the flag is set; argument = twoVsOne
  }
}
```

- `TwoVsOneActive` is set **inside** the SetTeamVersusMode prefix, before the original runs, i.e. before vanilla
  queues the HQ/Nest spawn. The prefix is patched with `HarmonyMethod { priority = Priority.First }`, so it runs before
  other mods' prefixes on the same method (Si_MapBalance reads the flag by reflection inside its own
  SetTeamVersusMode / spawn patches). It stays true until `MP_Strategy.Restart` (also cleared by Restart's
  `SetTeamVersusMode(NONE)`).
- It goes back to false mid-round only when 2v1 is withdrawn: Si_Alliance refused the alliance, or another mod's
  prefix changed the layout away from HvHvA (detected in the postfix). `LayoutDecided` then fires a second time with
  `false`. Handlers run inside the Harmony prefix (or OnUpdate for the withdrawal); exceptions are caught and logged.
- `LayoutShort` is `"2v1"` while `TwoVsOneActive`, else the layout this mod decided / saw applied, else the live
  `TeamsVersus` of the current game mode.

Reflection example:

```csharp
var t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Si_VersusVote.VersusApi", false)).FirstOrDefault(x => x != null);
bool twoVsOne = t != null && (bool)t.GetProperty("TwoVsOneActive").GetValue(null);
```

## Si_ModUI page

With **Si_ModUI_Server** (contract `SiModUI/2`) the mod has its own page in the F6 menu. The ModUI server finds
`public static string ModUIData()` on `Si_VersusVote.VersusModUI` by reflection on the `Si_VersusVote` assembly (the one
that registered `/versus`). The document is cached and rebuilt after a change or every 5 s. Every widget sends a
`/versus` chat command, so everything works from chat too.

| Who | Shows / does |
|---|---|
| everyone | map, counted / not counted this round with reasons, window counts (`last 20: HvHvA 4/4 (2v1 1), HvA 7/10, HvH 5/6`), 2v1 state; table "This round" (layout, counted, reason, count/max), table "All N rounds" (shares, 2v1 row), table "Per-map exclusions", server-wide disabled list + 2v1 maps; buttons Status, Statistics, Help |
| admins | switches filter / announce / debug; value rows window_rounds, ceiling HvHvA (= 2v1), HvA, HvH, vote line delay / repeat; multi-select "Disabled on this server" (HvH / HvA / HvHvA, click toggles); pickers "Exclude a layout on a map" (map:mode), "Remove a map exclusion", "Add a 2v1 map", "Remove a 2v1 map"; buttons All settings, Save config, Reload config, Reset history |

**Admin gating** (same as Si_WaspHarvest): rows exported with `admin: true` (`"a": true`) are merged by the ModUI server
only into frames of players for whom `Player.IsAdmin()` is true; other players never receive them. That is display only.
`/versus` is registered as a *player* command, so the Admin Mod's dispatcher does not check powers for it; the mod checks
`Player.CanAdminExecute(Power.Generic)` itself for every edit sub-command (`set`, `ceiling`, `block`, `mapblock`, `2v1`,
`save`, `reload`, `reset`) on the server, whether typed or sent from the page. A non-admin gets
`Admin only: !versus <sub> needs the Admin Mod Generic power.` and the attempt is logged.

## Config

`UserData/cfg/VersusVote/Si_VersusVote_Config.json`, written with defaults on first start; a 0.1.0 file gets
`map_two_vs_one` added on first load. Mode names: `HvH`, `HvA`, `HvHvA` (also `3way`, `3-way`), `H2vA`, or the enum
names (`HUMANS_VS_ALIENS` ...). Unknown names are logged and ignored. Live edits (chat or page) rewrite the file.

Default:

```json
{
  "enabled": true,
  "announce": true,
  "blocked_modes": [],
  "map_blocked_modes": {},
  "map_two_vs_one": [ "IndustrialQuarter" ],
  "ceilings": {
    "HvHvA": 0.2,
    "HvA": 0.5,
    "HvH": 0.3
  },
  "window_rounds": 20,
  "history_file": "history.json",
  "announce_delay_s": 3.0,
  "announce_repeat_s": 10.0,
  "debug": false
}
```

Example with per-map exclusions (no 3-way on the two small maps, HvH never on Badlands, no ceiling on HvA):

```json
{
  "map_blocked_modes": {
    "NarakaCity": ["HvHvA"],
    "CrystalChasm": ["3-way"],
    "Badlands": ["HvH"]
  },
  "ceilings": {
    "HvHvA": 0.2,
    "HvH": 0.3
  }
}
```

| Key | Meaning |
|---|---|
| `enabled` | false = never touch the vote result (history is still recorded, `!layout` still works, 2v1 still applies) |
| `announce` | false = no chat output (console log only); command replies still go to the caller |
| `blocked_modes` | layouts that never count on this server |
| `map_blocked_modes` | map -> layouts that never count there |
| `map_two_vs_one` | maps where the 3-way button plays 2v1 (Sol + Centauri allied vs Aliens); empty = no 2v1 |
| `ceilings` | layout -> max share of the window (0 = never, absent = unlimited); 2v1 rounds count as HvHvA |
| `window_rounds` | rounds the ceilings look back over (1..1000) |
| `history_file` | history path, relative to the config folder unless absolute |
| `announce_delay_s` | delay of the vote-open line after the vote phase starts (0..60) |
| `announce_repeat_s` | repeat the line this much later if nobody has voted (0 = off, max 120) |
| `debug` | extra console logging |

The vote-open line is only sent when at least one layout does not count, or when the map is a 2v1 map with 2v1
counted; otherwise the decision is only logged.

### History

`history.json` keeps every played round (`utc`, `map`, `mode`, and `two_vs_one: true` for 2v1 rounds) for statistics;
only the last `window_rounds` entries count toward the ceilings. A round is recorded when the server applies a non-NONE
layout through the vote path (not for `preventSpawning` loads), once per round.

## Commands (Admin Mod required for commands and private notes; the filter works without it)

| Command | Who | Output |
|---|---|---|
| `!layout`, `!versus`, `!versus status` | everyone | counted / not counted this round with reasons; `last 20: HvHvA 4/4, HvA 7/10, HvH 5/6` (count/max, `-` = unlimited); 2v1 line |
| `!versus stats` | everyone | shares over the whole history (2v1 separately), plus one line per map (top 12) |
| `!versus help` | everyone | command list (admin part for admins) |
| `!versus set` | admin | list every key with value and range |
| `!versus set <key> <value>` | admin | `enabled`, `announce`, `debug` (on/off), `window_rounds` (1..1000, `+N`/`-N`), `announce_delay_s`, `announce_repeat_s`, `ceiling_hvh` / `ceiling_hva` / `ceiling_hvhva` (0..1, `20%`, `none` = unlimited); also `!versus ceiling HvHvA 0.2` |
| `!versus block <mode> [on\|off]` | admin | toggle / set a server-wide blocked layout |
| `!versus mapblock [list]`, `add <map> <mode>`, `remove <map> [mode]` | admin | per-map exclusions (`<map>:<mode>` also works; `remove <map>` clears the map) |
| `!versus 2v1 [list]`, `add <map>`, `remove <map>` | admin | 2v1 maps (alias `twovone`) |
| `!versus save` | admin | write the config file now (every edit already saves) |
| `!versus reload` | admin | reload config and history |
| `!versus reset` | admin | clear the history (old file kept as `history.json.bak`) |

Admin = Admin Mod `Power.Generic`, checked by the mod on the server (see "Admin gating"). Unknown map names are accepted
with a spelling note; known names come from the game's level list and the `UserData/cfg/Spawns` folders.

## Build

```
cd Si_VersusVote
dotnet build -c Release                           # test branch 1.0.15 (release build)
dotnet build -c Release -p:SilicaBranch=default   # 0.9.47 refs, output bin\Release\default\
```

References come from `include\<branch>\` (git-ignored; `include\test` = `Projects\_silica_refs\test` with SilicaCore
1.0.15 from the dedicated server, `include\default` = `Projects\_silica_refs\default`). `ModUI_Export.cs` is the shared
Si_ModUI builder (copied from Si_WaspHarvest, Newtonsoft only).

## Caveats

- The blocked button stays visible and clickable on the clients (baked into the prefab); players learn from chat that
  their vote does not count. The on-screen vote counts still include blocked votes.
- Any other mod that sets the layout during the vote phase (for example data-bomb's Versus Auto-Select) goes through the
  same filter and the same 2v1 decision.
- The ceiling window counts rounds on all maps together, not per map.
- 2v1 needs Si_Alliance's `AllianceApi` (added after Si_Alliance 0.2.2). The team names passed are "Sol" and
  "Centauri"; Si_Alliance resolves names / short names case-insensitively.
- Not run live yet (0.2.0; 0.1.0 was not run live either).
