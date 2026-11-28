# JSAB Archipelago

A [BepInEx 6](https://github.com/BepInEx/BepInEx) mod that connects
[Just Shapes & Beats](https://store.steampowered.com/app/531510/) to an [Archipelago](https://archipelago.gg)
multiworld. The matching apworld (game definition, options and logic) lives in the
[SyntqxErrqr/Archipelago](https://github.com/SyntqxErrqr/Archipelago/tree/just-shapes-and-beats/worlds/jsab) fork, on
the `just-shapes-and-beats` branch under `worlds/jsab`.

The mod talks to the Archipelago server itself, so there is no separate client to run.

## What it does

The randomizer has two modes, picked in the player's YAML.

- **Story mode**: every level on the story map is locked until a *Progressive Level* item opens it, one at a time in
  story order. The Lost Chapter is optional (off by default) and its levels open with *Progressive Lost Chapter Level*
  items. With *Shuffle Story Songs*, each map spot plays a random story song instead of its own. Clearing story levels
  sends checks, as do the tutorial's 9 checkpoint triangles and S-ranking cleared story songs on the Playlist screen.
  Beat *Till It's Over* to win.
- **Playlist mode**: you start with a few songs on the Playlist screen and every other song is an item. Clears and
  S Ranks are checks. Collect enough *Beat Shards* to open the goal song, then clear it to win.

In both modes, *Dash* can be shuffled into the multiworld (Start With Dash off): the hero can't dash until it arrives.

While connected, the game plays on a separate save for that slot (`BepInEx\config\JSABArchipelago\saves`). **The
game's own save is never written**, so your normal progress is untouched. Settings (volume, controls, display) carry
over from your normal save.

## Installing

1. Install the **BepInEx 6 bleeding edge IL2CPP x64** build: download `BepInEx-Unity.IL2CPP-win-x64-*.zip` from
   [builds.bepinex.dev](https://builds.bepinex.dev/projects/bepinex_be) and extract it into the game folder
   (Steam: right-click the game > Manage > Browse local files), next to `JSB.exe`.
2. Start the game once and wait for the title screen (BepInEx needs a minute or two the first time), then quit.
3. Extract the mod release zip into the same folder, so the DLLs end up in `BepInEx\plugins\JSABArchipelago`.

## Playing

Start the game. An Archipelago panel sits in the top-left corner of the title screen: click a field to type in it
(Tab moves to the next field, Ctrl+V pastes, Enter connects). The title screen waits until you have connected, or
until you click **Play without Archipelago** to play your normal save. F1 shows or hides the panel.

Set `AutoConnect = true` in `BepInEx\config\com.syntqxerrqr.jsab.archipelago.cfg` to connect with the last used
details as soon as the title screen appears.

## Building

Requires the .NET SDK (6.0 or newer) and the game with BepInEx installed and started once (BepInEx generates the
`BepInEx\interop` assemblies the mod compiles against).

The build looks for the game in the default Steam folder. If yours is elsewhere, create `GamePath.user.props` (not
committed) next to `GameReferences.props`:

```xml
<Project><PropertyGroup><JSABGameDir>X:\SteamLibrary\steamapps\common\Just Shapes &amp; Beats</JSABGameDir></PropertyGroup></Project>
```

```powershell
dotnet build src/JSABArchipelago -c Release
```

Add `-p:DeployToGame=true` to copy the build into the game's plugins folder, or run `build-release.ps1` to make a
release zip in `dist/`.

## Repository layout

- `src/JSABArchipelago`: the mod.
  - `ArchipelagoController.cs`: connection, items, checks, chapter locks and the goal.
  - `Patches.cs`: every hook into the game, and why it is there.
  - `GameBridge.cs`: reading and writing the game's progress objects.
  - `ApSave.cs`: the per-slot save file.
  - `ArchipelagoBehaviour.cs`: the on-screen panel and message feed.
  - `DebugCommands.cs`: developer commands (off by default).
- `tools/JSABDataDump`: a developer plugin that dumps the game's song, chapter, rank and difficulty tables to JSON.
  The apworld's song table was built from its output, `tools/JSABDataDump/jsab_data_dump.json` (game v1.6.50).

## How it hooks the game

The game is a Unity 2020.3 IL2CPP build. Notes for anyone extending the mod:

- Hooks are applied two seconds after startup. Resolving some game classes (`MetaGameProgress`) during plugin load
  runs their native static constructors too early and crashes.
- Unity's `GUI.TextField` can't be used: the build strips `TextEditor`. The panel's text boxes are hand-rolled.
- `MetaArcadeSongProgress.complete` is inlined on the normal Playlist path, so Playlist results are read back from the
  save shortly after each level ends (`LogicArcadeEndLevel.onCompleteLastCheckpoint`) and every few seconds.
- Story levels are blocked at `LogicEnterSong.onAction`, before the game runs any of the node's enter logic, with
  `UI_Map.toIngame` as a backstop. In-map cutscene nodes (for example Paradise's fake credits) are not levels and are
  not blocked.
- Shuffled story songs: `UI_Map.toIngame` is called again with the other song's `MetaSongProgress`, so the level plays
  exactly as it does in its own chapter. The game remembers that song in several places (the level, its progress, its
  `MetaGameResult`, the `NormalLevelScene`, the pause menu); just before the level ends
  (`LogicEndLevel.onCompleteLastCheckpoint`) or is left from the pause menu, the mod points them all at the map spot
  the player entered. That is what makes the game clear that spot and return the player there rather than to the
  played song's own chapter. `LogicEnterSong.showLevelDetails` shows the played song's name on the map.
- The menu music comes from the save's progress (`UI_MainMenuBase.getMusicToPlay`). The title screen starts it for the
  normal save, so after a slot's save is loaded the mod keeps returning that track until it stops, or the main menu
  starts a second one on top.
- Tutorial checkpoints come from `ActorTutorialLevelLogic.playScene(n)`: collecting the Nth triangle starts scene N-1
  (9 scenes, 0-8); the triangle after scene 8 ends the level. Scene 5 is the wall you must dash through.
- `HeroDashComponent.canDash` and `inputForDash` are inlined on the game's own path; Dash is refused in
  `HeroDashComponent.startDash` and `HeroInputLocal.isDashingInput`.

## Debugging

`BepInEx\LogOutput.log` has everything the mod does. In the mod's config, `[Debug] Invincible` makes the player
impossible to hit (levels play through on their own), `[Debug] Commands` enables the command file described in
`DebugCommands.cs`, and `[Debug] Trace` logs the game's map, level and music calls (`TracePatches.cs`). All three are
for testing the mod only.

## License

MIT, see [LICENSE](LICENSE).
