using UnityEngine;

namespace Humanier.Terrain
{
    [CreateAssetMenu(menuName = "Humanier/Terrain/World Settings", fileName = "TerrainWorldSettings")]
    public sealed class TerrainWorldSettings : ScriptableObject
    {
        [Header("World")]
        public int seed = 12345;
        [Min(0.1f)] public float voxelSize = 0.5f;
        [Range(8, 64)] public int chunkResolution = 32;
        [Min(16f)] public float viewDistance = 256f;
        [Min(1)] public int chunksBuiltPerFrame = 2;
        [Min(16)] public int maxQueuedChunks = 1024;
        [Min(1)] public int maxQueuedEdits = 64;
        [Tooltip("Distance in metres at which each coarser mesh level begins.")]
        public float[] lodDistances = { 64f, 128f, 256f };
        [Header("Protection")]
        [Range(25f, 40f)] public float minBedrockDepth = 25f;
        [Range(25f, 40f)] public float maxBedrockDepth = 40f;
        [Min(60f)] public float absoluteProtectionDepth = 60f;
        [Header("Cache")]
        [Min(32)] public int memoryCacheLimitMb = 512;
        public Material terrainMaterial;

        public float ChunkSize => chunkResolution * voxelSize;
        public int SampleResolution => chunkResolution + 1;
        public void OnValidate()
        {
            chunkResolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(chunkResolution, 8, 64));
            maxBedrockDepth = Mathf.Max(minBedrockDepth, maxBedrockDepth);
            absoluteProtectionDepth = Mathf.Max(60f, absoluteProtectionDepth);
            if (lodDistances == null || lodDistances.Length == 0) lodDistances = new[] { 64f, 128f, 256f };
        }
    }
}
