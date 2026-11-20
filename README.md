# Shared Quests

A server mod for **SPT 4.1.x** that lets a group of players share quest progress.
Each player still turns quests in themselves and gets their own rewards.

- Version: 1.1.0
- SPT: 4.1.x (built against SPTarkov.Server.Core 4.1.2, tested on 4.1.6)
- Required mods: none
- Optional mods: [Fika](https://github.com/project-fika) (playing raids together), VisitAPI (EFT 1.2 story)
- License: MIT

## What's shared

- **Accepting a quest.** When one of you accepts a quest, it's accepted for the others too,
  if they have it unlocked (right level, faction and earlier quests).
- **Kills and other counters.** Raids you play apart add together. Raids you play together
  in Fika count once, because Fika already shares those kills (see `sameRaidCounting`).
- **Hand-ins and finished steps.** A step one of you completes is completed for everyone who
  has the quest started.
- **Finishing.** When one of you finishes all the steps, the quest becomes ready to turn in
  for the others. Each of you turns it in and gets the rewards.
- **Dailies and weeklies.** The first of you to get a new set each cycle sets it for the
  group. Rerolls are copied too, unless the other player already accepted the old quest.
- **The EFT 1.2 story (VisitAPI, optional).** Story variables, dialogue choices and story objectives.

## Install

This is a server mod. Players don't need to install anything (see *Seeing progress straight away*
for an optional client plugin).

1. Stop the server and back up `SPT/user/profiles`.
2. Extract the zip into your SPT folder, so you get `SPT/user/mods/SharedQuests/SharedQuests.dll`.
3. Open `SPT/user/mods/SharedQuests/config.json` and put your names in `groups`.
   A name can be the launcher username, the in-game nickname or the profile id.
4. Start the server. The console shows `[Shared Quests] Group 1: <name>` for each player found.

## Config

| Setting | Default | What it does |
| --- | --- | --- |
| `enabled` | `true` | Turns the mod on or off. |
| `groups` | `[["PLAYER_ONE","PLAYER_TWO"]]` | Players who share quests. Add more groups as more lists. |
| `syncStory` | `true` | Share the VisitAPI story. Does nothing without VisitAPI. |
| `syncDailiesAndWeeklies` | `true` | Same dailies and weeklies for the group. |
| `shareQuestAccept` | `true` | Accept a quest for the others when one of you accepts it. |
| `sameRaidCounting` | `"auto"` | Fika raids together: `"max"` counts kills once, `"sum"` adds them, `"auto"` reads Fika's `sharedQuestProgression`. |
| `checkIntervalSeconds` | `2` | How often menu changes (hand-ins, story choices) are checked. |
| `liveSync` | `true` | Answer the optional Shared Quests Live client plugin. |
| `logChanges` | `true` | Write each sync to the server console. |

## Good to know

- When the server starts, the mod combines the group's progress: everyone gets the furthest
  story state and every step anyone finished on quests they share. It works best when you
  start at a similar point.
- Without the client plugin, your game shows a friend's progress after it reloads your profile:
  after your next raid, or after restarting the game. The server has it straight away.
- The mod never starts, unlocks or turns in a quest by itself, except accepting one a friend
  accepted (`shareQuestAccept`).
- Failing a quest isn't shared.

## Seeing progress straight away (optional)

Install the [**Shared Quests Live**](https://github.com/KavXd/SharedQuestsLive) client plugin (`BepInEx/plugins/SharedQuestsLive/SharedQuestsLive.dll`)
in each player's game. While you're in the menus it asks the server every few seconds and puts your
group's progress (counters, finished steps, ready to turn in) into your quest list, with a short
message. It doesn't run during a raid, and it never turns in, starts or fails a quest.
The Fika headless client doesn't need it.

## Uninstall

Stop the server and delete `SPT/user/mods/SharedQuests`. Progress already shared stays.

## Building from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). The SPT references come from
NuGet, so no SPT install is needed to build.

```
dotnet build -c Release
```

The mod is `bin/Release/net10.0/SharedQuests.dll`. Copy it and `config.json` into
`SPT/user/mods/SharedQuests/`.

## Source

All source code is in this repository: https://github.com/KavXd/SharedQuests
The release DLL is built from it unchanged and isn't obfuscated.
