using System.Collections.Generic;
using UnityEngine;

/// PHASE C — bus stops + waiting crowds on the endless tiled road.
///
/// Driven by TiledRoadStreamer.OnForwardAdvanced: every few new sections it drops a bus stop on the
/// LEFT footpath (our driving side under LHT), borrows a waiting crowd from the shared PassengerPool,
/// and runs the same two-phase pickup:
///   1) bus within gatherRange  -> part of the crowd walks to the curb,
///   2) bus within boardRange + slow (BusPassengers.CanBoard) -> the curb crowd boards 1-by-1.
/// Stops ride the road (parented to it, so floating-origin still works) and are recycled — un-boarded
/// passengers Returned to the pool — once the bus is well past them. Nothing is Instantiated mid-game.
public class SplineStopSpawner : MonoBehaviour
{
    [Header("Placement")]
    [Tooltip("Drop a stop on roughly every Nth advanced section (randomised a little).")]
    public int sectionsPerStop = 4;
    [Tooltip("Max live stops at once (older ones recycle as the bus passes).")]
    public int maxLiveStops = 4;
    [Tooltip("Recycle a stop once the bus is this far PAST it (metres behind).")]
    public float recycleDistance = 70f;

    [Header("Waiting crowd")]
    public int crowdMin = 8;
    public int crowdMax = 22;
    public float crowdSpread = 6f;
    public float curbDepth = 1.2f;

    [Header("Fares")]
    public int baseFare = 20;
    public int fareVariance = 15;

    [Header("Crowd-up & boarding")]
    public float gatherRange = 45f;
    public float boardRange = 22f;
    [Range(0f, 1f)] public float boardFraction = 0.5f;

    [Header("Pool (auto-created if missing)")]
    public int ensurePoolSize = 250;

    static readonly Color[] Palette =
    {
        new Color(0.85f,0.3f,0.3f),  new Color(0.3f,0.5f,0.85f),  new Color(0.9f,0.75f,0.3f),
        new Color(0.35f,0.7f,0.45f), new Color(0.7f,0.45f,0.8f),  new Color(0.9f,0.6f,0.3f),
        new Color(0.85f,0.85f,0.85f),new Color(0.45f,0.7f,0.72f),
    };

    class Stop
    {
        public GameObject box;
        public Vector3 stopPos, curbBase;
        public readonly List<Passenger> crowd = new List<Passenger>();
        public bool gathered, boardingDone;
    }

    TiledRoadStreamer _tiled;
    RoadZone _zone;
    readonly List<Stop> _live = new List<Stop>();
    readonly Stack<Stop> _freeStops = new Stack<Stop>();
    int _sectionsSinceStop;
    int _colorRot;

    void Awake()
    {
        _tiled = GetComponent<TiledRoadStreamer>();
        EnsurePool();
    }

    void OnEnable()
    {
        if (_tiled == null) _tiled = GetComponent<TiledRoadStreamer>();
        if (_tiled != null) _tiled.OnForwardAdvanced += OnSection;
    }
    void OnDisable()
    {
        if (_tiled != null) _tiled.OnForwardAdvanced -= OnSection;
    }

    RoadZone RoadZoneRef => _tiled != null ? _tiled.Zone : null;
    bool LeadFrame(out Vector3 p, out Vector3 f, out Vector3 r)
    {
        if (_tiled != null) return _tiled.TryGetLeadFrame(out p, out f, out r);
        p = default; f = Vector3.forward; r = Vector3.right; return false;
    }

    void EnsurePool()
    {
        if (PassengerPool.Instance != null) return;
        // Create inactive, set the size, THEN activate — so PassengerPool.Awake builds with our size,
        // not the default. (Awake runs on activation, not on AddComponent, for an inactive object.)
        GameObject go = new GameObject("PassengerPool (auto)");
        go.SetActive(false);
        PassengerPool pool = go.AddComponent<PassengerPool>();
        pool.poolSize = ensurePoolSize;
        go.SetActive(true);
    }

    void OnSection(Vector3 leadKnotWorld)
    {
        _zone = RoadZoneRef;
        if (_zone == null || PassengerPool.Instance == null) return;
        if (_live.Count >= maxLiveStops) return;

        if (++_sectionsSinceStop < Random.Range(sectionsPerStop, sectionsPerStop + 2)) return;
        _sectionsSinceStop = 0;

        if (!LeadFrame(out Vector3 pos, out Vector3 fwd, out Vector3 right)) return;
        PlaceStop(pos, fwd, right);
    }

    void PlaceStop(Vector3 framePos, Vector3 fwd, Vector3 right)
    {
        // LEFT footpath under LHT is on -X (right points +X), between the lane edge and the kerb.
        float footCenter = (_zone.DriveHalf + _zone.RoadHalf) * 0.5f;
        Vector3 left = -right;
        Vector3 stopPos = framePos + left * footCenter;
        Vector3 curbBase = framePos + left * (_zone.DriveHalf + curbDepth);
        float groundY = framePos.y;
        stopPos.y = groundY; curbBase.y = groundY;

        Stop st = _freeStops.Count > 0 ? _freeStops.Pop() : new Stop();
        st.gathered = false; st.boardingDone = false;
        st.crowd.Clear();
        st.stopPos = stopPos; st.curbBase = curbBase;

        EnsureBox(st);
        st.box.SetActive(true);
        st.box.transform.SetPositionAndRotation(stopPos + Vector3.up * 0.75f, Quaternion.LookRotation(fwd, Vector3.up));

        // borrow a crowd scattered on the footpath behind the kerb
        int want = Random.Range(crowdMin, crowdMax + 1);
        for (int i = 0; i < want; i++)
        {
            Passenger p = PassengerPool.Instance.Take();
            if (p == null) break;                       // pool drained — fine, fewer people
            Vector2 o = Random.insideUnitCircle * crowdSpread;
            Vector3 wp = stopPos + right * (o.x * 0.4f) + fwd * o.y;   // hug the footpath (thin in X)
            wp.y = groundY;
            p.transform.SetParent(transform, true);     // ride the road
            p.transform.position = wp;
            p.ResetWaiting(Random.Range(baseFare, baseFare + fareVariance + 1), Palette[_colorRot++ % Palette.Length]);
            st.crowd.Add(p);
        }

        _live.Add(st);
    }

    void EnsureBox(Stop st)
    {
        if (st.box != null) return;
        st.box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        st.box.name = "SplineBusStop";
        Collider col = st.box.GetComponent<Collider>();
        if (col != null) Destroy(col);
        st.box.transform.SetParent(transform, false);
        st.box.transform.localScale = new Vector3(1.2f, 1.5f, 1.2f);
        Renderer br = st.box.GetComponent<Renderer>();
        if (br != null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh != null)
            {
                Material m = new Material(sh);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", new Color(0.95f, 0.5f, 0.1f));
                br.material = m;
            }
        }
    }

    void Update()
    {
        BusPassengers bus = BusPassengers.Instance;
        if (bus == null) return;
        Vector3 busPos = bus.transform.position;

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            Stop st = _live[i];
            float dSqr = (busPos - st.stopPos).sqrMagnitude;

            // Phase 1 — crowd up at the curb as the bus approaches.
            if (!st.gathered && dSqr <= gatherRange * gatherRange)
            {
                st.gathered = true;
                int g = 0;
                for (int c = 0; c < st.crowd.Count; c++)
                    if (st.crowd[c] != null && st.crowd[c].state == Passenger.State.Waiting && Random.value < boardFraction)
                        st.crowd[c].BeginGather(CurbPoint(st, g++));
            }

            // Phase 2 — bus pulled up slow & close: the curb crowd boards.
            if (st.gathered && !st.boardingDone && bus.CanBoard && dSqr <= boardRange * boardRange)
            {
                st.boardingDone = true;
                for (int c = 0; c < st.crowd.Count; c++)
                    if (st.crowd[c] != null && st.crowd[c].state == Passenger.State.Gathering)
                        st.crowd[c].BeginBoarding(bus);
            }

            // Recycle once the bus is well past this stop.
            if (Vector3.Distance(busPos, st.stopPos) > recycleDistance && IsBehind(busPos, st.stopPos))
                Recycle(i);
        }
    }

    // Is the stop behind the bus's travel? Cheap heuristic: project onto the bus forward.
    bool IsBehind(Vector3 busPos, Vector3 stopPos)
    {
        Transform b = BusPassengers.Instance.transform;
        return Vector3.Dot(stopPos - busPos, b.forward) < 0f;
    }

    Vector3 CurbPoint(Stop st, int g)
    {
        // a loose line along the kerb in front of the stop
        Vector3 along = (st.box != null) ? st.box.transform.forward : Vector3.forward;
        return st.curbBase + along * ((g - 3f) * 1.3f);
    }

    void Recycle(int index)
    {
        Stop st = _live[index];
        for (int c = 0; c < st.crowd.Count; c++)
        {
            Passenger p = st.crowd[c];
            if (p == null) continue;
            // only reclaim ones that never boarded; aboard passengers belong to the bus now
            if ((p.state == Passenger.State.Waiting || p.state == Passenger.State.Gathering)
                && PassengerPool.Instance != null)
                PassengerPool.Instance.Return(p);
        }
        st.crowd.Clear();
        if (st.box != null) st.box.SetActive(false);
        _live.RemoveAt(index);
        _freeStops.Push(st);
    }

    /// Called by FloatingOrigin after a recenter. Stop boxes + waiting passengers are children of the
    /// road and move with the scene shift automatically, but our cached world positions (used for the
    /// distance checks) must be shifted to match.
    public void OnOriginShifted(Vector3 delta)
    {
        for (int i = 0; i < _live.Count; i++)
        {
            _live[i].stopPos += delta;
            _live[i].curbBase += delta;
        }
    }

    void OnDrawGizmosSelected()
    {
        for (int i = 0; i < _live.Count; i++)
        {
            Stop st = _live[i];
            Gizmos.color = new Color(0.95f, 0.5f, 0.1f, 0.9f);
            Gizmos.DrawWireSphere(st.stopPos, 1f);
            Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.25f);    // gather range
            Gizmos.DrawWireSphere(st.stopPos, gatherRange);
            Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.35f);    // board range
            Gizmos.DrawWireSphere(st.stopPos, boardRange);
        }
    }
}
