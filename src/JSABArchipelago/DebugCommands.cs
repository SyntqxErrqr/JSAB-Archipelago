using System;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;

namespace JSABArchipelago;

/// <summary>
/// Developer commands, read from BepInEx\jsab_ap_command.txt when Debug.Commands is on. Each line is one command:
///   state                          log the mod's state and what the game reports for every song
///   story-complete &lt;songId&gt;        mark a story level cleared, as if its last triangle had been collected
///   playlist-complete &lt;songId&gt; &lt;rankId&gt;   record a Playlist result on the current difficulty (fires the hooks)
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
            case "force-chapters":
                // Pretend to have this many Progressive Chapters (or "off"), to test the chapter locks.
                ArchipelagoController.ForcedChapters = args[1] == "off" ? null : int.Parse(args[1]);
                break;
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
            text.AppendLine($"mode={slot.Mode} chapters={ArchipelagoController.ProgressiveChapters} " +
                            $"lostKey={ArchipelagoController.HasLostChapterKey} shards={ArchipelagoController.BeatShards}" +
                            $"/{slot.BeatShardsRequired} goal={slot.GoalSong} goalOpen={ArchipelagoController.GoalSongOpen}");
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
}
