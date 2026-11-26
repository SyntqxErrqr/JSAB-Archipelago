using System;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace JSABArchipelago;

/// <summary>Drives the controller every frame and draws the connection panel and message feed.</summary>
public class ArchipelagoBehaviour : MonoBehaviour
{
    public ArchipelagoBehaviour(IntPtr ptr) : base(ptr) { }

    private const float PatchDelay = 2f;
    private float _elapsed;
    private bool _patched;
    private bool _panelOpen = true;
    private int _focused = -1;
    private bool _wasActive;

    private string _server = "";
    private string _slot = "";
    private string _password = "";

    private GUIStyle? _label;
    private GUIStyle? _title;
    private GUIStyle? _message;
    private GUIStyle? _field;
    private GUIStyle? _button;

    private void Start()
    {
        _server = Plugin.Instance.Server.Value;
        _slot = Plugin.Instance.Slot.Value;
        _password = Plugin.Instance.Password.Value;
    }

    private void Update()
    {
        _elapsed += Time.unscaledDeltaTime;
        if (!_patched && _elapsed > PatchDelay)
        {
            _patched = true;
            try
            {
                new Harmony(Plugin.Guid).PatchAll(typeof(Patches));
                Plugin.Logger.LogInfo("Game hooks applied");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("Could not hook the game, the mod is disabled: " + e);
            }
        }
        ArchipelagoController.Update(Time.unscaledDeltaTime);
        if (_patched) DebugCommands.Update(Time.unscaledDeltaTime);

        // Fold the panel away once connected, so it doesn't sit over the song's progress bar.
        if (ArchipelagoController.Active && !_wasActive) _panelOpen = false;
        _wasActive = ArchipelagoController.Active;
    }

    private void OnApplicationQuit() => ArchipelagoController.WriteSave();

    // ---- drawing -----------------------------------------------------------------------------------------------------

    [HideFromIl2Cpp]
    private void EnsureStyles()
    {
        if (_label != null) return;
        _label = new GUIStyle(GUI.skin.label) { fontSize = 18, richText = true, wordWrap = true };
        _title = new GUIStyle(GUI.skin.label) { fontSize = 22, richText = true, fontStyle = FontStyle.Bold };
        _message = new GUIStyle(GUI.skin.label) { fontSize = 20, richText = true, wordWrap = true };
        _field = new GUIStyle(GUI.skin.textField) { fontSize = 18 };
        _button = new GUIStyle(GUI.skin.button) { fontSize = 18 };
    }

    private bool _guiErrorLogged;

    private void OnGUI()
    {
        try
        {
            DrawGui();
        }
        catch (Exception e)
        {
            // OnGUI runs several times a frame; log a failure once rather than flooding the log.
            if (_guiErrorLogged) return;
            _guiErrorLogged = true;
            Plugin.Logger.LogError("Overlay failed to draw: " + e);
        }
    }

    [HideFromIl2Cpp]
    private void DrawGui()
    {
        EnsureStyles();
        var current = Event.current;
        if (current != null && current.type == EventType.KeyDown && current.keyCode == KeyCode.F1)
        {
            _panelOpen = !_panelOpen;
            current.Use();
        }
        if (current != null) HandleTyping(current);

        // Before the player has decided, the panel always shows: the title screen is waiting on it.
        if (_panelOpen || !ArchipelagoController.TitleMayContinue) DrawPanel();
        else DrawStatusLine();
        DrawMessages();
    }

    [HideFromIl2Cpp]
    private void DrawStatusLine()
    {
        GUI.Label(new Rect(16, 8, 900, 30), $"<b>Archipelago</b>: {ArchipelagoController.Status}  <color=#999>(F1)</color>",
            _label);
    }

    [HideFromIl2Cpp]
    private void DrawPanel()
    {
        const float x = 16, width = 480, row = 34;
        float y = 16;
        bool deciding = !ArchipelagoController.SaveRedirected && !ArchipelagoController.PlayingOffline;
        bool showFields = deciding || (ArchipelagoController.SaveRedirected && !ArchipelagoController.Connected);
        float height = 92 + (showFields ? 4 * row + 46 : 0) + (ArchipelagoController.Active ? 3 * row : 0) +
                       (deciding ? 40 : 0);

        // The default box is quite transparent; drawing it twice keeps the text readable over the game.
        GUI.Box(new Rect(x - 8, y - 8, width + 16, height), "");
        GUI.Box(new Rect(x - 8, y - 8, width + 16, height), "");
        GUI.Label(new Rect(x, y, width, row), "Archipelago <size=14><color=#999>F1 to hide</color></size>", _title);
        y += row;
        GUI.Label(new Rect(x, y, width, 2 * row), ArchipelagoController.Status, _label);
        y += 50;

        if (showFields)
        {
            Field(0, "Server", _server, ref y, false);
            if (!ArchipelagoController.SaveRedirected) Field(1, "Slot", _slot, ref y, false);
            Field(2, "Password", _password, ref y, true);
            y += 6;
            GUI.enabled = !ArchipelagoController.Connecting;
            if (GUI.Button(new Rect(x, y, 200, 36), ArchipelagoController.SaveRedirected ? "Reconnect" : "Connect",
                    _button))
            {
                _focused = -1;
                ConnectFromFields();
            }
            GUI.enabled = true;
            y += 46;
        }
        else
        {
            _focused = -1;
        }

        if (deciding)
        {
            if (GUI.Button(new Rect(x, y, 300, 32), "Play without Archipelago", _button))
                ArchipelagoController.PlayOffline();
            y += 40;
        }

        if (ArchipelagoController.Active) DrawSlotInfo(x, ref y, width, row);
    }

    /// <summary>
    /// A text box. Unity's own GUI.TextField can't be used: the game's build strips TextEditor, which it relies on.
    /// So the box is a button that takes focus, and <see cref="HandleTyping"/> feeds it keystrokes.
    /// </summary>
    [HideFromIl2Cpp]
    private string Field(int index, string name, string value, ref float y, bool password)
    {
        GUI.Label(new Rect(16, y + 4, 110, 30), name, _label);
        var rect = new Rect(130, y, 366, 30);
        string shown = password ? new string('*', value.Length) : value;
        if (_focused == index && Time.unscaledTime % 1f < 0.5f) shown += "|";
        if (GUI.Button(rect, shown, _field)) _focused = index;
        y += 34;
        return value;
    }

    private const int FieldCount = 3;

    [HideFromIl2Cpp]
    private ref string FocusedValue(out bool valid)
    {
        valid = true;
        switch (_focused)
        {
            case 0: return ref _server;
            case 1: return ref _slot;
            case 2: return ref _password;
            default:
                valid = false;
                return ref _server;
        }
    }

    [HideFromIl2Cpp]
    private void HandleTyping(Event e)
    {
        if (_focused < 0 || e.type != EventType.KeyDown) return;
        ref string value = ref FocusedValue(out bool valid);
        if (!valid) return;

        if (e.keyCode == KeyCode.Backspace)
        {
            if (value.Length > 0) value = value[..^1];
        }
        else if (e.keyCode == KeyCode.Tab)
        {
            _focused = (_focused + 1) % FieldCount;
            if (_focused == 1 && ArchipelagoController.SaveRedirected) _focused = 2;
        }
        else if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
        {
            _focused = -1;
            ConnectFromFields();
        }
        else if (e.keyCode == KeyCode.Escape)
        {
            _focused = -1;
        }
        else if (e.keyCode == KeyCode.V && (e.control || e.command))
        {
            try { value += (GUIUtility.systemCopyBuffer ?? "").Trim(); }
            catch (Exception ex) { Plugin.Logger.LogWarning("Clipboard unavailable: " + ex.Message); }
        }
        else if (e.character != '\0' && !char.IsControl(e.character) && !e.control)
        {
            value += e.character;
        }
        else
        {
            return;
        }
        e.Use();
    }

    [HideFromIl2Cpp]
    private void ConnectFromFields()
    {
        if (ArchipelagoController.Connecting) return;
        Plugin.Instance.Server.Value = _server.Trim();
        if (!ArchipelagoController.SaveRedirected) Plugin.Instance.Slot.Value = _slot.Trim();
        Plugin.Instance.Password.Value = _password;
        ArchipelagoController.Connect(_server.Trim(), ArchipelagoController.SlotName ?? _slot.Trim(), _password);
    }

    [HideFromIl2Cpp]
    private void DrawSlotInfo(float x, ref float y, float width, float row)
    {
        var slot = ArchipelagoController.Slot!;
        if (slot.IsStory)
        {
            int open = ArchipelagoController.ProgressiveChapters;
            GUI.Label(new Rect(x, y, width, row),
                $"Chapters open: up to <b>{slot.ChapterName(Math.Min(open, slot.LostChapterIndex - 1))}</b> ({open}/5)",
                _label);
            y += row;
            if (slot.LostChapter != SlotData.LostChapterExclude)
                GUI.Label(new Rect(x, y, width, row),
                    $"Lost Chapter: {(ArchipelagoController.HasLostChapterKey ? "<color=#7f7>open</color>" : "locked")}",
                    _label);
        }
        else
        {
            GUI.Label(new Rect(x, y, width, row),
                $"Beat Shards: <b>{ArchipelagoController.BeatShards}</b> / {slot.BeatShardsRequired}", _label);
            y += row;
            var goal = slot.GoalSong == null ? null : GameBridge.Song(slot.GoalSong);
            string state = ArchipelagoController.GoalSongOpen ? "<color=#7f7>open</color>" : "locked";
            GUI.Label(new Rect(x, y, width, row), $"Goal song: <b>{goal?.name ?? slot.GoalSong}</b> ({state})", _label);
        }
        y += row;
    }

    [HideFromIl2Cpp]
    private void DrawMessages()
    {
        var entries = Messages.Current;
        float y = Screen.height - 40 - entries.Count * 30;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            float age = Time.unscaledTime - entry.Time;
            float alpha = Mathf.Clamp01((Messages.Lifetime - age) / 1.5f);
            var rect = new Rect(18, y, Screen.width * 0.6f, 30);
            var shadow = new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height);
            GUI.color = new Color(0, 0, 0, alpha);
            GUI.Label(shadow, entry.Text, _message);
            GUI.color = new Color(1, 1, 1, alpha);
            GUI.Label(rect, entry.Text, _message);
            y += 30;
        }
        GUI.color = Color.white;
    }
}
