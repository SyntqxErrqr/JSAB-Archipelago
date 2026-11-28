using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace JSABArchipelago;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.syntqxerrqr.jsab.archipelago";
    public const string Name = "JSAB Archipelago";
    public const string Version = "0.2.0";

    internal static Plugin Instance = null!;
    internal static ManualLogSource Logger = null!;

    internal ConfigEntry<string> Server = null!;
    internal ConfigEntry<string> Slot = null!;
    internal ConfigEntry<string> Password = null!;
    internal ConfigEntry<bool> AutoConnect = null!;
    internal ConfigEntry<bool> DebugInvincible = null!;
    internal ConfigEntry<bool> DebugCommands = null!;
    internal ConfigEntry<bool> DebugTrace = null!;

    public override void Load()
    {
        Instance = this;
        Logger = Log;

        Server = Config.Bind("Connection", "Server", "archipelago.gg:38281", "Last Archipelago server address used.");
        Slot = Config.Bind("Connection", "Slot", "", "Last slot name used.");
        Password = Config.Bind("Connection", "Password", "", "Last room password used.");
        AutoConnect = Config.Bind("Connection", "AutoConnect", false,
            "Connect with the saved server, slot and password as soon as the title screen appears.");
        DebugInvincible = Config.Bind("Debug", "Invincible", false,
            "For testing the mod only: the player can't be hit, so levels play through on their own.");
        DebugCommands = Config.Bind("Debug", "Commands", false,
            "For testing the mod only: run commands from BepInEx\\jsab_ap_command.txt (see DebugCommands.cs).");
        DebugTrace = Config.Bind("Debug", "Trace", false,
            "For testing the mod only: log the game's map, level and music calls (see TracePatches.cs).");

        ClassInjector.RegisterTypeInIl2Cpp<ArchipelagoBehaviour>();
        AddComponent<ArchipelagoBehaviour>();
        Log.LogInfo($"{Name} {Version} loaded");
    }
}
