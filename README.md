# JSAB Archipelago

A [BepInEx 6](https://github.com/BepInEx/BepInEx) mod that connects
[Just Shapes & Beats](https://store.steampowered.com/app/531510/) to an [Archipelago](https://archipelago.gg)
multiworld. The matching apworld (game definition, options and logic) lives in the
[SyntqxErrqr/Archipelago](https://github.com/SyntqxErrqr/Archipelago) fork, under `worlds/jsab`.

The mod talks to the Archipelago server itself, so there is no separate client to run.

## What it does

The randomizer has two modes, picked in the player's YAML.

- **Story mode**: chapters open one at a time in story order (Tutorial, Paradise, Island, Volcano, Industry, Tower)
  through *Progressive Chapter* items. The Lost Chapter is opened by a *Lost Chapter Key*. Clearing story levels sends
  checks, and S-ranking those songs on the Playlist screen sends more. Beat *Till It's Over* to win.
- **Playlist mode**: you start with a few songs on the Playlist screen and every other song is an item. Clears and
  S Ranks are checks. Collect enough *Beat Shards* to open the goal song, then clear it to win.

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

## Debugging

`BepInEx\LogOutput.log` has everything the mod does. In the mod's config, `[Debug] Invincible` makes the player
impossible to hit (levels play through on their own) and `[Debug] Commands` enables the command file described in
`DebugCommands.cs`. Both are for testing the mod only.

## License

MIT, see [LICENSE](LICENSE).
