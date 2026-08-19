using HumanAPI;
using UnityEngine;
using UnityEngine.Rendering;
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

    /// <summary>
    /// True for M3 chained holds: the NEXT level of a running collection,
    /// preloaded during play and consumed by the swap-in at level advance.
    /// Never reported to the server (preload_report belongs to the round-start
    /// gate only); its "still wanted" test is "the collection run is active
    /// and still expects this level next".
    /// </summary>
    public bool Chained;

    /// <summary>
    /// Snapshot of Game.currentLevel when the load started. The dormant Level's
    /// Awake/OnEnable hijacks Game.currentLevel at load completion; the capture
    /// hook restores this — null at the menu (M2), the level being played
    /// mid-round (M3).
    /// </summary>
    public HumanAPI.Level PreserveLevel;

    // ── Global lighting state (Unity 2017.4 additive-load semantics) ────────
    // RenderSettings behave like a global "last writer wins" set: scene
    // activation APPLIES the scene's stored values (so an additive load
    // overwrites the values the level being played is using), and
    // SetActiveScene re-applies nothing — the swap-in must write the held
    // scene's values back explicitly. LightProbes is a global pointer switched
    // to the newly-loaded scene the same way (left engine-owned: manual
    // assignment breaks dynamic-object sampling — player darkening). The
    // lightmap TABLE, by contrast, is coherently index-managed by the engine
    // across additive loads — never touch it (restoring a stale array orphans
    // every renderer's remapped indices and the scene goes black).
    public LightProbes PreLoadProbes;
    public RenderSettingsSnapshot PreLoadRS;
    public RenderSettingsSnapshot SceneRS;   // the held scene's values (RS probe, see pipeline)
    public LightmapsMode PreLoadLMMode;
    public LightmapsMode SceneLMMode;
    public int PreLoadLMCount;      // diagnostics: table size before the load

    /// <summary>Captured/applied bundle of the global RenderSettings.</summary>
    internal struct RenderSettingsSnapshot
    {
        public Material Skybox;
        public AmbientMode AmbientMode;
        public Color AmbientLight, AmbientSky, AmbientEq, AmbientGround;
        public float AmbientIntensity;
        public Light Sun;
        public bool Fog;
        public Color FogColor;
        public float FogDensity;
        public FogMode FogMode;
        public float ReflectionIntensity;

        public static RenderSettingsSnapshot Capture()
        {
            return new RenderSettingsSnapshot
            {
                Skybox = RenderSettings.skybox,
                AmbientMode = RenderSettings.ambientMode,
                AmbientLight = RenderSettings.ambientLight,
                AmbientSky = RenderSettings.ambientSkyColor,
                AmbientEq = RenderSettings.ambientEquatorColor,
                AmbientGround = RenderSettings.ambientGroundColor,
                AmbientIntensity = RenderSettings.ambientIntensity,
                Sun = RenderSettings.sun,
                Fog = RenderSettings.fog,
                FogColor = RenderSettings.fogColor,
                FogDensity = RenderSettings.fogDensity,
                FogMode = RenderSettings.fogMode,
                ReflectionIntensity = RenderSettings.reflectionIntensity,
            };
        }

        /// <summary>
        /// Write only the fields that actually differ. Setting ambientMode to
        /// Skybox re-bakes the ambient probe from the skybox SYNCHRONOUSLY (a
        /// visible ~1s hitch) — identical values must not be rewritten.
        /// </summary>
        public void Apply()
        {
            if (RenderSettings.skybox != Skybox) RenderSettings.skybox = Skybox;
            if (RenderSettings.ambientMode != AmbientMode) RenderSettings.ambientMode = AmbientMode;
            if (RenderSettings.ambientLight != AmbientLight) RenderSettings.ambientLight = AmbientLight;
            if (RenderSettings.ambientSkyColor != AmbientSky) RenderSettings.ambientSkyColor = AmbientSky;
            if (RenderSettings.ambientEquatorColor != AmbientEq) RenderSettings.ambientEquatorColor = AmbientEq;
            if (RenderSettings.ambientGroundColor != AmbientGround) RenderSettings.ambientGroundColor = AmbientGround;
            if (!Mathf.Approximately(RenderSettings.ambientIntensity, AmbientIntensity)) RenderSettings.ambientIntensity = AmbientIntensity;
            if (RenderSettings.sun != Sun) RenderSettings.sun = Sun;
            if (RenderSettings.fog != Fog) RenderSettings.fog = Fog;
            if (RenderSettings.fogColor != FogColor) RenderSettings.fogColor = FogColor;
            if (!Mathf.Approximately(RenderSettings.fogDensity, FogDensity)) RenderSettings.fogDensity = FogDensity;
            if (RenderSettings.fogMode != FogMode) RenderSettings.fogMode = FogMode;
            if (!Mathf.Approximately(RenderSettings.reflectionIntensity, ReflectionIntensity)) RenderSettings.reflectionIntensity = ReflectionIntensity;
        }

        /// <summary>Compact one-line description for diagnostics.</summary>
        public string Describe() =>
            $"skybox={(Skybox != null ? Skybox.name : "none")} ambMode={AmbientMode} amb={AmbientLight} fog={Fog}/{FogColor}/d={FogDensity:0.###}/{FogMode} refl={ReflectionIntensity:0.###}";
    }

    public HeldSceneState State = HeldSceneState.Resolving;

    /// <summary>Why the hold failed / was dropped (logging + preload_report detail).</summary>
    public string FailDetail;

    public bool IsBundleLevel => Bundle != null;
}
