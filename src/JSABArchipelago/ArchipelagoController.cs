using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Models;

namespace JSABArchipelago;

/// <summary>
/// The mod's state and rules. Everything here runs on the game's main thread; network callbacks are queued and
/// handled in <see cref="Update"/>, since the game's IL2CPP objects must not be touched from other threads.
/// </summary>
internal static class ArchipelagoController
{
    public const string GameName = "Just Shapes & Beats";

    private static readonly ConcurrentQueue<Action> MainThread = new();

    public static ArchipelagoSession? Session { get; private set; }
    public static SlotData? Slot { get; private set; }
    public static ApSave? Save { get; private set; }
    public static string? SlotName { get; private set; }

    /// <summary>True once a slot's save has replaced the normal save. Stays true until the game is restarted.</summary>
    public static bool SaveRedirected { get; private set; }

    /// <summary>The player chose to play their normal save without Archipelago this session.</summary>
    public static bool PlayingOffline { get; private set; }

    public static bool Connecting { get; private set; }
    public static bool Connected => Session?.Socket.Connected ?? false;
    public static string Status { get; private set; } = "Not connected";

    private static readonly List<long> ReceivedItems = new();
    private static int _itemsProcessed;
    private static bool _goalSent;
    private static float _sinceResync;

    public static bool TitleMayContinue => SaveRedirected || PlayingOffline;
    public static bool Active => SaveRedirected && Slot != null;

    // ---- derived item state ------------------------------------------------------------------------------------------

    public static int ProgressiveLevels => ForcedLevels ?? Count(Slot?.ItemProgressiveLevel);

    /// <summary>Debug override for <see cref="ProgressiveLevels"/>, set with the force-levels command.</summary>
    public static int? ForcedLevels;
    public static int ProgressiveLostLevels => Count(Slot?.ItemProgressiveLostLevel);
    public static int BeatShards => Count(Slot?.ItemBeatShard);

    private static int Count(long? itemId) => itemId == null ? 0 : ReceivedItems.Count(id => id == itemId);

    public static bool GoalSongOpen => Slot != null && BeatShards >= Slot.BeatShardsRequired;

    public static bool HasDash => ForcedDash ?? (Slot == null || Slot.StartWithDash || Count(Slot.ItemDash) > 0);

    /// <summary>Debug override for <see cref="HasDash"/>, set with the force-dash command.</summary>
    public static bool? ForcedDash;

    /// <summary>Whether the player may dash right now. Always true without a slot loaded.</summary>
    public static bool MayDash => !Active || HasDash;

    private static float _lastDashMessage = -999f;

    /// <summary>The player pressed dash without having Dash. Says so, at most every few seconds.</summary>
    public static void OnDashBlocked()
    {
        if (UnityEngine.Time.unscaledTime - _lastDashMessage < 6f) return;
        _lastDashMessage = UnityEngine.Time.unscaledTime;
        Messages.Add("You can't dash yet: Dash hasn't been found.");
    }

    /// <summary>
    /// What a story map node needs before it can be entered: whether it's a Lost Chapter spot, and how many Progressive
    /// Levels (or Progressive Lost Chapter Levels) it takes. Null for nodes that are always open, such as cutscenes and
    /// the Lost Chapter when it isn't part of the seed.
    /// </summary>
    public static (bool Lost, int Needed)? SpotRequirement(string spot)
    {
        if (Slot == null) return null;
        int main = Slot.MainStorySpots.IndexOf(spot);
        if (main >= 0) return (false, main);
        int lost = Slot.LostChapterSpots.IndexOf(spot);
        if (lost >= 0 && Slot.LostChapter != SlotData.LostChapterExclude) return (true, lost + 1);
        return null;
    }

    public static bool IsSpotOpen(string spot)
    {
        if (SpotRequirement(spot) is not var (lost, needed)) return true;
        return (lost ? ProgressiveLostLevels : ProgressiveLevels) >= needed;
    }

    /// <summary>Whether a song is open on the Playlist screen. Null leaves it to the game.</summary>
    public static bool? IsPlaylistSongOpen(string gameId)
    {
        if (!Active || Slot == null || Save == null) return null;
        if (!Slot.IsStory) return IsPlaylistSongUnlocked(gameId);
        // Story mode: a story song opens on the Playlist once it has been cleared in the story, wherever it was
        // played. The game's own rule follows the map spot instead, which is wrong with shuffled songs.
        return Slot.IsStorySongInSeed(gameId) ? Save.StorySongsBeaten.Contains(gameId) : null;
    }

    public static bool IsPlaylistSongUnlocked(string gameId)
    {
        if (Slot == null) return true;
        if (Slot.StartingSongs.Contains(gameId)) return true;
        if (gameId == Slot.GoalSong) return GoalSongOpen;
        return Slot.SongNumbers.TryGetValue(gameId, out var number) && ReceivedItems.Contains(Slot.ItemSongBase + number);
    }

    // ---- connection --------------------------------------------------------------------------------------------------

    private static bool _autoConnectTried;

    /// <summary>
    /// Called every frame the title screen waits for input. The game has loaded its normal save by then, so this is
    /// the earliest safe moment to swap in a slot's save.
    /// </summary>
    public static void OnTitleScreen()
    {
        if (_autoConnectTried || TitleMayContinue || Connecting) return;
        _autoConnectTried = true;
        var config = Plugin.Instance;
        if (!config.AutoConnect.Value || string.IsNullOrWhiteSpace(config.Slot.Value)) return;
        Connect(config.Server.Value.Trim(), config.Slot.Value.Trim(), config.Password.Value);
    }

    public static void PlayOffline()
    {
        if (SaveRedirected) return;
        PlayingOffline = true;
        Status = "Playing without Archipelago";
        Messages.Add("Playing your normal save. Restart the game to use Archipelago.");
    }

    public static void Connect(string server, string slot, string password)
    {
        if (Connecting || PlayingOffline) return;
        if (SaveRedirected && slot != SlotName)
        {
            Messages.Add("Restart the game to switch to a different slot.");
            return;
        }
        Connecting = true;
        Status = $"Connecting to {server}...";
        Task.Run(() =>
        {
            try
            {
                var session = ArchipelagoSessionFactory.CreateSession(server);
                // Subscribe before logging in so the first batch of items isn't missed.
                session.Items.ItemReceived += helper =>
                {
                    var item = helper.DequeueItem();
                    MainThread.Enqueue(() => OnItemReceived(item));
                };
                session.MessageLog.OnMessageReceived += message => MainThread.Enqueue(() => OnMessage(message));
                session.Socket.SocketClosed += reason => MainThread.Enqueue(() => OnSocketClosed(reason));
                var result = session.TryConnectAndLogin(GameName, slot, ItemsHandlingFlags.AllItems,
                    new Version(0, 6, 0), Array.Empty<string>(), null,
                    string.IsNullOrEmpty(password) ? null : password, true);
                MainThread.Enqueue(() => OnLoginResult(session, result, slot));
            }
            catch (Exception e)
            {
                MainThread.Enqueue(() =>
                {
                    Connecting = false;
                    Status = "Connection failed: " + e.Message;
                    Plugin.Logger.LogError(e);
                });
            }
        });
    }

    private static void OnLoginResult(ArchipelagoSession session, LoginResult result, string slotName)
    {
        Connecting = false;
        if (result is not LoginSuccessful success)
        {
            var errors = result is LoginFailure failure ? string.Join("; ", failure.Errors) : "unknown error";
            Status = "Connection refused: " + errors;
            Plugin.Logger.LogWarning(Status);
            TryDisconnect(session);
            return;
        }

        var seed = session.RoomState.Seed;
        if (SaveRedirected && (Save == null || Save.Seed != seed || Save.Slot != slotName))
        {
            Status = "That is a different multiworld. Restart the game to switch.";
            TryDisconnect(session);
            return;
        }

        SlotData slot;
        try
        {
            slot = SlotData.Parse(success.SlotData);
        }
        catch (Exception e)
        {
            Status = "Could not read slot data: " + e.Message;
            Plugin.Logger.LogError(e);
            TryDisconnect(session);
            return;
        }
        if (slot.IsOldStorySlot)
        {
            Status = "This Story slot was made with an older jsab.apworld (0.1.x). Generate it again with 0.2.0 or newer.";
            Plugin.Logger.LogWarning(Status);
            TryDisconnect(session);
            return;
        }

        Session = session;
        Slot = slot;
        SlotName = slotName;
        // The server resends every item on connect; rebuild the item state from scratch.
        ReceivedItems.Clear();
        _itemsProcessed = 0;

        if (!SaveRedirected)
        {
            // The title screen already started the menu music for the normal save. Keep that track until it ends,
            // or the main menu starts the slot's track on top of it.
            _keepMenuMusic = _menuMusicBeforeSlot;
            Save = ApSave.LoadOrCreate(seed, slotName);
            bool fresh = Save.Game == null;
            GameBridge.LoadSlotProgress(Save.Game, unlockMainMenu: !slot.IsStory);
            SaveRedirected = true;
            RecoverShuffledLevel();
            WriteSave();
            Plugin.Logger.LogInfo($"{(fresh ? "Started" : "Loaded")} slot save {ApSave.PathFor(seed, slotName)}");
        }

        Status = $"Connected as {slotName} ({(slot.IsStory ? "Story" : "Playlist")} mode)";
        Messages.Add(Status);
        Resync();
    }

    private static void OnSocketClosed(string reason)
    {
        if (Session == null) return;
        Status = "Disconnected: " + reason + ". Reconnect from the panel (F1).";
        Messages.Add(Status);
    }

    private static void TryDisconnect(ArchipelagoSession session)
    {
        try { session.Socket.DisconnectAsync(); }
        catch (Exception e) { Plugin.Logger.LogDebug(e); }
    }

    // ---- items and messages ------------------------------------------------------------------------------------------

    private static void OnItemReceived(ItemInfo item)
    {
        if (Slot == null || Save == null) return;
        ReceivedItems.Add(item.ItemId);
        int index = _itemsProcessed++;
        if (index < Save.ItemsHandled) return;

        // First time this item is seen in this slot: apply one-time effects.
        Save.ItemsHandled = index + 1;
        if (item.ItemId == Slot.ItemBeatPoints)
            GameBridge.AddBeatPoints(Slot.BeatPointsPerItem);
        if (item.ItemId == Slot.ItemBeatShard && BeatShards == Slot.BeatShardsRequired && Slot.GoalSong != null)
            Messages.Add("You have enough Beat Shards: the goal song is open!");
        if (item.ItemId == Slot.ItemProgressiveLevel)
            Messages.Add($"Story level {ProgressiveLevels + 1} of {Slot.MainStorySpots.Count} is now open.");
        if (item.ItemId == Slot.ItemProgressiveLostLevel)
            Messages.Add($"Lost Chapter level {ProgressiveLostLevels} of {Slot.LostChapterSpots.Count} is now open.");
        if (item.ItemId == Slot.ItemDash && !Slot.StartWithDash)
            Messages.Add("You can dash now!");
        WriteSave();
    }

    private static void OnMessage(LogMessage message)
    {
        switch (message)
        {
            case ItemSendLogMessage send when send.IsReceiverTheActivePlayer || send.IsSenderTheActivePlayer:
            case HintItemSendLogMessage:
            case GoalLogMessage:
            case CountdownLogMessage:
            case ServerChatLogMessage:
                Messages.Add(message.ToString());
                break;
        }
    }

    // ---- checks ------------------------------------------------------------------------------------------------------

    private static void SendLocations(IEnumerable<long?> ids)
    {
        var session = Session;
        if (session == null || !Connected) return;
        var all = session.Locations.AllLocations;
        var done = session.Locations.AllLocationsChecked;
        var toSend = ids.Where(id => id != null && all.Contains(id.Value) && !done.Contains(id.Value))
            .Select(id => id!.Value).Distinct().ToArray();
        if (toSend.Length == 0) return;
        Plugin.Logger.LogInfo("Sending locations " + string.Join(", ", toSend));
        session.Locations.CompleteLocationChecksAsync(toSend);
    }

    private static readonly List<float> PendingResyncs = new();

    /// <summary>
    /// The player collected a level's last triangle. <paramref name="kind"/> is the runtime type of the game's
    /// end-of-level logic, which tells the modes apart.
    /// </summary>
    public static void OnLevelEnd(string kind, string? gameId)
    {
        if (!Active) return;
        Plugin.Logger.LogInfo($"Level end ({kind}): {gameId} on difficulty {GameBridge.CurrentDifficulty}");
        if (kind is "LogicEndLevel" or "LogicItllItsOverEndLevel") OnStoryCompleted(gameId);
        // Playlist ranks are recorded on the results screen, a moment after this; pick them up from the save.
        PendingResyncs.Add(UnityEngine.Time.unscaledTime + 2f);
        PendingResyncs.Add(UnityEngine.Time.unscaledTime + 8f);
    }

    /// <summary>
    /// The tutorial started scene <paramref name="scene"/> (0-8) because the player collected a triangle: collecting
    /// the first triangle starts scene 0. The triangle after the last scene ends the level and is its clear.
    /// </summary>
    public static void OnTutorialScene(int scene)
    {
        if (!Active || Slot == null || Save == null || !Slot.IsStory || Slot.TutorialCheckpoints <= 0) return;
        int trianglesCollected = scene + 1;
        Plugin.Logger.LogInfo($"Tutorial scene {scene} (triangle {trianglesCollected})");
        int reached = Math.Min(trianglesCollected, Slot.TutorialCheckpoints);
        if (reached > Save.TutorialCheckpointsReached)
        {
            Save.TutorialCheckpointsReached = reached;
            WriteSave();
        }
        SendTutorialCheckpoints();
    }

    private static void SendTutorialCheckpoints()
    {
        if (Slot == null || Save == null) return;
        var ids = new List<long?>();
        for (int n = 1; n <= Save.TutorialCheckpointsReached; n++) ids.Add(Slot.TutorialCheckpointBase + n);
        SendLocations(ids);
    }

    /// <summary>A level was cleared in Story mode. <paramref name="gameId"/> is the song that was played.</summary>
    private static void OnStoryCompleted(string? gameId)
    {
        if (Slot == null || Save == null || gameId == null || !Slot.IsStory) return;
        AfterShuffledLevel(gameId);
        Save.StorySongsBeaten.Add(gameId);
        if (GameBridge.CurrentDifficulty < Slot.MinimumDifficulty && !Slot.DifficultyExempt.Contains(gameId))
        {
            WriteSave();
            Messages.Add("Cleared below the minimum difficulty, so it doesn't count.");
            return;
        }
        Save.StoryClears.Add(gameId);
        WriteSave();
        SendLocations(new[] { Slot.ClearLocation(gameId) });
        CheckGoal();
    }

    // ---- Shuffle Story Songs -----------------------------------------------------------------------------------------

    /// <summary>Set while a level plays a song other than its map spot's own: the spot, and the song playing.</summary>
    private static string? _shuffledSpot, _shuffledSong;

    /// <summary>The playing song's own story progress from before the level, to put back afterwards.</summary>
    private static Il2CppSystem.Collections.Hashtable? _shuffledSongBefore;

    /// <summary>
    /// The game is leaving the story map for the level at <paramref name="spotLevel"/>. Returns the progress of the song
    /// that spot plays in this seed when that isn't the spot's own song, or null to play the spot's own song.
    /// </summary>
    public static MetaSongProgress? SongToPlayAt(MetaSongProgress spotLevel)
    {
        _shuffledSpot = _shuffledSong = null;
        if (!Active || Slot is not { IsStory: true }) return null;
        var spot = GameBridge.IdOf(spotLevel.modelSong);
        if (spot == null) return null;
        var song = Slot.SongAt(spot);
        if (song == spot) return null;
        var level = GameBridge.StoryLevel(song);
        if (level == null)
        {
            Plugin.Logger.LogWarning($"Spot {spot} should play {song}, but the game has no story level for it");
            return null;
        }
        _shuffledSpot = spot;
        _shuffledSong = song;
        _shuffledSongBefore = level.encode();
        if (Save != null)
        {
            Save.ShuffledSpotInProgress = spot;
            Save.ShuffledSongInProgress = song;
            WriteSave();
        }
        Plugin.Logger.LogInfo($"Spot {spot} plays {song}");
        return level;
    }

    private static void EndShuffledLevel()
    {
        _shuffledSpot = _shuffledSong = null;
        _shuffledSongBefore = null;
        if (Save == null) return;
        Save.ShuffledSpotInProgress = Save.ShuffledSongInProgress = null;
        WriteSave();
    }

    /// <summary>
    /// The player is leaving a level from the pause menu without finishing it. If it was a shuffled song, the game would
    /// take them to that song's own spot (possibly in another chapter): point it at the spot they entered instead.
    /// </summary>
    public static void OnLeavingStoryLevel(UI_PauseNew pause)
    {
        if (_shuffledSpot == null || GameBridge.IdOf(pause.modelSong) != _shuffledSong) return;
        var spot = GameBridge.Song(_shuffledSpot);
        var spotLevel = GameBridge.StoryLevel(_shuffledSpot);
        if (spot == null || spotLevel == null) return;
        Plugin.Logger.LogInfo($"Leaving spot {_shuffledSpot} (playing {_shuffledSong}) for the map");
        pause.modelSong = spot;
        var level = ActorNormalLevelLogic.getNormalLevelLogic();
        if (level != null)
        {
            level.modelSong = spot;
            level.metaLevelProgress = spotLevel;
        }
        GameBridge.SetLevelSceneSong(spot);
        if (_shuffledSongBefore != null) GameBridge.StoryLevel(_shuffledSong!)?.decode(_shuffledSongBefore);
        GameBridge.EnterSpot(_shuffledSpot);
        EndShuffledLevel();
    }

    /// <summary>A slot was loaded: if the game closed during a shuffled level, put the player back at its spot.</summary>
    private static void RecoverShuffledLevel()
    {
        if (Save?.ShuffledSpotInProgress is not { } spot || Save.ShuffledSongInProgress is not { } song) return;
        var story = GameBridge.Progress?.metaStoryProgress;
        if (story != null && GameBridge.IdOf(story.lastEnteredSong) == song)
        {
            Plugin.Logger.LogInfo($"The game closed while spot {spot} played {song}; back to the spot");
            GameBridge.EnterSpot(spot);
        }
        EndShuffledLevel();
    }

    /// <summary>
    /// The last triangle of a story level was just collected, and the game is about to record the clear and pick where
    /// on the map to go next, both from the level's song. After a shuffled song, hand it the spot the player entered
    /// instead, so the clear, the chapter's own end-of-level events and the way back all belong to that spot.
    /// </summary>
    public static void BeforeStoryLevelEnd(ActorNormalLevelLogic level)
    {
        if (_shuffledSpot == null || GameBridge.IdOf(level.modelSong) != _shuffledSong) return;
        var spot = GameBridge.Song(_shuffledSpot);
        var spotLevel = GameBridge.StoryLevel(_shuffledSpot);
        if (spot == null || spotLevel == null) return;
        // The level, its progress, its result and the scene each keep the song from the start of the level. The result
        // is what the game marks cleared and what the map uses to pick the spot to return to.
        level.modelSong = spot;
        level.metaLevelProgress = spotLevel;
        GameBridge.SetResultLevel(level.metaGameResult, spotLevel);
        GameBridge.SetLevelSceneSong(spot);
        GameBridge.EnterSpot(_shuffledSpot);
        Plugin.Logger.LogInfo($"Level for spot {_shuffledSpot} ended (played {_shuffledSong}): {GameBridge.DescribeStory()}");
    }

    /// <summary>
    /// A shuffled level ended. The game should have cleared the spot the player entered; make sure of that, and put the
    /// played song's own spot back the way it was in case the game cleared it too.
    /// </summary>
    private static void AfterShuffledLevel(string playedSong)
    {
        if (_shuffledSpot == null || playedSong != _shuffledSong) return;
        GameBridge.MoveStoryClear(playedSong, _shuffledSpot, _shuffledSongBefore);
        EndShuffledLevel();
    }

    /// <summary>The song a story map spot plays in this seed, when it isn't the spot's own song.</summary>
    public static string? ShuffledSongAt(string? spot)
    {
        if (!Active || Slot is not { IsStory: true } || spot == null) return null;
        var song = Slot.SongAt(spot);
        return song == spot ? null : song;
    }

    /// <summary>A song was finished on the Playlist screen with the given rank.</summary>
    public static void OnPlaylistCompleted(MetaArcadeSongProgress songProgress, ModelRank? rank)
    {
        if (!Active || Slot == null) return;
        var gameId = GameBridge.IdOf(songProgress.modelSong);
        if (gameId == null) return;
        int difficulty = GameBridge.DifficultyOf(songProgress) ?? GameBridge.CurrentDifficulty;
        bool sRank = rank != null && !rank.isNull && rank.isRankS();
        Plugin.Logger.LogInfo($"Playlist clear: {gameId} difficulty {difficulty} rank {GameBridge.IdOf(rank)}");
        if (difficulty < Slot.MinimumDifficulty)
        {
            Messages.Add("Played below the minimum difficulty, so it doesn't count.");
            return;
        }
        // Other modes (Challenge, Party) can record Playlist results too; only songs open on the Playlist count.
        if (!IsSongPlayable(gameId, songProgress)) return;
        var ids = new List<long?>();
        if (!Slot.IsStory) ids.Add(Slot.ClearLocation(gameId));
        if (Slot.SRankChecks && sRank) ids.Add(Slot.SRankLocation(gameId));
        SendLocations(ids);
        CheckGoal();
    }

    /// <summary>Whether the song is open on the Playlist screen: by items in Playlist mode, by story clears in Story.</summary>
    private static bool IsSongPlayable(string gameId, MetaArcadeSongProgress songProgress) =>
        IsPlaylistSongOpen(gameId) ?? songProgress.isUnlocked();

    /// <summary>
    /// Send every check the slot's save already shows as done. Covers checks earned while disconnected, and anything a
    /// completion hook missed.
    /// </summary>
    public static void Resync()
    {
        if (!Active || Slot == null || !Connected) return;
        var ids = new List<long?>();
        // Story clears come only from levels the mod saw being played: the game's own record is per map spot, and
        // jumping ahead from the chapter select marks every earlier spot as cleared without playing it.
        if (Slot.IsStory) ids.AddRange(Save!.StoryClears.Select(Slot.ClearLocation));
        foreach (var gameId in Slot.SongNumbers.Keys)
        {
            if (!(IsPlaylistSongOpen(gameId) ?? true)) continue;
            foreach (var (difficulty, completed, sRank) in GameBridge.ArcadeResults(gameId))
            {
                if (difficulty < Slot.MinimumDifficulty) continue;
                if (completed && !Slot.IsStory) ids.Add(Slot.ClearLocation(gameId));
                if (sRank && Slot.SRankChecks) ids.Add(Slot.SRankLocation(gameId));
            }
        }
        SendLocations(ids);
        if (Slot.IsStory) SendTutorialCheckpoints();
        CheckGoal();
    }

    private static void CheckGoal()
    {
        if (_goalSent || !Active || Slot == null || Session == null || !Connected) return;
        bool done;
        if (Slot.IsStory)
        {
            // Till It's Over never moves. The Lost Chapter's last spot can play any song with Shuffle Story Songs.
            done = Save!.StorySongsBeaten.Contains(Slot.Finale) || GameBridge.HasCompletedStoryMode;
            if (Slot.LostChapter == SlotData.LostChapterRequire)
                done &= Save.StorySongsBeaten.Contains(Slot.SongAt(Slot.LostChapterBoss)) ||
                        (!Slot.SongsShuffled && GameBridge.HasCompletedLostChapter);
        }
        else
        {
            done = Slot.GoalSong != null && GoalSongOpen && GameBridge.ArcadeResults(Slot.GoalSong)
                .Any(result => result.Completed && result.Difficulty >= Slot.MinimumDifficulty);
        }
        if (!done) return;
        _goalSent = true;
        Session.SetGoalAchieved();
        Messages.Add("Goal complete!");
        Plugin.Logger.LogMessage("Goal complete");
    }

    // ---- gating ------------------------------------------------------------------------------------------------------

    /// <summary>Whether the player may enter this story map node. Shows a message when they may not.</summary>
    public static bool MayEnterStoryNode(string? spot)
    {
        if (!Active || Slot == null || !Slot.IsStory || spot == null) return true;
        if (IsSpotOpen(spot) || SpotRequirement(spot) is not var (lost, needed)) return true;
        Messages.Add(lost
            ? $"This Lost Chapter level is locked. It opens with Progressive Lost Chapter Level #{needed} " +
              $"(you have {ProgressiveLostLevels})."
            : $"This level is locked. It opens with Progressive Level #{needed} (you have {ProgressiveLevels}).");
        return false;
    }

    // ---- menu music --------------------------------------------------------------------------------------------------

    /// <summary>The last menu music the game picked before a slot was loaded.</summary>
    private static string? _menuMusicBeforeSlot;

    /// <summary>Menu music to keep returning until it stops playing (see <see cref="OnMusicDestroyed"/>).</summary>
    private static string? _keepMenuMusic;

    /// <summary>
    /// The game picks the menu music from the save's progress (UI_MainMenuBase.getMusicToPlay), once on the title
    /// screen and again on the main menu. Loading a slot in between changes the pick, and the main menu then starts a
    /// second track over the title's. Returns the song to use instead, or null to keep the game's pick.
    /// </summary>
    public static ModelStoryCheckpoint? MenuMusic(ModelStoryCheckpoint? picked)
    {
        if (!SaveRedirected)
        {
            _menuMusicBeforeSlot = GameBridge.IdOf(picked);
            return null;
        }
        return _keepMenuMusic == null ? null : GameBridge.Song(_keepMenuMusic);
    }

    public static void OnMusicDestroyed(string? song)
    {
        if (song != null && song == _keepMenuMusic) _keepMenuMusic = null;
    }

    // ---- save --------------------------------------------------------------------------------------------------------

    /// <summary>Write the game's current progress into the slot save. Called instead of the game's own save.</summary>
    public static void WriteSave()
    {
        if (!SaveRedirected || Save == null) return;
        try
        {
            Save.Game = GameBridge.Serialize();
            Save.Write();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("Could not write the slot save: " + e);
        }
    }

    // ---- per frame ---------------------------------------------------------------------------------------------------

    public static void Update(float deltaTime)
    {
        while (MainThread.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { Plugin.Logger.LogError(e); }
        }

        _sinceResync += deltaTime;
        if (PendingResyncs.RemoveAll(time => UnityEngine.Time.unscaledTime >= time) > 0) _sinceResync = 999f;
        if (_sinceResync > 5f)
        {
            _sinceResync = 0f;
            try { Resync(); }
            catch (Exception e) { Plugin.Logger.LogError("Resync failed: " + e); }
        }
    }
}
