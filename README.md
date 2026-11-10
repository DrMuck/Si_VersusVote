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

Since 0.3.0:

- **2v1 lone-team compensation** (config section `two_vs_one`): the lone team (Aliens) gets its starting resources
  x `lone_start_money_mult` (default 2.0), and Si_StarterUnits 1.2.0 multiplies its starter unit counts by
  `lone_starter_units_mult` (default 2). `VersusApi` v2 exposes the lone team and both multipliers.

Since 0.3.1:

- **Four layout buttons.** The vanilla Battle Layout screen has four buttons, not three: Cent vs Sol (HvH, enum 1),
  Alien vs Sol (**SvA**, enum 2), Alien vs Cent (**CvA**, enum 4) and Sol vs Cent vs Alien (HvHvA, enum 3). Up to 0.3.0
  the mod treated enum 4 as a stub ("H2vA"): not counted, no ceiling, never in the random pool. It is a plain two-team
  round Centauri vs Aliens and is now handled like the others.
- **Category HvA** = Sol vs Aliens + Centauri vs Aliens. The `HvA` ceiling limits both together; `HvA` in a blocklist
  blocks both, `SvA` / `CvA` block one of them. Existing config files keep working unchanged.
- **History compatibility.** New rows are written as `SvA` / `CvA`; rows written as `HvA` (= Sol vs Aliens) and
  `H2vA` (= Centauri vs Aliens) by older versions still load and count.

Since 0.3.2:

- **2v1 maps ignore HvHvA blocklists.** On a map in `map_two_vs_one` (with Si_Alliance loaded) the 3-way button plays
  Sol + Centauri vs Aliens, which the admin chose by listing the map. HvHvA entries in `blocked_modes` and
  `map_blocked_modes` no longer block it there; only the HvHvA ceiling does. Without Si_Alliance the button falls back
  to a normal 3-way and the blocklists apply again. To block 3-way entirely on such a map, remove the map from the 2v1
  list and keep the HvHvA exclusion.

Optional companions: **Admin Mod** (chat commands, private voter notes; the filter works without it), **Si_Alliance**
with `AllianceApi` (needed for 2v1), **Si_ModUI_Server** 1.6.x (the page), **Si_StarterUnits** 1.2.0 (lone-team starter
unit multiplier).

## How the vote works in vanilla

Decompile of SilicaCore 1.0.15 (`MP_Strategy.cs`, `GameModeExt.cs`):

- After `Restart()` the mission is in `INIT` and `TeamsVersus == NONE`; every client shows the Battle Layout screen
  (`HandleUI`). The buttons come from the client prefab (`BattleLayoutsScreen`), so the server cannot hide one.
- A click sends RPC `VOTE_TEAMVERSUS`. The server stores it in `GameModeExt.TeamsVersusVotes`
  (`Dictionary<Player, ETeamsVersus>`, one entry per player, last vote wins) and rebroadcasts it with
  `RPC_VoteTeamVersus(Player, ETeamsVersus)`.
- The vote countdown only runs while `TeamsVersusVotes.Count > 0`. When it reaches 0 the server calls
  `SetTeamVersusMode(GetTeamsVersusVoteResult())`: most votes wins, ties by coin flip, no votes = `Random.Range(1,4)`
  (HvH, Sol vs Aliens or HvHvA - never Centauri vs Aliens, which only comes from real votes).
- `SetTeamVersusMode` only stores the layout, sets `QueueCoopSpawnBaseStructures` and syncs; the HQs / Nests are
  spawned later from that queue.
- The screen has **four** buttons (client prefab): "Cent vs Sol" = HUMANS_VS_HUMANS (1), "Alien vs Sol" =
  HUMANS_VS_ALIENS (2), "Alien vs Cent" = HUMANS2_VS_ALIENS (4), "Sol vs Cent vs Alien" = HUMANS_VS_HUMANS_VS_ALIENS (3).
  The teams come from the map's team setups (`BaseTeamSetup.ForTeamVersusModes`): Sol {1,2,3}, Centauri {1,3,4},
  Alien {2,3,4}. So enum 4 is a plain two-team round Centauri vs Aliens.

| Enum | Button | Mod short name | Display | Ceiling category |
|---|---|---|---|---|
| 1 HUMANS_VS_HUMANS | Cent vs Sol | `HvH` | Humans vs Humans | HvH |
| 2 HUMANS_VS_ALIENS | Alien vs Sol | `SvA` (old rows: `HvA`) | Sol vs Aliens | HvA |
| 4 HUMANS2_VS_ALIENS | Alien vs Cent | `CvA` (old rows: `H2vA`) | Centauri vs Aliens | HvA |
| 3 HUMANS_VS_HUMANS_VS_ALIENS | Sol vs Cent vs Alien | `HvHvA` | 3-way (2v1 on a 2v1 map) | HvHvA |

## What the mod does

| Hook | Kind | Purpose |
|---|---|---|
| `MP_Strategy.SetTeamVersusMode(ETeamsVersus, bool preventSpawning = false)` | prefix, `Priority.First` | if the layout does not count: re-tally, replace the argument, announce; then decide 2v1 and set `VersusApi.TwoVsOneActive` |
| same | postfix | record the applied layout in the history (once per round), schedule the 2v1 alliance |
| `MP_Strategy.RPC_VoteTeamVersus(Player, ETeamsVersus)` (protected) | prefix | private note to a player whose vote will not count, or whose 3-way vote counts as 2v1 (vote untouched) |
| `MP_Strategy.Restart()` | postfix | new round: reset the per-round flags, `TwoVsOneActive = false`, lone team cleared |
| `MP_Strategy.SpawnStructuresAndStartingResources()` (protected virtual) | postfix, `Priority.Last` | 2v1: lone team's starting money x `lone_start_money_mult` (see "Lone-team compensation") |
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
3. `ceilings` - per category (`HvHvA`, `HvA`, `HvH`). A layout is blocked when the count of its category in the last
   `window_rounds` rounds reaches `max = ceiling == 0 ? 0 : max(1, round(ceiling * window_rounds))`. For **HvA** the
   count is Sol vs Aliens + Centauri vs Aliens together, and reaching the max blocks both. A category without a
   ceiling is unlimited; a ceiling of 1.0 or more means no limit. The window covers the last `window_rounds` rounds on
   any map. Defaults with `window_rounds` 20: HvHvA 4, HvA 10, HvH 6. A `ceilings` key `SvA`, `CvA` or `H2vA` is ignored
   with a warning (the two alien layouts have no ceiling of their own).
4. If the ceilings (on top of the blocklists) would leave no layout, the ceiling layer is ignored for that round; the
   blocklists still apply.
5. If the blocklists alone leave no layout, nothing is filtered that round (a warning is logged).

When the vote result does not count: votes for allowed layouts are tallied, most votes wins, ties are random. With no
vote for an allowed layout the mod picks at random among the allowed vote-screen layouts (all four buttons, so
Centauri vs Aliens is in the pool too, unlike vanilla's own 1..3 pick).

Chat lines (prefix `[LAYOUT]`):

- vote open / reminder (only when a layout does not count, or on a 2v1 map):
  `Layout vote - counted this round: Humans vs Humans, Sol vs Aliens, Centauri vs Aliens. Not counted: 3-way (ceiling 4/20 reached)`
  - both alien layouts blocked for the same reason are one entry:
    `Layout vote - counted this round: Humans vs Humans, 3-way. Not counted: Sol + Centauri vs Aliens (ceiling 11/20 reached together)`
  - one of them: `... Not counted: Centauri vs Aliens (excluded on this map)`
- replacement: `3-way was voted but is not counted this round (ceiling 4/20 reached) - playing Sol vs Aliens.` /
  `Centauri vs Aliens was voted but is not counted this round (ceiling 11/20 reached, Sol + Centauri vs Aliens together) - playing Humans vs Humans.`
- private note to a voter: `Your Sol vs Aliens vote will not be counted this round (ceiling 11/20 reached, Sol + Centauri vs Aliens together). Counted: Humans vs Humans, 3-way.`

## 2v1 maps

On a map listed in `map_two_vs_one` (default `["IndustrialQuarter"]`, matched like `map_blocked_modes`):

- A plain HvHvA 3-way is not played. A 3-way vote, or vanilla's random 3-way pick, is a **2v1** request: the vanilla
  layout HUMANS_VS_HUMANS_VS_ALIENS is applied unchanged (so the vote works as usual and layout-dependent code sees 3
  teams), and the two human teams are allied for the whole round via Si_Alliance: Humans + Humans vs Aliens.
- **Same button, own blocklist rule (0.3.2).** 2v1 is the HvHvA button. The HvHvA **ceiling** applies to it as to any
  3-way (2v1 rounds count as HvHvA). The HvHvA entries of `blocked_modes` and `map_blocked_modes` do **not** apply to
  it while Si_Alliance's AllianceApi is loaded: listing the map under 2v1 is the admin's explicit choice, and the button
  never plays a free-for-all there. If Si_Alliance is missing, the button is a normal 3-way and the blocklists apply
  again. **To block 3-way entirely on a map, remove it from `map_two_vs_one` and keep the HvHvA map exclusion.**
  When the filter lets HvHvA stand (allowed, `enabled: false`, ceilings ignored, or blocklists excluding everything),
  2v1 applies. The map list itself is the 2v1 switch; `enabled` does not turn 2v1 off.
- **Visible override.** When an HvHvA block is overridden this way, `!versus` status and the ModUI page show
  `HvHvA is excluded on Citadel, but Citadel is a 2v1 map: the 3-way button plays 2v1 and stays votable. Remove Citadel
  from the 2v1 list to block it.` (server-wide: `HvHvA is disabled on this server, but ...`), and the ModUI "This
  round" table shows `map exclusion HvHvA not applied (2v1 map)` in the Reason column of the 2v1 row. The same note is
  the reply to `!versus mapblock add <2v1 map> HvHvA` and to `!versus 2v1 add <map>` on a map with such an exclusion;
  `!versus block HvHvA` lists the 2v1 maps that keep their button.
- **Ceilings and history.** A 2v1 round is recorded as mode `HvHvA` with `"two_vs_one": true`, so it counts toward the
  HvHvA ceiling. Status, stats and the ModUI page show the 2v1 rounds separately (`HvHvA 4/4 (2v1 1)`).
- **Chat.** Vote-open line on such a map (sent even when nothing is blocked):
  `Layout vote - counted this round: Humans vs Humans, Sol vs Aliens, Centauri vs Aliens, 2v1 (3-way button = Sol+Centauri allied vs Aliens)`.
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

### Lone-team compensation (0.3.0)

The lone team is the team of the round that is not in the alliance. It is derived in the SetTeamVersusMode prefix,
together with `TwoVsOneActive`: among `MP_Strategy.TeamSetups` present in HvHvA (`Enabled`, `ForTeamVersusModes`
contains HUMANS_VS_HUMANS_VS_ALIENS, `Team` set, not `IsSpecial`), the one whose TeamShortName / TeamName does not
start with "Sol" or "Centauri" (the names passed to ForceAlliance). On the vanilla scenes that is `Alien`. If not
exactly one team is left, a warning is logged and the round has no compensation (2v1 itself still applies).
(`GetTeamSetupActive` cannot be used there: it needs `TeamsVersus`, which the original has not set yet.)

**Starting money** (`lone_start_money_mult`, float 1..10, default 2.0, 1 = off). Vanilla 1.0.15 sets each team's
starting money at exactly one place per round: `MP_Strategy.SpawnStructuresAndStartingResources()` (MP_Strategy.cs
2734-2755, the assignment `teamSetup.Team.StartingResources = Max(Ceil(teamSetup.StartingResources * MultStartingRes *
scale), first deposit cost)` at line 2750). `SetTeamVersusMode` only queues it (`QueueCoopSpawnBaseStructures`); it
runs on the first player team change after the layout vote (MP_Strategy.cs 1954-1957), together with the HQ / Nest
spawn. Si_Resources (the build deployed for 1.0.x) and Si_RTS_AI's StartingResourcesGuard overwrite `Team.StartingResources` in their own
postfixes on the same method, so this mod's postfix runs with `Priority.Last` and reads the final amount `S`. It then
adds `ceil(S * (mult - 1))` to `Team.StartingResources` of the lone team, once per round, **when both** have happened
(either order): the money was set, and Si_Alliance confirmed the alliance (AllianceBridge success). In a normal round
the alliance is confirmed ~0.2 s after the vote and the bases spawn when the first player picks a team, so the bonus
is granted in the postfix. If the alliance is refused, 2v1 is withdrawn before anything was granted (no bonus); should
2v1 ever be withdrawn after a grant, what is left of the bonus is taken back. Log:
`[VERSUS] 2v1: Alien starting resources 9000 -> 18000 (x2)`. Si_MapBalance does not touch starting money.

**Starter units** (`lone_starter_units_mult`, integer 1..10, default 2, 1 = off). Applied by Si_StarterUnits 1.2.0
(reads `VersusApi.IsLoneTeam` / `LoneStarterUnitsMult` when it spawns, round start + 5 s); this mod only stores the
value. Si_StarterUnits keys on `TwoVsOneActive` + `LoneTeam` from the prefix, not on the alliance confirmation.

Both values are read live: an edit applies to whatever is granted after it. Si_Alliance's own `lone_income_mult`
(harvest income bonus) is independent of these.

### API for other mods (`VersusApi.cs`)

```csharp
namespace Si_VersusVote {
  public static class VersusApi {
    public const int ApiVersion = 2;              // 2 since 0.3.0; every v1 member unchanged
    public static bool TwoVsOneActive { get; }   // see below
    public static string CurrentMap { get; }     // NetworkGameServer.GetServerMap(), fallback last loaded scene
    public static string LayoutShort { get; }    // "HvH", "SvA", "CvA", "HvHvA", "2v1", "" (NONE); before 0.3.1 "HvA" / "H2vA"
    public static bool IsTwoVsOneMap(string map);// listed in map_two_vs_one (case/space-insensitive)
    public static event Action<bool> LayoutDecided; // fired right after the flag is set; argument = twoVsOne
    // v2 (0.3.0)
    public static string LoneTeam { get; }            // TeamShortName of the lone team ("Alien"); "" when not 2v1
    public static float LoneStartMoneyMult { get; }   // two_vs_one.lone_start_money_mult while 2v1 (with a lone team), else 1
    public static int LoneStarterUnitsMult { get; }   // two_vs_one.lone_starter_units_mult while 2v1 (with a lone team), else 1
    public static bool IsLoneTeam(Team team);         // same Team object, or TeamShortName / TeamName equal (case-insensitive)
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
- The v2 members are set in the same prefix, before `LayoutDecided` fires, and cleared with `TwoVsOneActive`
  (Restart, withdrawal). The multipliers read the live config.
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
| everyone | map, counted / not counted this round with reasons, window counts (`last 20: HvHvA 4/6 (2v1 1), HvA 10/11 (Sol 8, Cent 2), HvH 6/7`), 2v1 state; table "This round" (four rows Humans vs Humans, Sol vs Aliens, Centauri vs Aliens, 3-way / 2v1: counted, reason, count/max; the alien rows show their own count plus the shared HvA count, `8 (HvA 10/11)`), table "All N rounds" (HvH, Sol vs Aliens, Centauri vs Aliens, Humans vs Aliens total, HvHvA, of which 2v1), table "Per-map exclusions", server-wide disabled list + 2v1 maps; buttons Status, Statistics, Help |
| admins | switches filter / announce / debug; value rows 2v1 lone team start money (x) and starter units (x), window_rounds, ceiling HvHvA (= 2v1), ceiling HvA (Sol vs Aliens + Centauri vs Aliens together), ceiling HvH, vote line delay / repeat; multi-select "Disabled on this server" (HvH / HvA / SvA / CvA / HvHvA, click toggles); pickers "Exclude a layout on a map" (map:mode, same five names), "Remove a map exclusion", "Add a 2v1 map", "Remove a 2v1 map"; buttons All settings, Save config, Reload config, Reset history |

**Admin gating** (same as Si_WaspHarvest): rows exported with `admin: true` (`"a": true`) are merged by the ModUI server
only into frames of players for whom `Player.IsAdmin()` is true; other players never receive them. That is display only.
`/versus` is registered as a *player* command, so the Admin Mod's dispatcher does not check powers for it; the mod checks
`Player.CanAdminExecute(Power.Generic)` itself for every edit sub-command (`set`, `ceiling`, `block`, `mapblock`, `2v1`,
`save`, `reload`, `reset`) on the server, whether typed or sent from the page. A non-admin gets
`Admin only: !versus <sub> needs the Admin Mod Generic power.` and the attempt is logged.

## Config

`UserData/cfg/VersusVote/Si_VersusVote_Config.json`, written with defaults on first start; an older file gets the
missing keys (`map_two_vs_one` from 0.2.0, the `two_vs_one` section from 0.3.0) added on first load. `map_two_vs_one`
stays a top-level key (unchanged). Layout names in `blocked_modes` / `map_blocked_modes`: `HvH`, `HvA` (Sol vs Aliens
**and** Centauri vs Aliens), `SvA`, `CvA` (also `H2vA`), `HvHvA` (also `3way`, `3-way`), the enum names
(`HUMANS_VS_ALIENS` / "Humans vs Aliens" = the HvA category, `HUMANS2_VS_ALIENS` = CvA) or 1..4. `ceilings` keys:
`HvHvA`, `HvA`, `HvH` only. Unknown names are logged and ignored. Live edits (chat or page) rewrite the file; a
blocklist edited live is written back as canonical names (`HvA` when both alien layouts are blocked, else `SvA` / `CvA`).
0.3.0 files load unchanged; an `"HvA"` entry in a blocklist now blocks both alien layouts.

Default:

```json
{
  "enabled": true,
  "announce": true,
  "blocked_modes": [],
  "map_blocked_modes": {},
  "map_two_vs_one": [ "IndustrialQuarter" ],
  "two_vs_one": {
    "lone_start_money_mult": 2.0,
    "lone_starter_units_mult": 2
  },
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
| `blocked_modes` | layouts that never count on this server (`HvA` = both alien layouts) |
| `map_blocked_modes` | map -> layouts that never count there |
| `map_two_vs_one` | maps where the 3-way button plays 2v1 (Sol + Centauri allied vs Aliens); HvHvA blocklist entries do not block it there (only the HvHvA ceiling); empty = no 2v1 |
| `two_vs_one.lone_start_money_mult` | 2v1: lone team's starting resources x this (1..10, 1 = off), granted once the alliance is confirmed |
| `two_vs_one.lone_starter_units_mult` | 2v1: Si_StarterUnits multiplies the lone team's starter unit counts by this integer (1..10, 1 = off) |
| `ceilings` | category `HvHvA` / `HvA` / `HvH` -> max share of the window (0 = never, absent = unlimited); HvA counts Sol vs Aliens + Centauri vs Aliens together; 2v1 rounds count as HvHvA |
| `window_rounds` | rounds the ceilings look back over (1..1000) |
| `history_file` | history path, relative to the config folder unless absolute |
| `announce_delay_s` | delay of the vote-open line after the vote phase starts (0..60) |
| `announce_repeat_s` | repeat the line this much later if nobody has voted (0 = off, max 120) |
| `debug` | extra console logging |

The vote-open line is only sent when at least one layout does not count, or when the map is a 2v1 map with 2v1
counted; otherwise the decision is only logged.

### History

`history.json` keeps every played round (`utc`, `map`, `mode`, and `two_vs_one: true` for 2v1 rounds) for statistics;
only the last `window_rounds` entries count toward the ceilings. `mode` is `HvH`, `SvA`, `CvA` or `HvHvA` since 0.3.1;
older rows say `HvA` (Sol vs Aliens) or `H2vA` (Centauri vs Aliens) and keep loading. (A downgrade to 0.3.0 would drop
`SvA` / `CvA` rows on load.) A round is recorded when the server applies a non-NONE
layout through the vote path (not for `preventSpawning` loads), once per round.

## Commands (Admin Mod required for commands and private notes; the filter works without it)

| Command | Who | Output |
|---|---|---|
| `!layout`, `!versus`, `!versus status` | everyone | counted / not counted this round with reasons; `last 20: HvHvA 4/6 (2v1 1), HvA 10/11 (Sol 8, Cent 2), HvH 6/7` (count/max per category, `-` = unlimited); 2v1 line |
| `!versus stats` | everyone | shares over the whole history (`HvH 5 (25%), HvA 8 (40%: Sol 6, Cent 2), HvHvA 7 (35%, 2v1 2)`), plus one line per map (top 12) |
| `!versus help` | everyone | command list (admin part for admins) |
| `!versus set` | admin | list every key with value and range |
| `!versus set <key> <value>` | admin | `enabled`, `announce`, `debug` (on/off), `window_rounds` (1..1000, `+N`/`-N`), `announce_delay_s`, `announce_repeat_s`, `ceiling_hvh` / `ceiling_hva` (Sol vs Aliens + Centauri vs Aliens together) / `ceiling_hvhva` (0..1, `20%`, `none` = unlimited), `lone_money` (1..10, `x2` works) / `lone_units` (integer 1..10); also `!versus ceiling HvHvA 0.2` |
| `!versus block <mode> [on\|off]` | admin | toggle / set a server-wide blocked layout: `HvH`, `HvA` (both alien layouts), `SvA`, `CvA`, `HvHvA` |
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

Offline rules check (no game needed; loads the built test-branch DLL and calls the internals by reflection): HvA
ceiling over both alien layouts, blocklists, ceiling-layer fallback, `H2vA` history rows:

```
cd tools\RulesCheck
dotnet run -c Release [-- <path to a history.json>]   # exit code 0 = all checks passed
```

## Caveats

- The blocked button stays visible and clickable on the clients (baked into the prefab); players learn from chat that
  their vote does not count. The on-screen vote counts still include blocked votes.
- Any other mod that sets the layout during the vote phase (for example data-bomb's Versus Auto-Select) goes through the
  same filter and the same 2v1 decision.
- The ceiling window counts rounds on all maps together, not per map.
- 2v1 needs Si_Alliance's `AllianceApi` (added after Si_Alliance 0.2.2). The team names passed are "Sol" and
  "Centauri"; Si_Alliance resolves names / short names case-insensitively.
- 0.2.0's 2v1 path ran on the local test server on 2026-11-08 (one player, IndustrialQuarter: layout decided, alliance
  OK after 1 attempt). The 0.3.0 lone-team compensation, the 0.3.1 four-button handling and the 0.3.2 2v1 blocklist
  rule are not run live yet.
