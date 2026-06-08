using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Random = UnityEngine.Random;

/// The bulletproof endless road: a pool of fixed-length RoadTiles streamed around the bus. Each tile is
/// lofted ONCE (Burst job, zero-GC, off-thread collider bake) from a stable run of centreline samples and
/// never rebuilt while live — so steady-state streaming costs at most ONE tile's work per recycle, not a
/// whole-road rebuild. Adjacent tiles share their boundary samples exactly, so joins are watertight.
///
/// Replaces EndlessRoadGenerator + SplineRoadLoft for runtime. Keeps the same procedural walk (variety),
/// left-lane spawn, OnForwardAdvanced / OnOriginShifted hooks, and uses RoadZone for the cross-section.
[ExecuteAlways]
[RequireComponent(typeof(RoadZone))]
public class TiledRoadStreamer : MonoBehaviour
{
    [Header("Who to follow")]
    public Transform target;

    [Header("Editor preview")]
    [Tooltip("Build the first tiles in the Scene view before Play, so the road isn't empty in the editor.")]
    public bool editorPreview = true;

    [Header("Tiles")]
    [Tooltip("Length of one tile (metres). Bigger = fewer joins, smoother long curves.")]
    public float tileLength = 60f;
    [Tooltip("Cross-section rings per tile — mesh density along the road. More = smoother curves within a tile.")]
    [Min(2)] public int ringsPerTile = 12;
    [Tooltip("Tiles kept AHEAD of the bus.")]
    [Min(2)] public int tilesAhead = 6;
    [Tooltip("Tiles kept BEHIND the bus before recycling.")]
    [Min(1)] public int tilesBehind = 3;

    [Header("Variety (random each run)")]
    [Tooltip("Max GENTLE turn in degrees per 30m of road. Higher = curvier baseline.")]
    public float maxTurnRate = 14f;
    [Range(0f, 1f)] public float behaviourChangeChance = 0.4f;
    [Range(0f, 1f)] public float straightChance = 0.45f;
    public float wobble = 3f;
    [Tooltip("0 = a stable auto-seed is generated & saved on this object (random-looking but identical in " +
             "editor preview and Play, so the pre-placed bus matches the runtime road). Set a value to pin it.")]
    public int seed = 0;
    [SerializeField, HideInInspector] int _autoSeed;   // persisted so editor preview == runtime build

    [Header("Major turns / U-turns")]
    [Tooltip("Chance (per tile) to START a hard sustained turn — a real corner or U-turn, not a gentle curve.")]
    [Range(0f, 1f)] public float sharpTurnChance = 0.12f;
    [Tooltip("Hard-turn rate in degrees per 30m while a sharp turn is active. Higher = tighter radius — but " +
             "too tight and a U-turn overlaps itself (radius must exceed the road half-width). 35 is a safe corner.")]
    public float sharpTurnRate = 35f;
    [Tooltip("Total degrees a sharp turn sweeps before ending. 90 = corner, 180 = U-turn (randomised up to this).")]
    public float sharpTurnMaxSweep = 170f;
    [Tooltip("Don't start another sharp turn until at least this much road (m) has passed since the last.")]
    public float sharpTurnCooldown = 220f;

    [Header("Materials (auto double-sided URP if empty)")]
    public Material roadMaterial, footpathMaterial, groundMaterial, markingMaterial;
    public float curbHeight = 0.15f;
    public float roadThickness = 0.5f;
    public float markingWidth = 0.15f, dashLength = 3f, gapLength = 4f;

    [Header("Build budget")]
    [Tooltip("Max tiles to build per frame. 1 is smoothest; a couple lets it catch up after a hitch or at " +
             "high speed without leaving a gap. Tiles are cheap (collider bakes off-thread), so 2-3 is fine.")]
    [Min(1)] public int maxTilesPerFrame = 3;
    [Tooltip("Use the Burst loft job (zero-GC, fastest). Off = proven managed loft. Try managed first; " +
             "flip on once you've confirmed the road looks right, for max headroom.")]
    public bool useBurst = false;

    public System.Action<Vector3> OnForwardAdvanced;
    public System.Action<Vector3> OnOriginShiftedEvent;
    public RoadZone Zone => _zone;

    RoadZone _zone;
    readonly List<TileSpan> _live = new List<TileSpan>();     // ordered front-to-back along the road
    readonly Stack<RoadTile> _pool = new Stack<RoadTile>();
    Transform _tilesParent;

    // procedural walk for the LEADING centreline, advanced one tile at a time
    struct Walk { public Vector3 head; public float yaw, turnRate; }
    Walk _lead;
    Random.State _rng;
    bool _busPlaced;

    // The bus's CHAIN position, tracked incrementally. We never re-derive it by global nearest-distance —
    // on a U-turn the road doubles back near itself, and a global search would snap onto the parallel old
    // leg (the "parallel road far away" bug). Instead we nudge this index one step at a time toward
    // whichever ADJACENT tile is closer, and shift it when tiles are added at the front.
    int _busTileIndex;
    bool _busIndexValid;

    // sharp-turn state: when active, the walk holds a hard turn rate until it has swept `_sharpTarget`
    // degrees (a corner / U-turn), then returns to gentle curving. `_sinceSharp` enforces the cooldown.
    bool _sharpActive;
    int _sharpSign;
    float _sharpSwept, _sharpTarget;
    float _sinceSharp = 9999f;

    // continuity carry-over: the previous tile's LAST sample (world) + its forward, so the next tile's
    // first ring is identical to the previous tile's last ring → frames match → the seam is invisible.
    Vector3 _carryPt;
    Vector3 _carryFwd = Vector3.forward;
    bool _haveCarry;

    // Shared cross-section (constant), kept as MANAGED arrays — NOT persistent NativeArrays. Persistent
    // native allocations leak across editor domain reloads (the leak detector flags them every recompile);
    // managed arrays are GC-owned and can't leak. We wrap them in Allocator.Temp NativeArrays per tile
    // build (auto-freed each frame). Rebuilt only when RoadZone widths change.
    float2[] _profile;
    int[] _seg;
    float[] _cum;
    RoadProfileMeta _meta;
    bool _profileReady;
    float _profGroundHalf;

    class TileSpan
    {
        public RoadTile tile;
        public Vector3 startPt, endPt;   // world centreline endpoints (for recycle distance + joins)
    }

    void Awake()
    {
        _zone = GetComponent<RoadZone>();
        if (target == null && BusController.Instance != null) target = BusController.Instance.transform;
    }

    void Start()
    {
        if (!Application.isPlaying) return;

        // Runtime-created meshes don't survive the edit→play domain reload, so we DO rebuild the road at
        // runtime — but deterministically (fixed seed + fixed origin), so it reproduces the EXACT road the
        // editor preview showed. The bus, already seated on that road in edit mode, therefore lands on the
        // identical geometry. We still re-assert the spawn pose once (cheap, no async dependency) to be
        // pixel-perfect, which is reliable here because the road is already built this frame.
        BuildInitial();
        TryPlaceBus();
    }

    void OnEnable()
    {
        if (!Application.isPlaying && editorPreview) RebuildEditorPreview();
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (Application.isPlaying) return;
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this != null && !Application.isPlaying && editorPreview) RebuildEditorPreview();
        };
    }
#endif

    // One-shot preview build for the Scene view (not playing). Lays the initial tiles so you can see the
    // road before pressing Play, AND drops the bus onto the road so there's nothing to place at runtime.
    [ContextMenu("Rebuild Preview + Seat Bus")]
    public void RebuildEditorPreview()
    {
        if (this == null) return;
        BuildInitial();
        PlaceBusInEditor();
    }

    // EDIT MODE: snap the BusPlayer onto the left lane at the middle of the road, so before Play the bus is
    // already correctly seated — no runtime teleport, no async-bake / init-order race (the source of the
    // flaky "spawn in the sky"). At runtime we keep this exact road (no rebuild), so the position holds.
    public void PlaceBusInEditor()
    {
#if UNITY_EDITOR
        if (Application.isPlaying) return;
        ResolveTarget();
        if (target == null) return;
        if (!TryGetSpawnPose(out Vector3 pos, out Quaternion rot)) return;

        // The bus root pivot is at ground contact, so just sit the root on the lane surface.
        target.SetPositionAndRotation(pos, rot);
        UnityEditor.EditorUtility.SetDirty(target);
#endif
    }

    void Update()
    {
        if (!Application.isPlaying) return;        // no streaming/spawn in edit mode
        Stream();
    }

    void ResolveTarget()
    {
        if (target != null) return;
        var bc = BusController.Instance;
        if (bc == null) bc = FindAnyObjectByType<BusController>();
        if (bc != null) target = bc.transform;
    }

    void EnsureParent()
    {
        if (_tilesParent != null) return;
        // Reuse an existing "Tiles" container if one survived a recompile / edit-mode rebuild, and reclaim
        // its tiles into the pool so previews don't spawn duplicates.
        Transform existing = transform.Find("Tiles");
        if (existing != null)
        {
            _tilesParent = existing;
            foreach (var rt in existing.GetComponentsInChildren<RoadTile>(true))
            {
                rt.Release();
                if (!_pool.Contains(rt)) _pool.Push(rt);
            }
            return;
        }
        var go = new GameObject("Tiles");
        go.transform.SetParent(transform, false);
        _tilesParent = go.transform;
    }

    void EnsureMaterials()
    {
        if (roadMaterial == null) roadMaterial = MakeUrp("RoadAsphalt", new Color(0.28f, 0.28f, 0.3f));
        if (footpathMaterial == null) footpathMaterial = MakeUrp("Footpath", new Color(0.62f, 0.6f, 0.56f));
        if (groundMaterial == null) groundMaterial = MakeUrp("Ground", new Color(0.42f, 0.4f, 0.34f));
        if (markingMaterial == null) markingMaterial = MakeUrp("LaneMarking", new Color(0.92f, 0.92f, 0.85f));
    }

    void BuildProfile()
    {
        float mh = _zone.MedianHalf, dh = _zone.DriveHalf, rh = _zone.RoadHalf, gh = _zone.GroundHalf, h = curbHeight;
        var prof = new float2[]
        {
            new float2(-gh, h), new float2(-rh, h), new float2(-dh, h), new float2(-dh, 0f),
            new float2(-mh, 0f), new float2(-mh, h), new float2(mh, h), new float2(mh, 0f),
            new float2(dh, 0f), new float2(dh, h), new float2(rh, h), new float2(gh, h),
        };
        int[] seg = { 2, 1, 1, 0, 1, 1, 1, 0, 1, 1, 2 };
        int P = prof.Length;
        float[] cum = new float[P];
        for (int j = 1; j < P; j++) cum[j] = cum[j - 1] + math.distance(prof[j], prof[j - 1]);

        _profile = prof;          // managed arrays — no native allocation to leak
        _seg = seg;
        _cum = cum;
        _profGroundHalf = gh;
        _meta = new RoadProfileMeta
        {
            profileCount = P, leftEdgeIndex = 0, rightEdgeIndex = P - 1,
            laneHalf = dh, medianHalf = mh, laneWidth = _zone.laneWidth, lanesPerDir = _zone.lanesPerDirection,
            markWidth = markingWidth, markLift = 0.02f, dashLength = dashLength, gapLength = gapLength,
        };
        _profileReady = true;
    }

    // A stable non-zero seed, generated once and persisted in _autoSeed. Uses the global RNG (varies per
    // session when first created in the editor), then stays fixed so preview == runtime.
    int MakeAutoSeed()
    {
        int s = Random.Range(1, int.MaxValue);
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);     // persist the chosen seed with the scene/prefab
#endif
        return s == 0 ? 1 : s;
    }

    public void BuildInitial()
    {
        _zone = GetComponent<RoadZone>();
        EnsureParent(); EnsureMaterials(); BuildProfile();

        // clear any existing
        for (int i = _live.Count - 1; i >= 0; i--) ReleaseSpan(i);

        // Deterministic seed so the editor preview and the runtime build produce the IDENTICAL road (the
        // pre-placed bus must match what gets built at runtime). seed>0 pins it; seed==0 uses a stable
        // auto-seed saved on this object (generated once, random-looking but persistent).
        int effective = seed != 0 ? seed : (_autoSeed != 0 ? _autoSeed : (_autoSeed = MakeAutoSeed()));
        Random.InitState(effective);
        _rng = Random.state;
        _haveCarry = false;                       // fresh build: no boundary carry-over yet
        _sharpActive = false; _sharpSwept = 0f; _sinceSharp = 9999f;
        _busIndexValid = false; _busTileIndex = 0;   // re-seed the bus chain tracker on a fresh build

        // Build the centreline at WORLD Y=0 (ground plane), independent of this object's transform Y, so the
        // road never sits high (and the bus never spawns in the sky because the road itself was elevated).
        int behind = (tilesAhead + tilesBehind) / 2;
        Vector3 origin = transform.position; origin.y = 0f;
        _lead = new Walk { head = origin - Vector3.forward * tileLength * behind, yaw = 0f, turnRate = 0f };

        int n = tilesAhead + tilesBehind;
        for (int i = 0; i < n; i++) AppendTileAhead();
    }

    void Stream()
    {
        if (target == null) { ResolveTarget(); if (target == null) return; }
        if (!_busPlaced) TryPlaceBus();
        if (!_profileReady || _live.Count == 0) return;

        TrackBusIndex();                                  // incremental — immune to U-turn parallel legs

        // EXTEND: keep `tilesAhead` tiles in front of the bus (lower indices). Appending at index 0 pushes
        // the bus index up by 1, so we track that as we go.
        int built = 0;
        int budget = Mathf.Max(1, maxTilesPerFrame);
        while (_busTileIndex < tilesAhead && built < budget)
        {
            AppendTileAhead();                           // inserts at index 0
            _busTileIndex++;                             // everything shifted up one
            built++;
            OnForwardAdvanced?.Invoke(_live[0].endPt);
        }

        // RECYCLE the back of the chain — but ONLY tiles that are BOTH far in chain-order AND physically
        // far from the bus. The physical guard is the safety net: even if the tracked index briefly drifts,
        // we can never drop a tile near the bus and strand it (that was the chain-split / parallel-road bug).
        float keepRadius = tileLength * (tilesBehind + 1f);
        while (_live.Count - 1 - _busTileIndex > tilesBehind)
        {
            int backIdx = _live.Count - 1;
            Vector3 mid = (_live[backIdx].startPt + _live[backIdx].endPt) * 0.5f;
            if ((mid - target.position).sqrMagnitude < keepRadius * keepRadius) break;   // too close — keep it
            ReleaseSpan(backIdx);
        }
    }

    // Track the bus's chain index by a BOUNDED-WINDOW nearest search around the last index. A single-step
    // hill-climb stalls in local minima on curves (a tile 2 ahead can be closer than the adjacent one →
    // the index freezes → recycle drops the wrong tiles → the chain splits into a parallel fragment).
    // A window of ±searchRadius can't get stuck, yet (being bounded) can't jump to a U-turn's far leg.
    [Tooltip("How many tiles either side of the last bus position to search. Must exceed the most tiles the " +
             "bus can cross in one frame; large enough to ride curves, small enough to ignore U-turn legs.")]
    public int trackSearchRadius = 4;

    void TrackBusIndex()
    {
        if (_live.Count == 0) { _busTileIndex = 0; return; }
        if (!_busIndexValid) { _busTileIndex = GlobalNearestIndex(); _busIndexValid = true; }
        _busTileIndex = Mathf.Clamp(_busTileIndex, 0, _live.Count - 1);

        Vector3 b = target.position;
        int lo = Mathf.Max(0, _busTileIndex - trackSearchRadius);
        int hi = Mathf.Min(_live.Count - 1, _busTileIndex + trackSearchRadius);
        float best = float.MaxValue; int bestIdx = _busTileIndex;
        for (int i = lo; i <= hi; i++)
        {
            float d = SqrToTile(i, b);
            if (d < best) { best = d; bestIdx = i; }
        }
        _busTileIndex = bestIdx;
    }

    float SqrToTile(int i, Vector3 b)
    {
        Vector3 mid = (_live[i].startPt + _live[i].endPt) * 0.5f;
        return (mid - b).sqrMagnitude;
    }

    // Only used to (re)seed the tracker — e.g. right after spawn, before any U-turn exists.
    int GlobalNearestIndex()
    {
        Vector3 b = target.position;
        float best = float.MaxValue; int bestIdx = 0;
        for (int i = 0; i < _live.Count; i++)
        {
            float d = SqrToTile(i, b);
            if (d < best) { best = d; bestIdx = i; }
        }
        return bestIdx;
    }

    // Lay down one tile ahead, sampling the continuous walk. The tile's FIRST ring reuses the previous
    // tile's LAST sample + forward (carry-over), so adjacent tiles share an identical boundary ring —
    // same position AND same frame → the seam is invisible (no bump, no normal crease).
    void AppendTileAhead()
    {
        int R = Mathf.Max(2, ringsPerTile);
        var cen = new NativeArray<float3>(R, Allocator.TempJob);
        var rt  = new NativeArray<float3>(R, Allocator.TempJob);
        var up  = new NativeArray<float3>(R, Allocator.TempJob);

        float step = tileLength / (R - 1);

        // first ring = the carried boundary from the previous tile (or the current head on the very first)
        Vector3 startWorld = _haveCarry ? _carryPt : _lead.head;
        Vector3 prevPt = startWorld;
        Vector3 prevFwd = _haveCarry ? _carryFwd : (Quaternion.Euler(0, _lead.yaw, 0) * Vector3.forward);

        // make sure the walk head IS the start point so the next samples continue from here
        _lead.head = startWorld;

        // Decide (once per tile) whether to START a sharp sustained turn — a real corner / U-turn.
        MaybeStartSharpTurn();

        for (int i = 0; i < R; i++)
        {
            Vector3 cur; Vector3 fwd;
            if (i == 0) { cur = startWorld; fwd = prevFwd; }
            else
            {
                cur = StepWalk(step);
                fwd = cur - prevPt; fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-6f) fwd = prevFwd;
                fwd.Normalize();
            }
            cen[i] = (float3)(cur - startWorld);
            rt[i] = (float3)Vector3.Cross(Vector3.up, fwd).normalized;
            up[i] = new float3(0, 1, 0);
            prevPt = cur; prevFwd = fwd;
        }

        // Wrap the managed profile in short-lived NativeArrays for the build (disposed below — never leaks).
        var prof = new NativeArray<float2>(_profile, Allocator.TempJob);
        var seg  = new NativeArray<int>(_seg, Allocator.TempJob);
        var cum  = new NativeArray<float>(_cum, Allocator.TempJob);

        RoadTile tile = TakeTile();
        tile.transform.SetPositionAndRotation(startWorld, Quaternion.identity);
        tile.Build(cen, rt, up, R, prof, seg, cum, _profGroundHalf, roadThickness, _meta,
                   new[] { roadMaterial, footpathMaterial, groundMaterial, markingMaterial }, useBurst);

        cen.Dispose(); rt.Dispose(); up.Dispose();
        prof.Dispose(); seg.Dispose(); cum.Dispose();

        Vector3 endWorld = _lead.head;
        _live.Insert(0, new TileSpan { tile = tile, startPt = startWorld, endPt = endWorld });

        // carry this tile's last sample + forward to the next tile's first ring
        _carryPt = endWorld;
        _carryFwd = prevFwd;
        _haveCarry = true;
    }

    // Roll once per tile to begin a hard sustained turn (corner / U-turn), respecting the cooldown.
    void MaybeStartSharpTurn()
    {
        if (_sharpActive) return;
        if (_sinceSharp < sharpTurnCooldown) return;
        Random.state = _rng;
        bool go = Random.value < sharpTurnChance;
        float sign = Random.value < 0.5f ? -1f : 1f;
        float target = Random.Range(70f, Mathf.Max(80f, sharpTurnMaxSweep));   // corner..U-turn
        _rng = Random.state;
        if (!go) return;
        _sharpActive = true;
        _sharpSign = (int)sign;
        _sharpSwept = 0f;
        _sharpTarget = target;
    }

    // Continuous procedural walk. turnRate is degrees per TurnRefDistance metres, so the road curves the
    // SAME amount regardless of ring density or tile length (longer step → proportionally more turn).
    const float TurnRefDistance = 30f;
    Vector3 StepWalk(float step)
    {
        Random.state = _rng;
        float k = step / TurnRefDistance;
        float deltaYaw;

        if (_sharpActive)
        {
            // hold a hard turn until the target sweep is reached, then ease the last bit and end it
            float remaining = _sharpTarget - _sharpSwept;
            float thisStep = Mathf.Min(sharpTurnRate * k, Mathf.Max(2f, remaining));
            deltaYaw = _sharpSign * thisStep;
            _sharpSwept += thisStep;
            if (_sharpSwept >= _sharpTarget) { _sharpActive = false; _sinceSharp = 0f; _lead.turnRate = 0f; }
        }
        else
        {
            if (Random.value < behaviourChangeChance)
                _lead.turnRate = Random.value < straightChance ? 0f : Random.Range(-maxTurnRate, maxTurnRate);
            deltaYaw = (_lead.turnRate + Random.Range(-wobble, wobble)) * k;
            _sinceSharp += step;
        }

        _lead.yaw += deltaYaw;
        _lead.head += Quaternion.Euler(0f, _lead.yaw, 0f) * Vector3.forward * step;
        _rng = Random.state;
        return _lead.head;
    }

    RoadTile TakeTile()
    {
        RoadTile t = _pool.Count > 0 ? _pool.Pop() : NewTile();
        t.Acquire();
        return t;
    }

    RoadTile NewTile()
    {
        EnsureParent();
        var go = new GameObject("RoadTile");
        go.transform.SetParent(_tilesParent, false);
        return go.AddComponent<RoadTile>();
    }

    void ReleaseSpan(int index)
    {
        TileSpan s = _live[index];
        s.tile.Release();
        _pool.Push(s.tile);
        _live.RemoveAt(index);
    }

    /// World frame at the leading edge (front tile's far end), so Phase C can place footpath/lane content
    /// aligned to the road there.
    public bool TryGetLeadFrame(out Vector3 pos, out Vector3 fwd, out Vector3 right)
    {
        pos = default; fwd = Vector3.forward; right = Vector3.right;
        if (_live.Count == 0) return false;
        TileSpan s = _live[0];                       // front-most
        pos = s.endPt;
        Vector3 d = s.endPt - s.startPt; d.y = 0f;
        fwd = d.sqrMagnitude < 1e-6f ? Vector3.forward : d.normalized;
        right = Vector3.Cross(Vector3.up, fwd).normalized;
        return true;
    }

    // --- spawn ---
    // Geometric (NO raycast — raycasts depend on async collider bake timing and can hit the bus itself).
    // The road's top surface at a LANE is exactly the centreline Y (profile Y = 0 on the road), so the
    // spawn Y is simply the span-start Y. Deterministic and order-independent.
    public bool TryGetSpawnPose(out Vector3 pos, out Quaternion rot)
    {
        pos = default; rot = default;
        if (_live.Count == 0) return false;
        // MIDDLE tile, MIDDLE of its span → road both ahead and behind the bus from the very first frame.
        int idx = Mathf.Clamp(_live.Count / 2, 0, _live.Count - 1);
        TileSpan s = _live[idx];
        Vector3 mid = (s.startPt + s.endPt) * 0.5f;
        Vector3 fwd = (s.endPt - s.startPt); fwd.y = 0f;
        fwd = fwd.sqrMagnitude < 1e-6f ? Vector3.forward : fwd.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
        float laneX = _zone.LaneCenterX(_zone.lanesPerDirection - 1, true);
        pos = mid + right * laneX;            // Y = midpoint Y = the lane surface
        rot = Quaternion.LookRotation(fwd, Vector3.up);
        return true;
    }

    void TryPlaceBus()
    {
        if (_busPlaced) return;
        ResolveTarget();
        if (target == null) return;

        var bc = target.GetComponent<BusController>() ?? target.GetComponentInParent<BusController>();
        // Wait until the bus is FULLY initialised: BusController.Start detaches the sphere (parent==null)
        // and measures the radius. Teleporting before that desyncs the rig → the "spawn in the sky" bug.
        if (bc != null && bc.sphere != null && bc.sphere.transform.parent != null) return;

        if (!TryGetSpawnPose(out Vector3 pos, out Quaternion rot)) return;
        if (bc != null) bc.TeleportTo(pos, rot); else target.SetPositionAndRotation(pos, rot);
        _busPlaced = true;

        // seed the chain tracker at the spawn span (the middle tile — matches TryGetSpawnPose)
        _busTileIndex = Mathf.Clamp(_live.Count / 2, 0, _live.Count - 1);
        _busIndexValid = true;
    }

    // --- floating origin ---
    public void OnOriginShifted(Vector3 delta)
    {
        // Shift EVERY cached world position by the same delta. Missing even one desyncs it from the rest:
        // forgetting _carryPt made the NEXT tile spawn at the pre-shift spot, far from the shifted road —
        // the giant connector gap / "road generating way further away" bug.
        _lead.head += delta;
        _carryPt += delta;
        for (int i = 0; i < _live.Count; i++) { _live[i].startPt += delta; _live[i].endPt += delta; }
        OnOriginShiftedEvent?.Invoke(delta);
        // tile transforms are children of this object, which the scene-wide shift already moved.
    }

    static Material MakeUrp(string name, Color color)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit"); if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh) { name = name };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 0f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.1f);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
        return m;
    }

    // Diagnostic: draw the chain (front=green → back=red), connect consecutive tiles to reveal any split,
    // and mark the tile the streamer THINKS the bus is on (magenta) + a line to the actual bus.
    void OnDrawGizmosSelected()
    {
        if (_live == null || _live.Count == 0) return;
        for (int i = 0; i < _live.Count; i++)
        {
            float t = _live.Count > 1 ? i / (float)(_live.Count - 1) : 0f;
            Gizmos.color = Color.Lerp(Color.green, Color.red, t);
            Vector3 a = _live[i].startPt, b = _live[i].endPt;
            Gizmos.DrawLine(a, b);
            if (i < _live.Count - 1)                       // connector to next tile — a gap here = a real split
            {
                Gizmos.color = Color.white;
                Gizmos.DrawLine(_live[i].endPt, _live[i + 1].startPt);
            }
        }
        if (_busIndexValid && _busTileIndex >= 0 && _busTileIndex < _live.Count)
        {
            Vector3 mid = (_live[_busTileIndex].startPt + _live[_busTileIndex].endPt) * 0.5f;
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(mid, 2f);
            if (target != null) Gizmos.DrawLine(mid, target.position);
        }
    }
}
