using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwilightCore.Chat;

/// <summary>
/// Parses a human-friendly hotkey spec like <c>"Ctrl+T"</c>, <c>"Ctrl+Shift+Y"</c>,
/// <c>"Alt+F8"</c>, or <c>"F8"</c> into modifier flags + a main key, and exposes two
/// checks used by <see cref="ChatView"/>:
/// <list type="bullet">
/// <item><see cref="IsDown"/> — for <c>Update</c> via <c>Input.GetKey</c>/<c>Input.GetKeyDown</c>
/// (unreliable while a focused IMGUI TextField swallows key events).</item>
/// <item><see cref="Matches(Event)"/> — for <c>OnGUI</c> via the current <c>Event</c>, where the
/// focused control's swallowed keydown is still visible.</item>
/// </list>
/// <c>"Ctrl"</c> matches either the Control key or the Command (⌘) key, so Windows (Ctrl) and
/// macOS (Cmd) players get the same spec. Whitespace and case are ignored.
/// </summary>
internal sealed class ChatHotkey
{
    public bool Ctrl { get; private set; }
    public bool Shift { get; private set; }
    public bool Alt { get; private set; }
    public KeyCode Key { get; private set; }

    private readonly string _spec;

    public ChatHotkey(string spec)
    {
        _spec = (spec ?? "").Trim();
        Parse(_spec, out bool ctrl, out bool shift, out bool alt, out KeyCode key, out bool ok);
        Ctrl = ctrl;
        Shift = shift;
        Alt = alt;
        Key = key;
        Valid = ok && key != KeyCode.None;
    }

    /// <summary>True if the spec parsed into something usable.</summary>
    public bool Valid { get; }

    /// <summary>True while the configured modifiers are held right now (Input API).</summary>
    private bool ModifiersHeldInput()
    {
        // Read each modifier only when the spec requires it (avoids querying keys whose
        // KeyCode may not exist on this platform, and short-circuits the common case).
        if (Ctrl)
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
                        || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
            if (!ctrl) return false;
        }
        if (Shift && !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) return false;
        if (Alt && !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))) return false;
        return true;
    }

    /// <summary>Main key pressed this frame (edge), modifiers held — for <c>Update</c>.</summary>
    public bool IsDown => Valid && ModifiersHeldInput() && Input.GetKeyDown(Key);

    /// <summary>The given GUI event is a keydown of the main key with the configured modifiers.</summary>
    public bool Matches(Event ev)
    {
        if (!Valid || ev.type != EventType.KeyDown || ev.keyCode != Key) return false;
        // "Ctrl" matches either the Control key or the Command (⌘) key — Windows vs macOS.
        bool ctrlHeld = ev.control || ev.command;
        return ctrlHeld == Ctrl && ev.shift == Shift && ev.alt == Alt;
    }

    public override string ToString() => _spec;

    // ── Parsing ────────────────────────────────────────────────────

    private static readonly Dictionary<string, KeyCode> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Space", KeyCode.Space }, { "Enter", KeyCode.Return }, { "Return", KeyCode.Return },
        { "KeypadEnter", KeyCode.KeypadEnter }, { "Tab", KeyCode.Tab }, { "Esc", KeyCode.Escape },
        { "Escape", KeyCode.Escape }, { "Backspace", KeyCode.Backspace }, { "Delete", KeyCode.Delete },
        { "Up", KeyCode.UpArrow }, { "Down", KeyCode.DownArrow }, { "Left", KeyCode.LeftArrow }, { "Right", KeyCode.RightArrow },
        { "Home", KeyCode.Home }, { "End", KeyCode.End }, { "PageUp", KeyCode.PageUp }, { "PageDown", KeyCode.PageDown },
        { "Insert", KeyCode.Insert },
    };

    private static void Parse(string spec, out bool ctrl, out bool shift, out bool alt, out KeyCode key, out bool ok)
    {
        ctrl = shift = alt = false;
        key = KeyCode.None;
        ok = false;

        if (string.IsNullOrWhiteSpace(spec)) return;
        var tokens = spec.Split('+');
        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i].Trim();
            if (t.Length == 0) continue;
            if (i < tokens.Length - 1)
            {
                // Modifier token (anything but the last).
                switch (t.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                    case "cmd":
                    case "command":
                        ctrl = true; break;
                    case "shift": shift = true; break;
                    case "alt":
                    case "option": alt = true; break;
                    default:
                        // Unknown modifier — treat as parse failure.
                        return;
                }
            }
            else
            {
                // Main key token (last).
                if (t.Length == 1 && char.IsLetterOrDigit(t[0]))
                {
                    // Unity's KeyCode letter values are LOWERCASE ascii (A=97..Z=122), and
                    // digits use the ascii code of '0'..'9' (Alpha0=48..). Casting the
                    // UPPERCASE char would give 65..90 (wrong) — so normalise letters to lower.
                    char c = t[0];
                    if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
                    key = (KeyCode)c;
                }
                else if (NamedKeys.TryGetValue(t, out var named))
                    key = named;
                else if (Enum.TryParse(t, ignoreCase: true, out KeyCode parsed))
                    key = parsed;
                else
                    return; // unknown key name
            }
        }
        ok = key != KeyCode.None;
    }
}
