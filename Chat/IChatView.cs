namespace TwilightCore.Chat;

/// <summary>
/// The player-facing chat surface. Implemented by the built-in
/// <c>Multiplayer.NetChat</c> adapter (preferred) or a fallback panel; both
/// display incoming chat/system lines and forward the player's typed input.
/// All methods are called on the main thread.
/// </summary>
internal interface IChatView
{
    /// <summary>Append a chat line from a peer (senderName/seat from the server).</summary>
    void DisplayChat(string senderName, string seat, string text);

    /// <summary>Append a system line (countdown, round info, command reply, error, …).</summary>
    void DisplaySystem(string text, string kind);

    /// <summary>Append a local plugin info line (not from the server).</summary>
    void ShowInfo(string text);

    /// <summary>Open / focus the chat input so the player can type.</summary>
    void FocusInput();
}

/// <summary>No-op chat view used until a real adapter is wired up.</summary>
internal sealed class NullChatView : IChatView
{
    public static readonly NullChatView Instance = new NullChatView();
    public void DisplayChat(string senderName, string seat, string text) { }
    public void DisplaySystem(string text, string kind) { }
    public void ShowInfo(string text) { }
    public void FocusInput() { }
}
