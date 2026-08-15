using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwilightCore.Chat;

/// <summary>
/// A standalone chat console (OnGUI overlay) — the player client's chat surface,
/// independent of the game's multiplayer-only <c>NetChat</c>.
///
/// Toggle with <b>Ctrl+T</b> (Esc closes). Shows a rolling log of chat + system
/// messages and an input line; <b>Enter</b> (or the Send button) forwards the text
/// to the match controller. Works the same in menus and in-game.
///
/// While open, <c>MenuSystem.keyboardState</c> is held at <c>NetChat</c> so the game
/// drops grab/jump input (the game's own chat does the same). Walking input is not
/// gated by the game, so movement keys may still register — type when stopped.
/// </summary>
public class ChatView : MonoBehaviour, IChatView
{
    public static ChatView Instance { get; private set; }

    /// <summary>Set by the plugin to the match controller's SendChat.</summary>
    public Action<string> OnOutgoing;

    private readonly List<string> _lines = new List<string>();
    private const int MaxLines = 500;
    private string _input = "";
    private Vector2 _scroll;
    private bool _open;
    private bool _focusInputNext;
    private const string InputControlName = "twChatInput";

    // Passive popup: a log-only panel (no input) shown briefly when a message
    // arrives while the full console is closed, then faded out.
    private float _passiveStart;
    private float _passiveUntil;
    private const float FadeInSecs = 0.2f;
    private const float FadeOutSecs = 0.5f;

    // Styles built lazily inside OnGUI (GUI.skin isn't available before the first OnGUI).
    private static Font _font;
    private static GUIStyle _labelStyle, _inputStyle, _boxStyle, _hintStyle;
    private static Texture2D _bgTex;

    // Parsed toggle hotkey, refreshed from config on first use and after `twi reload`.
    // Falls back to Ctrl+T if the config value is empty/unparseable.
    private static ChatHotkey _hotkey;
    private static string _hotkeySpec;

    public bool IsOpen => _open;

    /// <summary>The current toggle hotkey, (re)built from <c>Chat.ToggleHotkey</c> when it changes.</summary>
    private static ChatHotkey Hotkey
    {
        get
        {
            string spec = TwilightConfig.ChatToggleHotkey?.Value;
            if (_hotkey == null || spec != _hotkeySpec)
            {
                _hotkeySpec = spec;
                var parsed = new ChatHotkey(spec);
                _hotkey = parsed.Valid ? parsed : new ChatHotkey("Ctrl+T");
                if (!string.IsNullOrEmpty(spec) && !parsed.Valid)
                    Plugin.Logger.LogWarning($"[ChatView] Chat.ToggleHotkey '{spec}' didn't parse — using Ctrl+T.");
            }
            return _hotkey;
        }
    }

    /// <summary>Human-readable hint for the input row, e.g. "Ctrl+T toggle".</summary>
    private static string ToggleHint => (Hotkey.ToString()?.Length > 0 ? Hotkey.ToString() : "Ctrl+T") + " toggle";

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
            ReleaseKeyboard();
        }
    }

    private void Update()
    {
        // OPEN the console (default Ctrl/Cmd+T) when closed. CLOSING — including while the
        // input field is focused — is handled at the top of OnGUI, because a focused
        // IMGUI TextField swallows key events and Input.GetKeyDown is unreliable then.
        if (!_open && IsToggleHotkeyDown())
            Open();

        // Hold the keyboard state while open so the game keeps dropping grab/jump input
        // (other systems may reset it; re-assert every frame).
        if (_open)
        {
            try { MenuSystem.keyboardState = KeyboardState.NetChat; } catch { /* MenuSystem not ready */ }
        }
    }

    private static bool IsToggleHotkeyDown() => Hotkey.IsDown;

    // ── Open / close ───────────────────────────────────────────────

    public void Open()
    {
        _open = true;
        _focusInputNext = true;
    }

    public void Close()
    {
        _open = false;
        // Drop IMGUI keyboard focus so the (now hidden) TextField stops capturing keys.
        GUIUtility.keyboardControl = 0;
        ReleaseKeyboard();
    }

    private static void ReleaseKeyboard()
    {
        try { MenuSystem.keyboardState = KeyboardState.None; } catch { }
    }

    // ── IChatView ──────────────────────────────────────────────────

    public void DisplayChat(string senderName, string seat, string text)
    {
        string label = SeatLabel(seat);
        string line = string.IsNullOrEmpty(label)
            ? $"{senderName}: {text}"
            : $"[{label}] {senderName}: {text}";
        PushLine(line);
    }

    public void DisplaySystem(string text, string kind)
        => PushLine("[System] " + text);

    public void ShowInfo(string text) => PushLine(text);

    public void FocusInput() => Open();

    /// <summary>Route player-typed text to the server.</summary>
    public void ForwardOutgoing(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        OnOutgoing?.Invoke(text);
    }

    private void PushLine(string line)
    {
        _lines.Add(line);
        while (_lines.Count > MaxLines) _lines.RemoveAt(0);
        _scroll.y = float.MaxValue; // stick to bottom

        // Trigger the passive popup (log only) unless the full console is open.
        if (!_open && TwilightConfig.ChatPopupEnabled.Value)
        {
            float now = Time.realtimeSinceStartup;
            if (now >= _passiveUntil) _passiveStart = now;      // new popup → fade in
            _passiveUntil = now + Mathf.Max(0.5f, TwilightConfig.ChatPopupSecs.Value);
        }
    }

    private static string SeatLabel(string seat)
    {
        switch (seat)
        {
            case "PLAYER_A": return "Player A";
            case "PLAYER_B": return "Player B";
            case "REFEREE": return "Referee";
            case "DIRECTOR": return "Director";
            default: return "";
        }
    }

    // ── OnGUI ──────────────────────────────────────────────────────

    private void OnGUI()
    {
        EnsureStyles();
        float now = Time.realtimeSinceStartup;
        bool passive = !_open && TwilightConfig.ChatPopupEnabled.Value && now < _passiveUntil;
        if (!_open && !passive) return;

        // Close keys — only in active (open) mode. Handled FIRST, before the focused
        // TextField can swallow them. Esc closes; the configured toggle hotkey (default
        // Ctrl/Cmd+T) toggles closed.
        if (_open && Event.current.type == EventType.KeyDown)
        {
            if (Event.current.keyCode == KeyCode.Escape || Hotkey.Matches(Event.current))
            {
                Close();
                Event.current.Use();
                return;
            }
        }

        float alpha = _open ? 1f : PassiveAlpha(now);
        if (alpha <= 0f) return;

        float w = Mathf.Min(640f, Screen.width - 32f);
        float h = _open ? Mathf.Min(340f, Screen.height * 0.55f) : Mathf.Min(220f, Screen.height * 0.4f);
        Rect rect = new Rect(16f, Screen.height - h - 16f, w, h);

        Color prevColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);

        GUILayout.BeginArea(rect, _boxStyle);
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(h - (_open ? 68f : 16f)));
        int start = Mathf.Max(0, _lines.Count - 80);
        for (int i = start; i < _lines.Count; i++)
            GUILayout.Label(_lines[i], _labelStyle);
        GUILayout.EndScrollView();

        // Input row + hint only when the full console is open (not in passive popup mode).
        if (_open)
        {
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName(InputControlName);
            _input = GUILayout.TextField(_input, _inputStyle, GUILayout.ExpandWidth(true));
            bool focused = GUI.GetNameOfFocusedControl() == InputControlName;
            bool entered = Event.current.type == EventType.KeyUp
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && focused;
            bool send = GUILayout.Button("Send", GUILayout.Width(60f));
            if (send || entered)
            {
                ForwardOutgoing(_input);
                _input = "";
                Event.current.Use();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(ToggleHint + "  ·  Enter send  ·  Esc close", _hintStyle);
        }
        GUILayout.EndArea();

        GUI.color = prevColor;

        if (_open && _focusInputNext)
        {
            _focusInputNext = false;
            GUI.FocusControl(InputControlName);
        }
    }

    /// <summary>
    /// Fade alpha for the passive popup: fade in over <see cref="FadeInSecs"/>, hold,
    /// fade out over the final <see cref="FadeOutSecs"/> before it expires.
    /// </summary>
    private float PassiveAlpha(float now)
    {
        float elapsed = now - _passiveStart;
        float remaining = _passiveUntil - now;
        float a = 1f;
        if (elapsed < FadeInSecs) a = elapsed / FadeInSecs;
        if (remaining < FadeOutSecs) a = Mathf.Min(a, remaining / FadeOutSecs);
        return Mathf.Clamp01(a);
    }

    // ── Styles ─────────────────────────────────────────────────────

    private static void EnsureStyles()
    {
        if (_labelStyle != null) return;

        // OS dynamic font covering CJK (player display names may be Chinese) + Latin.
        _font = Font.CreateDynamicFontFromOSFont(new[]
        {
            "Microsoft YaHei", "PingFang SC", "Heiti SC", "STHeiti",
            "Noto Sans CJK SC", "WenQuanYi Zen Hei", "Arial Unicode MS", "Arial"
        }, 14);

        _labelStyle = new GUIStyle(GUI.skin.label) { font = _font, fontSize = 14, wordWrap = true };
        _labelStyle.normal.textColor = new Color(0.92f, 0.94f, 1f);

        _inputStyle = new GUIStyle(GUI.skin.textField) { font = _font, fontSize = 14 };

        _boxStyle = new GUIStyle(GUI.skin.box);
        _boxStyle.normal.background = DarkBg();
        _boxStyle.padding = new RectOffset(8, 8, 8, 8);

        _hintStyle = new GUIStyle(GUI.skin.label) { font = _font, fontSize = 11 };
        _hintStyle.normal.textColor = new Color(0.6f, 0.65f, 0.7f);
    }

    private static Texture2D DarkBg()
    {
        if (_bgTex != null) return _bgTex;
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        t.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.09f, 0.92f));
        t.Apply();
        _bgTex = t;
        return _bgTex;
    }
}
