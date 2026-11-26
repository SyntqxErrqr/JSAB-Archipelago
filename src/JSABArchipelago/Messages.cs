using System.Collections.Generic;
using UnityEngine;

namespace JSABArchipelago;

/// <summary>Short-lived on-screen messages (items sent and received, locked chapters, connection status).</summary>
internal static class Messages
{
    public const float Lifetime = 8f;
    private const int MaxShown = 6;

    public readonly struct Entry
    {
        public readonly string Text;
        public readonly float Time;

        public Entry(string text, float time)
        {
            Text = text;
            Time = time;
        }
    }

    private static readonly List<Entry> Entries = new();

    public static void Add(string text)
    {
        Plugin.Logger.LogInfo("[message] " + text);
        Entries.Add(new Entry(text, Time.unscaledTime));
        if (Entries.Count > MaxShown) Entries.RemoveAt(0);
    }

    public static IReadOnlyList<Entry> Current
    {
        get
        {
            Entries.RemoveAll(entry => Time.unscaledTime - entry.Time > Lifetime);
            return Entries;
        }
    }
}
