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

    public static int ProgressiveChapters => ForcedChapters ?? Count(Slot?.ItemProgressiveChapter);

    /// <summary>Debug override for <see cref="ProgressiveChapters"/>, set with the force-chapters command.</summary>
    public static int? ForcedChapters;
    public static bool HasLostChapterKey => Count(Slot?.ItemLostChapterKey) > 0;
    public static int BeatShards => Count(Slot?.ItemBeatShard);

    private static int Count(long? itemId) => itemId == null ? 0 : ReceivedItems.Count(id => id == itemId);

    public static bool GoalSongOpen => Slot != null && BeatShards >= Slot.BeatShardsRequired;

    public static bool IsChapterUnlocked(int chapter)
    {
        if (Slot == null) return true;
        if (chapter == Slot.LostChapterIndex)
            return Slot.LostChapter == SlotData.LostChapterExclude || HasLostChapterKey;
        return chapter <= ProgressiveChapters;
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

        Session = session;
        Slot = slot;
        SlotName = slotName;
        // The server resends every item on connect; rebuild the item state from scratch.
        ReceivedItems.Clear();
        _itemsProcessed = 0;

        if (!SaveRedirected)
        {
            Save = ApSave.LoadOrCreate(seed, slotName);
            bool fresh = Save.Game == null;
            GameBridge.LoadSlotProgress(Save.Game, unlockMainMenu: !slot.IsStory);
            SaveRedirected = true;
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
        if (item.ItemId == Slot.ItemProgressiveChapter)
            Messages.Add($"{Slot.ChapterName(ProgressiveChapters)} is now open.");
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

    /// <summary>A level was cleared in Story mode.</summary>
    private static void OnStoryCompleted(string? gameId)
    {
        if (Slot == null || Save == null || gameId == null || !Slot.IsStory) return;
        if (GameBridge.CurrentDifficulty < Slot.MinimumDifficulty)
        {
            Messages.Add("Cleared below the minimum difficulty, so it doesn't count.");
            return;
        }
        Save.StoryClears.Add(gameId);
        WriteSave();
        SendLocations(new[] { Slot.ClearLocation(gameId) });
        CheckGoal();
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

    /// <summary>
    /// Whether the song is open on the Playlist screen: by items in Playlist mode, by story progress (the game's
    /// own rule) in Story mode.
    /// </summary>
    private static bool IsSongPlayable(string gameId, MetaArcadeSongProgress songProgress) =>
        Slot is { IsStory: true } ? songProgress.isUnlocked() : IsPlaylistSongUnlocked(gameId);

    /// <summary>
    /// Send every check the slot's save already shows as done. Covers checks earned while disconnected, and anything a
    /// completion hook missed.
    /// </summary>
    public static void Resync()
    {
        if (!Active || Slot == null || !Connected) return;
        var ids = new List<long?>();
        foreach (var gameId in Slot.SongNumbers.Keys)
        {
            // The game's story save doesn't record the difficulty a level was cleared on, so above Normal only clears
            // the mod saw being played on a high enough difficulty count.
            if (Slot.IsStory && GameBridge.IsStoryCompleted(gameId) &&
                (Slot.MinimumDifficulty <= 1 || Save!.StoryClears.Contains(gameId)))
                ids.Add(Slot.ClearLocation(gameId));
            if (!Slot.IsStory && !IsPlaylistSongUnlocked(gameId)) continue;
            foreach (var (difficulty, completed, sRank) in GameBridge.ArcadeResults(gameId))
            {
                if (difficulty < Slot.MinimumDifficulty) continue;
                if (completed && !Slot.IsStory) ids.Add(Slot.ClearLocation(gameId));
                if (sRank && Slot.SRankChecks) ids.Add(Slot.SRankLocation(gameId));
            }
        }
        SendLocations(ids);
        CheckGoal();
    }

    private static void CheckGoal()
    {
        if (_goalSent || !Active || Slot == null || Session == null || !Connected) return;
        bool done;
        if (Slot.IsStory)
        {
            done = GameBridge.IsStoryCompleted(Slot.Finale) || GameBridge.HasCompletedStoryMode;
            if (Slot.LostChapter == SlotData.LostChapterRequire)
                done &= GameBridge.IsStoryCompleted(Slot.LostChapterBoss) || GameBridge.HasCompletedLostChapter;
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
    public static bool MayEnterStoryNode(string? gameId)
    {
        if (!Active || Slot == null || !Slot.IsStory || gameId == null) return true;
        int chapter = Slot.ChapterOf(gameId);
        if (chapter < 0 || IsChapterUnlocked(chapter)) return true;
        Messages.Add(chapter == Slot.LostChapterIndex
            ? "The Lost Chapter is locked. You need the Lost Chapter Key."
            : $"{Slot.ChapterName(chapter)} is locked. You need Progressive Chapter #{chapter}.");
        return false;
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
