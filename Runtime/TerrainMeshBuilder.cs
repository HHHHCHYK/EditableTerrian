using System;
using Unity.Burst;
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
        internal static TerrainMeshBuildRequest Schedule(TerrainChunkData data, Vector3 origin, int lod, TransitionFaceMask faces = TransitionFaceMask.None, int expectedVersion = -1)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (expectedVersion >= 0 && expectedVersion != data.Version) throw new InvalidOperationException("Density data changed before meshing began.");
            int stride = 1 << Mathf.Clamp(lod, 0, 3), samples = data.Density.Length, cells = data.Resolution / stride;
            var density = new NativeArray<float>(samples, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var materials = new NativeArray<byte>(samples, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            density.CopyFrom(data.Density); materials.CopyFrom(data.Material);
            var tables = new TransvoxelTableSnapshot(Allocator.TempJob);
            int capacity = math.max(16, cells * cells * cells * 12 + (data.Resolution / (stride * 2)) * (data.Resolution / (stride * 2)) * 12 * FaceCount(faces));
            var vertices = new NativeList<float3>(capacity, Allocator.TempJob);
            var colors = new NativeList<Color32>(capacity, Allocator.TempJob);
            var normals = new NativeList<float3>(capacity, Allocator.TempJob);
            var indices = new NativeList<int>(capacity * 3, Allocator.TempJob);
            var job = new TerrainMeshingJob { density = density, materials = materials,
                regularVertexCount = tables.regularVertexCount, regularTriangleIndexCount = tables.regularTriangleIndexCount, regularVertices = tables.regularVertices, regularIndices = tables.regularIndices,
                transitionVertexCount = tables.transitionVertexCount, transitionTriangleIndexCount = tables.transitionTriangleIndexCount, transitionVertices = tables.transitionVertices, transitionIndices = tables.transitionIndices, transitionFlip = tables.transitionFlip,
                vertices = vertices, colors = colors, normals = normals, indices = indices,
                resolution = data.Resolution, stride = stride, voxelSize = data.VoxelSize, offset = new float3(data.Id.x * data.Resolution * data.VoxelSize, data.Id.y * data.Resolution * data.VoxelSize, data.Id.z * data.Resolution * data.VoxelSize) - (float3)origin, faces = faces };
            return new TerrainMeshBuildRequest(data.Version, job.Schedule(), density, materials, tables, vertices, colors, normals, indices);
        }

        internal static Mesh Build(TerrainChunkData data, Vector3 origin, int lod) => Build(data, origin, lod, TransitionFaceMask.None);
        internal static Mesh Build(TerrainChunkData data, Vector3 origin, int lod, TransitionFaceMask faces) { using (var request = Schedule(data, origin, lod, faces, data.Version)) return request.Complete(); }
        private static int FaceCount(TransitionFaceMask faces) { int n = 0; for (int i = 0; i < 6; i++) if (((int)faces & (1 << i)) != 0) n++; return n; }
    }

    internal sealed class TerrainMeshBuildRequest : IDisposable
    {
        private JobHandle handle; private NativeArray<float> density; private NativeArray<byte> materials; private TransvoxelTableSnapshot tables;
        private NativeList<float3> vertices; private NativeList<Color32> colors; private NativeList<float3> normals; private NativeList<int> indices; private bool disposed;
        internal int Version { get; } internal bool IsCompleted => handle.IsCompleted;
        internal TerrainMeshBuildRequest(int version, JobHandle handle, NativeArray<float> density, NativeArray<byte> materials, TransvoxelTableSnapshot tables, NativeList<float3> vertices, NativeList<Color32> colors, NativeList<float3> normals, NativeList<int> indices)
        { Version = version; this.handle = handle; this.density = density; this.materials = materials; this.tables = tables; this.vertices = vertices; this.colors = colors; this.normals = normals; this.indices = indices; }
        internal Mesh Complete()
        {
            if (disposed) throw new ObjectDisposedException(nameof(TerrainMeshBuildRequest));
            handle.Complete(); if (vertices.Length == 0) return null;
            var mesh = new Mesh { indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            var output = new Vector3[vertices.Length]; var outputNormals = new Vector3[normals.Length]; for (int i = 0; i < output.Length; i++) { output[i] = vertices[i]; outputNormals[i] = normals[i]; }
            mesh.vertices = output; mesh.normals = outputNormals; mesh.SetColors(colors.AsArray()); mesh.SetIndices(indices.AsArray(), MeshTopology.Triangles, 0, true); mesh.RecalculateBounds(); return mesh;
        }
        public void Dispose()
        {
            if (disposed) return; handle.Complete(); if (density.IsCreated) density.Dispose(); if (materials.IsCreated) materials.Dispose(); tables.Dispose();
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
            for (int i = 0; i < count; i++) { ushort edge = regularVertices[code * 12 + i]; int a = (edge >> 4) & 15, b = edge & 15; float da = CornerD(x,y,z,a), db = CornerD(x,y,z,b); float3 p = math.lerp(CornerP(x,y,z,a), CornerP(x,y,z,b), math.clamp(da / (da - db), 0f, 1f)); vertices.Add(p); colors.Add(C(p)); normals.Add(OutwardNormal(p)); }
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
                for(int i=0;i<count;i++){ushort edge=transitionVertices[code*12+i];int a=(edge>>4)&15,b=edge&15;float da=TD(a,d0,d1,d2,d3,d4,d5,d6,d7,d8),db=TD(b,d0,d1,d2,d3,d4,d5,d6,d7,d8);float3 p=math.lerp(TP(face,u,v,a),TP(face,u,v,b),math.clamp(da/(da-db),0f,1f));vertices.Add(p);colors.Add(C(p));normals.Add(OutwardNormal(p));}
                bool flip=transitionFlip[code] != 0; for(int i=0,n=transitionTriangleIndexCount[code];i<n;i+=3){int a=start+transitionIndices[code*36+i],b=start+transitionIndices[code*36+i+1],c=start+transitionIndices[code*36+i+2];AddOriented(a,flip?c:b,flip?b:c);}
            }
        }
        private float TD(int i,float a,float b,float c,float d,float e,float f,float g,float h,float j){switch(i){case 0:return a;case 1:return b;case 2:return c;case 3:return d;case 4:return e;case 5:return f;case 6:return g;case 7:return h;case 8:return j;case 9:return a;case 10:return c;case 11:return g;default:return j;}}
        private float3 TP(int face,int u,int v,int i){int x=(i==1||i==4||i==7)?stride:(i==2||i==5||i==8||i==10||i==12?2*stride:0),y=(i==3||i==4||i==5)?stride:(i==6||i==7||i==8||i==11||i==12?2*stride:0);return FP(face,u+x,v+y,0);}
        private float FD(int face,int u,int v){int3 p=FG(face,u,v,0);return D(p.x,p.y,p.z);}
        private float3 FP(int face,int u,int v,int depth)=>(float3)FG(face,u,v,depth)*voxelSize+offset;
        private int3 FG(int face,int u,int v,int depth){switch(face){case 0:return new int3(depth,u,v);case 1:return new int3(resolution-depth,v,u);case 2:return new int3(v,depth,u);case 3:return new int3(u,resolution-depth,v);case 4:return new int3(u,v,depth);default:return new int3(v,u,resolution-depth);}}
        private float3 CornerP(int x,int y,int z,int i)=>new float3(x+((i&1)*stride),y+(((i>>2)&1)*stride),z+(((i>>1)&1)*stride))*voxelSize+offset;
        private float CornerD(int x,int y,int z,int i)=>D(x+((i&1)*stride),y+(((i>>2)&1)*stride),z+(((i>>1)&1)*stride));
        private float D(int x,int y,int z)=>density[x+(resolution+1)*(y+(resolution+1)*z)];
        private void AddOriented(int a, int b, int c)
        {
            float3 pa = vertices[a], pb = vertices[b], pc = vertices[c];
            float3 outward = OutwardNormal((pa + pb + pc) / 3f);
            if (math.dot(math.cross(pb - pa, pc - pa), outward) < 0f) { int swap = b; b = c; c = swap; }
            indices.Add(a); indices.Add(b); indices.Add(c);
        }
        private float3 OutwardNormal(float3 point)
        {
            int x = math.clamp((int)math.round((point.x - offset.x) / voxelSize), 1, resolution - 1), y = math.clamp((int)math.round((point.y - offset.y) / voxelSize), 1, resolution - 1), z = math.clamp((int)math.round((point.z - offset.z) / voxelSize), 1, resolution - 1);
            return math.normalizesafe(new float3(D(x - 1,y,z) - D(x + 1,y,z), D(x,y - 1,z) - D(x,y + 1,z), D(x,y,z - 1) - D(x,y,z + 1)));
        }
        private Color32 C(float3 p){int x=math.clamp((int)math.round((p.x-offset.x)/voxelSize),0,resolution),y=math.clamp((int)math.round((p.y-offset.y)/voxelSize),0,resolution),z=math.clamp((int)math.round((p.z-offset.z)/voxelSize),0,resolution);byte m=materials[x+(resolution+1)*(y+(resolution+1)*z)];return m==2?new Color32(199,148,74,255):m==3?new Color32(97,107,102,255):m==4?new Color32(117,87,58,255):new Color32(64,148,66,255);}
    }
}
