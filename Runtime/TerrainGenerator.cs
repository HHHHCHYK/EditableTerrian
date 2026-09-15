using System.Collections.Generic;
using UnityEngine;

namespace Humanier.Terrain
{
    internal readonly struct TerrainColumnSample
    {
        public readonly float SurfaceHeight;
        public readonly float BedrockHeight;
        public readonly TerrainBiome Biome;
        public readonly byte SurfaceMaterial;
        public readonly byte InteriorMaterial;

        public TerrainColumnSample(float surfaceHeight, float bedrockHeight, TerrainBiome biome, byte surfaceMaterial, byte interiorMaterial)
        {
            SurfaceHeight = surfaceHeight;
            BedrockHeight = bedrockHeight;
            Biome = biome;
            SurfaceMaterial = surfaceMaterial;
            InteriorMaterial = interiorMaterial;
        }

        public float InitialDensity(TerrainWorldSettings settings, float y)
        {
            float density = SurfaceHeight - y;
            return IsProtected(settings, y) ? Mathf.Max(1f, density) : density;
        }

        public byte MaterialAt(TerrainWorldSettings settings, float y) => SurfaceHeight - y <= settings.surfaceMaterialDepth ? SurfaceMaterial : InteriorMaterial;
        public bool IsProtected(TerrainWorldSettings settings, float y) => y <= BedrockHeight || y <= SurfaceHeight - settings.absoluteProtectionDepth;
    }

    public static class TerrainGenerator
    {
        private static readonly Dictionary<int, FastNoiseLite> Noises = new Dictionary<int, FastNoiseLite>();
        public static TerrainBiome SampleBiome(int seed, float x, float z)
        {
            GetBiomeWeights(seed, x, z, out float grassland, out float desert, out float mountains);
            return SelectBiome(grassland, desert, mountains);
        }

        public static float SurfaceHeight(TerrainWorldSettings settings, float x, float z)
        {
            return CalculateSurfaceHeight(settings, x, z, out _);
        }

        private static float CalculateSurfaceHeight(TerrainWorldSettings settings, float x, float z, out TerrainBiome biome)
        {
            int seed = settings.seed;
            GetBiomeWeights(seed, x, z, out float grasslandWeight, out float desertWeight, out float mountainWeight);
            biome = SelectBiome(grasslandWeight, desertWeight, mountainWeight);
            TerrainBiomeDefinition grassland = settings.GetBiomeDefinition(TerrainBiome.Grassland);
            TerrainBiomeDefinition desert = settings.GetBiomeDefinition(TerrainBiome.Desert);
            TerrainBiomeDefinition mountains = settings.GetBiomeDefinition(TerrainBiome.RockyMountains);
            float broadAmplitude = Blend(grassland.broadAmplitude, desert.broadAmplitude, mountains.broadAmplitude, grasslandWeight, desertWeight, mountainWeight);
            float detailAmplitude = Blend(grassland.detailAmplitude, desert.detailAmplitude, mountains.detailAmplitude, grasslandWeight, desertWeight, mountainWeight);
            float broad = (Fractal(seed, x * .006f, z * .006f, 4) - .5f) * broadAmplitude;
            float detail = (Fractal(seed + 37, x * .035f, z * .035f, 3) - .5f) * detailAmplitude;
            float mountainShape = Mathf.Pow(Fractal(seed + 103, x * .012f, z * .012f, 5), 2.5f);
            float mountainAmplitude = Blend(grassland.mountainAmplitude, desert.mountainAmplitude, mountains.mountainAmplitude, grasslandWeight, desertWeight, mountainWeight);
            return broad + detail + mountainShape * mountainAmplitude;
        }

        public static float BedrockHeight(TerrainWorldSettings settings, float x, float z)
        {
            return SampleColumn(settings, x, z).BedrockHeight;
        }

        public static float InitialDensity(TerrainWorldSettings settings, Vector3 world)
        {
            return SampleColumn(settings, world.x, world.z).InitialDensity(settings, world.y);
        }

        public static byte MaterialAt(TerrainWorldSettings settings, Vector3 world)
        {
            return SampleColumn(settings, world.x, world.z).MaterialAt(settings, world.y);
        }

        public static byte SurfaceMaterialAt(TerrainWorldSettings settings, float x, float z) => settings.GetBiomeDefinition(SampleBiome(settings.seed, x, z)).surfaceMaterial;

        internal static TerrainColumnSample SampleColumn(TerrainWorldSettings settings, float x, float z)
        {
            float surface = CalculateSurfaceHeight(settings, x, z, out TerrainBiome biome);
            float depth = Mathf.Lerp(settings.minBedrockDepth, settings.maxBedrockDepth, Fractal(settings.seed + 191, x * .009f, z * .009f, 3));
            TerrainBiomeDefinition definition = settings.GetBiomeDefinition(biome);
            return new TerrainColumnSample(surface, surface - depth, biome, definition.surfaceMaterial, definition.interiorMaterial);
        }

        internal static void PopulateChunk(TerrainWorldSettings settings, TerrainChunkId id, int resolution, float voxelSize, float[] density, byte[] material)
        {
            int samplesPerAxis = resolution + 1;
            for (int z = 0; z <= resolution; z++)
            for (int x = 0; x <= resolution; x++)
            {
                float worldX = (id.x * resolution + x) * voxelSize;
                float worldZ = (id.z * resolution + z) * voxelSize;
                TerrainColumnSample column = SampleColumn(settings, worldX, worldZ);
                for (int y = 0; y <= resolution; y++)
                {
                    float worldY = (id.y * resolution + y) * voxelSize;
                    int index = x + samplesPerAxis * (y + samplesPerAxis * z);
                    density[index] = column.InitialDensity(settings, worldY);
                    material[index] = column.MaterialAt(settings, worldY);
                }
            }
        }

        private static void GetBiomeWeights(int seed, float x, float z, out float grassland, out float desert, out float mountains)
        {
            float climate = Fractal(seed + 17, x * .0018f, z * .0018f, 3);
            float erosion = Fractal(seed + 71, x * .0025f, z * .0025f, 3);
            mountains = Smooth01((erosion - .52f) / .20f);
            desert = (1f - mountains) * (1f - Smooth01((climate - .34f) / .20f));
            grassland = Mathf.Max(0f, 1f - mountains - desert);
        }
        private static TerrainBiome SelectBiome(float grassland, float desert, float mountains)
        {
            if (mountains >= grassland && mountains >= desert) return TerrainBiome.RockyMountains;
            return desert > grassland ? TerrainBiome.Desert : TerrainBiome.Grassland;
        }
        private static float Blend(float grassland, float desert, float mountains, float grasslandWeight, float desertWeight, float mountainWeight) => grassland * grasslandWeight + desert * desertWeight + mountains * mountainWeight;

        private static float Fractal(int seed, float x, float z, int octaves)
        {
            int key = seed * 10 + octaves;
            if (!Noises.TryGetValue(key, out FastNoiseLite noise))
            {
                noise = new FastNoiseLite(seed);
                noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
                noise.SetFrequency(1f);
                noise.SetFractalType(FastNoiseLite.FractalType.FBm);
                noise.SetFractalOctaves(octaves);
                Noises.Add(key, noise);
            }
            return (noise.GetNoise(x, z) + 1f) * .5f;
        }
        private static float Smooth01(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }
    }
}
