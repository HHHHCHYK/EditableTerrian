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

        public static TerrainEditRequest Dig(Vector3 center, float radius, float strength = 1f) => new TerrainEditRequest
        { mode = TerrainBrushMode.Dig, worldCenter = center, radius = radius, strength = strength };
        public static TerrainEditRequest Fill(Vector3 center, float radius, float strength = 1f, byte material = 0) => new TerrainEditRequest
        { mode = TerrainBrushMode.Fill, worldCenter = center, radius = radius, strength = strength, material = material };
        public static TerrainEditRequest Flatten(Vector3 center, float radius, float height, float strength = 1f) => new TerrainEditRequest
        { mode = TerrainBrushMode.Flatten, worldCenter = center, radius = radius, flattenHeight = height, strength = strength };
    }

    public sealed class TerrainEditHandle
    {
        public TerrainEditStatus Status { get; internal set; } = TerrainEditStatus.Queued;
        public float Progress { get; internal set; }
        public string Error { get; internal set; }
        public event Action<TerrainEditHandle> Changed;
        internal void Notify() => Changed?.Invoke(this);
    }

    public struct TerrainRaycastHit
    {
        public Vector3 point;
        public Vector3 normal;
        public TerrainChunkId chunk;
        public Collider collider;
    }
}
