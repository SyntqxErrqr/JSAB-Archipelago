using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace JSABDataDump;

[BepInPlugin("com.syntqxerrqr.jsab.datadump", "JSAB Data Dump", "1.0.0")]
public class DumpPlugin : BasePlugin
{
    internal static DumpPlugin Instance = null!;

    public override void Load()
    {
        Instance = this;
        ClassInjector.RegisterTypeInIl2Cpp<Dumper>();
        AddComponent<Dumper>();
    }

    /// <summary>
    /// Patching resolves the IL2CPP classes involved, which runs their native static constructors. Some of those
    /// (MetaGameProgress) crash if that happens before the game has booted, so this runs a few seconds in.
    /// </summary>
    internal void PatchTraces()
    {
        var harmony = new HarmonyLib.Harmony("com.syntqxerrqr.jsab.datadump");
        try { harmony.PatchAll(typeof(Trace)); Log.LogInfo("Trace patches applied"); }
        catch (Exception e) { Log.LogError("Trace patching failed: " + e); }
    }
}

/// <summary>Waits until the game has built its static model tables, then writes them to JSON once.</summary>
public class Dumper : MonoBehaviour
{
    public Dumper(IntPtr ptr) : base(ptr) { }

    private float _elapsed;
    private float _sinceTriggerCheck;
    private bool _done;

    private void Update()
    {
        // Create BepInEx\dump_progress.trigger (or press F9) to dump the loaded save's structure.
        _sinceTriggerCheck += Time.unscaledDeltaTime;
        var trigger = Path.Combine(Paths.BepInExRootPath, "dump_progress.trigger");
        bool triggered = false;
        if (_sinceTriggerCheck > 1f)
        {
            _sinceTriggerCheck = 0f;
            if (File.Exists(trigger))
            {
                File.Delete(trigger);
                triggered = true;
            }
        }
        if (triggered || Input.GetKeyDown(KeyCode.F9))
        {
            try
            {
                var path = Path.Combine(Paths.BepInExRootPath, "jsab_progress_dump.json");
                File.WriteAllText(path, JsonSerializer.Serialize(CollectProgress(), new JsonSerializerOptions { WriteIndented = true }));
                DumpPlugin.Instance.Log.LogMessage($"Wrote {path}");
            }
            catch (Exception e)
            {
                DumpPlugin.Instance.Log.LogError(e);
            }
        }
        if (_done) return;
        _elapsed += Time.unscaledDeltaTime;
        if (_elapsed < 8f) return;
        _done = true;
        DumpPlugin.Instance.PatchTraces();
        try
        {
            var path = Path.Combine(Paths.BepInExRootPath, "jsab_data_dump.json");
            File.WriteAllText(path, JsonSerializer.Serialize(Collect(), new JsonSerializerOptions { WriteIndented = true }));
            DumpPlugin.Instance.Log.LogMessage($"Wrote {path}");
        }
        catch (Exception e)
        {
            DumpPlugin.Instance.Log.LogError(e);
        }
    }

    private static object? Try<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception e) { return "ERR: " + e.GetType().Name + ": " + e.Message; }
    }

    private static string? Id(ModelBase? m) => m == null ? null : m.id;

    private static Dictionary<string, object?> Collect()
    {
        var result = new Dictionary<string, object?>
        {
            // Application.version is the game's own version string ("1.6.50"), not Unity's.
            ["gameVersion"] = Application.version,
        };

        var songs = new List<object>();
        var all = ModelStoryCheckpointEnum.ALL;
        for (int i = 0; i < all.Length; i++)
        {
            var s = all[i];
            var req = s.modelRequirement;
            songs.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["nId"] = (int)s.nId,
                ["id"] = s.id,
                ["name"] = s.name,
                ["artist"] = s.artist,
                ["difficulty"] = s.difficulty,
                ["isStory"] = s.isStory,
                ["map"] = Try(() => Id(s.map)),
                ["soundTrack"] = Try(() => Id(s.modelSoundTrack)),
                ["album"] = Try(() => s.modelAlbum == null ? null : s.modelAlbum.name),
                ["isSecret"] = Try(() => s.isSecret()),
                ["isUnlockedInPlaylist"] = Try(() => s.isUnlockedInPlaylist()),
                ["isBossBattle"] = Try(() => s.isBossBattle()),
                ["isStoryModeBoss"] = Try(() => s.isStoryModeBoss()),
                ["isTutorial"] = Try(() => s.isTutorial()),
                ["isRemix"] = Try(() => s.isRemix()),
                ["isExtra"] = Try(() => s.isExtra()),
                ["isExtraAndLocked"] = Try(() => s.isExtraAndLocked()),
                ["isLostChapter"] = Try(() => s.isLostChapter()),
                ["isShovelKnight"] = Try(() => s.isShovelKnight()),
                ["isTillItsOver"] = Try(() => s.isTillItsOver()),
                ["isMissingSong"] = Try(() => s.isMissingSong()),
                ["length"] = Try(() => s.getLenght()),
                ["assetBundle"] = Try(() => s.GetAssetBundlePath()),
                ["requirement"] = req == null ? null : new Dictionary<string, object?>
                {
                    ["id"] = req.id,
                    ["type"] = Try(() => req.m_ReqType.ToString()),
                    ["valueToReach"] = Try(() => req.valueToReach),
                    ["linkedLevel"] = Try(() => Id(req.linkedLevel)),
                    ["rankToObtain"] = Try(() => Id(req.rankToObtain)),
                    ["modelRank"] = Try(() => Id(req.modelRank)),
                    ["modelDiff"] = Try(() => Id(req.modelDiff)),
                    ["text"] = Try(() => req.getRequirementStr()),
                },
            });
        }
        result["songs"] = songs;

        result["allExtra"] = Ids(ModelStoryCheckpointEnum.ALL_EXTRA);
        result["allMulti"] = Ids(ModelStoryCheckpointEnum.ALL_MULTI);

        var chapters = new List<object>();
        var chapterList = ModelChapterEnum.ALL;
        for (int i = 0; i < chapterList.Length; i++)
        {
            var c = chapterList[i];
            chapters.Add(new Dictionary<string, object?>
            {
                ["id"] = c.id,
                ["index"] = c.index,
                ["group"] = Id(c.modelGroup),
                ["song"] = Id(c.modelSong),
            });
        }
        result["chapters"] = chapters;

        var groups = new List<object>();
        var groupList = ModelChapterGroupEnum.ALL;
        for (int i = 0; i < groupList.Length; i++)
        {
            var g = groupList[i];
            groups.Add(new Dictionary<string, object?>
            {
                ["id"] = g.id, ["index"] = g.index, ["frame"] = g.frame,
                ["preview"] = Try(() => g.getPreviewStr()),
            });
        }
        result["chapterGroups"] = groups;

        var ranks = new List<object>();
        var rankList = ModelRankEnum.ALL;
        for (int i = 0; i < rankList.Length; i++)
        {
            var r = rankList[i];
            ranks.Add(new Dictionary<string, object?>
            {
                ["id"] = r.id, ["nId"] = (int)r.nId, ["completionValue"] = r.completionValue,
                ["letter"] = Try(() => r.getRankLetter()), ["isS"] = Try(() => r.isRankS()),
            });
        }
        result["ranks"] = ranks;

        var diffs = new List<object>();
        var diffList = ModelGameDifficultyEnum.ALL;
        for (int i = 0; i < diffList.Length; i++)
        {
            var d = diffList[i];
            diffs.Add(new Dictionary<string, object?>
            {
                ["id"] = d.id, ["value"] = d.difficultyValue, ["name"] = d.difficultyName, ["bpMultiplier"] = d.bpMultiplier,
            });
        }
        result["difficulties"] = diffs;

        var maps = new List<object?>();
        var mapCount = ModelMapEnum.getNum();
        for (int i = 0; i < mapCount; i++) maps.Add(Try(() => ModelMapEnum.getFromIndex(i).id));
        result["maps"] = maps;

        return result;
    }

    /// <summary>The shape of the save: which songs the story and each playlist difficulty track, in order.</summary>
    private static Dictionary<string, object?> CollectProgress()
    {
        var game = MetaGameProgress.instance;
        var result = new Dictionary<string, object?>
        {
            ["typeOfGame"] = Try(() => Id(game.modelTypeOfGame)),
            ["difficulty"] = Try(() => Id(game.modelGameDifficulty)),
            ["optionsDifficulty"] = Try(() => game.metaOptions.difficulty),
            ["isNewGame"] = Try(() => game.metaStoryProgress.isNewGame()),
            ["hasCompletedStoryMode"] = Try(() => game.metaStoryProgress.hasCompletedStoryMode),
            ["hasCompletedLostChapter"] = Try(() => game.metaStoryProgress.hasCompletedLostChapter),
            ["lastCompletedSong"] = Try(() => Id(game.metaStoryProgress.lastCompletedSong)),
            ["hasUnlockedPartyMainMenu"] = Try(() => game.metaMainMenuProgress.hasUnlockedPartyMainMenu),
            ["saveLength"] = Try(() => game.Save().Length),
        };

        var story = new List<object>();
        var levels = game.metaStoryProgress.metaSongGroup.metaLevels;
        for (int i = 0; i < levels.Length; i++)
        {
            var l = levels[i];
            story.Add($"{Id(l.modelSong)} completed={Try(() => l.isCompleted())} locked={Try(() => l.isLocked())} deaths={l.nbDeaths}");
        }
        result["story"] = story;

        var arcade = new Dictionary<string, object?>();
        var groups = game.metaArcadeProgress.list;
        for (int g = 0; g < groups.Length; g++)
        {
            var group = groups[g];
            var songs = new List<object>();
            for (int i = 0; i < group.songProgress.Length; i++)
            {
                var p = group.songProgress[i];
                songs.Add($"{Id(p.modelSong)} unlocked={Try(() => p.isUnlocked())} byStory={p.unlockedByStory} rank={Try(() => Id(p.modelRank))} req={Try(() => p.isrequirementSatisfied())}");
            }
            arcade[Id(group.modelDiff) ?? g.ToString()] = songs;
        }
        result["arcade"] = arcade;
        return result;
    }

    private static List<string> Ids(VectorFlash<ModelStoryCheckpoint>? list)
    {
        var ids = new List<string>();
        if (list == null) return ids;
        for (int i = 0; i < list.Length; i++) ids.Add(list[i].id);
        return ids;
    }
}
