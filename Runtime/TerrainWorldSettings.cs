using UnityEngine;

namespace Humanier.Terrain
{
    [System.Serializable]
    public sealed class TerrainBiomeDefinition
    {
        [Min(0f)] public float broadAmplitude = 18f;
        [Min(0f)] public float detailAmplitude = 3f;
        [Min(0f)] public float mountainAmplitude;
        public byte surfaceMaterial = 1;
        public byte interiorMaterial = 4;
    }

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
        [Min(1)] public int maxInFlightMeshBuilds = 8;
        [Min(1)] public int maxMeshReplacementsPerFrame = 2;
        [Tooltip("Distance in metres at which each coarser mesh level begins.")]
        public float[] lodDistances = { 64f, 128f, 256f };
        [Header("Protection")]
        [Range(25f, 40f)] public float minBedrockDepth = 25f;
        [Range(25f, 40f)] public float maxBedrockDepth = 40f;
        [Min(60f)] public float absoluteProtectionDepth = 60f;
        [Header("Biomes")]
        [Min(0.1f)] public float surfaceMaterialDepth = 2f;
        public TerrainBiomeDefinition grassland = new TerrainBiomeDefinition { broadAmplitude = 18f, detailAmplitude = 3f, mountainAmplitude = 2f, surfaceMaterial = 1, interiorMaterial = 4 };
        public TerrainBiomeDefinition desert = new TerrainBiomeDefinition { broadAmplitude = 7f, detailAmplitude = 1f, mountainAmplitude = 0f, surfaceMaterial = 2, interiorMaterial = 2 };
        public TerrainBiomeDefinition rockyMountains = new TerrainBiomeDefinition { broadAmplitude = 24f, detailAmplitude = 5f, mountainAmplitude = 42f, surfaceMaterial = 3, interiorMaterial = 3 };
        [Header("Cache")]
        [Min(32)] public int memoryCacheLimitMb = 512;
        public Material terrainMaterial;

        public float ChunkSize => chunkResolution * voxelSize;
        public int SampleResolution => chunkResolution + 1;
        public TerrainBiomeDefinition GetBiomeDefinition(TerrainBiome biome)
        {
            switch (biome)
            {
                case TerrainBiome.Desert: return desert ?? new TerrainBiomeDefinition { broadAmplitude = 7f, detailAmplitude = 1f, surfaceMaterial = 2, interiorMaterial = 2 };
                case TerrainBiome.RockyMountains: return rockyMountains ?? new TerrainBiomeDefinition { broadAmplitude = 24f, detailAmplitude = 5f, mountainAmplitude = 42f, surfaceMaterial = 3, interiorMaterial = 3 };
                default: return grassland ?? new TerrainBiomeDefinition { broadAmplitude = 18f, detailAmplitude = 3f, mountainAmplitude = 2f, surfaceMaterial = 1, interiorMaterial = 4 };
            }
        }
        public void OnValidate()
        {
            chunkResolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(chunkResolution, 8, 64));
            maxBedrockDepth = Mathf.Max(minBedrockDepth, maxBedrockDepth);
            absoluteProtectionDepth = Mathf.Max(60f, absoluteProtectionDepth);
            if (lodDistances == null || lodDistances.Length == 0) lodDistances = new[] { 64f, 128f, 256f };
            maxInFlightMeshBuilds = Mathf.Max(1, maxInFlightMeshBuilds);
            maxMeshReplacementsPerFrame = Mathf.Max(1, maxMeshReplacementsPerFrame);
            surfaceMaterialDepth = Mathf.Max(.1f, surfaceMaterialDepth);
            if (grassland == null) grassland = new TerrainBiomeDefinition { broadAmplitude = 18f, detailAmplitude = 3f, mountainAmplitude = 2f, surfaceMaterial = 1, interiorMaterial = 4 };
            if (desert == null) desert = new TerrainBiomeDefinition { broadAmplitude = 7f, detailAmplitude = 1f, surfaceMaterial = 2, interiorMaterial = 2 };
            if (rockyMountains == null) rockyMountains = new TerrainBiomeDefinition { broadAmplitude = 24f, detailAmplitude = 5f, mountainAmplitude = 42f, surfaceMaterial = 3, interiorMaterial = 3 };
        }
    }
}
