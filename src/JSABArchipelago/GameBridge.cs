using System.Collections.Generic;

namespace JSABArchipelago;

/// <summary>Reads and writes the game's own progress objects. Main thread only.</summary>
internal static class GameBridge
{
    public static MetaGameProgress? Progress => MetaGameProgress.instance;

    public static ModelStoryCheckpoint? Song(string gameId)
    {
        var song = ModelStoryCheckpointEnum.getFromId(gameId);
        return song == null || song.isNull ? null : song;
    }

    public static string? IdOf(ModelBase? model) => model == null || model.isNull ? null : model.id;

    /// <summary>The difficulty being played right now: 1 Normal, 2 Hard, 3 Expert.</summary>
    public static int CurrentDifficulty
    {
        get
        {
            var difficulty = Progress?.modelGameDifficulty;
            return difficulty == null || difficulty.isNull ? 1 : difficulty.difficultyValue;
        }
    }

    /// <summary>The story progress of a map spot (or of the song the game normally plays there).</summary>
    public static MetaSongProgress? StoryLevel(string gameId)
    {
        var progress = Progress;
        var song = Song(gameId);
        if (progress == null || song == null) return null;
        var level = progress.metaStoryProgress.metaSongGroup.getMetaLevelFromOnModel(song);
        return level == null || level.isNull() ? null : level;
    }

    public static bool IsStoryCompleted(string gameId) => StoryLevel(gameId)?.isCompleted() ?? false;

    /// <summary>Point a level's result (and the story's last result, if it's another object) at a story progress.</summary>
    public static void SetResultLevel(MetaGameResult? result, MetaSongProgress level)
    {
        if (result != null && !result.isNull()) result.metaLevelProgress = level;
        var last = Progress?.metaStoryProgress.metaResultLastGame;
        if (last != null && !last.isNull() && (result == null || last.Pointer != result.Pointer))
            last.metaLevelProgress = level;
    }

    /// <summary>Set the song of the level scene being played, which decides where the game goes when it ends.</summary>
    public static void SetLevelSceneSong(ModelStoryCheckpoint song)
    {
        var scene = GameScene.instance?.TryCast<NormalLevelScene>();
        if (scene != null) scene.modelSong = song;
    }

    /// <summary>Make a map spot the level being played, as far as the story progress is concerned.</summary>
    public static void EnterSpot(string spotId)
    {
        var story = Progress?.metaStoryProgress;
        var spot = StoryLevel(spotId);
        if (story == null || spot == null) return;
        story.lastEnteredSong = spot.modelSong;
        story.metaCurrentLevel = spot;
    }

    /// <summary>
    /// A shuffled level ended: the game cleared <paramref name="fromSong"/>'s own map spot. Put that spot back the way
    /// it was (<paramref name="fromSongBefore"/>, from MetaSongProgress.encode), clear <paramref name="toSpot"/>, the
    /// spot the player entered, instead, and make it the last level entered and completed.
    /// </summary>
    public static void MoveStoryClear(string fromSong, string toSpot, Il2CppSystem.Collections.Hashtable? fromSongBefore)
    {
        var story = Progress?.metaStoryProgress;
        var played = StoryLevel(fromSong);
        var spot = StoryLevel(toSpot);
        if (story == null || played == null || spot == null) return;
        Plugin.Logger.LogInfo($"Moving the clear of {fromSong} to spot {toSpot}: {DescribeStory()}");
        int deaths = played.nbDeaths;
        if (fromSongBefore != null) played.decode(fromSongBefore);
        if (!spot.isCompleted()) spot.complete(deaths);
        story.lastCompletedSong = spot.modelSong;
        story.lastEnteredSong = spot.modelSong;
        story.metaCurrentLevel = spot;
        Plugin.Logger.LogInfo($"Moved: {DescribeStory()} ({fromSong} completed={played.isCompleted()})");
    }

    public static string DescribeStory()
    {
        var story = Progress?.metaStoryProgress;
        if (story == null) return "(no progress)";
        return $"lastEntered={IdOf(story.lastEnteredSong)} lastCompleted={IdOf(story.lastCompletedSong)} " +
               $"current={IdOf(story.metaCurrentLevel?.modelSong)}";
    }

    public static bool HasCompletedStoryMode => Progress?.metaStoryProgress.hasCompletedStoryMode ?? false;
    public static bool HasCompletedLostChapter => Progress?.metaStoryProgress.hasCompletedLostChapter ?? false;

    /// <summary>Playlist results for a song on every difficulty: (difficulty, completed, S rank).</summary>
    public static IEnumerable<(int Difficulty, bool Completed, bool SRank)> ArcadeResults(string gameId)
    {
        var progress = Progress;
        var song = Song(gameId);
        if (progress == null || song == null) yield break;
        var groups = progress.metaArcadeProgress.list;
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var result = group.getSongProgressFromOnModel(song);
            if (result == null || result.isNull()) continue;
            var rank = result.modelRank;
            bool completed = result.isCompleted();
            yield return (group.modelDiff.difficultyValue, completed, completed && rank != null && rank.isRankS());
        }
    }

    /// <summary>Which difficulty's Playlist record this song progress belongs to, or null if it isn't in any.</summary>
    public static int? DifficultyOf(MetaArcadeSongProgress songProgress)
    {
        var progress = Progress;
        if (progress == null) return null;
        var groups = progress.metaArcadeProgress.list;
        for (int i = 0; i < groups.Length; i++)
        {
            var songs = groups[i].songProgress;
            for (int j = 0; j < songs.Length; j++)
                if (songs[j].Pointer == songProgress.Pointer) return groups[i].modelDiff.difficultyValue;
        }
        return null;
    }

    public static string Serialize() => Progress?.Save() ?? "";

    /// <summary>
    /// Replace the loaded progress with a slot's save, or with a fresh one, keeping the player's settings
    /// (volume, controls, display) from their normal save.
    /// </summary>
    public static void LoadSlotProgress(string? save, bool unlockMainMenu)
    {
        var progress = Progress;
        if (progress == null) return;
        var options = progress.metaOptions.encode();
        progress.Clear();
        if (!string.IsNullOrEmpty(save)) progress.Load(save);
        progress.metaOptions.decode(options);
        if (unlockMainMenu) progress.SetMainMenuUnlocked();
    }

    public static void AddBeatPoints(uint amount) => Progress?.addBPToPlayerAndBank(amount);
}
