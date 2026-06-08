using UnityEngine;
using UnityEditor;

/// One-click setup for the bulletproof POOLED-TILE endless road. Drops a single GameObject with
/// RoadZone + TiledRoadStreamer + SplineStopSpawner, and a FloatingOrigin if the scene lacks one.
/// Streams fixed-length road tiles around the bus — each lofted once and pooled, so there's no
/// whole-road rebuild and effectively zero streaming lag. Press Play with the bus in the scene.
///
/// Menu: Bame Plastic ▸ Create Tiled Road (Fast)
public static class CreateTiledRoad
{
    [MenuItem("Bame Plastic/Create Tiled Road (Fast)")]
    static void Create()
    {
        GameObject go = new GameObject("TiledRoad");
        Undo.RegisterCreatedObjectUndo(go, "Create Tiled Road");

        go.AddComponent<RoadZone>();
        var streamer = go.AddComponent<TiledRoadStreamer>();
        go.AddComponent<SplineStopSpawner>();   // Phase C: bus stops + crowds (binds to the streamer)

        streamer.RebuildEditorPreview();         // build tiles + seat the bus on the left lane, in the Scene view

#if UNITY_2022_2_OR_NEWER
        if (Object.FindFirstObjectByType<FloatingOrigin>() == null)
#else
        if (Object.FindObjectOfType<FloatingOrigin>() == null)
#endif
        {
            GameObject fo = new GameObject("FloatingOrigin");
            fo.AddComponent<FloatingOrigin>();
            Undo.RegisterCreatedObjectUndo(fo, "Create Floating Origin");
        }

        Selection.activeGameObject = go;
        SceneView.FrameLastActiveSceneView();
        Debug.Log("[CreateTiledRoad] Tiled road created. Press Play with the bus in the scene — it streams " +
                  "pooled tiles forever with no rebuild lag. Set a Seed for a repeatable layout; flip " +
                  "'Use Burst' on TiledRoadStreamer once you've confirmed it looks right, for max headroom.");
    }
}
