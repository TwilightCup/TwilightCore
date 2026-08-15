using Multiplayer;

namespace TwilightCore;

/// <summary>
/// Print a line to the game's developer console (Shell — open with BackQuote/F1),
/// falling back to the BepInEx log when the Shell scene object isn't alive yet.
/// Use this for user-facing status the player should see in the console
/// (connection milestones, command replies, …).
/// </summary>
internal static class TwilightLog
{
    public static void Print(string msg)
    {
        if (Shell.instance != null && Shell.instance)
            Shell.Print(msg);
        else
            Plugin.Logger.LogInfo(msg);
    }
}
