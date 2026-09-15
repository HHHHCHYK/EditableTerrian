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
            for (int z = 0; z <= Resolution; z++)
            for (int y = 0; y <= Resolution; y++)
            for (int x = 0; x <= Resolution; x++)
            {
                int index = Index(x, y, z);
                Vector3 point = WorldPoint(x, y, z);
                Density[index] = TerrainGenerator.InitialDensity(settings, point);
                Material[index] = TerrainGenerator.MaterialAt(settings, point.x, point.z);
            }
        }

        public Vector3 WorldPoint(int x, int y, int z) => new Vector3((Id.x * Resolution + x) * VoxelSize, (Id.y * Resolution + y) * VoxelSize, (Id.z * Resolution + z) * VoxelSize);
        public int Index(int x, int y, int z) => x + (Resolution + 1) * (y + (Resolution + 1) * z);
        public float GetDensity(int x, int y, int z) => Density[Index(x, y, z)];
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
            for (int z = 0; z <= Resolution; z++)
            for (int y = 0; y <= Resolution; y++)
            for (int x = 0; x <= Resolution; x++)
            {
                Vector3 point = WorldPoint(x, y, z);
                Vector3 delta = point - request.worldCenter;
                float sqr = delta.sqrMagnitude;
                if (sqr > radiusSquared || IsProtected(settings, point)) continue;
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
                if (request.mode == TerrainBrushMode.Fill && Density[index] > 0f) Material[index] = request.material;
                changed |= !Mathf.Approximately(before, Density[index]);
            }
            if (changed) { IsModified = true; Version++; }
            return changed;
        }

        private static bool IsProtected(TerrainWorldSettings settings, Vector3 point)
        {
            float surface = TerrainGenerator.SurfaceHeight(settings, point.x, point.z);
            return point.y <= TerrainGenerator.BedrockHeight(settings, point.x, point.z) || point.y <= surface - settings.absoluteProtectionDepth;
        }
    }
}
