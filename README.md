# PartyRaider

Custom bot for [CopilotBuddy](https://github.com/Likon69/CopilotBuddy) (WoW WotLK 3.3.5a, build 12340, private servers, .NET 10). A responsive "follow the group leader" bot with a backup-tank DEFEND mode — it doesn't just follow, it fights back when the protected target is hit.

## Features

- **Re-evaluated every game pulse** (Root runs per-tick): starts moving the instant the leader breaks away — no delayed kick-in like the built-in Party bot.
- **Follows the group leader, the main tank, or ANY player by name** — a party member or one NOT in the party — and keeps up across adjacent-map borders by chasing the last known position.
- **Flightor movement**: walks when close, ground-mounts when medium, flies when the leader is far; never freezes on a failed ground path across the map.
- **MountLikeLeader**: mounts instantly when the follow target mounts (ground mount in the old world, flying mount in Outland/Northrend when available).
- **NoJumpWhenMounted**: installs its own stuck handler — while mounted, stuck recovery strafes / dismounts / backs up instead of jumping; on foot it keeps the original single jump when the way ahead is clear.
- **DEFEND (backup tank)**: when the protected target is in combat it runs the character's OWN combat routine (by talents, via RoutineManager — not a fixed spell list). Target priority: (1) the hostile attacking the protected target, (2) the hostile attacking the bot, (3) the nearest hostile in `DefendRadius`.
- **Looting**: after combat, loots nearby corpses/chests, with optional auto-roll on group loot (Give / Greed / Need).
- **LeaderPlugin** (reuses the built-in PartyBot IPC protocol, TCP `127.0.0.1:1337`): `Role = Leader` holds position, fights if attacked, and broadcasts position/state to followers; `FollowViaLeaderPlugin` follows a leader streamed over TCP — fully compatible with the built-in Party Bot / DiscoBot.
- **Config window** in the bot's main UI (bot selector → config button) with a follow-target / leader dropdown.

## Requirements

- A CopilotBuddy (WotLK) install.
- For DEFEND mode: a character with combat talents (the routine is chosen from the talent trees, same as the built-in combat routines).

## Install

Option A — source (recommended): copy `PartyRaider.cs` to
`<CopilotBuddy install>/Bots/PartyRaider/`. The file is compiled by Roslyn at bot
startup (no build step, no IDE). Pick "PartyRaider" from the bot selector and
check `Logs/` after the first start.

Option B — precompiled (net10.0 assembly): copy `bin/PartyRaider.dll` to
`<CopilotBuddy install>/Bots/PartyRaider/PartyRaider.dll`.

## Settings

Per character: `{AppPath}/Settings/PartyRaider_{char}.xml`
(example: `settings-example/PartyRaider_Pipyau.xml`). Every value is also
editable in the in-bot config window.

| Setting | Type | Default | Description |
|---|---|---|---|
| `FollowLeader` | bool | true | true = follow the group leader, false = follow the main tank |
| `FollowDistance` | int | 6 | Yards to keep from the follow target before stopping |
| `UseMounts` | bool | true | Allow ground/flying mounts to keep up with the target |
| `StopInCombat` | bool | false | Hold position (do not follow) while you are in combat |
| `DismountWhenClose` | bool | true | Step off the mount once stopped within range (follow mode) |
| `DefendLeader` | bool | true | DEFEND mode: attack the enemies hitting the follow target / you (backup tank) |
| `DefendRadius` | int | 15 | Yards around the follow target to look for attackers |
| `DefendSpell` | string | Hammer of Justice | Legacy / unused — defense runs the character's own combat routine |
| `FollowTarget` | string | (empty) | Follow this exact player name — party or non-party (empty = leader/tank mode) |
| `MountLikeLeader` | bool | true | Mount up as soon as the follow target mounts |
| `NoJumpWhenMounted` | bool | true | Never jump while mounted — stuck recovery strafes / dismounts / backs up |
| `Role` | enum | Follower | Follower = follow as usual; Leader = LeaderPlugin (hold + fight, broadcast position over 127.0.0.1:1337) |
| `FollowViaLeaderPlugin` | bool | false | Follow the LeaderPlugin leader (position streamed over TCP 127.0.0.1:1337) |
| `Loot` | bool | true | Loot corpses and chests near you (after combat) |
| `LootRadius` | int | 8 | Yards around you to look for lootable corpses/chests |
| `AutoRollLoot` | bool | true | Automatically roll on group loot items |
| `LootRollType` | enum | Greed | Roll type for group loot items: Give / Greed / Need |

## Version

1.5.0

## License

MIT
