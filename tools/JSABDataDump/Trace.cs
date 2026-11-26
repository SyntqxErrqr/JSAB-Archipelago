using HarmonyLib;

namespace JSABDataDump;

/// <summary>Log-only patches on the methods the Archipelago mod hooks, to watch the game's real call flow.</summary>
[HarmonyPatch]
internal static class Trace
{
    private static void Log(string message) => DumpPlugin.Instance.Log.LogInfo("[trace] " + message);
    private static string Id(ModelBase? m) => m == null ? "null" : m.id;

    [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Save)), HarmonyPrefix]
    private static void Save() => Log("SaveGameManager.Save");

    [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Load)), HarmonyPrefix]
    private static void Load(bool createSaveIfNone) => Log($"SaveGameManager.Load createIfNone={createSaveIfNone}");

    [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.OnGameLoad)), HarmonyPrefix]
    private static void OnGameLoad() => Log("SaveGameManager.OnGameLoad");

    [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.GetSaveFileName)), HarmonyPostfix]
    private static void GetSaveFileName(string __result) => Log($"SaveGameManager.GetSaveFileName -> {__result}");

    [HarmonyPatch(typeof(MetaGameProgress), nameof(MetaGameProgress.Load)), HarmonyPrefix]
    private static void GameLoad(string saveData) => Log($"MetaGameProgress.Load len={saveData?.Length}");

    [HarmonyPatch(typeof(MetaGameProgress), nameof(MetaGameProgress.Save)), HarmonyPostfix]
    private static void GameSave(string __result) => Log($"MetaGameProgress.Save len={__result?.Length}");

    [HarmonyPatch(typeof(MetaSongProgress), nameof(MetaSongProgress.complete)), HarmonyPrefix]
    private static void SongComplete(MetaSongProgress __instance, int numDeath) =>
        Log($"MetaSongProgress.complete {Id(__instance.modelSong)} deaths={numDeath}");

    [HarmonyPatch(typeof(MetaSongProgress), nameof(MetaSongProgress.completeNoNew)), HarmonyPrefix]
    private static void SongCompleteNoNew(MetaSongProgress __instance, int numDeath) =>
        Log($"MetaSongProgress.completeNoNew {Id(__instance.modelSong)} deaths={numDeath}");

    [HarmonyPatch(typeof(MetaGameResult), nameof(MetaGameResult.triggerComplete)), HarmonyPrefix]
    private static void ResultComplete(MetaGameResult __instance) =>
        Log($"MetaGameResult.triggerComplete {Id(__instance.metaLevelProgress?.modelSong)} deaths={__instance.deaths}");

    [HarmonyPatch(typeof(MetaStoryProgress), nameof(MetaStoryProgress.completeUpToSong)), HarmonyPrefix]
    private static void UpTo(ModelStoryCheckpoint modelSong) => Log($"MetaStoryProgress.completeUpToSong {Id(modelSong)}");

    [HarmonyPatch(typeof(MetaStoryProgress), nameof(MetaStoryProgress.completeUpToSongAndIncluding)), HarmonyPrefix]
    private static void UpToIncl(ModelStoryCheckpoint modelSong) =>
        Log($"MetaStoryProgress.completeUpToSongAndIncluding {Id(modelSong)}");

    [HarmonyPatch(typeof(MetaArcadeSongProgress), nameof(MetaArcadeSongProgress.complete)), HarmonyPrefix]
    private static void ArcadeComplete(MetaArcadeSongProgress __instance, ModelRank pModelCompletion) =>
        Log($"MetaArcadeSongProgress.complete {Id(__instance.modelSong)} rank={Id(pModelCompletion)} " +
            $"diff={Id(MetaGameProgress.instance?.modelGameDifficulty)}");

    [HarmonyPatch(typeof(LogicEnterSong), nameof(LogicEnterSong.onAction)), HarmonyPrefix]
    private static void EnterAction(LogicEnterSong __instance) =>
        Log($"LogicEnterSong.onAction {Id(__instance.metaLevelProgress?.modelSong)}");

    [HarmonyPatch(typeof(LogicEnterSong), nameof(LogicEnterSong.EnterLevel)), HarmonyPrefix]
    private static void EnterLevel(LogicEnterSong __instance) =>
        Log($"LogicEnterSong.EnterLevel {Id(__instance.metaLevelProgress?.modelSong)}");

    [HarmonyPatch(typeof(LogicEnterSong), nameof(LogicEnterSong.onReached)), HarmonyPrefix]
    private static void Reached(LogicEnterSong __instance) =>
        Log($"LogicEnterSong.onReached {Id(__instance.metaLevelProgress?.modelSong)}");

    [HarmonyPatch(typeof(LogicEnterNode), nameof(LogicEnterNode.onAction)), HarmonyPrefix]
    private static void NodeAction(LogicEnterNode __instance)
    {
        // Never throw from a trace: an exception here makes the game skip onAction itself.
        try
        {
            Log($"LogicEnterNode.onAction {__instance.GetIl2CppType().Name} " +
                $"song={Id(__instance.node?.metaSongProgress?.modelSong)}");
        }
        catch (System.Exception e)
        {
            Log("LogicEnterNode.onAction (unreadable): " + e.GetType().Name);
        }
    }

    [HarmonyPatch(typeof(UI_Map), nameof(UI_Map.toIngame)), HarmonyPrefix]
    private static void ToIngame(MetaSongProgress metaLevel) => Log($"UI_Map.toIngame {Id(metaLevel?.modelSong)}");

    // Don't add traces on NormalLevelScene's static start methods or LogicStartLevel.initLevel: with those patched,
    // the game quit silently about 20 seconds after launch.

    [HarmonyPatch(typeof(UI_MainMenu_StorySelect), nameof(UI_MainMenu_StorySelect.StartFromSelectedChapter)), HarmonyPrefix]
    private static void StartChapter(UI_MainMenu_StorySelect __instance) =>
        Log($"UI_MainMenu_StorySelect.StartFromSelectedChapter {Id(__instance.getModelChapterSelected())}");

    [HarmonyPatch(typeof(MetaStoryProgress), nameof(MetaStoryProgress.setForStoryChapterSelect)), HarmonyPrefix]
    private static void ChapterSelect() => Log("MetaStoryProgress.setForStoryChapterSelect");

    [HarmonyPatch(typeof(MetaArcadeSongProgress), nameof(MetaArcadeSongProgress.isUnlocked)), HarmonyPostfix]
    private static void ArcadeUnlocked(MetaArcadeSongProgress __instance, bool __result)
    {
        if (++_unlockLogCount <= 5) Log($"MetaArcadeSongProgress.isUnlocked {Id(__instance.modelSong)} -> {__result}");
    }

    private static int _unlockLogCount;
}
