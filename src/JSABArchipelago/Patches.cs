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

    // ---- checks -----------------------------------------------------------------------------------------------------

    // The end of a level: the player has collected the last triangle. LogicEndLevel is the story version; each mode
    // overrides it (LogicArcadeEndLevel for the Playlist), and an override is a separate method to patch.
    // MetaSongProgress.complete is not hooked: jumping to a chapter from the chapter select calls it for every earlier
    // level, which would count levels that weren't played.

    [HarmonyPatch(typeof(LogicEndLevel), nameof(LogicEndLevel.onCompleteLastCheckpoint)), HarmonyPostfix]
    private static void LevelEnd(LogicEndLevel __instance) =>
        Guard(() => ArchipelagoController.OnLevelEnd(__instance.GetIl2CppType().Name,
            GameBridge.IdOf(__instance.actorNormalLevel?.modelSong)), "LevelEnd");

    [HarmonyPatch(typeof(LogicArcadeEndLevel), nameof(LogicArcadeEndLevel.onCompleteLastCheckpoint)), HarmonyPostfix]
    private static void PlaylistLevelEnd(LogicArcadeEndLevel __instance) =>
        Guard(() => ArchipelagoController.OnLevelEnd(nameof(LogicArcadeEndLevel),
            GameBridge.IdOf(__instance.actorNormalLevel?.modelSong)), "PlaylistLevelEnd");

    // The game records Playlist ranks through this when it isn't inlined; the resync after each level covers the rest.
    [HarmonyPatch(typeof(MetaArcadeSongProgress), nameof(MetaArcadeSongProgress.complete)), HarmonyPostfix]
    private static void PlaylistComplete(MetaArcadeSongProgress __instance, ModelRank pModelCompletion) =>
        Guard(() => ArchipelagoController.OnPlaylistCompleted(__instance, pModelCompletion), "PlaylistComplete");

    // ---- Playlist mode: songs are unlocked by items ------------------------------------------------------------------

    private static bool ControlsPlaylist =>
        ArchipelagoController.Active && ArchipelagoController.Slot is { IsStory: false };

    [HarmonyPatch(typeof(MetaArcadeSongProgress), nameof(MetaArcadeSongProgress.isUnlocked)), HarmonyPostfix]
    private static void SongUnlocked(MetaArcadeSongProgress __instance, ref bool __result)
    {
        if (!ControlsPlaylist) return;
        var gameId = GameBridge.IdOf(__instance.modelSong);
        if (gameId != null) __result = ArchipelagoController.IsPlaylistSongUnlocked(gameId);
    }

    [HarmonyPatch(typeof(MetaArcadeSongProgress), nameof(MetaArcadeSongProgress.isrequirementSatisfied)), HarmonyPostfix]
    private static void SongRequirement(MetaArcadeSongProgress __instance, ref bool __result)
    {
        if (!ControlsPlaylist) return;
        var gameId = GameBridge.IdOf(__instance.modelSong);
        if (gameId != null) __result = ArchipelagoController.IsPlaylistSongUnlocked(gameId);
    }

    // ---- Story mode: chapters are unlocked by items ------------------------------------------------------------------

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

    // Not every map node is a LogicEnterSong (boats, helicopters and cutscenes have their own), but entering any
    // level from the map goes through here.
    [HarmonyPatch(typeof(UI_Map), nameof(UI_Map.toIngame)), HarmonyPrefix]
    private static bool MapToLevel(MetaSongProgress metaLevel)
    {
        try
        {
            var gameId = GameBridge.IdOf(metaLevel?.modelSong);
            Plugin.Logger.LogInfo($"Story node action: UI_Map.toIngame {gameId}");
            return ArchipelagoController.MayEnterStoryNode(gameId);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("MapToLevel: " + e);
            return true;
        }
    }

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

    // ---- testing -----------------------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(HeroCooldown), nameof(HeroCooldown.isInvincible)), HarmonyPostfix]
    private static void Invincible(ref bool __result)
    {
        if (Plugin.Instance.DebugInvincible.Value) __result = true;
    }
}
