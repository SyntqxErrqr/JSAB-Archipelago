using System;
using HarmonyLib;

namespace JSABArchipelago;

/// <summary>
/// Hooks into the game. Applied a moment after the game starts (see <see cref="ArchipelagoBehaviour"/>): resolving
/// these classes runs their native static constructors, and some of those crash if that happens during plugin load.
/// </summary>
[HarmonyPatch]
internal static class Patches
{
    private static void Guard(Action action, string where)
    {
        try { action(); }
        catch (Exception e) { Plugin.Logger.LogError($"{where}: {e}"); }
    }

    // ---- save isolation: while a slot is loaded, the game's own save is never touched ----------------------------

    [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Save)), HarmonyPrefix]
    private static bool SaveGame()
    {
        if (!ArchipelagoController.SaveRedirected) return true;
        ArchipelagoController.WriteSave();
        return false;
    }

    [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Delete)), HarmonyPrefix]
    private static bool DeleteSave() => !ArchipelagoController.SaveRedirected;

    [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Load)), HarmonyPrefix]
    private static bool LoadSave(Callback LoadCompleteCallback)
    {
        if (!ArchipelagoController.SaveRedirected) return true;
        // The slot's progress is already in memory; loading the normal save would overwrite it.
        Plugin.Logger.LogInfo("Skipped a reload of the normal save while a slot is loaded");
        LoadCompleteCallback?.call();
        return false;
    }

    // ---- title screen waits until the player connects or chooses to play offline ------------------------------------

    [HarmonyPatch(typeof(UI_MainMenu_Intro), nameof(UI_MainMenu_Intro.checkForInputNext)), HarmonyPrefix]
    private static bool TitleInput()
    {
        ArchipelagoController.OnTitleScreen();
        return ArchipelagoController.TitleMayContinue;
    }

    // ---- menu music: loading a slot on the title screen must not start a second track -------------------------------

    [HarmonyPatch(typeof(UI_MainMenuBase), nameof(UI_MainMenuBase.getMusicToPlay)), HarmonyPostfix]
    private static void MenuMusic(ref ModelStoryCheckpoint __result)
    {
        try
        {
            var keep = ArchipelagoController.MenuMusic(__result);
            if (keep != null) __result = keep;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("MenuMusic: " + e);
        }
    }

    [HarmonyPatch(typeof(LogicMap_Music), nameof(LogicMap_Music.destroy)), HarmonyPrefix]
    private static void MusicDestroyed(LogicMap_Music __instance) =>
        Guard(() => ArchipelagoController.OnMusicDestroyed(GameBridge.IdOf(__instance.modelSong)), "MusicDestroyed");

    // ---- checks -----------------------------------------------------------------------------------------------------

    // The end of a level: the player has collected the last triangle. LogicEndLevel is the story version; each mode
    // overrides it (LogicArcadeEndLevel for the Playlist), and an override is a separate method to patch.
    // MetaSongProgress.complete is not hooked: jumping to a chapter from the chapter select calls it for every earlier
    // level, which would count levels that weren't played.

    // Before the game records a story clear: after a shuffled song, it has to record it for the map spot the player
    // entered, and take them back there. __state carries the song that was actually played to the postfix.
    [HarmonyPatch(typeof(LogicEndLevel), nameof(LogicEndLevel.onCompleteLastCheckpoint)), HarmonyPrefix]
    private static void LevelEnding(LogicEndLevel __instance, out string? __state)
    {
        __state = null;
        try
        {
            var level = __instance.actorNormalLevel;
            __state = GameBridge.IdOf(level?.modelSong);
            if (level != null) ArchipelagoController.BeforeStoryLevelEnd(level);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("LevelEnding: " + e);
        }
    }

    [HarmonyPatch(typeof(LogicEndLevel), nameof(LogicEndLevel.onCompleteLastCheckpoint)), HarmonyPostfix]
    private static void LevelEnd(LogicEndLevel __instance, string? __state) =>
        Guard(() => ArchipelagoController.OnLevelEnd(__instance.GetIl2CppType().Name,
            __state ?? GameBridge.IdOf(__instance.actorNormalLevel?.modelSong)), "LevelEnd");

    [HarmonyPatch(typeof(LogicArcadeEndLevel), nameof(LogicArcadeEndLevel.onCompleteLastCheckpoint)), HarmonyPostfix]
    private static void PlaylistLevelEnd(LogicArcadeEndLevel __instance) =>
        Guard(() => ArchipelagoController.OnLevelEnd(nameof(LogicArcadeEndLevel),
            GameBridge.IdOf(__instance.actorNormalLevel?.modelSong)), "PlaylistLevelEnd");

    // The game records Playlist ranks through this when it isn't inlined; the resync after each level covers the rest.
    [HarmonyPatch(typeof(MetaArcadeSongProgress), nameof(MetaArcadeSongProgress.complete)), HarmonyPostfix]
    private static void PlaylistComplete(MetaArcadeSongProgress __instance, ModelRank pModelCompletion) =>
        Guard(() => ArchipelagoController.OnPlaylistCompleted(__instance, pModelCompletion), "PlaylistComplete");

    // ---- Playlist screen: songs open by items (Playlist mode) or by story clears (Story mode) -------------------------

    [HarmonyPatch(typeof(MetaArcadeSongProgress), nameof(MetaArcadeSongProgress.isUnlocked)), HarmonyPostfix]
    private static void SongUnlocked(MetaArcadeSongProgress __instance, ref bool __result)
    {
        if (!ArchipelagoController.Active) return;
        var gameId = GameBridge.IdOf(__instance.modelSong);
        if (gameId != null && ArchipelagoController.IsPlaylistSongOpen(gameId) is bool open) __result = open;
    }

    [HarmonyPatch(typeof(MetaArcadeSongProgress), nameof(MetaArcadeSongProgress.isrequirementSatisfied)), HarmonyPostfix]
    private static void SongRequirement(MetaArcadeSongProgress __instance, ref bool __result)
    {
        if (!ArchipelagoController.Active) return;
        var gameId = GameBridge.IdOf(__instance.modelSong);
        if (gameId != null && ArchipelagoController.IsPlaylistSongOpen(gameId) is bool open) __result = open;
    }

    // ---- Story mode: each level spot opens with an item, and may play a shuffled song --------------------------------

    [HarmonyPatch(typeof(LogicEnterSong), nameof(LogicEnterSong.onAction)), HarmonyPrefix]
    private static bool EnterStoryNode(LogicEnterSong __instance)
    {
        try
        {
            var gameId = GameBridge.IdOf(__instance.metaLevelProgress?.modelSong);
            Plugin.Logger.LogInfo($"Story node action: LogicEnterSong.onAction {gameId}");
            return ArchipelagoController.MayEnterStoryNode(gameId);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("EnterStoryNode: " + e);
            return true;
        }
    }

    private static bool _enteringShuffledLevel;

    // Not every map node is a LogicEnterSong (boats, helicopters and cutscenes have their own), but entering any
    // level from the map goes through here. With Shuffle Story Songs, the spot's level is swapped for the song the
    // spot plays in this seed by calling this again with that song's progress.
    [HarmonyPatch(typeof(UI_Map), nameof(UI_Map.toIngame)), HarmonyPrefix]
    private static bool MapToLevel(UI_Map __instance, MetaSongProgress metaLevel)
    {
        if (_enteringShuffledLevel) return true;
        try
        {
            var gameId = GameBridge.IdOf(metaLevel?.modelSong);
            Plugin.Logger.LogInfo($"Story node action: UI_Map.toIngame {gameId}");
            if (!ArchipelagoController.MayEnterStoryNode(gameId)) return false;
            var shuffled = metaLevel == null ? null : ArchipelagoController.SongToPlayAt(metaLevel);
            if (shuffled == null) return true;
            _enteringShuffledLevel = true;
            try { __instance.toIngame(shuffled); }
            finally { _enteringShuffledLevel = false; }
            return false;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("MapToLevel: " + e);
            return true;
        }
    }

    // Leaving a level from the pause menu: to the map, or to the title (Story then continues from the level entered).
    [HarmonyPatch(typeof(UI_PauseNew), nameof(UI_PauseNew.toMap)), HarmonyPrefix]
    private static void PauseToMap(UI_PauseNew __instance) =>
        Guard(() => ArchipelagoController.OnLeavingStoryLevel(__instance), "PauseToMap");

    [HarmonyPatch(typeof(UI_PauseNew), nameof(UI_PauseNew.leaveStory)), HarmonyPrefix]
    private static void PauseLeaveStory(UI_PauseNew __instance) =>
        Guard(() => ArchipelagoController.OnLeavingStoryLevel(__instance), "PauseLeaveStory");

    [HarmonyPatch(typeof(UI_PauseNew), nameof(UI_PauseNew.leaveToTitleScreen)), HarmonyPrefix]
    private static void PauseLeaveToTitle(UI_PauseNew __instance) =>
        Guard(() => ArchipelagoController.OnLeavingStoryLevel(__instance), "PauseLeaveToTitle");

    [HarmonyPatch(typeof(UI_PauseNew), nameof(UI_PauseNew.toTitleScreen)), HarmonyPrefix]
    private static void PauseToTitle(UI_PauseNew __instance) =>
        Guard(() => ArchipelagoController.OnLeavingStoryLevel(__instance), "PauseToTitle");

    // The spot's name and artist on the map show the song it plays in this seed.
    private static MetaSongProgress? _detailsSpotLevel;

    [HarmonyPatch(typeof(LogicEnterSong), nameof(LogicEnterSong.showLevelDetails)), HarmonyPrefix]
    private static void ShowDetails(LogicEnterSong __instance) => Guard(() =>
    {
        _detailsSpotLevel = null;
        var song = ArchipelagoController.ShuffledSongAt(GameBridge.IdOf(__instance.metaLevelProgress?.modelSong));
        var level = song == null ? null : GameBridge.StoryLevel(song);
        if (level == null) return;
        _detailsSpotLevel = __instance.metaLevelProgress;
        __instance.metaLevelProgress = level;
    }, "ShowDetails");

    [HarmonyPatch(typeof(LogicEnterSong), nameof(LogicEnterSong.showLevelDetails)), HarmonyPostfix]
    private static void ShowDetailsDone(LogicEnterSong __instance) => Guard(() =>
    {
        if (_detailsSpotLevel == null) return;
        __instance.metaLevelProgress = _detailsSpotLevel;
        _detailsSpotLevel = null;
    }, "ShowDetailsDone");

    [HarmonyPatch(typeof(UI_MainMenu_StorySelect), nameof(UI_MainMenu_StorySelect.StartFromSelectedChapter)), HarmonyPrefix]
    private static bool StartChapter(UI_MainMenu_StorySelect __instance)
    {
        try
        {
            var chapter = __instance.getModelChapterSelected();
            return ArchipelagoController.MayEnterStoryNode(GameBridge.IdOf(chapter?.modelSong));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("StartChapter: " + e);
            return true;
        }
    }

    // ---- tutorial checkpoints: each triangle collected in the tutorial moves it to the next scene ------------------

    [HarmonyPatch(typeof(ActorTutorialLevelLogic), nameof(ActorTutorialLevelLogic.playScene)), HarmonyPrefix]
    private static void TutorialScene(int numTriangleCollected) =>
        Guard(() => ArchipelagoController.OnTutorialScene(numTriangleCollected), "TutorialScene");

    // ---- Dash item: without it, the hero can't dash -----------------------------------------------------------------

    [HarmonyPatch(typeof(HeroDashComponent), nameof(HeroDashComponent.canDash)), HarmonyPostfix]
    private static void CanDash(ref bool __result)
    {
        if (__result && !ArchipelagoController.MayDash) __result = false;
    }

    [HarmonyPatch(typeof(HeroDashComponent), nameof(HeroDashComponent.inputForDash)), HarmonyPostfix]
    private static void DashInput(ref bool __result)
    {
        if (!__result || ArchipelagoController.MayDash) return;
        __result = false;
        ArchipelagoController.OnDashBlocked();
    }

    // canDash and inputForDash are small enough that the game's own calls can be inlined past the hooks above, so
    // the dash is also refused where it starts and where the button is read.
    [HarmonyPatch(typeof(HeroDashComponent), nameof(HeroDashComponent.startDash)), HarmonyPrefix]
    private static bool StartDash()
    {
        if (ArchipelagoController.MayDash) return true;
        ArchipelagoController.OnDashBlocked();
        return false;
    }

    [HarmonyPatch(typeof(HeroInputLocal), nameof(HeroInputLocal.isDashingInput)), HarmonyPostfix]
    private static void DashButton(ref bool __result)
    {
        if (!__result || ArchipelagoController.MayDash) return;
        __result = false;
        ArchipelagoController.OnDashBlocked();
    }

    // ---- testing -----------------------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(HeroCooldown), nameof(HeroCooldown.isInvincible)), HarmonyPostfix]
    private static void Invincible(ref bool __result)
    {
        if (Plugin.Instance.DebugInvincible.Value) __result = true;
    }
}
