using UnityEngine;

namespace Humanier.Terrain
{
    public static class TerrainGenerator
    {
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
            float total = 0f, amplitude = 1f, weight = 0f, frequency = 1f;
            for (int i = 0; i < octaves; i++)
            {
                total += ValueNoise(seed + i * 811, x * frequency, z * frequency) * amplitude;
                weight += amplitude;
                amplitude *= .5f;
                frequency *= 2f;
            }
            return total / weight;
        }

        private static float ValueNoise(int seed, float x, float z)
        {
            int xi = Mathf.FloorToInt(x), zi = Mathf.FloorToInt(z);
            float tx = Smooth01(x - xi), tz = Smooth01(z - zi);
            float a = Hash01(seed, xi, zi), b = Hash01(seed, xi + 1, zi);
            float c = Hash01(seed, xi, zi + 1), d = Hash01(seed, xi + 1, zi + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
        }

        private static float Hash01(int seed, int x, int z)
        {
            unchecked
            {
                uint h = (uint)seed;
                h ^= (uint)x * 0x9e3779b9u; h = (h << 13) | (h >> 19);
                h ^= (uint)z * 0x85ebca6bu; h *= 0xc2b2ae35u;
                return (h & 0x00ffffffu) / 16777215f;
            }
        }
        private static float Smooth01(float t) => t * t * (3f - 2f * t);
    }
}
