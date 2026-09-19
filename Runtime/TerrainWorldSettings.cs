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
        [Tooltip("Main-thread time budget for incremental surface-column planning.")]
        [Range(.25f, 4f)] public float streamingPlanningBudgetMs = 1.5f;
        [Min(16f)] public float nearUndergroundDistance = 64f;
        [Tooltip("Distance in metres at which each coarser mesh level begins.")]
        public float[] lodDistances = { 64f, 128f, 256f };
        [Tooltip("Minimum mesh LOD used for resident chunks. One step doubles the surface sampling stride without changing the density grid.")]
        [Range(0, 3)] public int minimumMeshLod;
        [Header("Low-poly Surface")]
        [Min(0f)] public float facetDetailAmplitude;
        [Min(0.5f)] public float facetDetailSpacing = 2f;
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
        [Tooltip("Budget reserved for the procedural surface-column cache. The visible 256m working set is kept when this budget allows it.")]
        [Min(8)] public int surfaceTileCacheBudgetMb = 32;
        public Material terrainMaterial;
        [Header("Far Heightfield")]
        public bool farHeightfieldEnabled;
        [Min(512f)] public float farHeightfieldCoverage = 4096f;
        [Min(32f)] public float farHeightfieldPatchSize = 512f;
        [Range(2, 32)] public int farHeightfieldResolution = 8;
        [Range(2, 64)] public int farHeightfieldNearResolution = 32;
        [Min(1)] public int maxFarPatchesBuiltPerFrame = 2;

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
            minimumMeshLod = Mathf.Clamp(minimumMeshLod, 0, 3);
            facetDetailAmplitude = Mathf.Max(0f, facetDetailAmplitude);
            facetDetailSpacing = Mathf.Max(0.5f, facetDetailSpacing);
            maxInFlightMeshBuilds = Mathf.Max(1, maxInFlightMeshBuilds);
            maxMeshReplacementsPerFrame = Mathf.Max(1, maxMeshReplacementsPerFrame);
            streamingPlanningBudgetMs = Mathf.Clamp(streamingPlanningBudgetMs, .25f, 4f);
            surfaceTileCacheBudgetMb = Mathf.Max(8, surfaceTileCacheBudgetMb);
            nearUndergroundDistance = Mathf.Max(16f, nearUndergroundDistance);
            surfaceMaterialDepth = Mathf.Max(.1f, surfaceMaterialDepth);
            farHeightfieldCoverage = Mathf.Max(512f, farHeightfieldCoverage);
            farHeightfieldPatchSize = Mathf.Max(32f, farHeightfieldPatchSize);
            farHeightfieldResolution = Mathf.Clamp(farHeightfieldResolution, 2, 32);
            farHeightfieldNearResolution = Mathf.Clamp(farHeightfieldNearResolution, farHeightfieldResolution, 64);
            maxFarPatchesBuiltPerFrame = Mathf.Max(1, maxFarPatchesBuiltPerFrame);
            if (grassland == null) grassland = new TerrainBiomeDefinition { broadAmplitude = 18f, detailAmplitude = 3f, mountainAmplitude = 2f, surfaceMaterial = 1, interiorMaterial = 4 };
            if (desert == null) desert = new TerrainBiomeDefinition { broadAmplitude = 7f, detailAmplitude = 1f, surfaceMaterial = 2, interiorMaterial = 2 };
            if (rockyMountains == null) rockyMountains = new TerrainBiomeDefinition { broadAmplitude = 24f, detailAmplitude = 5f, mountainAmplitude = 42f, surfaceMaterial = 3, interiorMaterial = 3 };
        }
    }
}
