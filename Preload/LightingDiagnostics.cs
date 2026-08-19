using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwilightCore.Preload;

/// <summary>
/// Lightmap-table &amp; runtime-LOD diagnostics for the additive-preload
/// crash investigation. The freeze log's <c>lmCount 8-&gt;9</c> observation
/// is ambiguous between three engine behaviours with different consequences:
///
/// <list type="bullet">
/// <item><b>append</b> — the additive scene's lightmaps are appended to the
/// global table (8+9=17; contradicts the observation) and the new scene's
/// renderers are index-offset;</item>
/// <item><b>dedup/share</b> — entries whose textures are already in the table
/// are shared (net +1): the old scene's unload then mutates entries the held
/// scene still uses;</item>
/// <item><b>replace</b> — the table becomes the newly-loaded scene's maps
/// (also net 8→9): the old scene's statics (and its runtime-combined meshes)
/// silently sample the WRONG atlas, and the later unload walks lightmap data
/// that is no longer in the table.</item>
/// </list>
///
/// Dedup vs replace is decided by TEXTURE IDENTITY (instance IDs), not counts:
/// <see cref="TableIds"/> snapshots the table before/after each hold (logged in
/// the pipeline's freeze line — surviving IDs = dedup, all-new IDs = replace),
/// and <see cref="Dump"/> (`twi preload rs`) additionally dumps per-entry
/// texture names/IDs, a per-scene renderer lightmapIndex histogram, and the
/// state of the game's MeshBaker LOD layer — the ONLY game code that touches
/// lightmap state at runtime (Assembly-CSharp: MB2_LOD/MB2_LODManager; the
/// combiner core is MeshBakerCore.dll). Every bake of a runtime "CombinedMesh-*"
/// renderer reads source renderers' lightmapIndex and writes the combined
/// renderer's index (= the source's only when the scene-serialized
/// <c>lightmapOption</c> preserves lightmapping, else -1) — a channel this
/// investigation had never instrumented. MeshBakerCore is deliberately NOT a
/// compile-time reference (diagnostics must degrade, not fail, if it changes):
/// the <c>meshBaker.meshCombiner.lightmapOption</c> chain is read by reflection.
/// </summary>
internal static class LightingDiagnostics
{
    /// <summary>
    /// Instance IDs of every table entry's color texture (0 for a null slot).
    /// IDs are stable while the texture lives — the before/after pair around a
    /// hold is the dedup-vs-replace discriminator.
    /// </summary>
    public static int[] TableIds()
    {
        var lms = LightmapSettings.lightmaps;
        if (lms == null || lms.Length == 0) return new int[0];
        var ids = new int[lms.Length];
        for (int i = 0; i < lms.Length; i++)
        {
            var tex = lms[i] != null ? lms[i].lightmapColor : null;
            ids[i] = tex != null ? tex.GetInstanceID() : 0;
        }
        return ids;
    }

    /// <summary>Compact "#id,#id,…" for one-line log contexts (freeze line).</summary>
    public static string FormatTableIds(int[] ids)
    {
        if (ids == null || ids.Length == 0) return "[]";
        var sb = new StringBuilder();
        for (int i = 0; i < ids.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('#').Append(ids[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Process memory snapshot for the OOM investigation (the additive
    /// preload path leaks memory per DISTINCT scene until the process dies
    /// at a scene integration): working set, private bytes, Mono heap.
    /// Logged at every load start / swap completion so one run charts the
    /// whole growth curve.
    /// </summary>
    public static string MemorySignature()
    {
        try
        {
            using (var p = System.Diagnostics.Process.GetCurrentProcess())
                return $"ws={p.WorkingSet64 >> 20}MB priv={p.PrivateMemorySize64 >> 20}MB mono={GC.GetTotalMemory(false) >> 20}MB";
        }
        catch (Exception)
        {
            return "mem=?";
        }
    }

    /// <summary>
    /// Full dump for `twi preload rs`: table identity + per-scene renderer
    /// lightmapIndex histograms + MeshBaker LOD layer state. Pure reads.
    /// </summary>
    public static string Dump()
    {
        var sb = new StringBuilder();

        // ── the global lightmap table: whose textures are in it right now ──
        var lms = LightmapSettings.lightmaps;
        sb.Append($"lm table: {(lms != null ? lms.Length : 0)} entries ids={FormatTableIds(TableIds())}");
        if (lms != null)
        {
            for (int i = 0; i < lms.Length; i++)
            {
                if (i >= 32) { sb.Append($"\n  … (+{lms.Length - i} more)"); break; }
                var d = lms[i];
                var c = d != null ? d.lightmapColor : null;
                var dir = d != null ? d.lightmapDir : null;
                sb.Append($"\n  [{i}] color={(c != null ? $"'{c.name}'(#{c.GetInstanceID()})" : "null")} dir={(dir != null ? $"'{dir.name}'(#{dir.GetInstanceID()})" : "-")}");
            }
        }

        // ── per scene: what renderers EXPECT from the table, and the LOD layer ──
        Scene active = SceneManager.GetActiveScene();
        sb.Append($"\nscenes loaded: {SceneManager.sceneCount} (MB2_LODManager.ENABLED={MB2_LODManager.ENABLED})");
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var sc = SceneManager.GetSceneAt(i);
            if (!sc.IsValid() || !sc.isLoaded) continue;
            var roots = sc.GetRootGameObjects();

            var hist = new Dictionary<int, int>();       // renderer.lightmapIndex → count
            var combinedHist = new Dictionary<int, int>(); // CombinedMesh-* renderers only
            int combinedObjs = 0, lods = 0, inCombined = 0, inQueue = 0, rendererCount = 0;
            MB2_LODManager mgr = null;
            foreach (var root in roots)
            {
                if (root == null) continue;
                bool isCombined = root.name != null && root.name.StartsWith("CombinedMesh");
                if (isCombined) combinedObjs++;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    int idx = r.lightmapIndex;
                    rendererCount++;
                    Bump(hist, idx);
                    if (isCombined) Bump(combinedHist, idx);
                }
                if (mgr == null)
                {
                    var m = root.GetComponentInChildren<MB2_LODManager>(true);
                    if (m != null) mgr = m;
                }
                foreach (var lod in root.GetComponentsInChildren<MB2_LOD>(true))
                {
                    if (lod == null) continue;
                    lods++;
                    if (lod.isInCombined) inCombined++;
                    if (lod.isInQueue) inQueue++;
                }
            }

            bool isActive = sc.name == active.name;
            sb.Append($"\n  [{i}] '{sc.name}'{(isActive ? " ACTIVE" : "")} roots={roots.Length} renderers={rendererCount}");
            sb.Append($"\n    lmIdx hist: {FormatHist(hist)}");
            sb.Append($"\n    combinedMesh: objs={combinedObjs} lmIdx hist: {FormatHist(combinedHist)}");
            sb.Append($"\n    MB2_LOD: total={lods} inCombined={inCombined} inQueue={inQueue}");
            if (mgr != null)
            {
                sb.Append($"\n    LODManager: active={mgr.gameObject.activeInHierarchy} enabled={mgr.enabled} ignoreLM={mgr.ignoreLightmapping} baking={mgr.baking_enabled} bakers={(mgr.bakers != null ? mgr.bakers.Length : 0)}");
                if (mgr.bakers != null)
                {
                    for (int b = 0; b < mgr.bakers.Length && b < 8; b++)
                    {
                        var bp = mgr.bakers[b];
                        if (bp == null) continue;
                        sb.Append($"\n      baker[{b}] label='{bp.label}' lightMapIdx={bp.lightMapIndex} cluster={bp.clusterType} maxVerts={bp.maxVerticesPerCombinedMesh} lightmapOption={ReadLightmapOption(bp)}");
                    }
                }
            }
            else
            {
                sb.Append("\n    LODManager: (none in scene)");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The scene-serialized <c>lightmapOption</c> of a baker's MB3_MeshBaker —
    /// preserve_current_lightmapping / generate_new_UV2_layout mean runtime
    /// combined renderers carry REAL lightmap table indices; ignore_UV2 /
    /// copy_UV2_unchanged mean they get -1 and never touch the table.
    /// MeshBakerCore.dll is not a compile-time reference — reflect the chain.
    /// </summary>
    private static string ReadLightmapOption(MB2_LODManager.BakerPrototype bp)
    {
        try
        {
            var meshBakerField = typeof(MB2_LODManager.BakerPrototype).GetField("meshBaker");
            var meshBaker = meshBakerField != null ? meshBakerField.GetValue(bp) as MonoBehaviour : null;
            if (meshBaker == null) return "n/a(no meshBaker)";
            var combinerProp = meshBaker.GetType().GetProperty("meshCombiner");
            var combiner = combinerProp != null ? combinerProp.GetValue(meshBaker, null) : null;
            if (combiner == null) return "n/a(no meshCombiner)";
            var optionProp = combiner.GetType().GetProperty("lightmapOption");
            var option = optionProp != null ? optionProp.GetValue(combiner, null) : null;
            return option != null ? option.ToString() : "n/a(no lightmapOption)";
        }
        catch (System.Exception ex)
        {
            return "n/a(" + ex.GetType().Name + ")";
        }
    }

    private static void Bump(Dictionary<int, int> hist, int key)
    {
        int n;
        hist.TryGetValue(key, out n);
        hist[key] = n + 1;
    }

    private static string FormatHist(Dictionary<int, int> hist)
    {
        if (hist.Count == 0) return "(no renderers)";
        var keys = new List<int>(hist.Keys);
        keys.Sort();
        var sb = new StringBuilder();
        for (int i = 0; i < keys.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(keys[i]).Append(':').Append(hist[keys[i]]);
        }
        return sb.ToString();
    }
}
