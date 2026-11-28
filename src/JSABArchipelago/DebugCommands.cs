using System;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;

namespace JSABArchipelago;

/// <summary>
/// Developer commands, read from BepInEx\jsab_ap_command.txt when Debug.Commands is on. Each line is one command:
///   state                          log the mod's state and what the game reports for every song and story spot
///   story-complete &lt;songId&gt;        mark a story level cleared, as if its last triangle had been collected
///   story-jump &lt;spotId&gt;            clear every story spot before this one (as the chapter select does)
///   playlist-complete &lt;songId&gt; &lt;rankId&gt;   record a Playlist result on the current difficulty (fires the hooks)
///   tutorial-scene &lt;n&gt;             as if the tutorial had just started scene n (0-8)
///   force-levels &lt;n&gt;|off          pretend to have n Progressive Levels
///   force-dash on|off|auto         pretend Dash has or hasn't been received
///   invincible on|off              switch Debug.Invincible
///   audio                          log every Unity audio source that is playing
/// Rank ids: completeMultipleDeaths (C), completeOneDeath (B), completeNoDeath (A), completeNoDmg (S).
/// </summary>
internal static class DebugCommands
{
    private static float _sinceCheck;
    private static string CommandFile => Path.Combine(Paths.BepInExRootPath, "jsab_ap_command.txt");

    public static void Update(float deltaTime)
    {
        if (!Plugin.Instance.DebugCommands.Value) return;
        _sinceCheck += deltaTime;
        if (_sinceCheck < 1f) return;
        _sinceCheck = 0f;
        if (!File.Exists(CommandFile)) return;
        var lines = File.ReadAllLines(CommandFile);
        File.Delete(CommandFile);
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try { Run(line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)); }
            catch (Exception e) { Plugin.Logger.LogError($"[debug] '{line}' failed: {e}"); }
        }
    }

    private static void Run(string[] args)
    {
        Plugin.Logger.LogInfo("[debug] " + string.Join(' ', args));
        switch (args[0])
        {
            case "state":
                LogState();
                break;
            case "tutorial-scene":
                // As if the tutorial had just started this scene (0-8).
                ArchipelagoController.OnTutorialScene(int.Parse(args[1]));
                break;
            case "force-dash":
                // Pretend Dash has (on) or hasn't (off) been received, or clear the override (auto).
                ArchipelagoController.ForcedDash = args[1] switch { "on" => true, "off" => false, _ => null };
                break;
            case "force-levels":
                // Pretend to have this many Progressive Levels (or "off"), to test the level locks.
                ArchipelagoController.ForcedLevels = args[1] == "off" ? null : int.Parse(args[1]);
                break;
            case "audio":
                LogAudio();
                break;
            case "invincible":
                Plugin.Instance.DebugInvincible.Value = args[1] == "on";
                break;
            case "story-jump":
            {
                // Clear every story spot before this one the way the chapter select does, so Story starts there.
                var song = GameBridge.Song(args[1]) ?? throw new ArgumentException("no song " + args[1]);
                var story = GameBridge.Progress!.metaStoryProgress;
                story.completeUpToSong(song, true);
                Plugin.Logger.LogInfo("[debug] story now " + GameBridge.DescribeStory());
                break;
            }
            case "story-complete":
            {
                var song = GameBridge.Song(args[1]) ?? throw new ArgumentException("no song " + args[1]);
                var level = GameBridge.Progress!.metaStoryProgress.metaSongGroup.getMetaLevelFromOnModel(song);
                level.complete(0);
                ArchipelagoController.OnLevelEnd("LogicEndLevel", args[1]);
                break;
            }
            case "playlist-complete":
            {
                var song = GameBridge.Song(args[1]) ?? throw new ArgumentException("no song " + args[1]);
                var rank = ModelRankEnum.getFromId(args.Length > 2 ? args[2] : "completeNoDeath");
                var difficulty = GameBridge.Progress!.modelGameDifficulty;
                var group = GameBridge.Progress.metaArcadeProgress.getFromDiff(difficulty);
                group.getSongProgressFromOnModel(song).complete(rank);
                break;
            }
            default:
                Plugin.Logger.LogWarning("[debug] unknown command " + args[0]);
                break;
        }
    }

    private static void LogState()
    {
        var text = new StringBuilder();
        var slot = ArchipelagoController.Slot;
        text.AppendLine($"redirected={ArchipelagoController.SaveRedirected} connected={ArchipelagoController.Connected} " +
                        $"status='{ArchipelagoController.Status}' difficulty={GameBridge.CurrentDifficulty}");
        if (slot != null)
        {
            text.AppendLine($"dash={ArchipelagoController.HasDash} startWithDash={slot.StartWithDash} " +
                            $"tutorialCheckpoints={ArchipelagoController.Save?.TutorialCheckpointsReached}" +
                            $"/{slot.TutorialCheckpoints}");
            text.AppendLine($"mode={slot.Mode} levels={ArchipelagoController.ProgressiveLevels} " +
                            $"lostLevels={ArchipelagoController.ProgressiveLostLevels} shards={ArchipelagoController.BeatShards}" +
                            $"/{slot.BeatShardsRequired} goal={slot.GoalSong} goalOpen={ArchipelagoController.GoalSongOpen}");
            text.AppendLine($"story: {GameBridge.DescribeStory()} beaten=[{string.Join(", ",
                ArchipelagoController.Save?.StorySongsBeaten ?? new())}]");
            foreach (var spot in slot.MainStorySpots.Concat(slot.LostChapterSpots))
                text.AppendLine($"  spot {spot,-22} plays {slot.SongAt(spot),-22} open={ArchipelagoController.IsSpotOpen(spot),-5} " +
                                $"completed={GameBridge.IsStoryCompleted(spot)}");
            var progress = GameBridge.Progress!;
            var normal = progress.metaArcadeProgress.getFromNormalDiff();
            foreach (var gameId in slot.SongNumbers.Keys)
            {
                var song = GameBridge.Song(gameId);
                if (song == null) continue;
                var arcade = normal.getSongProgressFromOnModel(song);
                bool? unlocked = arcade == null || arcade.isNull() ? null : arcade.isUnlocked();
                text.AppendLine($"  {gameId,-22} story={GameBridge.IsStoryCompleted(gameId),-5} " +
                                $"chapter={slot.ChapterOf(gameId),2} playlistUnlocked={unlocked} " +
                                $"rank={GameBridge.IdOf(arcade?.modelRank)}");
            }
        }
        Plugin.Logger.LogInfo("[debug] state\n" + text);
    }

    /// <summary>Every Unity audio source that is playing right now, to spot music playing twice.</summary>
    public static void LogAudio()
    {
        var text = new StringBuilder();
        var sources = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.AudioSource>());
        int playing = 0;
        foreach (var obj in sources)
        {
            var source = obj.TryCast<UnityEngine.AudioSource>();
            if (source == null || !source.isPlaying) continue;
            playing++;
            var clip = source.clip;
            text.AppendLine($"  {source.gameObject.name} clip={(clip == null ? "null" : clip.name)} " +
                            $"length={(clip == null ? 0 : clip.length):0.0}s at={source.time:0.0}s volume={source.volume:0.00} " +
                            $"loop={source.loop}");
        }
        Plugin.Logger.LogInfo($"[debug] audio: {playing} of {sources.Length} sources playing\n{text}");
    }
}
