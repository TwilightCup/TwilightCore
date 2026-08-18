using HumanAPI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwilightCore.Preload;

/// <summary>Lifecycle of one held (dormant preloaded) scene.</summary>
internal enum HeldSceneState
{
    /// <summary>Resolving level type / scene name (and, for workshop, downloading).</summary>
    Resolving,
    /// <summary>SteamUGC download/update in flight (Subscription levels only).</summary>
    Downloading,
    /// <summary>Additive LoadSceneAsync in flight.</summary>
    LoadingScene,
    /// <summary>Loaded, roots deactivated, invisible to the game — ready to swap in.</summary>
    Dormant,
    /// <summary>Consumed by a swap-in (or the round started without it).</summary>
    Consumed,
    /// <summary>Broken / externally destroyed / dropped.</summary>
    Invalid,
}

/// <summary>
/// Everything the preload pipeline keeps alive for one held scene: the additive
/// scene handle, its root objects (deactivated for dormancy), the HumanAPI
/// <see cref="Level"/> component, and the workshop <see cref="AssetBundle"/>
/// handle (bundle levels only — the game's own flow keeps the bundle open until
/// <c>FixupLoadedBundle</c> has run, then calls <c>Unload(false)</c>).
/// </summary>
internal sealed class HeldScene
{
    /// <summary>LevelId as used by the collection engine (display name / workshop id / "lvl:…" path).</summary>
    public string LevelId;

    /// <summary>Resolved source type (mirrors what LaunchLevelStandard would pass to App).</summary>
    public WorkshopItemSource Type;

    /// <summary>Value for <c>Game.currentLevelNumber</c>: built-in/editor-pick index, workshop id cast to int, or 0 for local levels.</summary>
    public int Number;

    /// <summary>Scene name the level lives in (resolved like Game.LoadLevel does).</summary>
    public string SceneName;

    /// <summary>Additive scene handle (valid once loaded).</summary>
    public Scene Scene;

    /// <summary>The scene's HumanAPI Level component (its Awake ran at load time).</summary>
    public Level Level;

    /// <summary>Root objects captured at load time.</summary>
    public GameObject[] Roots = new GameObject[0];

    /// <summary>Per-root authored active state — the swap-in reactivates exactly these, never more.</summary>
    public bool[] RootWasActive = new bool[0];

    /// <summary>Bundle handle for Subscription/LocalWorkshop levels; null for built-in/editor-pick.</summary>
    public AssetBundle Bundle;

    /// <summary>Workshop metadata for bundle levels; null otherwise (as in the standard path).</summary>
    public WorkshopLevelMetadata Metadata;

    /// <summary>True when this hold was driven by the match flow (reports to the server); false for `twi preload` debug holds.</summary>
    public bool MatchDriven;

    public HeldSceneState State = HeldSceneState.Resolving;

    /// <summary>Why the hold failed / was dropped (logging + preload_report detail).</summary>
    public string FailDetail;

    public bool IsBundleLevel => Bundle != null;
}
