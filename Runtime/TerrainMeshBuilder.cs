using System;
using Unity.Burst;
using Unity.Profiling;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Humanier.Terrain.Meshing;

namespace Humanier.Terrain
{
    [Flags]
    public enum TransitionFaceMask { None = 0, NegativeX = 1, PositiveX = 2, NegativeY = 4, PositiveY = 8, NegativeZ = 16, PositiveZ = 32 }

    internal static class TerrainMeshBuilder
    {
        private static readonly ProfilerMarker ScheduleMarker = new ProfilerMarker("Terrain.Mesh.Schedule");
        internal static TerrainMeshBuildRequest Schedule(TerrainChunkData data, Vector3 origin, int lod, TransitionFaceMask faces = TransitionFaceMask.None, int expectedVersion = -1, TerrainMeshResources resources = null)
        {
            using var profileScope = ScheduleMarker.Auto();
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (expectedVersion >= 0 && expectedVersion != data.Version) throw new InvalidOperationException("Density data changed before meshing began.");
            if (data.ClassifyDensity() != TerrainDensityClass.Mixed) return TerrainMeshBuildRequest.CompletedEmpty(data.Version);
            int stride = 1 << Mathf.Clamp(lod, 0, 3), samples = data.Density.Length, cells = data.Resolution / stride;
            // Mesh work can remain queued for more than four frames. TempJob is
            // invalid in that case, so the request owns persistent containers and
            // releases them when its mesh has been applied or discarded.
            var density = new NativeArray<float>(samples, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var materials = new NativeArray<byte>(samples, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            density.CopyFrom(data.Density); materials.CopyFrom(data.Material);
            bool ownsTables = resources == null;
            var tables = ownsTables ? new TransvoxelTableSnapshot(Allocator.Persistent) : resources.Tables;
            int capacity = math.max(16, cells * cells * cells * 12 + (data.Resolution / (stride * 2)) * (data.Resolution / (stride * 2)) * 12 * FaceCount(faces));
            var vertices = new NativeList<float3>(capacity, Allocator.Persistent);
            var colors = new NativeList<Color32>(capacity, Allocator.Persistent);
            var normals = new NativeList<float3>(capacity, Allocator.Persistent);
            var indices = new NativeList<int>(capacity * 3, Allocator.Persistent);
            var job = new TerrainMeshingJob { density = density, materials = materials,
                regularVertexCount = tables.regularVertexCount, regularTriangleIndexCount = tables.regularTriangleIndexCount, regularVertices = tables.regularVertices, regularIndices = tables.regularIndices,
                transitionVertexCount = tables.transitionVertexCount, transitionTriangleIndexCount = tables.transitionTriangleIndexCount, transitionVertices = tables.transitionVertices, transitionIndices = tables.transitionIndices, transitionFlip = tables.transitionFlip, transitionCornerOffsets = tables.transitionCornerOffsets,
                vertices = vertices, colors = colors, normals = normals, indices = indices,
                resolution = data.Resolution, stride = stride, voxelSize = data.VoxelSize, offset = new float3(data.Id.x * data.Resolution * data.VoxelSize, data.Id.y * data.Resolution * data.VoxelSize, data.Id.z * data.Resolution * data.VoxelSize) - (float3)origin, faces = faces };
            return new TerrainMeshBuildRequest(data.Version, job.Schedule(), density, materials, tables, ownsTables, vertices, colors, normals, indices);
        }

        internal static Mesh Build(TerrainChunkData data, Vector3 origin, int lod) => Build(data, origin, lod, TransitionFaceMask.None);
        internal static Mesh Build(TerrainChunkData data, Vector3 origin, int lod, TransitionFaceMask faces) { using (var request = Schedule(data, origin, lod, faces, data.Version)) return request.Complete(); }
        private static int FaceCount(TransitionFaceMask faces) { int n = 0; for (int i = 0; i < 6; i++) if (((int)faces & (1 << i)) != 0) n++; return n; }
    }

    internal sealed class TerrainMeshResources : IDisposable
    {
        private TransvoxelTableSnapshot tables;
        private bool disposed;

        internal TerrainMeshResources() => tables = new TransvoxelTableSnapshot(Allocator.Persistent);
        internal TransvoxelTableSnapshot Tables => !disposed ? tables : throw new ObjectDisposedException(nameof(TerrainMeshResources));

        public void Dispose()
        {
            if (disposed) return;
            tables.Dispose();
            disposed = true;
        }
    }

    internal sealed class TerrainMeshBuildRequest : IDisposable
    {
        private static readonly ProfilerMarker CompleteMarker = new ProfilerMarker("Terrain.Mesh.Complete");
        private JobHandle handle; private NativeArray<float> density; private NativeArray<byte> materials; private TransvoxelTableSnapshot tables;
        private NativeList<float3> vertices; private NativeList<Color32> colors; private NativeList<float3> normals; private NativeList<int> indices; private bool disposed; private readonly bool completedEmpty; private readonly bool ownsTables;
        internal int Version { get; }
        internal bool IsCompleted => completedEmpty || handle.IsCompleted;
        internal bool HasScheduledJob => !completedEmpty;
        internal TerrainMeshBuildRequest(int version, JobHandle handle, NativeArray<float> density, NativeArray<byte> materials, TransvoxelTableSnapshot tables, bool ownsTables, NativeList<float3> vertices, NativeList<Color32> colors, NativeList<float3> normals, NativeList<int> indices)
        { Version = version; this.handle = handle; this.density = density; this.materials = materials; this.tables = tables; this.ownsTables = ownsTables; this.vertices = vertices; this.colors = colors; this.normals = normals; this.indices = indices; }
        private TerrainMeshBuildRequest(int version)
        {
            Version = version;
            completedEmpty = true;
        }
        internal static TerrainMeshBuildRequest CompletedEmpty(int version) => new TerrainMeshBuildRequest(version);
        internal Mesh Complete()
        {
            using var profileScope = CompleteMarker.Auto();
            if (disposed) throw new ObjectDisposedException(nameof(TerrainMeshBuildRequest));
            if (completedEmpty) return null;
            handle.Complete();
            // A chunk can contain edge vertices but no valid triangles (for example
            // an all-air cell or a fully collapsed transition). Treat it as empty so
            // MeshCollider never receives an invalid zero-triangle mesh.
            if (vertices.Length == 0 || indices.Length < 3) return null;
            var mesh = new Mesh { indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            var output = new Vector3[vertices.Length]; var outputNormals = new Vector3[normals.Length]; for (int i = 0; i < output.Length; i++) { output[i] = vertices[i]; outputNormals[i] = normals[i]; }
            mesh.vertices = output; mesh.normals = outputNormals; mesh.SetColors(colors.AsArray()); mesh.SetIndices(indices.AsArray(), MeshTopology.Triangles, 0, true); mesh.RecalculateBounds(); return mesh;
        }
        public void Dispose()
        {
            if (disposed) return; if (!completedEmpty) handle.Complete(); if (density.IsCreated) density.Dispose(); if (materials.IsCreated) materials.Dispose(); if (ownsTables) tables.Dispose();
            if (vertices.IsCreated) vertices.Dispose(); if (colors.IsCreated) colors.Dispose(); if (normals.IsCreated) normals.Dispose(); if (indices.IsCreated) indices.Dispose(); disposed = true;
        }
    }

    [BurstCompile]
    internal struct TerrainMeshingJob : IJob
    {
        [ReadOnly] internal NativeArray<float> density; [ReadOnly] internal NativeArray<byte> materials;
        [ReadOnly] internal NativeArray<byte> regularVertexCount, regularTriangleIndexCount, regularIndices;
        [ReadOnly] internal NativeArray<ushort> regularVertices;
        [ReadOnly] internal NativeArray<byte> transitionVertexCount, transitionTriangleIndexCount, transitionIndices, transitionFlip;
        [ReadOnly] internal NativeArray<ushort> transitionVertices;
        [ReadOnly] internal NativeArray<int2> transitionCornerOffsets;
        internal NativeList<float3> vertices; internal NativeList<Color32> colors; internal NativeList<float3> normals; internal NativeList<int> indices;
        internal int resolution, stride; internal float voxelSize; internal float3 offset; internal TransitionFaceMask faces;
        public void Execute()
        {
            for (int z = 0; z < resolution; z += stride) for (int y = 0; y < resolution; y += stride) for (int x = 0; x < resolution; x += stride) Regular(x, y, z);
            if ((faces & TransitionFaceMask.NegativeX) != 0) Transition(0); if ((faces & TransitionFaceMask.PositiveX) != 0) Transition(1);
            if ((faces & TransitionFaceMask.NegativeY) != 0) Transition(2); if ((faces & TransitionFaceMask.PositiveY) != 0) Transition(3);
            if ((faces & TransitionFaceMask.NegativeZ) != 0) Transition(4); if ((faces & TransitionFaceMask.PositiveZ) != 0) Transition(5);
        }
        private void Regular(int x, int y, int z)
        {
            int code = 0; for (int i = 0; i < 8; i++) if (D(x + ((i & 1) * stride), y + (((i >> 2) & 1) * stride), z + (((i >> 1) & 1) * stride)) > 0f) code |= 1 << i;
            int count = regularVertexCount[code]; if (count == 0) return; int start = vertices.Length;
            for (int i = 0; i < count; i++)
            {
                ushort edge = regularVertices[code * 12 + i];
                int a = (edge >> 4) & 15, b = edge & 15;
                float da = CornerD(x, y, z, a), db = CornerD(x, y, z, b);
                float3 primary = EdgeIntersection(PrimaryCornerP(x, y, z, a), PrimaryCornerP(x, y, z, b), da, db);
                float3 normal = OutwardNormal(primary);
                float3 vertex = SecondaryPosition(primary, normal);
                vertices.Add(vertex); colors.Add(C(primary)); normals.Add(normal);
            }
            for (int i = 0, n = regularTriangleIndexCount[code]; i < n; i += 3) AddOriented(start + regularIndices[code * 36 + i], start + regularIndices[code * 36 + i + 1], start + regularIndices[code * 36 + i + 2]);
        }
        // Transvoxel's 13-point cell joins a two-by-two fine face patch to its coarser representation.
        private void Transition(int face)
        {
            int span = stride * 2;
            for (int v = 0; v < resolution; v += span) for (int u = 0; u < resolution; u += span)
            {
                float d0=FD(face,u,v), d1=FD(face,u+stride,v), d2=FD(face,u+span,v), d3=FD(face,u,v+stride), d4=FD(face,u+stride,v+stride), d5=FD(face,u+span,v+stride), d6=FD(face,u,v+span), d7=FD(face,u+stride,v+span), d8=FD(face,u+span,v+span);
                int code=(d0>0?1:0)|(d1>0?2:0)|(d2>0?4:0)|(d5>0?8:0)|(d8>0?16:0)|(d7>0?32:0)|(d6>0?64:0)|(d3>0?128:0)|(d4>0?256:0), count=transitionVertexCount[code]; if(count==0) continue; int start=vertices.Length;
                for (int i = 0; i < count; i++)
                {
                    ushort edge = transitionVertices[code * 12 + i];
                    int a = (edge >> 4) & 15, b = edge & 15;
                    float da = TD(a, d0, d1, d2, d3, d4, d5, d6, d7, d8), db = TD(b, d0, d1, d2, d3, d4, d5, d6, d7, d8);
                    float3 primary = EdgeIntersection(TP(face, u, v, a), TP(face, u, v, b), da, db);
                    float3 normal = OutwardNormal(primary);
                    // The Transvoxel table stores the low-resolution face in cache slots 7-8.
                    // Only the high-resolution side moves to the secondary position.
                    bool highResolutionSide = ((edge >> 8) & 15) <= 6;
                    float3 vertex = highResolutionSide ? SecondaryPosition(primary, normal) : primary;
                    vertices.Add(vertex); colors.Add(C(primary)); normals.Add(normal);
                }
                bool flip=transitionFlip[code] != 0; for(int i=0,n=transitionTriangleIndexCount[code];i<n;i+=3){int a=start+transitionIndices[code*36+i],b=start+transitionIndices[code*36+i+1],c=start+transitionIndices[code*36+i+2];AddOriented(a,flip?c:b,flip?b:c);}
            }
        }
        private float TD(int i,float a,float b,float c,float d,float e,float f,float g,float h,float j){switch(i){case 0:return a;case 1:return b;case 2:return c;case 3:return d;case 4:return e;case 5:return f;case 6:return g;case 7:return h;case 8:return j;case 9:return a;case 10:return c;case 11:return g;default:return j;}}
        private float3 TP(int face, int u, int v, int i)
        {
            int2 corner = transitionCornerOffsets[i];
            // The third Transvoxel table coordinate is topological. Transition
            // vertices are projected on the shared face and separated through
            // their secondary positions, matching the source implementation.
            return FP(face, u + corner.x * stride, v + corner.y * stride, 0f);
        }
        private float FD(int face,int u,int v){float3 p=FG(face,u,v,0f);return D((int)p.x,(int)p.y,(int)p.z);}
        private float3 FP(int face,int u,int v,float depth)=>FG(face,u,v,depth)*voxelSize+offset;
        private float3 FG(int face,int u,int v,float depth){switch(face){case 0:return new float3(depth,u,v);case 1:return new float3(resolution-depth,v,u);case 2:return new float3(v,depth,u);case 3:return new float3(u,resolution-depth,v);case 4:return new float3(u,v,depth);default:return new float3(v,u,resolution-depth);}}
        private float3 PrimaryCornerP(int x, int y, int z, int i) => new float3(x + ((i & 1) * stride), y + (((i >> 2) & 1) * stride), z + (((i >> 1) & 1) * stride)) * voxelSize + offset;
        private float3 SecondaryPosition(float3 point, float3 normal)
        {
            float width = stride * voxelSize * .5f;
            float3 local = (point - offset) / voxelSize;
            float3 delta = float3.zero;
            if ((faces & TransitionFaceMask.NegativeX) != 0 && math.abs(local.x) < .0001f) delta.x += width;
            if ((faces & TransitionFaceMask.PositiveX) != 0 && math.abs(local.x - resolution) < .0001f) delta.x -= width;
            if ((faces & TransitionFaceMask.NegativeY) != 0 && math.abs(local.y) < .0001f) delta.y += width;
            if ((faces & TransitionFaceMask.PositiveY) != 0 && math.abs(local.y - resolution) < .0001f) delta.y -= width;
            if ((faces & TransitionFaceMask.NegativeZ) != 0 && math.abs(local.z) < .0001f) delta.z += width;
            if ((faces & TransitionFaceMask.PositiveZ) != 0 && math.abs(local.z - resolution) < .0001f) delta.z -= width;
            if (math.all(delta == float3.zero)) return point;
            return point + delta - normal * math.dot(normal, delta);
        }
        private float CornerD(int x,int y,int z,int i)=>D(x+((i&1)*stride),y+(((i>>2)&1)*stride),z+(((i>>1)&1)*stride));
        private float D(int x,int y,int z) => density[x+(resolution+1)*(y+(resolution+1)*z)];
        private static float3 EdgeIntersection(float3 a, float3 b, float da, float db)
        {
            const float epsilon = .000001f;
            if (math.abs(da) <= epsilon) return a;
            if (math.abs(db) <= epsilon) return b;
            float denominator = da - db;
            return math.abs(denominator) <= epsilon ? (a + b) * .5f : math.lerp(a, b, math.clamp(da / denominator, 0f, 1f));
        }
        private void AddOriented(int a, int b, int c)
        {
            float3 pa = vertices[a], pb = vertices[b], pc = vertices[c];
            float3 cross = math.cross(pb - pa, pc - pa);
            if (math.lengthsq(cross) <= .0000000001f) return;
            float3 outward = OutwardNormal((pa + pb + pc) / 3f);
            if (math.dot(cross, outward) < 0f) { int swap = b; b = c; c = swap; }
            // Keep a single palette color across the face: interpolating different
            // material colors creates a blurred band at soil/rock boundaries.
            Color32 ca = colors[a], cb = colors[b], cc = colors[c];
            int ka = ColorKey(ca), kb = ColorKey(cb), kc = ColorKey(cc);
            Color32 faceColor = ka == kb || ka == kc ? ca : kb == kc ? cb
                : ka < kb && ka < kc ? ca : kb < kc ? cb : cc;
            AddColoredIndex(a, faceColor); AddColoredIndex(b, faceColor); AddColoredIndex(c, faceColor);
        }
        private static int ColorKey(Color32 color) => (color.r << 16) | (color.g << 8) | color.b;
        private void AddColoredIndex(int source, Color32 color)
        {
            if (ColorKey(colors[source]) == ColorKey(color)) { indices.Add(source); return; }
            // Original cell vertices are still used by later triangles. Copy only
            // conflicting colors so their geometry and normals remain unchanged.
            int index = vertices.Length;
            float3 position = vertices[source], normal = normals[source];
            vertices.Add(position); normals.Add(normal); colors.Add(color);
            indices.Add(index);
        }
        private float3 OutwardNormal(float3 point)
        {
            int x = math.clamp((int)math.round((point.x - offset.x) / voxelSize), 0, resolution), y = math.clamp((int)math.round((point.y - offset.y) / voxelSize), 0, resolution), z = math.clamp((int)math.round((point.z - offset.z) / voxelSize), 0, resolution);
            float dx = x == 0 ? D(0, y, z) - D(1, y, z) : x == resolution ? D(resolution - 1, y, z) - D(resolution, y, z) : D(x - 1, y, z) - D(x + 1, y, z);
            float dy = y == 0 ? D(x, 0, z) - D(x, 1, z) : y == resolution ? D(x, resolution - 1, z) - D(x, resolution, z) : D(x, y - 1, z) - D(x, y + 1, z);
            float dz = z == 0 ? D(x, y, 0) - D(x, y, 1) : z == resolution ? D(x, y, resolution - 1) - D(x, y, resolution) : D(x, y, z - 1) - D(x, y, z + 1);
            return math.normalizesafe(new float3(dx, dy, dz));
        }
        private Color32 C(float3 p){int x=math.clamp((int)math.round((p.x-offset.x)/voxelSize),0,resolution),y=math.clamp((int)math.round((p.y-offset.y)/voxelSize),0,resolution),z=math.clamp((int)math.round((p.z-offset.z)/voxelSize),0,resolution);byte m=materials[x+(resolution+1)*(y+(resolution+1)*z)];return TerrainMaterialPalette.ColorFor(m);}
    }
}
