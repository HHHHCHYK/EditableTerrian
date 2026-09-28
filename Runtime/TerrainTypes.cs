using System;
using UnityEngine;

namespace Humanier.Terrain
{
    public enum TerrainBiome { Grassland, Desert, RockyMountains }
    public enum TerrainBrushMode { Dig, Fill, Flatten }
    public enum TerrainEditStatus { Queued, Processing, Completed, Rejected, CacheFailure, Cancelled }
    public enum TerrainTopology { Flat, InfiniteCurved }

    [Serializable]
    public struct TerrainChunkId : IEquatable<TerrainChunkId>
    {
        public long x;
        public int y;
        public long z;

        public TerrainChunkId(long x, int y, long z) { this.x = x; this.y = y; this.z = z; }
        public bool Equals(TerrainChunkId other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is TerrainChunkId other && Equals(other);
        public override int GetHashCode() { unchecked { return ((x.GetHashCode() * 397) ^ y) * 397 ^ z.GetHashCode(); } }
        public override string ToString() => $"({x}, {y}, {z})";
    }

    internal readonly struct TerrainColumnId : IEquatable<TerrainColumnId>
    {
        public readonly long x;
        public readonly long z;
        public TerrainColumnId(long x, long z) { this.x = x; this.z = z; }
        public bool Equals(TerrainColumnId other) => x == other.x && z == other.z;
        public override bool Equals(object obj) => obj is TerrainColumnId other && Equals(other);
        public override int GetHashCode() { unchecked { return (x.GetHashCode() * 397) ^ z.GetHashCode(); } }
    }

    /// <summary>Stable logical address for the endless world. Horizontal chunk coordinates never rebase.</summary>
    [Serializable]
    public readonly struct InfiniteWorldPosition : IEquatable<InfiniteWorldPosition>
    {
        public readonly long chunkX;
        public readonly long chunkZ;
        public readonly double localX;
        public readonly double localZ;
        public readonly double radialHeight;

        public InfiniteWorldPosition(long chunkX, long chunkZ, double localX, double localZ, double radialHeight)
        {
            this.chunkX = chunkX;
            this.chunkZ = chunkZ;
            this.localX = localX;
            this.localZ = localZ;
            this.radialHeight = radialHeight;
        }

        public double LogicalX(double chunkSize) => chunkX * chunkSize + localX;
        public double LogicalZ(double chunkSize) => chunkZ * chunkSize + localZ;
        public bool Equals(InfiniteWorldPosition other) => chunkX == other.chunkX && chunkZ == other.chunkZ &&
            localX.Equals(other.localX) && localZ.Equals(other.localZ) && radialHeight.Equals(other.radialHeight);
        public override bool Equals(object obj) => obj is InfiniteWorldPosition other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = chunkX.GetHashCode();
                hash = hash * 397 ^ chunkZ.GetHashCode();
                hash = hash * 397 ^ localX.GetHashCode();
                hash = hash * 397 ^ localZ.GetHashCode();
                return hash * 397 ^ radialHeight.GetHashCode();
            }
        }
    }

    public readonly struct CurvedWorldFrameShift
    {
        public readonly Vector3 Pivot;
        public readonly Quaternion Rotation;
        public readonly Vector3 Translation;

        public CurvedWorldFrameShift(Vector3 pivot, Quaternion rotation)
        {
            Pivot = pivot;
            Rotation = rotation;
            Translation = pivot - rotation * pivot;
        }

        private CurvedWorldFrameShift(Quaternion rotation, Vector3 translation)
        {
            Pivot = Vector3.zero;
            Rotation = rotation;
            Translation = translation;
        }

        public Vector3 TransformPoint(Vector3 point) => Rotation * point + Translation;
        public Vector3 TransformDirection(Vector3 direction) => Rotation * direction;
        public Vector3 RelocationDelta => Rotation == Quaternion.identity ? Translation : Vector3.zero;

        /// <summary>Returns the rigid transform produced by applying this shift and then <paramref name="next"/>.</summary>
        public CurvedWorldFrameShift Then(CurvedWorldFrameShift next) => new CurvedWorldFrameShift(
            next.Rotation * Rotation,
            next.Rotation * Translation + next.Translation);
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
