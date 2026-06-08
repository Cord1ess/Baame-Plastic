using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;

/// One pooled, fixed-span section of the tiled endless road. A tile owns its own mesh + MeshCollider and
/// is built ONCE per (re)use from a short run of centreline samples handed to it by TiledRoadStreamer.
/// Because adjacent tiles share their boundary ring EXACTLY (same sample point → identical verts), the
/// joins are watertight with zero seam. After Build() the tile is never touched again until it's recycled
/// and rebuilt for a new span — so steady-state streaming does at most ONE tile's work per recycle, not
/// the whole road. Vertex generation runs in a Burst job (zero managed GC); the collider bakes off-thread.
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class RoadTile : MonoBehaviour
{
    Mesh _mesh;
    Mesh _bakeMesh;
    MeshFilter _mf;
    MeshRenderer _mr;
    MeshCollider _mc;

    bool _bakeRunning;
    volatile bool _bakeDone;

    public bool InUse { get; private set; }
    public int Generation { get; private set; }   // bumped each reuse, so late bakes from a prior life are ignored

    void EnsureRefs()
    {
        if (_mf != null) return;
        _mf = GetComponent<MeshFilter>();
        _mr = GetComponent<MeshRenderer>();
        _mc = GetComponent<MeshCollider>();
        _mesh = new Mesh { name = "RoadTile" };
        _mesh.MarkDynamic();
        _mf.sharedMesh = _mesh;
    }

    /// Build this tile's geometry from `rings` centreline samples (local to this tile's transform).
    /// cen/right/up are parallel arrays. profile/seg/cum describe the cross-section. Materials are shared.
    /// `useBurst` runs the Burst job (zero-GC, fastest); otherwise a proven managed loft. Either way the
    /// per-tile vertex count is small (~250), this happens at most once per frame, and the collider bakes
    /// off-thread — so it's effectively free. Tiles are NEVER rebuilt while live.
    public void Build(
        NativeArray<float3> cen, NativeArray<float3> right, NativeArray<float3> up, int rings,
        NativeArray<float2> profile, NativeArray<int> seg, NativeArray<float> cum,
        float groundHalf, float roadThickness, RoadProfileMeta meta,
        Material[] materials, bool useBurst)
    {
        EnsureRefs();
        InUse = true;
        Generation++;

        if (useBurst) RoadLoftJob.Run(cen, right, up, rings, profile, seg, cum, groundHalf, roadThickness, meta, _mesh);
        else BuildManaged(cen, right, up, rings, profile, seg, cum, groundHalf, roadThickness, meta);

        if (_mr.sharedMaterials == null || _mr.sharedMaterials.Length != materials.Length)
            _mr.sharedMaterials = materials;

        ScheduleBake();
    }

    // Proven managed loft (same math as the original SplineRoadLoft), filling the reused tile mesh.
    // Buffers are tile-local and small; List<>s are members so steady-state reuse avoids most GC.
    readonly List<Vector3> _v = new List<Vector3>();
    readonly List<Vector2> _uv = new List<Vector2>();
    readonly List<int>[] _t = { new List<int>(), new List<int>(), new List<int>(), new List<int>() };

    void BuildManaged(
        NativeArray<float3> cen, NativeArray<float3> right, NativeArray<float3> up, int rings,
        NativeArray<float2> profile, NativeArray<int> seg, NativeArray<float> cum,
        float gh, float roadThickness, RoadProfileMeta meta)
    {
        int P = meta.profileCount;
        _v.Clear(); _uv.Clear();
        for (int s = 0; s < 4; s++) _t[s].Clear();

        float length = 0f;
        for (int i = 1; i < rings; i++) length += math.distance(cen[i], cen[i - 1]);

        int topCount = rings * P;
        // top ring verts
        for (int i = 0; i < rings; i++)
        {
            float dist = rings > 1 ? (i / (float)(rings - 1)) * length : 0f;
            for (int j = 0; j < P; j++)
            {
                _v.Add((Vector3)(cen[i] + right[i] * profile[j].x + up[i] * profile[j].y));
                _uv.Add(new Vector2(cum[j], dist));
            }
        }
        // bottom outer verts (2/ring)
        for (int i = 0; i < rings; i++)
        {
            float dist = rings > 1 ? (i / (float)(rings - 1)) * length : 0f;
            _v.Add((Vector3)(cen[i] + right[i] * (-gh) + up[i] * (-roadThickness))); _uv.Add(new Vector2(0f, dist));
            _v.Add((Vector3)(cen[i] + right[i] * (gh)  + up[i] * (-roadThickness))); _uv.Add(new Vector2(gh * 2f, dist));
        }

        // top surface
        for (int i = 0; i < rings - 1; i++)
            for (int j = 0; j < P - 1; j++)
            {
                int A = i * P + j, B = i * P + j + 1, C = (i + 1) * P + j + 1, D = (i + 1) * P + j;
                var tl = _t[seg[j]];
                tl.Add(A); tl.Add(C); tl.Add(B); tl.Add(A); tl.Add(D); tl.Add(C);
            }

        // slab → submesh 2. NO transverse end caps: adjacent tiles abut, so a cap at each tile end would
        // be a redundant vertical wall at the seam — and over the road (which dips below curb height) it
        // pokes up as a thin protruding line (the seam "bump"). The road is continuous, so leave ends open;
        // the only truly-open ends are the despawning extremes, which are off-screen.
        var g = _t[2];
        for (int i = 0; i < rings - 1; i++)
        {
            int bl0 = topCount + i * 2, br0 = topCount + i * 2 + 1, bl1 = topCount + (i + 1) * 2, br1 = topCount + (i + 1) * 2 + 1;
            Quad(g, bl0, br0, br1, bl1);                                        // underside
            Quad(g, i * P, (i + 1) * P, bl1, bl0);                              // left wall (pt 0)
            Quad(g, i * P + (P - 1), br0, br1, (i + 1) * P + (P - 1));          // right wall (pt P-1)
        }

        // lane markings → submesh 3
        int markLines = 2 + Mathf.Max(0, meta.lanesPerDir - 1) * 2;
        float period = Mathf.Max(0.1f, meta.dashLength + meta.gapLength);
        float mwHalf = meta.markWidth * 0.5f;
        var mk = _t[3];
        for (int line = 0; line < markLines; line++)
        {
            float X; bool dashed;
            if (line == 0) { X = meta.laneHalf; dashed = false; }
            else if (line == 1) { X = -meta.laneHalf; dashed = false; }
            else { int il = line - 2; int k = il / 2 + 1; bool neg = (il % 2) == 1; X = meta.medianHalf + k * meta.laneWidth; if (neg) X = -X; dashed = true; }

            int lineBase = _v.Count;
            for (int i = 0; i < rings; i++)
            {
                float dist = rings > 1 ? (i / (float)(rings - 1)) * length : 0f;
                _v.Add((Vector3)(cen[i] + right[i] * (X - mwHalf) + up[i] * meta.markLift)); _uv.Add(new Vector2(0f, dist));
                _v.Add((Vector3)(cen[i] + right[i] * (X + mwHalf) + up[i] * meta.markLift)); _uv.Add(new Vector2(1f, dist));
            }
            for (int i = 0; i < rings - 1; i++)
            {
                if (dashed)
                {
                    float d = rings > 1 ? ((i + 0.5f) / (rings - 1)) * length : 0f;
                    if ((d % period) >= meta.dashLength) continue;
                }
                int A = lineBase + i * 2, B = lineBase + i * 2 + 1, C = lineBase + (i + 1) * 2 + 1, D = lineBase + (i + 1) * 2;
                mk.Add(A); mk.Add(C); mk.Add(B); mk.Add(A); mk.Add(D); mk.Add(C);
            }
        }

        _mesh.Clear();
        _mesh.indexFormat = _v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        _mesh.SetVertices(_v);
        _mesh.SetUVs(0, _uv);
        _mesh.subMeshCount = 4;
        for (int s = 0; s < 4; s++) _mesh.SetTriangles(_t[s], s);
        _mesh.RecalculateNormals();

        // Force the TOP surface + markings to flat-up normals. Every tile then has identical boundary
        // normals, so there's no lighting crease at the seam (RecalculateNormals would otherwise give
        // each tile's edge ring a slightly different normal on curves). The slab keeps its real normals.
        _nrm.Clear();
        _mesh.GetNormals(_nrm);
        for (int i = 0; i < topCount && i < _nrm.Count; i++) _nrm[i] = Vector3.up;
        int markStart = topCount + rings * 2;     // markings begin after the 2 bottom verts/ring
        for (int i = markStart; i < _nrm.Count; i++) _nrm[i] = Vector3.up;
        _mesh.SetNormals(_nrm);

        _mesh.RecalculateBounds();
    }
    readonly List<Vector3> _nrm = new List<Vector3>();

    static void Quad(List<int> t, int a, int b, int c, int d)
    {
        t.Add(a); t.Add(b); t.Add(c); t.Add(a); t.Add(c); t.Add(d);
    }

    void ScheduleBake()
    {
        // collider needs positions + drivable submeshes only (skip lane-marking submesh 3); reuse one mesh
        if (_bakeMesh == null) { _bakeMesh = new Mesh { name = "RoadTile (collision)" }; _bakeMesh.MarkDynamic(); }
        _bakeMesh.Clear();
        _bakeMesh.indexFormat = _mesh.indexFormat;
        _bakeMesh.vertices = _mesh.vertices;
        var tris = new System.Collections.Generic.List<int>();
        int sub = Mathf.Min(3, _mesh.subMeshCount);
        for (int s = 0; s < sub; s++) tris.AddRange(_mesh.GetTriangles(s));
        _bakeMesh.SetTriangles(tris, 0);

        if (_bakeMesh.vertexCount < 3) { _mc.sharedMesh = null; return; }

        if (!Application.isPlaying)      // editor preview: cook synchronously (no Update loop to pick it up)
        {
            _mc.sharedMesh = null; _mc.sharedMesh = _bakeMesh;
            return;
        }

        _bakeRunning = true;
        _bakeDone = false;
        // NOTE: GetInstanceID()/BakeMesh(int,bool) are flagged obsolete-in-future (the EntityId replacement
        // isn't guaranteed present in this Unity version), but they still work. Suppress the noise locally.
#pragma warning disable 0618
        int id = _bakeMesh.GetInstanceID();
        System.Threading.Tasks.Task.Run(() =>
        {
            try { Physics.BakeMesh(id, false); } catch { }
            _bakeDone = true;
        });
#pragma warning restore 0618
    }

    void Update()
    {
        if (!_bakeRunning || !_bakeDone) return;
        _bakeRunning = false;
        if (InUse && _bakeMesh != null) { _mc.sharedMesh = null; _mc.sharedMesh = _bakeMesh; }
    }

    /// Return to the pool: hide + drop the collider (so nothing collides with a parked tile).
    public void Release()
    {
        InUse = false;
        if (_mc != null) _mc.sharedMesh = null;
        gameObject.SetActive(false);
    }

    public void Acquire()
    {
        gameObject.SetActive(true);
    }

    void OnDestroy()
    {
        if (_mesh != null) { if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh); }
        if (_bakeMesh != null) { if (Application.isPlaying) Destroy(_bakeMesh); else DestroyImmediate(_bakeMesh); }
    }
}

/// Constant cross-section metadata shared by all tiles (point count + which profile indices are walls/edges).
public struct RoadProfileMeta
{
    public int profileCount;     // P
    public int leftEdgeIndex;    // 0
    public int rightEdgeIndex;   // P-1
    public float laneHalf;       // DriveHalf — for marking lines
    public float medianHalf;     // MedianHalf
    public float laneWidth;
    public int lanesPerDir;
    public float markWidth;
    public float markLift;       // y above road for paint
    public float dashLength;
    public float gapLength;
}
