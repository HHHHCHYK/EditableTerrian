using System.Collections.Generic;
using UnityEngine;

namespace Humanier.Terrain
{
    public static class TerrainGenerator
    {
        private static readonly Dictionary<int, FastNoiseLite> Noises = new Dictionary<int, FastNoiseLite>();
        public static TerrainBiome SampleBiome(int seed, float x, float z)
        {
            GetBiomeWeights(seed, x, z, out float grassland, out float desert, out float mountains);
            if (mountains >= grassland && mountains >= desert) return TerrainBiome.RockyMountains;
            return desert > grassland ? TerrainBiome.Desert : TerrainBiome.Grassland;
        }

        public static float SurfaceHeight(TerrainWorldSettings settings, float x, float z)
        {
            int seed = settings.seed;
            GetBiomeWeights(seed, x, z, out float grasslandWeight, out float desertWeight, out float mountainWeight);
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
            float depth = Mathf.Lerp(settings.minBedrockDepth, settings.maxBedrockDepth, Fractal(settings.seed + 191, x * .009f, z * .009f, 3));
            return SurfaceHeight(settings, x, z) - depth;
        }

        public static float InitialDensity(TerrainWorldSettings settings, Vector3 world)
        {
            float surface = SurfaceHeight(settings, world.x, world.z);
            float density = surface - world.y;
            float bedrock = BedrockHeight(settings, world.x, world.z);
            if (world.y <= bedrock || world.y <= surface - settings.absoluteProtectionDepth) return Mathf.Max(1f, density);
            return density;
        }

        public static byte MaterialAt(TerrainWorldSettings settings, Vector3 world)
        {
            TerrainBiomeDefinition biome = settings.GetBiomeDefinition(SampleBiome(settings.seed, world.x, world.z));
            return SurfaceHeight(settings, world.x, world.z) - world.y <= settings.surfaceMaterialDepth ? biome.surfaceMaterial : biome.interiorMaterial;
        }

        public static byte SurfaceMaterialAt(TerrainWorldSettings settings, float x, float z) => settings.GetBiomeDefinition(SampleBiome(settings.seed, x, z)).surfaceMaterial;

        private static void GetBiomeWeights(int seed, float x, float z, out float grassland, out float desert, out float mountains)
        {
            float climate = Fractal(seed + 17, x * .0018f, z * .0018f, 3);
            float erosion = Fractal(seed + 71, x * .0025f, z * .0025f, 3);
            mountains = Smooth01((erosion - .52f) / .20f);
            desert = (1f - mountains) * (1f - Smooth01((climate - .34f) / .20f));
            grassland = Mathf.Max(0f, 1f - mountains - desert);
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
