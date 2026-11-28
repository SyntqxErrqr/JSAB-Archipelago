using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace JSABArchipelago;

/// <summary>
/// Everything the apworld tells the mod about this slot. The mod keeps no song table of its own: song numbers,
/// location and item ids and the chapter layout all come from here, so the apworld stays the single source of truth.
/// </summary>
internal sealed class SlotData
{
    public const int LostChapterExclude = 0;
    public const int LostChapterRequire = 2;

    public string Mode = "playlist";
    public bool IsStory => Mode == "story";
    public bool SRankChecks;
    public int MinimumDifficulty = 1;
    public int LostChapter;

    public List<List<string>> Chapters = new();
    public List<string> ChapterNames = new();
    public int LostChapterIndex;
    public string Finale = "";
    /// <summary>The Lost Chapter's last level spot.</summary>
    public string LostChapterBoss = "";

    /// <summary>
    /// Story level spots (map nodes that start a level) in map order, by the id of the song the game normally plays
    /// there. Main story spot N opens with N Progressive Levels (the first is open from the start); Lost Chapter spot
    /// N with N+1 Progressive Lost Chapter Levels.
    /// </summary>
    public List<string> MainStorySpots = new();
    public List<string> LostChapterSpots = new();
    /// <summary>The song each spot plays in this seed. Only differs from the spot with Shuffle Story Songs.</summary>
    public Dictionary<string, string> SpotSongs = new();

    public HashSet<string> SongPool = new();
    public HashSet<string> StartingSongs = new();
    public string? GoalSong;
    public int BeatShardsPlaced;
    public int BeatShardsRequired;

    public Dictionary<string, int> SongNumbers = new();
    public long ClearBase;
    public long SRankBase;

    public long ItemProgressiveLevel;
    public long ItemProgressiveLostLevel;
    public long ItemBeatShard;
    public long ItemBeatPoints;
    public long ItemDash;
    public long ItemSongBase;
    public uint BeatPointsPerItem;

    public bool StartWithDash = true;
    /// <summary>How many tutorial triangles are checks (0 when Tutorial Checkpoints is off).</summary>
    public int TutorialCheckpoints;
    public long TutorialCheckpointBase;
    public string TutorialSong = "tuto_0";
    /// <summary>Songs the game always plays on Normal, so the minimum difficulty doesn't apply to them.</summary>
    public HashSet<string> DifficultyExempt = new();

    /// <summary>Song unlock item id to the game's checkpoint id.</summary>
    public Dictionary<long, string> SongByItemId = new();

    public static SlotData Parse(Dictionary<string, object> raw)
    {
        var json = JObject.FromObject(raw);
        var data = new SlotData
        {
            Mode = (string?)json["mode"] ?? "playlist",
            SRankChecks = (bool?)json["s_rank_checks"] ?? true,
            MinimumDifficulty = (int?)json["minimum_difficulty"] ?? 1,
            LostChapter = (int?)json["lost_chapter"] ?? 1,
            Chapters = json["chapters"]?.ToObject<List<List<string>>>() ?? new(),
            ChapterNames = json["chapter_names"]?.ToObject<List<string>>() ?? new(),
            LostChapterIndex = (int?)json["lost_chapter_index"] ?? 6,
            Finale = (string?)json["finale"] ?? "towerBoss_3",
            LostChapterBoss = (string?)json["lost_chapter_boss"] ?? "lost_chapter_boss",
            MainStorySpots = json["main_story_spots"]?.ToObject<List<string>>() ?? new(),
            LostChapterSpots = json["lost_chapter_spots"]?.ToObject<List<string>>() ?? new(),
            SpotSongs = json["spot_songs"]?.ToObject<Dictionary<string, string>>() ?? new(),
            SongPool = new HashSet<string>(json["song_pool"]?.ToObject<List<string>>() ?? new()),
            StartingSongs = new HashSet<string>(json["starting_songs"]?.ToObject<List<string>>() ?? new()),
            GoalSong = (string?)json["goal_song"],
            BeatShardsPlaced = (int?)json["beat_shards_placed"] ?? 0,
            BeatShardsRequired = (int?)json["beat_shards_required"] ?? 0,
            SongNumbers = json["songs"]?.ToObject<Dictionary<string, int>>() ?? new(),
            ClearBase = (long?)json["location_id_bases"]?["clear"] ?? 1000,
            SRankBase = (long?)json["location_id_bases"]?["s_rank"] ?? 2000,
            ItemProgressiveLevel = (long?)json["item_ids"]?["progressive_level"] ?? 6,
            ItemProgressiveLostLevel = (long?)json["item_ids"]?["progressive_lost_level"] ?? 7,
            ItemBeatShard = (long?)json["item_ids"]?["beat_shard"] ?? 3,
            ItemBeatPoints = (long?)json["item_ids"]?["beat_points"] ?? 4,
            ItemDash = (long?)json["item_ids"]?["dash"] ?? 5,
            ItemSongBase = (long?)json["item_ids"]?["song_base"] ?? 100,
            BeatPointsPerItem = (uint?)json["beat_points_per_item"] ?? 100,
            StartWithDash = (bool?)json["start_with_dash"] ?? true,
            TutorialCheckpoints = (int?)json["tutorial_checkpoints"] ?? 0,
            TutorialCheckpointBase = (long?)json["tutorial_checkpoint_location_base"] ?? 3000,
            TutorialSong = (string?)json["tutorial_song"] ?? "tuto_0",
            DifficultyExempt = new HashSet<string>(json["difficulty_exempt"]?.ToObject<List<string>>() ?? new()),
        };
        data.SongByItemId = data.SongNumbers.ToDictionary(pair => data.ItemSongBase + pair.Value, pair => pair.Key);
        return data;
    }

    public long? ClearLocation(string gameId) =>
        SongNumbers.TryGetValue(gameId, out var number) ? ClearBase + number : null;

    public long? SRankLocation(string gameId) =>
        SongNumbers.TryGetValue(gameId, out var number) ? SRankBase + number : null;

    /// <summary>
    /// Story slots from apworld 0.1.x opened whole chapters with Progressive Chapter items and carry no level spots.
    /// </summary>
    public bool IsOldStorySlot => IsStory && MainStorySpots.Count == 0;

    public bool SongsShuffled => SpotSongs.Any(pair => pair.Key != pair.Value);

    /// <summary>The song played at a story spot in this seed.</summary>
    public string SongAt(string spot) => SpotSongs.TryGetValue(spot, out var song) ? song : spot;

    /// <summary>Whether a song is played somewhere in this seed's story.</summary>
    public bool IsStorySongInSeed(string gameId) => SpotSongs.ContainsValue(gameId);

    /// <summary>Index of the chapter a story map node belongs to, or -1 if it is not part of any chapter.</summary>
    public int ChapterOf(string nodeId)
    {
        for (int i = 0; i < Chapters.Count; i++)
            if (Chapters[i].Contains(nodeId)) return i;
        return -1;
    }

    public string ChapterName(int index) =>
        index >= 0 && index < ChapterNames.Count ? ChapterNames[index] : $"Chapter {index + 1}";
}
