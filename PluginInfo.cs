namespace TwilightCore;

/// <summary>
/// Static plugin identity constants used by the BepInEx plugin attribute
/// and the release pipeline. This is the single source of truth for the
/// plugin version; keep it in sync with &lt;Version&gt; in TwilightCore.csproj.
/// </summary>
internal static class PluginInfo
{
    public const string PLUGIN_GUID = "TwilightCore";
    public const string PLUGIN_NAME = "TwilightCore";
    public const string PLUGIN_VERSION = "0.0.0";
}
