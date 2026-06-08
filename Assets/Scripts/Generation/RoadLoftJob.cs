using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

/// Burst-compiled loft for ONE road tile. Fills a Mesh.MeshData (writable) with the slab (top surface +
/// flat bottom + outer walls + end caps) and lane markings, entirely off the managed heap. Called by
/// RoadTile.Build. The math mirrors the old SplineRoadLoft but runs in Burst with zero per-call GC.
///
/// Submeshes: 0 road, 1 footpath/curb/median, 2 ground (+slab walls/caps/bottom), 3 lane markings.
public static class RoadLoftJob
{
    public static void Run(
        NativeArray<float3> cen, NativeArray<float3> right, NativeArray<float3> up, int rings,
        NativeArray<float2> profile, NativeArray<int> seg, NativeArray<float> cum,
        float groundHalf, float roadThickness, RoadProfileMeta meta, Mesh target)
    {
        int P = meta.profileCount;
        int topCount = rings * P;
        int slabVerts = topCount + rings * 2;                       // + 2 bottom outer verts / ring

        // marking vertex count: 2 outer solid edges + (lanesPerDir-1)*2 dashed dividers, each 2 verts/ring
        int markLines = 2 + math.max(0, meta.lanesPerDir - 1) * 2;
        int markVerts = markLines * rings * 2;
        int totalVerts = slabVerts + markVerts;

        // index counts (upper bound; marking dashes may skip, so we count then truncate)
        int topTris = (rings - 1) * (P - 1) * 6;
        int wallTris = (rings - 1) * 3 * 6;                         // underside + 2 walls (NO end caps)
        int markTrisMax = markLines * (rings - 1) * 6;
        int idxMax = topTris + wallTris + markTrisMax;

        Mesh.MeshDataArray mda = Mesh.AllocateWritableMeshData(1);
        Mesh.MeshData md = mda[0];

        // One attribute per STREAM so GetVertexData<T>(stream) returns a tightly-packed array (no
        // interleave stride to fight). Stream 0 = position, 1 = normal, 2 = uv0.
        md.SetVertexBufferParams(totalVerts,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal,   VertexAttributeFormat.Float32, 3, 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0,VertexAttributeFormat.Float32, 2, 2));
        md.SetIndexBufferParams(idxMax, IndexFormat.UInt32);

        var counts = new NativeArray<int>(4, Allocator.TempJob);     // per-submesh actual index counts

        var job = new LoftJob
        {
            cen = cen, right = right, up = up, rings = rings,
            profile = profile, seg = seg, cum = cum,
            groundHalf = groundHalf, roadThickness = roadThickness, meta = meta,
            topCount = topCount, slabVerts = slabVerts, totalVerts = totalVerts,
            topTris = topTris, wallTris = wallTris,
            md = md, counts = counts,
        };
        job.Run();                                                  // Burst, synchronous but native + fast

        // Build 4 submeshes from the contiguous index buffer using the actual counts the job recorded.
        int c0 = counts[0], c1 = counts[1], c2 = counts[2], c3 = counts[3];
        int off0 = 0, off1 = c0, off2 = c0 + c1, off3 = c0 + c1 + c2;
        int used = c0 + c1 + c2 + c3;
        md.subMeshCount = 4;
        md.SetSubMesh(0, new SubMeshDescriptor(off0, c0), MeshUpdateFlags.DontRecalculateBounds);
        md.SetSubMesh(1, new SubMeshDescriptor(off1, c1), MeshUpdateFlags.DontRecalculateBounds);
        md.SetSubMesh(2, new SubMeshDescriptor(off2, c2), MeshUpdateFlags.DontRecalculateBounds);
        md.SetSubMesh(3, new SubMeshDescriptor(off3, c3), MeshUpdateFlags.DontRecalculateBounds);

        target.Clear();
        Mesh.ApplyAndDisposeWritableMeshData(mda, target,
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
        target.RecalculateBounds();

        counts.Dispose();
    }

    [BurstCompile]
    struct LoftJob : IJob
    {
        [ReadOnly] public NativeArray<float3> cen, right, up;
        public int rings;
        [ReadOnly] public NativeArray<float2> profile;
        [ReadOnly] public NativeArray<int> seg;
        [ReadOnly] public NativeArray<float> cum;
        public float groundHalf, roadThickness;
        public RoadProfileMeta meta;
        public int topCount, slabVerts, totalVerts, topTris, wallTris;

        public Mesh.MeshData md;
        public NativeArray<int> counts;   // [0..3] index counts per submesh

        public void Execute()
        {
            int P = meta.profileCount;
            var pos = md.GetVertexData<float3>(0);
            var nrm = md.GetVertexData<float3>(1);
            var uv  = md.GetVertexData<float2>(2);
            var idx = md.GetIndexData<uint>();

            float length = 0f;
            for (int i = 1; i < rings; i++) length += math.distance(cen[i], cen[i - 1]);
            float gh = groundHalf;

            // ---- vertices: top ring verts ----
            for (int i = 0; i < rings; i++)
            {
                float dist = rings > 1 ? (i / (float)(rings - 1)) * length : 0f;
                for (int j = 0; j < P; j++)
                {
                    float3 v = cen[i] + right[i] * profile[j].x + up[i] * profile[j].y;
                    int vi = i * P + j;
                    pos[vi] = v; nrm[vi] = new float3(0, 1, 0); uv[vi] = new float2(cum[j], dist);
                }
                int bl = topCount + i * 2 + 0, br = topCount + i * 2 + 1;
                pos[bl] = cen[i] + right[i] * (-gh) + up[i] * (-roadThickness);
                pos[br] = cen[i] + right[i] * (gh)  + up[i] * (-roadThickness);
                nrm[bl] = new float3(0, -1, 0); nrm[br] = new float3(0, -1, 0);
                uv[bl] = new float2(0, dist); uv[br] = new float2(gh * 2f, dist);
            }

            // ---- indices: write all into one buffer, tracking per-submesh counts ----
            // We lay them out grouped by submesh (0 road, 1 trim, 2 ground+slab, 3 marks) so the caller
            // can make 4 SubMeshDescriptors over contiguous ranges. First pass: count; then fill.
            // Simpler: fill sequentially into temp regions using running cursors.
            int c0 = 0, c1 = 0, c2 = 0, c3 = 0;

            // pass A — top surface, counting per submesh
            for (int i = 0; i < rings - 1; i++)
                for (int j = 0; j < P - 1; j++)
                {
                    int s = seg[j];
                    if (s == 0) c0 += 6; else if (s == 1) c1 += 6; else c2 += 6;
                }
            // slab (underside + walls + caps) all go to submesh 2
            c2 += wallTris;

            // marking counts (respect dashes)
            int markLines = 2 + math.max(0, meta.lanesPerDir - 1) * 2;
            float period = math.max(0.1f, meta.dashLength + meta.gapLength);
            for (int line = 0; line < markLines; line++)
            {
                bool dashed = line >= 2;
                for (int i = 0; i < rings - 1; i++)
                {
                    if (dashed)
                    {
                        float d = rings > 1 ? ((i + 0.5f) / (rings - 1)) * length : 0f;
                        if ((d % period) >= meta.dashLength) continue;
                    }
                    c3 += 6;
                }
            }

            counts[0] = c0; counts[1] = c1; counts[2] = c2; counts[3] = c3;

            // cursors into the contiguous index buffer
            int p0 = 0, p1 = c0, p2 = c0 + c1, p3 = c0 + c1 + c2;

            // pass B — top surface triangles into their submesh region
            for (int i = 0; i < rings - 1; i++)
                for (int j = 0; j < P - 1; j++)
                {
                    int A = i * P + j, B = i * P + j + 1, C = (i + 1) * P + j + 1, D = (i + 1) * P + j;
                    int s = seg[j];
                    if (s == 0)      { Tri(idx, ref p0, A, C, B); Tri(idx, ref p0, A, D, C); }
                    else if (s == 1) { Tri(idx, ref p1, A, C, B); Tri(idx, ref p1, A, D, C); }
                    else             { Tri(idx, ref p2, A, C, B); Tri(idx, ref p2, A, D, C); }
                }

            // slab underside + walls + caps → submesh 2
            for (int i = 0; i < rings - 1; i++)
            {
                int bl0 = topCount + i * 2, br0 = topCount + i * 2 + 1;
                int bl1 = topCount + (i + 1) * 2, br1 = topCount + (i + 1) * 2 + 1;
                Quad(idx, ref p2, bl0, br0, br1, bl1);                                  // underside
                Quad(idx, ref p2, i * P, (i + 1) * P, bl1, bl0);                        // left wall (pt 0)
                Quad(idx, ref p2, i * P + (P - 1), br0, br1, (i + 1) * P + (P - 1));    // right wall (pt P-1)
            }
            // NO end caps — adjacent tiles abut; caps would protrude over the road at seams.

            // ---- marking verts + triangles → submesh 3 ----
            int vbase = slabVerts;
            int vcursor = vbase;
            // line X positions: +DriveHalf, -DriveHalf (solid), then ±(medianHalf + k*laneWidth) dashed
            for (int line = 0; line < markLines; line++)
            {
                float X; bool dashed;
                if (line == 0) { X = meta.laneHalf; dashed = false; }
                else if (line == 1) { X = -meta.laneHalf; dashed = false; }
                else
                {
                    int idxLine = line - 2;
                    int k = idxLine / 2 + 1;
                    bool neg = (idxLine % 2) == 1;
                    X = meta.medianHalf + k * meta.laneWidth;
                    if (neg) X = -X;
                    dashed = true;
                }

                int lineBase = vcursor;
                float mwHalf = meta.markWidth * 0.5f;
                for (int i = 0; i < rings; i++)
                {
                    float dist = rings > 1 ? (i / (float)(rings - 1)) * length : 0f;
                    float3 a = cen[i] + right[i] * (X - mwHalf) + up[i] * meta.markLift;
                    float3 b = cen[i] + right[i] * (X + mwHalf) + up[i] * meta.markLift;
                    pos[vcursor] = a; nrm[vcursor] = new float3(0, 1, 0); uv[vcursor] = new float2(0, dist); vcursor++;
                    pos[vcursor] = b; nrm[vcursor] = new float3(0, 1, 0); uv[vcursor] = new float2(1, dist); vcursor++;
                }
                float period2 = math.max(0.1f, meta.dashLength + meta.gapLength);
                for (int i = 0; i < rings - 1; i++)
                {
                    if (dashed)
                    {
                        float d = rings > 1 ? ((i + 0.5f) / (rings - 1)) * length : 0f;
                        if ((d % period2) >= meta.dashLength) continue;
                    }
                    int A = lineBase + i * 2, B = lineBase + i * 2 + 1, C = lineBase + (i + 1) * 2 + 1, D = lineBase + (i + 1) * 2;
                    Tri(idx, ref p3, A, C, B); Tri(idx, ref p3, A, D, C);
                }
            }
        }

        static void Tri(NativeArray<uint> idx, ref int p, int a, int b, int c)
        {
            idx[p++] = (uint)a; idx[p++] = (uint)b; idx[p++] = (uint)c;
        }
        static void Quad(NativeArray<uint> idx, ref int p, int a, int b, int c, int d)
        {
            idx[p++] = (uint)a; idx[p++] = (uint)b; idx[p++] = (uint)c;
            idx[p++] = (uint)a; idx[p++] = (uint)c; idx[p++] = (uint)d;
        }
    }
}
