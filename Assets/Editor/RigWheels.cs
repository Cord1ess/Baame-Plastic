using UnityEngine;
using UnityEditor;

/// Rigs the bus wheels so they spin (and the fronts steer). Select the wheel object(s) in the Hierarchy,
/// then run the matching menu item: it sets the Front_Wheels / Back_Wheels pivot to the axle centre (so
/// the wheels rotate in place, not in an arc) and re-parents the selected wheels under it, keeping their
/// world position. BusController then spins these nodes on X and steers the fronts on Y.
///
/// Run per axle: select the front wheels → "Assign Selection as Front Wheels"; select the rear wheels
/// (all of them, incl. duals) → "Assign Selection as Back Wheels".
///
/// Menu: Bame ▸ Wheels ▸ …
public static class RigWheels
{
    [MenuItem("Bame Plastic/Bus/Wheels/Assign Selection as Front Wheels")]
    static void Front() => Assign(true);

    [MenuItem("Bame Plastic/Bus/Wheels/Assign Selection as Back Wheels")]
    static void Back() => Assign(false);

    static void Assign(bool front)
    {
        BusController bus = Object.FindFirstObjectByType<BusController>();
        if (bus == null) { Warn("No BusController in the scene."); return; }

        Transform pivot = front ? bus.frontWheels : bus.backWheels;
        if (pivot == null) { Warn((front ? "Front_Wheels" : "Back_Wheels") + " isn't assigned on BusController."); return; }

        Transform[] sel = Selection.transforms;
        if (sel == null || sel.Length == 0) { Warn("Select the wheel object(s) in the Hierarchy first."); return; }

        // Axle centre = combined renderer bounds centre of the selected wheels.
        Bounds b = default; bool has = false;
        foreach (Transform t in sel)
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>())
            {
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
        if (!has) { Warn("The selected objects have no meshes."); return; }

        Undo.RecordObject(pivot, "Rig Wheels");
        pivot.position = b.center;          // pivot at the axle centre so spin/steer happen in place
        pivot.localRotation = Quaternion.identity;

        foreach (Transform w in sel)
            Undo.SetTransformParent(w, pivot, "Rig Wheels");   // keeps world position

        EditorUtility.SetDirty(pivot);
        Debug.Log($"[RigWheels] {sel.Length} wheel object(s) parented under {pivot.name}; pivot at axle centre " +
                  $"{b.center}. They'll now {(front ? "spin + steer" : "spin")} with the bus.");
    }

    static void Warn(string msg) => EditorUtility.DisplayDialog("Rig Wheels", msg, "OK");
}
