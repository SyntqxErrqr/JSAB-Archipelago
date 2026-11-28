using System;
using HarmonyLib;

namespace JSABArchipelago;

/// <summary>
/// Logging-only hooks for working out how the game moves between the map, levels and menus, and which music it starts.
/// Applied only when Debug.Trace is on, one by one so a method that can't be hooked doesn't stop the rest. None of them
/// change what the game does.
/// </summary>
internal static class TracePatches
{
    private static Harmony? _harmony;

    public static void Apply()
    {
        _harmony = new Harmony(Plugin.Guid + ".trace");
        Prefix(typeof(UI_Map), nameof(UI_Map.changeMapFromSpot), nameof(ChangeMapFromSpot));
        Prefix(typeof(UI_Map), nameof(UI_Map.SwitchMap), nameof(SwitchMap));
        Prefix(typeof(UI_Map), nameof(UI_Map.continueGameBasedOnMode), nameof(ContinueGame));
        Prefix(typeof(LogicEndLevel), nameof(LogicEndLevel.toMap), nameof(ToMap));
        Prefix(typeof(LogicEnterSong), nameof(LogicEnterSong.EnterLevel), nameof(EnterLevel));
        Prefix(typeof(MetaSongProgress), nameof(MetaSongProgress.complete), nameof(Complete));
        Prefix(typeof(LogicMap_Music), "create", nameof(MusicCreate));
        Prefix(typeof(LogicMap_Music), nameof(LogicMap_Music.stopMusic), nameof(MusicStop));
        Prefix(typeof(LogicMap_Music), nameof(LogicMap_Music.destroy), nameof(MusicDestroy));
        Prefix(typeof(UI_MainMenu_Intro), nameof(UI_MainMenu_Intro.startIntroLoop), nameof(IntroLoop));
        Prefix(typeof(UI_MainMenu_Intro), nameof(UI_MainMenu_Intro.startBigMusic), nameof(IntroBigMusic));
        Prefix(typeof(UI_MainMenu_Intro), nameof(UI_MainMenu_Intro.stopIntroLoop), nameof(IntroStopLoop));
        Prefix(typeof(UI_MainMenu_Intro), nameof(UI_MainMenu_Intro.skipToMainMenu), nameof(IntroToMainMenu));
        Prefix(typeof(UI_MainMenu02), nameof(UI_MainMenu02.switchMusicToGuitar), nameof(MenuGuitar));
        Postfix(typeof(UI_MainMenuBase), nameof(UI_MainMenuBase.getMusicToPlay), nameof(MusicToPlay));
    }

    private static void Prefix(Type type, string method, string hook) => Hook(type, method, hook, prefix: true);
    private static void Postfix(Type type, string method, string hook) => Hook(type, method, hook, prefix: false);

    private static void Hook(Type type, string method, string hook, bool prefix)
    {
        try
        {
            var original = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.Name, method);
            var patch = new HarmonyMethod(AccessTools.Method(typeof(TracePatches), hook));
            _harmony!.Patch(original, prefix: prefix ? patch : null, postfix: prefix ? null : patch);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[trace] could not hook {type.Name}.{method}: {e.Message}");
        }
    }

    private static void Log(string text) => Plugin.Logger.LogInfo("[trace] " + text);

    private static string Id(ModelBase? model)
    {
        try { return GameBridge.IdOf(model) ?? "null"; }
        catch { return "?"; }
    }

    private static string Story()
    {
        try { return GameBridge.DescribeStory(); }
        catch (Exception e) { return "(story: " + e.Message + ")"; }
    }

    private static void Guard(Func<string> text)
    {
        try { Log(text()); }
        catch (Exception e) { Plugin.Logger.LogError("[trace] " + e); }
    }

    // ---- map and levels ----------------------------------------------------------------------------------------------

    private static void ChangeMapFromSpot(ModelMap m, string fromSpot) =>
        Guard(() => $"UI_Map.changeMapFromSpot map={Id(m)} spot={fromSpot} {Story()}");

    private static void SwitchMap(ModelMap modelMap) => Guard(() => $"UI_Map.SwitchMap map={Id(modelMap)} {Story()}");

    private static void ContinueGame(MetaSongProgress metaLevel) =>
        Guard(() => $"UI_Map.continueGameBasedOnMode level={Id(metaLevel?.modelSong)} {Story()}");

    private static void ToMap(LogicEndLevel __instance) =>
        Guard(() => $"LogicEndLevel.toMap ({__instance.GetIl2CppType().Name}) " +
                    $"song={Id(__instance.actorNormalLevel?.modelSong)} {Story()}");

    private static void EnterLevel(LogicEnterSong __instance) =>
        Guard(() => $"LogicEnterSong.EnterLevel {Id(__instance.metaLevelProgress?.modelSong)} {Story()}");

    private static void Complete(MetaSongProgress __instance, int numDeath) =>
        Guard(() => $"MetaSongProgress.complete {Id(__instance.modelSong)} deaths={numDeath}");

    // ---- music -------------------------------------------------------------------------------------------------------

    private static void MusicCreate(ModelStoryCheckpoint pModelSong) =>
        Guard(() => $"LogicMap_Music.create {Id(pModelSong)}");

    private static void MusicStop(LogicMap_Music __instance) =>
        Guard(() => $"LogicMap_Music.stopMusic {Id(__instance.modelSong)}");

    private static void MusicDestroy(LogicMap_Music __instance) =>
        Guard(() => $"LogicMap_Music.destroy {Id(__instance.modelSong)}");

    private static void IntroLoop() => Guard(() => "UI_MainMenu_Intro.startIntroLoop");

    private static void IntroBigMusic(UI_MainMenu_Intro __instance) =>
        Guard(() => $"UI_MainMenu_Intro.startBigMusic (intro music {Id(__instance.logicMusic?.modelSong)})");

    private static void IntroStopLoop() => Guard(() => "UI_MainMenu_Intro.stopIntroLoop");

    private static void IntroToMainMenu(UI_MainMenu_Intro __instance) =>
        Guard(() => $"UI_MainMenu_Intro.skipToMainMenu (intro music {Id(__instance.logicMusic?.modelSong)})");

    private static void MenuGuitar(UI_MainMenu02 __instance) =>
        Guard(() => $"UI_MainMenu02.switchMusicToGuitar (menu music {Id(__instance.logicMusic?.modelSong)})");

    private static void MusicToPlay(ModelStoryCheckpoint __result) =>
        Guard(() => $"UI_MainMenuBase.getMusicToPlay -> {Id(__result)}");
}
