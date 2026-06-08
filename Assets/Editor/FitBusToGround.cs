using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

/// One-click sizing + centring + grounding for the bus visual.
///
/// It keeps the rig nodes pristine: BusController.busModel ("BusModel") is reset to a clean identity
/// (0,0,0 / rot 0 / scale 1). The unavoidable offset that centres a model with an off-centre pivot is
/// applied to the MESH object you dropped under BusModel (its own transform — the natural place for it),
/// not to the rig node. Scaling snaps to 1 when the model is already ~TargetLength, so you don't get
/// ugly 0.9998-style values.
///
/// On the mesh it: (1) scales to TargetLength metres only if it's off by more than the snap tolerance,
/// (2) centres it on the rig origin in X/Z, (3) drops its bottom (wheels) onto the root = ground.
/// Run AFTER dropping the Bus mesh under BusModel.
///
/// Menu: Bame ▸ Fit Bus To Ground
public static class FitBusToGround
{
    const float TargetLength = 12.19f;   // bus reference length in metres (1 unit = 1 m)
    const float SnapTolerance = 0.05f;   // keep scale unchanged if within ±5% of target

    [MenuItem("Bame Plastic/Bus/Fit Bus To Ground")]
    public static void Fit()
    {
        BusController bus = Object.FindFirstObjectByType<BusController>();
        if (bus == null) { EditorUtility.DisplayDialog("Fit Bus", "No BusController found in the scene.", "OK"); return; }
        if (bus.busModel == null) { EditorUtility.DisplayDialog("Fit Bus", "BusController.busModel isn't assigned.", "OK"); return; }

        Transform model = bus.busModel;

        // Keep the rig node clean — wipe any offset a previous run may have left here.
        Undo.RecordObject(model, "Fit Bus To Ground");
        model.localPosition = Vector3.zero;
        model.localRotation = Quaternion.identity;
        model.localScale = Vector3.one;

        // The mesh object(s) under BusModel (skip the empty wheel pivots) carry the fit.
        List<Transform> movers = new List<Transform>();
        foreach (Transform child in model)
        {
            if (child == bus.frontWheels || child == bus.backWheels) continue;
            if (child.GetComponentsInChildren<Renderer>().Length > 0) movers.Add(child);
        }
        if (movers.Count == 0) movers.Add(model);   // fallback: mesh sits directly on BusModel

        List<Renderer> rends = new List<Renderer>();
        foreach (Transform m in movers) rends.AddRange(m.GetComponentsInChildren<Renderer>());
        if (rends.Count == 0)
        {
            EditorUtility.DisplayDialog("Fit Bus", "No mesh found under BusModel.\nDrop your Bus mesh under BusModel first.", "OK");
            return;
        }
        foreach (Transform m in movers) Undo.RecordObject(m, "Fit Bus To Ground");

        // 1) scale to target length (bus runs along +Z) — but snap to leave a near-correct model alone
        Bounds b = WorldBounds(rends);
        if (b.size.z > 0.0001f)
        {
            float factor = TargetLength / b.size.z;
            if (Mathf.Abs(factor - 1f) > SnapTolerance)
                foreach (Transform m in movers) m.localScale *= factor;
        }

        // 2) centre on the rig origin in X/Z, 3) drop the bottom (wheels) onto the root = ground
        b = WorldBounds(rends);   // recompute after any scaling
        Vector3 root = bus.transform.position;
        Vector3 delta = new Vector3(root.x - b.center.x, root.y - b.min.y, root.z - b.center.z);
        foreach (Transform m in movers) m.position += delta;

        foreach (Transform m in movers) EditorUtility.SetDirty(m);
        b = WorldBounds(rends);
        Debug.Log($"[FitBusToGround] Fit done: ~{b.size.z:0.0}m long x {b.size.x:0.0}m wide x {b.size.y:0.0}m tall. " +
                  "BusModel left at clean identity; the centring offset is on the mesh object.");
    }

    static Bounds WorldBounds(List<Renderer> rends)
    {
        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Count; i++) b.Encapsulate(rends[i].bounds);
        return b;
    }
}
