using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.InputSystem;

/// Arcade sphere-physics controller tuned for the bus (not a kart). A detached, interpolated Rigidbody
/// sphere does the physics; this transform follows its smoothed position (offset down by the sphere
/// radius so the root = the ground-contact point) and yaws with steering.
///
/// Feel: heavy pickup (low accelRate), strong brakes (high brakeRate), steering that only works while
/// moving and eases at top speed, an automatic gearbox (exposed for a speedometer), and a weighty
/// slide-drift that leans the body hard with only a little yaw angle.
public class BusController : MonoBehaviour
{
    public static BusController Instance;

    /// Raised on a hazard hit, with severity 0..1 (how much speed was lost). For camera shake / FX.
    public static System.Action<float> Impacted;

    [Header("Rig (wired by Build Clean Bus Rig)")]
    [FormerlySerializedAs("kartModel")] public Transform busModel;
    [FormerlySerializedAs("kartNormal")] public Transform busNormal;
    public Rigidbody sphere;
    public Transform frontWheels;
    public Transform backWheels;

    [Header("State")]
    [Tooltip("When false (a conductor is active) the bus ignores input and coasts.")]
    public bool controlEnabled = true;
    public bool drifting;

    [Header("Drive — heavy pickup, strong brakes")]
    public float maxSpeed = 22f;
    public float maxReverseSpeed = 6f;
    [Tooltip("How fast speed builds. Lower = heavier.")]
    public float accelRate = 10f;
    [Tooltip("Braking deceleration. High = sharp stops for dodging.")]
    public float brakeRate = 32f;
    [Tooltip("Engine-braking when you let off the throttle.")]
    public float coastRate = 6f;

    [Header("Auto gearbox (drives the speedometer)")]
    public int gearCount = 5;
    [Tooltip("Seconds the throttle eases during an upshift (gives the climb-shift-climb feel).")]
    public float shiftTime = 0.18f;

    [Header("Steering — only while moving")]
    public float turnSpeed = 85f;
    [Tooltip("Speed at which you get full steering authority (below it, steering fades to none).")]
    public float fullSteerSpeed = 7f;
    [Range(0f, 1f)] [Tooltip("Fraction of steering kept at top speed (lower = more stable).")]
    public float highSpeedSteer = 0.55f;

    [Header("Grip / Drift")]
    [Tooltip("How hard the bus snaps to its heading normally (higher = grippier).")]
    public float grip = 14f;
    [Tooltip("Grip while drifting (lower = slides more).")]
    public float driftGrip = 2.5f;
    public float driftTurnBoost = 1.5f;
    public float driftMinSpeed = 6f;
    [Tooltip("Small yaw angle of the body into the slide (deg).")]
    public float driftAngle = 6f;
    [Tooltip("Big body roll while drifting — as if the inner wheels lift (deg).")]
    public float driftLean = 16f;
    [Tooltip("Body roll into normal corners from speed (deg).")]
    public float cornerLean = 6f;

    [Header("Physics")]
    public float gravity = 20f;
    public LayerMask layerMask;

    [Header("Wheels")]
    [Tooltip("1 = physically correct roll from the auto-measured radius. Negate to flip spin direction.")]
    public float wheelSpinScale = 1f;
    public float frontSteerAngle = 22f;

    // --- Exposed for the speedometer / HUD ---
    public int Gear => _gear + 1;                  // 1-based
    public float Rpm01 { get; private set; }       // 0..1 within the current gear (sawtooth; drops on shift)
    public float SpeedKmh => Mathf.Abs(currentSpeed) * 3.6f;
    public float SpeedNormalized => Mathf.Clamp01(Mathf.Abs(currentSpeed) / Mathf.Max(0.01f, maxSpeed));
    public bool Shifting => _shiftTimer > 0f;

    float currentSpeed;
    float _yaw;
    int _driftDir = 1;
    int _gear;
    float _shiftTimer;
    float _wheelSpin;
    float _vLean, _vAngle;
    float _sphereRadius = 0.85f;
    float _wheelRadius = 0.5f;

    void Start()
    {
        Instance = this;
        sphere.transform.parent = null;

        SphereCollider sc = sphere.GetComponent<SphereCollider>();
        if (sc != null) _sphereRadius = sc.radius * Mathf.Abs(sphere.transform.lossyScale.x);

        _yaw = transform.eulerAngles.y;
        _wheelRadius = MeasureWheelRadius();
    }

    /// Place the bus (and its detached physics sphere) at a pose and kill momentum. Safe to call after
    /// Start() — used by the road generator to drop the bus onto the spawn point on the left lane.
    public void TeleportTo(Vector3 groundPos, Quaternion rotation)
    {
        _yaw = rotation.eulerAngles.y;
        currentSpeed = 0f;
        transform.SetPositionAndRotation(groundPos, Quaternion.Euler(0f, _yaw, 0f));
        if (sphere != null)
        {
            sphere.transform.position = groundPos + Vector3.up * _sphereRadius;
            sphere.linearVelocity = Vector3.zero;
            sphere.angularVelocity = Vector3.zero;
        }
    }


    void Update()
    {
        // Follow the sphere's INTERPOLATED position (smooth — no per-frame jitter).
        transform.position = sphere.transform.position - Vector3.up * _sphereRadius;

        GameInput gi = GameInput.Instance;
        float steerVal = controlEnabled ? gi.steer.ReadValue<float>() : 0f;
        bool accel = controlEnabled && gi.accelerate.IsPressed();
        bool brake = controlEnabled && gi.brake.IsPressed();
        bool driftDown = controlEnabled && gi.drift.WasPressedThisFrame();
        bool driftUp = controlEnabled && gi.drift.WasReleasedThisFrame();

        float dt = Time.deltaTime;
        float topSpeed = maxSpeed * HealthFactor();

        // --- Gearbox state (also feeds the speedometer) ---
        float gearSpan = maxSpeed / Mathf.Max(1, gearCount);
        int newGear = Mathf.Clamp(Mathf.FloorToInt(Mathf.Abs(currentSpeed) / gearSpan), 0, gearCount - 1);
        if (accel && newGear > _gear) _shiftTimer = shiftTime;   // upshift → brief throttle ease
        _gear = newGear;
        Rpm01 = Mathf.Clamp01((Mathf.Abs(currentSpeed) - _gear * gearSpan) / gearSpan);

        // --- Throttle: gear torque (strong low in gear, tapers to redline) + shift pauses, strong brakes ---
        if (accel)
        {
            if (_shiftTimer > 0f) _shiftTimer -= dt;             // mid-shift: hold speed
            else
            {
                float torque = Mathf.Lerp(1.15f, 0.55f, Rpm01);
                currentSpeed = Mathf.MoveTowards(currentSpeed, topSpeed, accelRate * torque * dt);
            }
        }
        else if (brake)
        {
            if (currentSpeed > 0.2f) currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, brakeRate * dt);
            else currentSpeed = Mathf.MoveTowards(currentSpeed, -maxReverseSpeed, accelRate * 0.7f * dt);
        }
        else currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, coastRate * dt);

        // --- Drift toggle (hold Space through a turn) ---
        if (driftDown && Mathf.Abs(currentSpeed) > driftMinSpeed && Mathf.Abs(steerVal) > 0.1f)
        {
            drifting = true;
            _driftDir = steerVal > 0f ? 1 : -1;
        }
        if (driftUp || Mathf.Abs(currentSpeed) < 1f) drifting = false;

        // --- Steering: none at a standstill, full once moving, eased at top speed, inverted in reverse ---
        float authority = Mathf.InverseLerp(0.3f, fullSteerSpeed, Mathf.Abs(currentSpeed));
        float stability = Mathf.Lerp(1f, highSpeedSteer, Mathf.Clamp01(Mathf.Abs(currentSpeed) / maxSpeed));
        float reverseSign = currentSpeed < -0.2f ? -1f : 1f;
        float turn = steerVal * authority * stability * turnSpeed * reverseSign;
        if (drifting) turn *= driftTurnBoost;
        _yaw += turn * dt;
        transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

        // --- Visual: small yaw angle + BIG body lean while drifting; gentle lean in normal corners ---
        float speedRatio = SpeedNormalized;
        float targetAngle = drifting ? _driftDir * driftAngle : steerVal * speedRatio * 3f;
        float targetLean = drifting ? -_driftDir * driftLean : -steerVal * speedRatio * cornerLean;
        _vAngle = Mathf.Lerp(_vAngle, targetAngle, dt * 8f);
        _vLean = Mathf.Lerp(_vLean, targetLean, dt * 8f);
        if (busModel != null) busModel.localRotation = Quaternion.Euler(0f, _vAngle, _vLean);

        // --- Wheels: roll at the physically correct rate from the bus's ACTUAL forward speed + the
        // auto-measured wheel radius (no tuning); fronts also steer with the input. ---
        float rollSpeed = Vector3.Dot(sphere.linearVelocity, transform.forward);
        _wheelSpin += rollSpeed / _wheelRadius * Mathf.Rad2Deg * wheelSpinScale * dt;
        if (frontWheels != null) frontWheels.localRotation = Quaternion.Euler(_wheelSpin, steerVal * frontSteerAngle, 0f);
        if (backWheels != null) backWheels.localRotation = Quaternion.Euler(_wheelSpin, 0f, 0f);
    }

    void FixedUpdate()
    {
        Vector3 v = sphere.linearVelocity;

        // Ground handling via VELOCITY (no per-frame position teleport → no jitter). Gravity only airborne.
        if (FindGroundY(out float gy))
        {
            float targetY = gy + _sphereRadius;
            float dy = targetY - sphere.position.y;
            if (dy > _sphereRadius)                       // severe penetration backstop (rare)
            {
                Vector3 p = sphere.position; p.y = targetY; sphere.position = p;
                v.y = 0f;
            }
            else
            {
                v.y = Mathf.Clamp(dy / Time.fixedDeltaTime, -8f, 12f);   // hold on the surface smoothly
            }
        }
        else
        {
            v.y -= gravity * Time.fixedDeltaTime;
        }

        // Drive + grip: forward velocity tracks currentSpeed; sideways velocity bleeds off (less drifting).
        Vector3 fwd = transform.forward;
        Vector3 flat = new Vector3(v.x, 0f, v.z);
        Vector3 lateral = flat - fwd * Vector3.Dot(flat, fwd);
        float g = drifting ? driftGrip : grip;
        lateral = Vector3.Lerp(lateral, Vector3.zero, Mathf.Clamp01(g * Time.fixedDeltaTime));
        Vector3 result = fwd * currentSpeed + lateral;
        sphere.linearVelocity = new Vector3(result.x, v.y, result.z);

        // Slope tilt for the visual, then re-apply heading.
        if (busNormal != null)
        {
            Vector3 targetUp = Vector3.up;
            if (Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 3f, layerMask))
                targetUp = hit.normal;
            busNormal.up = Vector3.Lerp(busNormal.up, targetUp, Time.deltaTime * 8f);
            busNormal.Rotate(0f, _yaw, 0f);
        }
    }

    /// Hazards/obstacles call this to shed speed on contact. 1 = no effect, 0 = dead stop.
    public void ApplyImpact(float speedMultiplier)
    {
        float severity = 1f - Mathf.Clamp01(speedMultiplier);
        currentSpeed *= Mathf.Clamp01(speedMultiplier);
        drifting = false;
        Impacted?.Invoke(severity);
    }

    // A damaged bus is sluggish (lower top speed): 1.0 at full health down to 0.4 at zero.
    float HealthFactor()
    {
        ShiftManager sm = ShiftManager.Instance;
        if (sm == null || sm.maxHealth <= 0f) return 1f;
        return Mathf.Lerp(0.4f, 1f, Mathf.Clamp01(sm.Health / sm.maxHealth));
    }

    // Auto-measure the wheel radius from the rigged wheel meshes so the roll rate matches the bus's real
    // speed with no tuning. (Diameter ≈ the wheel's vertical/forward extent.)
    float MeasureWheelRadius()
    {
        Transform w = (frontWheels != null && frontWheels.childCount > 0) ? frontWheels
                    : (backWheels != null && backWheels.childCount > 0) ? backWheels : null;
        if (w == null) return 0.5f;
        Renderer[] rs = w.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return 0.5f;
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return Mathf.Max(0.1f, Mathf.Max(b.size.y, b.size.z) * 0.5f);
    }

    static readonly RaycastHit[] _groundHits = new RaycastHit[8];

    // Highest solid surface below the sphere that isn't the bus. Returns true when the sphere is on/near
    // it (grounded); false when airborne. Layer-independent + ignores triggers, so a thin road collider
    // is fine and the bus can never sink through.
    bool FindGroundY(out float groundY)
    {
        groundY = 0f;
        Vector3 origin = sphere.position + Vector3.up * (_sphereRadius + 1.5f);
        int n = Physics.RaycastNonAlloc(origin, Vector3.down, _groundHits, _sphereRadius + 8f,
                                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float topY = float.NegativeInfinity;
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            Collider c = _groundHits[i].collider;
            if (c == null) continue;
            if (_groundHits[i].rigidbody == sphere) continue;
            if (c.GetComponentInParent<BusController>() != null) continue;
            if (_groundHits[i].point.y > topY) { topY = _groundHits[i].point.y; found = true; }
        }
        if (!found) return false;
        groundY = topY;
        return sphere.position.y <= topY + _sphereRadius + 0.3f;
    }
}
