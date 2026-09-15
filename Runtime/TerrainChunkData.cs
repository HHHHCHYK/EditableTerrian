using System;
using UnityEngine;

namespace Humanier.Terrain
{
    internal sealed class TerrainChunkData
    {
        public readonly TerrainChunkId Id;
        public readonly int Resolution;
        public readonly float VoxelSize;
        public readonly float[] Density;
        public readonly byte[] Material;
        public bool IsModified { get; private set; }
        public int Version { get; private set; }

        public TerrainChunkData(TerrainWorldSettings settings, TerrainChunkId id)
        {
            Id = id; Resolution = settings.chunkResolution; VoxelSize = settings.voxelSize;
            int sampleCount = (Resolution + 1) * (Resolution + 1) * (Resolution + 1);
            Density = new float[sampleCount]; Material = new byte[sampleCount];
            TerrainGenerator.PopulateChunk(settings, id, Resolution, VoxelSize, Density, Material);
        }

        public Vector3 WorldPoint(int x, int y, int z) => new Vector3((Id.x * Resolution + x) * VoxelSize, (Id.y * Resolution + y) * VoxelSize, (Id.z * Resolution + z) * VoxelSize);
        public int Index(int x, int y, int z) => x + (Resolution + 1) * (y + (Resolution + 1) * z);
        public float GetDensity(int x, int y, int z) => Density[Index(x, y, z)];
        public byte GetMaterial(int x, int y, int z) => Material[Index(x, y, z)];
        public float SampleDensity(Vector3 globalPoint)
        {
            float lx = globalPoint.x / VoxelSize - Id.x * Resolution;
            float ly = globalPoint.y / VoxelSize - Id.y * Resolution;
            float lz = globalPoint.z / VoxelSize - Id.z * Resolution;
            int x = Mathf.Clamp(Mathf.FloorToInt(lx), 0, Resolution - 1), y = Mathf.Clamp(Mathf.FloorToInt(ly), 0, Resolution - 1), z = Mathf.Clamp(Mathf.FloorToInt(lz), 0, Resolution - 1);
            float tx = Mathf.Clamp01(lx - x), ty = Mathf.Clamp01(ly - y), tz = Mathf.Clamp01(lz - z);
            float a = Mathf.Lerp(GetDensity(x, y, z), GetDensity(x + 1, y, z), tx);
            float b = Mathf.Lerp(GetDensity(x, y, z + 1), GetDensity(x + 1, y, z + 1), tx);
            float c = Mathf.Lerp(GetDensity(x, y + 1, z), GetDensity(x + 1, y + 1, z), tx);
            float d = Mathf.Lerp(GetDensity(x, y + 1, z + 1), GetDensity(x + 1, y + 1, z + 1), tx);
            return Mathf.Lerp(Mathf.Lerp(a, b, tz), Mathf.Lerp(c, d, tz), ty);
        }
        public void Restore(float[] density, byte[] material)
        {
            if (density == null || density.Length != Density.Length) return;
            Array.Copy(density, Density, Density.Length);
            if (material != null && material.Length == Material.Length) Array.Copy(material, Material, Material.Length);
            IsModified = true; Version++;
        }

        public bool Apply(TerrainWorldSettings settings, TerrainEditRequest request)
        {
            bool changed = false;
            float radius = Mathf.Clamp(request.radius, 0.1f, 64f);
            float strength = Mathf.Clamp01(request.strength <= 0f ? 1f : request.strength);
            float radiusSquared = radius * radius;
            for (int x = 0; x <= Resolution; x++)
            for (int z = 0; z <= Resolution; z++)
            {
                float worldX = (Id.x * Resolution + x) * VoxelSize;
                float worldZ = (Id.z * Resolution + z) * VoxelSize;
                TerrainColumnSample column = TerrainGenerator.SampleColumn(settings, worldX, worldZ);
                for (int y = 0; y <= Resolution; y++)
                {
                    Vector3 point = WorldPoint(x, y, z);
                    Vector3 delta = point - request.worldCenter;
                    float sqr = delta.sqrMagnitude;
                    if (sqr > radiusSquared || column.IsProtected(settings, point.y)) continue;
                    float distance = Mathf.Sqrt(sqr);
                    float falloff = 1f - distance / radius;
                    int index = Index(x, y, z);
                    float before = Density[index];
                    float target;
                    switch (request.mode)
                    {
                        case TerrainBrushMode.Dig: target = Mathf.Min(before, distance - radius); break;
                        case TerrainBrushMode.Fill: target = Mathf.Max(before, radius - distance); break;
                        default: target = request.flattenHeight - point.y; break;
                    }
                    Density[index] = Mathf.Lerp(before, target, falloff * strength);
                    if (request.mode == TerrainBrushMode.Fill && Density[index] > 0f) Material[index] = request.material == 0 ? column.SurfaceMaterial : request.material;
                    changed |= !Mathf.Approximately(before, Density[index]);
                }
            }
            if (changed) { IsModified = true; Version++; }
            return changed;
        }
    }
}
