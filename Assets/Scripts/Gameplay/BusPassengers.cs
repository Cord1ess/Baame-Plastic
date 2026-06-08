using System.Collections.Generic;
using UnityEngine;

/// Add this to the bus (the Player). Cabin + boarding coordinator.
///
/// Riders SIT first, filling seats from the FRONT. Once seats are `seatStandThreshold` full (≈70%),
/// new riders STAND at the front of the centre aisle. Boarding is 1-by-1 (door cooldown). Conductor 2
/// can SHOVE a standing rider, who then walks toward the back and takes a free seat (random — they're
/// naturally toward the rear since the front fills first), or, if none are free, the backmost free
/// standing spot. The centre aisle (widened by `aisleHalfWidth`) is the conductor's walkable lane.
public class BusPassengers : MonoBehaviour
{
    public static BusPassengers Instance { get; private set; }

    [Tooltip("Bus must be at or below this speed (m/s) for passengers to board.")]
    public float boardSpeedThreshold = 6f;
    [Tooltip("Seconds between each passenger boarding (the 1-by-1 stagger).")]
    public float boardInterval = 0.35f;

    [Header("Door (auto-created if empty)")]
    public Transform doorAnchor;
    public Vector3 doorLocalPos = new Vector3(-1.3f, 0f, 3.5f);

    [Header("Cabin (tune to your bus interior)")]
    public Vector2Int seatGrid = new Vector2Int(5, 6);            // columns x rows (centre column = aisle)
    public Vector3 cabinLocalCenter = new Vector3(0f, 1.1f, -0.5f);
    public Vector3 cabinLocalSize = new Vector3(2.0f, 0f, 7.5f);
    [Range(0f, 1f)] [Tooltip("Seats fill (front-first) to this fraction before riders start standing.")]
    public float seatStandThreshold = 0.7f;
    [Tooltip("How many standing spots to scatter across the walk box (standing-crowd density).")]
    public int standCapacity = 12;
    [Header("Conductor walk area (the aisle lane)")]
    [Tooltip("Walk-box centre as an offset from cabinLocalCenter.")]
    public Vector3 walkAreaOffset = Vector3.zero;
    [Tooltip("Half WIDTH of the walkable aisle (X). Lower = narrower lane.")]
    public float walkHalfWidth = 0.3f;
    [Tooltip("Half LENGTH of the walkable aisle (Z).")]
    public float walkHalfLength = 3.0f;
    [Tooltip("Small random sideways jitter so the standing line isn't a rigid row.")]
    public float standJitter = 0.12f;

    /// Local-space centre of the conductor's walkable lane.
    public Vector3 WalkCenter => cabinLocalCenter + walkAreaOffset;

    public int BoardedCount { get; private set; }
    public int AboardCount => _aboard.Count;

    /// A seat or standing spot in the cabin (local space).
    public class CabinSpot { public Vector3 local; public bool isSeat; public bool occupied; }

    BusController _controller;
    Transform _cabin;
    readonly List<CabinSpot> _seats = new List<CabinSpot>();    // ordered FRONT -> back
    readonly List<CabinSpot> _stands = new List<CabinSpot>();   // aisle, ordered FRONT -> back
    readonly List<CabinSpot> _tmp = new List<CabinSpot>();
    readonly List<Passenger> _aboard = new List<Passenger>();
    float _doorCooldownUntil;

    public float Speed => (_controller != null && _controller.sphere != null) ? _controller.sphere.linearVelocity.magnitude : 0f;
    public Vector3 DoorPosition => doorAnchor != null ? doorAnchor.position : transform.position;
    public Transform Cabin => _cabin;
    public bool CanBoard => Speed <= boardSpeedThreshold && HasRoom();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        _controller = GetComponent<BusController>();
        if (_controller == null) _controller = GetComponentInParent<BusController>();
        if (_controller == null) _controller = FindAnyObjectByType<BusController>();

        if (doorAnchor == null)
        {
            GameObject d = new GameObject("DoorAnchor");
            d.transform.SetParent(transform, false);
            d.transform.localPosition = doorLocalPos;
            doorAnchor = d.transform;
        }

        GameObject c = new GameObject("Cabin");
        c.transform.SetParent(transform, false);
        c.transform.localPosition = Vector3.zero;
        _cabin = c.transform;

        BuildSlots();
    }

    void BuildSlots()
    {
        _seats.Clear();
        _stands.Clear();
        int cols = Mathf.Max(1, seatGrid.x);
        int rows = Mathf.Max(1, seatGrid.y);

        // Seats: the side columns of the grid, ordered FRONT (+Z) -> back.
        for (int r = rows - 1; r >= 0; r--)
            for (int col = 0; col < cols; col++)
            {
                if (IsAisleColumn(col, cols)) continue;   // centre column is the aisle, not seats
                float fx = cols > 1 ? (col / (float)(cols - 1) - 0.5f) : 0f;
                float fz = rows > 1 ? (r / (float)(rows - 1) - 0.5f) : 0f;
                Vector3 pos = cabinLocalCenter + new Vector3(fx * cabinLocalSize.x, 0f, fz * cabinLocalSize.z);
                _seats.Add(new CabinSpot { local = pos, isSeat = true });
            }

        // Standing spots: scattered RANDOMLY across the walk box (denser, natural crowd), front-ordered
        // so boarders still fill the front of the aisle first.
        Vector3 wc = WalkCenter;
        for (int i = 0; i < Mathf.Max(1, standCapacity); i++)
        {
            float x = wc.x + Random.Range(-walkHalfWidth, walkHalfWidth);
            float z = wc.z + Random.Range(-walkHalfLength, walkHalfLength);
            _stands.Add(new CabinSpot { local = new Vector3(x, wc.y, z), isSeat = false });
        }
        _stands.Sort((a, b) => b.local.z.CompareTo(a.local.z));   // front (+Z) first
    }

    // Centre column(s) = the standing aisle / Conductor 2 lane; the outer columns are side seats.
    static bool IsAisleColumn(int col, int cols)
    {
        if (cols <= 1) return true;
        if (cols % 2 == 1) return col == cols / 2;
        return col == cols / 2 - 1 || col == cols / 2;
    }

    int OccupiedSeats()
    {
        int n = 0;
        for (int i = 0; i < _seats.Count; i++) if (_seats[i].occupied) n++;
        return n;
    }

    bool SeatsUnderThreshold() => OccupiedSeats() < seatStandThreshold * _seats.Count;

    static CabinSpot FrontFree(List<CabinSpot> list)   // lists are front-ordered, so first free = front-most
    {
        for (int i = 0; i < list.Count; i++) if (!list[i].occupied) return list[i];
        return null;
    }

    bool HasRoom()
    {
        if (SeatsUnderThreshold() && FrontFree(_seats) != null) return true;
        return FrontFree(_stands) != null;
    }

    /// Passenger calls this on reaching the door. Returns a reserved spot (seat front-first under the
    /// threshold, otherwise a standing spot at the front of the aisle), or null if full / on cooldown.
    public CabinSpot TakeBoardingSpot()
    {
        if (Time.time < _doorCooldownUntil) return null;

        CabinSpot spot = null;
        if (SeatsUnderThreshold()) spot = FrontFree(_seats);
        if (spot == null) spot = FrontFree(_stands);
        if (spot == null) return null;

        spot.occupied = true;
        _doorCooldownUntil = Time.time + boardInterval;
        return spot;
    }

    public void CompleteBoard(Passenger p, int fare)
    {
        _aboard.Add(p);
        BoardedCount++;
        if (ShiftManager.Instance != null) ShiftManager.Instance.AddEarnings(fare);
    }

    /// Conductor 2 shoves a standing rider: they walk back to a free seat (random — naturally rear-ward),
    /// or the backmost free standing spot if no seats are free. Returns false if nowhere to go.
    public bool ShovePassenger(Passenger p)
    {
        if (p == null || p.Spot == null || p.Spot.isSeat) return false;

        CabinSpot target = PickRandomFreeSeat();
        if (target == null) target = BackmostFreeStand();
        if (target == null || target == p.Spot) return false;

        p.Spot.occupied = false;
        target.occupied = true;
        p.PushTo(target, RandomJitter());
        return true;
    }

    CabinSpot PickRandomFreeSeat()
    {
        _tmp.Clear();
        for (int i = 0; i < _seats.Count; i++) if (!_seats[i].occupied) _tmp.Add(_seats[i]);
        if (_tmp.Count == 0) return null;
        return _tmp[Random.Range(0, _tmp.Count)];
    }

    CabinSpot BackmostFreeStand()
    {
        CabinSpot back = null;   // _stands is front->back, so the LAST free one is the backmost
        for (int i = 0; i < _stands.Count; i++) if (!_stands[i].occupied) back = _stands[i];
        return back;
    }

    Vector3 RandomJitter() => new Vector3(Random.Range(-standJitter, standJitter), 0f, Random.Range(-standJitter, standJitter));

    public void ReleaseSpot(CabinSpot spot) { if (spot != null) spot.occupied = false; }

    public void LeaveCabin(Passenger p, CabinSpot spot)
    {
        ReleaseSpot(spot);
        _aboard.Remove(p);
    }

    // Editor aid: select the bus to see seats (cyan), the aisle/standing lane (yellow), and the door (green).
    void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.5f);
        Gizmos.DrawWireCube(cabinLocalCenter, new Vector3(cabinLocalSize.x, 0.1f, cabinLocalSize.z));

        int cols = Mathf.Max(1, seatGrid.x);
        int rows = Mathf.Max(1, seatGrid.y);
        Gizmos.color = new Color(0.3f, 0.85f, 1f);
        for (int r = 0; r < rows; r++)
            for (int col = 0; col < cols; col++)
            {
                if (IsAisleColumn(col, cols)) continue;   // seats only; standing is the random crowd below
                float fx = cols > 1 ? (col / (float)(cols - 1) - 0.5f) : 0f;
                float fz = rows > 1 ? (r / (float)(rows - 1) - 0.5f) : 0f;
                Gizmos.DrawSphere(cabinLocalCenter + new Vector3(fx * cabinLocalSize.x, 0f, fz * cabinLocalSize.z), 0.1f);
            }

        // Conductor's walkable lane = the standing zone (controls: walkAreaOffset / walkHalfWidth / walkHalfLength)
        Vector3 wc = WalkCenter;
        Vector3 wsize = new Vector3(walkHalfWidth * 2f, 0.12f, walkHalfLength * 2f);
        Gizmos.color = new Color(1f, 0.25f, 0.9f, 0.25f);
        Gizmos.DrawCube(wc, wsize);
        Gizmos.color = new Color(1f, 0.25f, 0.9f, 1f);
        Gizmos.DrawWireCube(wc, wsize);

        // The actual scattered standing spots (Play mode only — they're randomised at start)
        Gizmos.color = new Color(1f, 0.85f, 0.2f);
        for (int i = 0; i < _stands.Count; i++)
            Gizmos.DrawSphere(_stands[i].local, 0.08f);

        Gizmos.matrix = Matrix4x4.identity;
        Vector3 doorPos = doorAnchor != null ? doorAnchor.position : transform.TransformPoint(doorLocalPos);
        Gizmos.color = Color.green;
        Gizmos.DrawSphere(doorPos, 0.25f);
    }
}
