using UnityEngine;

/// Cycles control + camera across the three crew roles with the toggle key (default C):
///   Driver (the bus) -> Conductor 1 (door) -> Conductor 2 (inside) -> Driver ...
/// While conducting, the bus ignores input and coasts. For Conductor 2 the camera goes top-down and a
/// CLIP PLANE slices the top of the bus off (works on a single-mesh bus — no separate roof needed) so
/// you can see into the cabin. Add this to a manager object: it finds the bus + camera and auto-creates
/// both conductors.
public class RoleController : MonoBehaviour
{
    public BusController bus;
    public Conductor conductor1;
    public InsideConductor conductor2;
    public BusCameraFollow cam;

    [Header("Camera framing (distance / height / pitch)")]
    public Vector3 driverCam = new Vector3(8f, 5f, 30f);
    public Vector3 conductor1Cam = new Vector3(4.5f, 3f, 30f);
    public Vector3 c2Cam = new Vector3(4f, 2.5f, 30f);   // close chase at the driver's angle, focused on conductor 2

    [Header("Conductor 2 roof cutaway")]
    [Tooltip("Fraction of the bus height sliced off the top so you can see inside. Raise it if the near wall blocks the angled view.")]
    [Range(0.02f, 0.8f)] public float roofCut = 0.3f;
    [Tooltip("If the cut takes the wrong half (you see only the roof, or nothing), tick this.")]
    public bool flipCut = false;

    enum Role { Driver, Conductor1, Conductor2 }
    Role _role = Role.Driver;
    Camera _camComponent;
    Renderer[] _busRenderers;
    bool _cutawayActive;

    void Start()
    {
        if (bus == null) bus = FindAnyObjectByType<BusController>();
        if (cam == null) cam = FindAnyObjectByType<BusCameraFollow>();
        if (cam != null) _camComponent = cam.GetComponent<Camera>();
        if (bus != null)
            _busRenderers = (bus.busModel != null ? bus.busModel : bus.transform).GetComponentsInChildren<Renderer>();
        if (conductor1 == null) conductor1 = CreateConductor1();
        if (conductor2 == null) conductor2 = CreateConductor2();
        Apply(Role.Driver);
    }

    void Update()
    {
        if (GameInput.Instance.toggleRole.WasPressedThisFrame())
        {
            _role = (Role)(((int)_role + 1) % 3);
            Apply(_role);
        }
    }

    // The clip plane has to track the (moving) bus every frame, so it lives in LateUpdate.
    void LateUpdate()
    {
        if (_camComponent == null) return;

        if (_role == Role.Conductor2)
        {
            float clipY = BusRoofClipY();
            float side = flipCut ? 1f : -1f;
            Vector4 plane = CameraSpacePlane(new Vector3(0f, clipY, 0f), Vector3.up, side);
            _camComponent.projectionMatrix = _camComponent.CalculateObliqueMatrix(plane);
            _cutawayActive = true;
        }
        else if (_cutawayActive)
        {
            _camComponent.ResetProjectionMatrix();
            _cutawayActive = false;
        }
    }

    void Apply(Role r)
    {
        bool driving = r == Role.Driver;
        bool c1 = r == Role.Conductor1;
        bool c2 = r == Role.Conductor2;

        // Enable exactly one action set: this is what keeps WASD from reaching the bus while on foot.
        if (driving) GameInput.Instance.EnableDriving();
        else GameInput.Instance.EnableOnFoot();

        if (bus != null) bus.controlEnabled = driving;
        if (conductor1 != null) conductor1.SetControlled(c1);
        if (conductor2 != null) conductor2.SetControlled(c2);

        if (cam == null || bus == null) return;
        if (driving) cam.Retarget(bus.transform, null, driverCam.x, driverCam.y, driverCam.z);
        else if (c1) cam.Retarget(conductor1.transform, bus.transform, conductor1Cam.x, conductor1Cam.y, conductor1Cam.z);
        else cam.Retarget(conductor2.transform, bus.transform, c2Cam.x, c2Cam.y, c2Cam.z);
    }

    // World Y to slice at: top of the bus minus roofCutFraction of its height.
    float BusRoofClipY()
    {
        bool has = false;
        Bounds b = default;
        if (_busRenderers != null)
        {
            foreach (Renderer r in _busRenderers)
            {
                if (r == null || !(r is MeshRenderer)) continue;
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
        }
        if (!has) return (bus != null ? bus.transform.position.y : 0f) + 3f;
        return b.max.y - b.size.y * roofCut;
    }

    // Standard oblique-clip plane in camera space (same technique as planar water/portals).
    Vector4 CameraSpacePlane(Vector3 pointOnPlane, Vector3 normal, float sideSign)
    {
        Matrix4x4 m = _camComponent.worldToCameraMatrix;
        Vector3 cp = m.MultiplyPoint(pointOnPlane);
        Vector3 cn = m.MultiplyVector(normal).normalized * sideSign;
        return new Vector4(cn.x, cn.y, cn.z, -Vector3.Dot(cp, cn));
    }

    Conductor CreateConductor1()
    {
        Transform home = bus != null ? bus.transform : transform;
        if (BusPassengers.Instance != null && BusPassengers.Instance.doorAnchor != null)
            home = BusPassengers.Instance.doorAnchor;
        BillboardCharacter v = BillboardCharacter.Create("Conductor1", new Color(0.12f, 0.12f, 0.16f), 1.9f, home.position);
        Conductor c = v.gameObject.AddComponent<Conductor>();
        c.Setup(v, home);
        return c;
    }

    InsideConductor CreateConductor2()
    {
        Transform cabin = bus != null ? bus.transform : transform;
        Vector3 center = new Vector3(0f, 1.4f, 0f);
        Vector3 size = new Vector3(2f, 0f, 8.5f);
        if (BusPassengers.Instance != null)
        {
            cabin = BusPassengers.Instance.Cabin;
            center = BusPassengers.Instance.cabinLocalCenter;
            size = BusPassengers.Instance.cabinLocalSize;
        }
        BillboardCharacter v = BillboardCharacter.Create("Conductor2", new Color(0.18f, 0.1f, 0.1f), 1.9f, cabin.position);
        InsideConductor c = v.gameObject.AddComponent<InsideConductor>();
        c.Setup(v, cabin, center, size);
        return c;
    }
}
