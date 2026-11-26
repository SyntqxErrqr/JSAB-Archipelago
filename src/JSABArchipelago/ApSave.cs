using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;

namespace JSABArchipelago;

/// <summary>
/// The per-slot save. While connected, the game's own save is never written: everything it would have saved goes
/// into this file instead, next to the mod's config.
/// </summary>
internal sealed class ApSave
{
    public int Version { get; set; } = 1;
    public string Seed { get; set; } = "";
    public string Slot { get; set; } = "";

    /// <summary>How many received items have already had their one-time effects (Beat Points, messages) applied.</summary>
    public int ItemsHandled { get; set; }

    /// <summary>
    /// Story levels the mod saw cleared on the minimum difficulty or above. The game's own save doesn't record the
    /// difficulty of a story clear, so this is what lets a clear count when the mod resends checks later.
    /// </summary>
    public System.Collections.Generic.HashSet<string> StoryClears { get; set; } = new();

    /// <summary>The game's own save data (MetaGameProgress.Save()), or null for a slot that hasn't been played yet.</summary>
    public string? Game { get; set; }

    public static string Directory => Path.Combine(Paths.ConfigPath, "JSABArchipelago", "saves");

    public static string PathFor(string seed, string slot) =>
        Path.Combine(Directory, $"{Sanitize(seed)}_{Sanitize(slot)}.json");

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean.Length > 60 ? clean[..60] : clean;
    }

    public static ApSave LoadOrCreate(string seed, string slot)
    {
        var path = PathFor(seed, slot);
        if (File.Exists(path))
        {
            try
            {
                var save = JsonSerializer.Deserialize<ApSave>(File.ReadAllText(path));
                if (save != null) return save;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Could not read {path}, keeping it as .bad and starting over: {e.Message}");
                File.Copy(path, path + ".bad", overwrite: true);
            }
        }
        return new ApSave { Seed = seed, Slot = slot };
    }

    public void Write()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(Seed, Slot);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, path, overwrite: true);
    }
}
