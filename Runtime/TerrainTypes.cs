using System;
using UnityEngine;

namespace Humanier.Terrain
{
    public enum TerrainBiome { Grassland, Desert, RockyMountains }
    public enum TerrainBrushMode { Dig, Fill, Flatten }
    public enum TerrainEditStatus { Queued, Processing, Completed, Rejected, CacheFailure, Cancelled }

    [Serializable]
    public struct TerrainChunkId : IEquatable<TerrainChunkId>
    {
        public int x;
        public int y;
        public int z;

        public TerrainChunkId(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public bool Equals(TerrainChunkId other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is TerrainChunkId other && Equals(other);
        public override int GetHashCode() { unchecked { return ((x * 397) ^ y) * 397 ^ z; } }
        public override string ToString() => $"({x}, {y}, {z})";
    }

    [Serializable]
    public struct TerrainEditRequest
    {
        public TerrainBrushMode mode;
        public Vector3 worldCenter;
        [Min(0.1f)] public float radius;
        public float strength;
        public float flattenHeight;
        public byte material;
        /// <summary>
        /// Optional upper bound, in cubic metres, for solid volume added by this
        /// edit. A null value leaves the edit unbounded.
        /// </summary>
        public float? maxAddedSolidVolume;

        public static TerrainEditRequest Dig(Vector3 center, float radius, float strength = 1f) => new TerrainEditRequest
        { mode = TerrainBrushMode.Dig, worldCenter = center, radius = radius, strength = strength };
        public static TerrainEditRequest Fill(Vector3 center, float radius, float strength = 1f, byte material = 0, float? maxAddedSolidVolume = null) => new TerrainEditRequest
        { mode = TerrainBrushMode.Fill, worldCenter = center, radius = radius, strength = strength, material = material, maxAddedSolidVolume = maxAddedSolidVolume };
        public static TerrainEditRequest Flatten(Vector3 center, float radius, float height, float strength = 1f) => new TerrainEditRequest
        { mode = TerrainBrushMode.Flatten, worldCenter = center, radius = radius, flattenHeight = height, strength = strength };
    }

    public sealed class TerrainEditHandle
    {
        // Completed means the edited density is committed. Every affected chunk
        // still resident has its current mesh applied; unloaded chunks are saved
        // and rebuild when streaming loads them again. When collision generation
        // is enabled, resident meshes are also assigned to their MeshColliders.
        public TerrainEditStatus Status { get; internal set; } = TerrainEditStatus.Queued;
        public float Progress { get; internal set; }
        public string Error { get; internal set; }
        /// <summary>True once the edit's complete density/material transaction has committed.</summary>
        public bool DataCommitted { get; internal set; }
        /// <summary>Solid volume removed by the committed edit, in cubic metres.</summary>
        public float RemovedSolidVolume { get; internal set; }
        /// <summary>Solid volume added by the committed edit, in cubic metres.</summary>
        public float AddedSolidVolume { get; internal set; }
        /// <summary>Removed solid volume grouped by the terrain's rendered material palette.</summary>
        public TerrainMaterialVolumeBreakdown RemovedMaterialVolumes { get; internal set; }
        public event Action<TerrainEditHandle> Changed;
        internal void Notify() => Changed?.Invoke(this);
    }

    /// <summary>
    /// Removed solid volume grouped into the four material colors currently used
    /// by the low-poly terrain renderer. Material 0 and unknown ids use the
    /// renderer's default bucket (material 1).
    /// </summary>
    public readonly struct TerrainMaterialVolumeBreakdown
    {
        public readonly float Material1Volume;
        public readonly float Material2Volume;
        public readonly float Material3Volume;
        public readonly float Material4Volume;

        public TerrainMaterialVolumeBreakdown(float material1Volume, float material2Volume,
            float material3Volume, float material4Volume)
        {
            Material1Volume = material1Volume;
            Material2Volume = material2Volume;
            Material3Volume = material3Volume;
            Material4Volume = material4Volume;
        }

        public float TotalVolume => Material1Volume + Material2Volume + Material3Volume + Material4Volume;

        public float GetVolume(byte materialId)
        {
            switch (materialId)
            {
                case 2: return Material2Volume;
                case 3: return Material3Volume;
                case 4: return Material4Volume;
                default: return Material1Volume;
            }
        }
    }

    /// <summary>
    /// Compact, session-scoped description of a committed edit. Consumers that
    /// render a derived representation can rebuild it without owning voxel data.
    /// </summary>
    public readonly struct TerrainEditSummary
    {
        public readonly TerrainBrushMode mode;
        public readonly Vector3 worldCenter;
        public readonly float radius;
        public readonly float effectiveStrength;
        public readonly float flattenHeight;
        public readonly float removedSolidVolume;
        public readonly float addedSolidVolume;
        public readonly TerrainMaterialVolumeBreakdown removedMaterialVolumes;

        public TerrainEditSummary(TerrainBrushMode mode, Vector3 worldCenter, float radius,
            float effectiveStrength, float flattenHeight, float removedSolidVolume, float addedSolidVolume,
            TerrainMaterialVolumeBreakdown removedMaterialVolumes = default)
        {
            this.mode = mode;
            this.worldCenter = worldCenter;
            this.radius = radius;
            this.effectiveStrength = effectiveStrength;
            this.flattenHeight = flattenHeight;
            this.removedSolidVolume = removedSolidVolume;
            this.addedSolidVolume = addedSolidVolume;
            this.removedMaterialVolumes = removedMaterialVolumes;
        }
    }

    public struct TerrainRaycastHit
    {
        public Vector3 point;
        public Vector3 normal;
        public TerrainChunkId chunk;
        public Collider collider;
    }
}
