# Si_VersusVote

Server-only MelonLoader mod for Silica (dedicated server, 1.0.15). It filters the result of the vanilla
pre-round **Battle Layout** vote of `MP_Strategy`: per round it works out which layouts **count**, and when the vote
picks a layout that does not count, it re-tallies the same votes over the allowed layouts only. Nothing is installed on
the clients.

Reasons a layout may not count: it is disabled on the whole server, it is excluded on the current map, or it has
reached its share ceiling over the last N rounds (for example "at most 4 of the last 20 rounds are 3-way").

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
- `ETeamsVersus`: NONE=0, HUMANS_VS_HUMANS=1 (HvH), HUMANS_VS_ALIENS=2 (HvA), HUMANS_VS_HUMANS_VS_ALIENS=3 (HvHvA,
  "3-way"), HUMANS2_VS_ALIENS=4 (H2vA, not on the vote screen).

## What the mod does

| Hook | Kind | Purpose |
|---|---|---|
| `MP_Strategy.SetTeamVersusMode(ETeamsVersus, bool preventSpawning = false)` | prefix | if the layout does not count: re-tally, replace the argument, announce |
| same | postfix | record the applied layout in the history (once per round) |
| `MP_Strategy.RPC_VoteTeamVersus(Player, ETeamsVersus)` (protected) | prefix | private note to a player whose vote will not count (vote untouched) |
| `MP_Strategy.Restart()` | postfix | new round: reset the per-round flags |
| `OnUpdate` (0.5 s poll) | - | "counted this round" line ~3 s after the vote opens, repeated ~10 s later if nobody voted |

The filter only acts on the plain `MP_Strategy` mode on the server, outside the campaign, and only on the call that picks
the round's layout (mission `INIT`, `TeamsVersus == NONE`). It lets `NONE` (Restart) and `preventSpawning == true`
(sandbox / save load) pass untouched. Coop and Matchmaking (subclasses) and the campaign's forced 3-way are left alone.
`TeamsVersusVotes` is only read, never modified (removing entries would stall the countdown).

### Filter rules (per round)

1. `blocked_modes` - never counts on this server ("disabled on this server").
2. `map_blocked_modes[<map>]` - never counts on that map ("excluded on this map"). Map names as in `mapcycle.txt`
   (NarakaCity, CrimsonPeak, GreatErg, RiftBasin, NorthPolarCap, Monumentvalley, CrystalChasm, WhisperingPlains,
   TheMaw, Badlands); matched case- and space-insensitively against `NetworkGameServer.GetServerMap()` (fallback: the
   loaded scene name).
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

### History

`history.json` keeps every played round (`utc`, `map`, `mode`) for statistics; only the last `window_rounds` entries
count toward the ceilings. A round is recorded when the server applies a non-NONE layout through the vote path
(not for `preventSpawning` loads), once per round.

## Config

`UserData/cfg/VersusVote/Si_VersusVote_Config.json`, written with defaults on first start. Mode names: `HvH`, `HvA`,
`HvHvA` (also `3way`, `3-way`), `H2vA`, or the enum names (`HUMANS_VS_ALIENS` ...). Unknown names are logged and ignored.

Default:

```json
{
  "enabled": true,
  "announce": true,
  "blocked_modes": [],
  "map_blocked_modes": {},
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
  "enabled": true,
  "announce": true,
  "blocked_modes": [],
  "map_blocked_modes": {
    "NarakaCity": ["HvHvA"],
    "CrystalChasm": ["3-way"],
    "Badlands": ["HvH"]
  },
  "ceilings": {
    "HvHvA": 0.2,
    "HvH": 0.3
  },
  "window_rounds": 20,
  "history_file": "history.json",
  "announce_delay_s": 3.0,
  "announce_repeat_s": 10.0,
  "debug": false
}
```

| Key | Meaning |
|---|---|
| `enabled` | false = never touch the result (history is still recorded, `!layout` still works) |
| `announce` | false = no chat output (console log only); command replies still go to the caller |
| `blocked_modes` | layouts that never count on this server |
| `map_blocked_modes` | map -> layouts that never count there |
| `ceilings` | layout -> max share of the window (0 = never, absent = unlimited) |
| `window_rounds` | rounds the ceilings look back over (1..1000) |
| `history_file` | history path, relative to the config folder unless absolute |
| `announce_delay_s` | delay of the vote-open line after the vote phase starts (0..60) |
| `announce_repeat_s` | repeat the line this much later if nobody has voted (0 = off, max 120) |
| `debug` | extra console logging |

The vote-open line is only sent when at least one layout does not count; otherwise the decision is only logged.

## Commands (Admin Mod required for commands and private notes; the filter works without it)

| Command | Who | Output |
|---|---|---|
| `!layout`, `!versus`, `!versus status` | everyone | counted / not counted this round with reasons; `last 20: HvHvA 4/4, HvA 7/10, HvH 5/6` (count/max, `-` = unlimited) |
| `!versus stats` | everyone | shares over the whole history, plus one line per map (top 12) |
| `!versus reload` | admin (`Power.Generic`) | reload config and history |
| `!versus reset` | admin (`Power.Generic`) | clear the history (old file kept as `history.json.bak`) |

## Build

```
cd Si_VersusVote
dotnet build -c Release                           # test branch 1.0.15 (release build)
dotnet build -c Release -p:SilicaBranch=default   # 0.9.47 refs, output bin\Release\default\
```

References come from `include\<branch>\` (git-ignored; `include\test` = `Projects\_silica_refs\test` with SilicaCore
1.0.15 from the dedicated server, `include\default` = `Projects\_silica_refs\default`).

## Caveats

- The blocked button stays visible and clickable on the clients (baked into the prefab); players learn from chat that
  their vote does not count. The on-screen vote counts still include blocked votes.
- Any other mod that sets the layout during the vote phase (for example data-bomb's Versus Auto-Select) goes through the
  same filter.
- The ceiling window counts rounds on all maps together, not per map.
- Not run live yet (0.1.0).
