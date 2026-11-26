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

    public static bool IsStoryCompleted(string gameId)
    {
        var progress = Progress;
        var song = Song(gameId);
        if (progress == null || song == null) return false;
        var level = progress.metaStoryProgress.metaSongGroup.getMetaLevelFromOnModel(song);
        return level != null && level.isCompleted();
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
