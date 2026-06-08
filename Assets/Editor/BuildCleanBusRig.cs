using UnityEngine;
using UnityEditor;

/// Builds a clean, correctly-wired bus rig in the open scene so you don't have to assemble the
/// hierarchy + references by hand (which is what drifted out of alignment last time). It creates only
/// the SKELETON — empties + components + a tuned physics sphere — and wires every BusController /
/// BusPassengers reference. You then drop your Bus mesh under "BusModel" and do the visual placement
/// (scale, orientation, wheel alignment, door/cabin), which has to be done by eye anyway.
///
///   Player                         [BusController, BusPassengers]   <- root, follows the sphere
///   ├── Sphere                     [Rigidbody, SphereCollider, BusTag]   <- physics body (detaches at runtime)
///   ├── DoorAnchor                                                    <- move to the real door
///   └── Normal                     (ground-align node = busNormal)
///       └── Parent                 (drift-slide pivot = busModel.parent)
///           └── BusModel           (your visual mesh goes UNDER here = busModel)
///               ├── Front_Wheels   (nest the front wheel meshes here — spins + steers)
///               └── Back_Wheels    (nest the rear wheel meshes here — spins)
///
/// Menu: Bame ▸ Build Clean Bus Rig
public static class BuildCleanBusRig
{
    [MenuItem("Bame Plastic/Bus/Build Clean Bus Rig")]
    public static void Build()
    {
        GameObject root = new GameObject("Player");
        Undo.RegisterCreatedObjectUndo(root, "Build Clean Bus Rig");

        // --- Physics sphere (matches the current tuned bus; detached at runtime by BusController.Start) ---
        GameObject sphereGO = new GameObject("Sphere");
        sphereGO.transform.SetParent(root.transform, false);
        // Horizontally centred (x/z = 0); raised by its radius so the ball rests with its BOTTOM on the
        // root origin (the ground-contact point). The bus visual follows this sphere — never the reverse.
        sphereGO.transform.localPosition = new Vector3(0f, 0.85f, 0f);

        Rigidbody rb = sphereGO.AddComponent<Rigidbody>();
        rb.mass = 300f;
        rb.linearDamping = 2f;
        rb.angularDamping = 0f;
        rb.useGravity = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;                 // smoother than the old rig
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;         // stops tunneling

        SphereCollider sc = sphereGO.AddComponent<SphereCollider>();
        sc.radius = 0.85f;

        sphereGO.AddComponent<BusTag>();   // REQUIRED here: chunk ExitTrigger detects the bus by a BusTag
                                           // on the moving collider, and the sphere detaches from the root.

        // --- Visual chain: Normal -> Parent (drift pivot) -> BusModel ---
        GameObject normal = new GameObject("Normal");
        normal.transform.SetParent(root.transform, false);

        GameObject pivot = new GameObject("Parent");
        pivot.transform.SetParent(normal.transform, false);

        GameObject model = new GameObject("BusModel");
        model.transform.SetParent(pivot.transform, false);

        GameObject front = new GameObject("Front_Wheels");
        front.transform.SetParent(model.transform, false);
        GameObject back = new GameObject("Back_Wheels");
        back.transform.SetParent(model.transform, false);

        // --- Door anchor (starts at the origin for a tidy rig; you move it onto the real door later) ---
        GameObject door = new GameObject("DoorAnchor");
        door.transform.SetParent(root.transform, false);
        door.transform.localPosition = Vector3.zero;

        // --- Components + wiring ---
        BusController bus = root.AddComponent<BusController>();
        BusPassengers passengers = root.AddComponent<BusPassengers>();

        SerializedObject so = new SerializedObject(bus);
        so.FindProperty("busModel").objectReferenceValue = model.transform;
        so.FindProperty("busNormal").objectReferenceValue = normal.transform;
        so.FindProperty("sphere").objectReferenceValue = rb;
        so.FindProperty("frontWheels").objectReferenceValue = front.transform;
        so.FindProperty("backWheels").objectReferenceValue = back.transform;
        so.ApplyModifiedPropertiesWithoutUndo();

        SerializedObject sop = new SerializedObject(passengers);
        sop.FindProperty("doorAnchor").objectReferenceValue = door.transform;
        sop.ApplyModifiedPropertiesWithoutUndo();

        Selection.activeGameObject = root;
        EditorGUIUtility.PingObject(root);
        Debug.Log("[BuildCleanBusRig] Clean rig created (everything at origin). Next: drop your Bus mesh " +
                  "under 'BusModel' and reset its transform to 0,0,0, then run Bame > Fit Bus To Ground " +
                  "(centres + scales + grounds it). After that: wheels, DoorAnchor, layerMask, swap into scene.");
    }
}
