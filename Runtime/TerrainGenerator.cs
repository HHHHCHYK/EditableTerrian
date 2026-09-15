using System.Collections.Generic;
using UnityEngine;

namespace Humanier.Terrain
{
    public static class TerrainGenerator
    {
        private static readonly Dictionary<int, FastNoiseLite> Noises = new Dictionary<int, FastNoiseLite>();
        public static TerrainBiome SampleBiome(int seed, float x, float z)
        {
            float climate = Fractal(seed + 17, x * 0.0018f, z * 0.0018f, 3);
            float erosion = Fractal(seed + 71, x * 0.0025f, z * 0.0025f, 3);
            if (erosion > 0.60f) return TerrainBiome.RockyMountains;
            return climate < 0.42f ? TerrainBiome.Desert : TerrainBiome.Grassland;
        }

        public static float SurfaceHeight(TerrainWorldSettings settings, float x, float z)
        {
            int seed = settings.seed;
            float broad = (Fractal(seed, x * .006f, z * .006f, 4) - .5f) * 18f;
            float detail = (Fractal(seed + 37, x * .035f, z * .035f, 3) - .5f) * 3f;
            float mountainMask = Smooth01((Fractal(seed + 71, x * .0025f, z * .0025f, 3) - .57f) * 3f);
            float mountains = Mathf.Pow(Fractal(seed + 103, x * .012f, z * .012f, 5), 2.5f) * 42f * mountainMask;
            return broad + detail + mountains;
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

        public static byte MaterialAt(TerrainWorldSettings settings, float x, float z)
        {
            switch (SampleBiome(settings.seed, x, z))
            {
                case TerrainBiome.Desert: return 2;
                case TerrainBiome.RockyMountains: return 3;
                default: return 1;
            }
        }

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
