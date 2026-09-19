using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;

namespace Humanier.Terrain
{
    internal readonly struct TerrainSurfaceRange
    {
        public readonly float MinHeight;
        public readonly float MaxHeight;

        public TerrainSurfaceRange(float minHeight, float maxHeight)
        {
            MinHeight = minHeight;
            MaxHeight = maxHeight;
        }
    }

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

    internal sealed class TerrainGenerationContext
    {
        private const int MinimumColumnCacheCapacity = 512;
        private static readonly ProfilerMarker SurfaceRangeMarker = new ProfilerMarker("Terrain.SurfaceRange");
        private static readonly ProfilerMarker SurfaceTileMarker = new ProfilerMarker("Terrain.SurfaceTile.Build");
        private static readonly ProfilerMarker SurfaceTileCacheMarker = new ProfilerMarker("Terrain.SurfaceTile.Cache");
        private static readonly ProfilerMarker SurfaceColumnMarker = new ProfilerMarker("Terrain.Column.Surface");
        private static readonly ProfilerMarker CompleteColumnMarker = new ProfilerMarker("Terrain.Column.Complete");
        private static readonly ProfilerMarker PopulateMarker = new ProfilerMarker("Terrain.PopulateChunk");
        private readonly TerrainWorldSettings settings;
        private readonly TerrainGenerationSettingsKey settingsKey;
        private readonly TerrainNoiseSampler noise;
        private readonly int columnCacheCapacity;
        private readonly Dictionary<Vector2Int, TerrainColumnGrid> columns = new Dictionary<Vector2Int, TerrainColumnGrid>();
        private readonly LinkedList<Vector2Int> columnOrder = new LinkedList<Vector2Int>();
        private readonly Dictionary<Vector2Int, LinkedListNode<Vector2Int>> columnNodes = new Dictionary<Vector2Int, LinkedListNode<Vector2Int>>();
        private readonly Dictionary<Vector2Int, PendingSurfaceColumn> pendingColumns = new Dictionary<Vector2Int, PendingSurfaceColumn>();

        internal int SurfaceBuildCount { get; private set; }
        internal int FullBuildCount { get; private set; }
        internal int CachedColumnCount => columns.Count;
        internal static long EstimatedCacheCapacityBytes(TerrainWorldSettings settings)
        {
            long samples = (long)settings.SampleResolution * settings.SampleResolution;
            return RecommendedColumnCacheCapacity(settings) * samples * (sizeof(float) * 2L + sizeof(byte) * 3L);
        }

        internal TerrainGenerationContext(TerrainWorldSettings settings)
        {
            this.settings = settings != null ? settings : throw new System.ArgumentNullException(nameof(settings));
            settingsKey = new TerrainGenerationSettingsKey(settings);
            noise = new TerrainNoiseSampler(settings);
            columnCacheCapacity = RecommendedColumnCacheCapacity(settings);
        }

        internal int ColumnCacheCapacity => columnCacheCapacity;

        internal static int RecommendedColumnCacheCapacity(TerrainWorldSettings settings)
        {
            if (settings == null || settings.ChunkSize <= 0f) return MinimumColumnCacheCapacity;
            int radius = Mathf.CeilToInt(Mathf.Max(settings.viewDistance, settings.nearUndergroundDistance) / settings.ChunkSize) + 2;
            int side = radius * 2 + 1;
            long bytesPerColumn = (long)settings.SampleResolution * settings.SampleResolution * (sizeof(float) * 2L + sizeof(byte) * 3L);
            long budgetBytes = (long)Mathf.Max(8, settings.surfaceTileCacheBudgetMb) * 1024L * 1024L;
            long safeBytesPerColumn = bytesPerColumn > 0L ? bytesPerColumn : 1L;
            int budgetCapacity = (int)Mathf.Clamp((float)(budgetBytes / safeBytesPerColumn), MinimumColumnCacheCapacity, 4096);
            return Mathf.Clamp(Mathf.Max(MinimumColumnCacheCapacity, side * side), MinimumColumnCacheCapacity, budgetCapacity);
        }

        internal TerrainSurfaceRange SurfaceRange(TerrainChunkId id)
        {
            using var profileScope = SurfaceRangeMarker.Auto();
            return GetColumn(id).Range;
        }

        internal bool TryBuildSurfaceRange(TerrainChunkId id, float budgetMs, out TerrainSurfaceRange range)
        {
            using var profileScope = SurfaceTileMarker.Auto();
            ValidateSettings(settings.chunkResolution, settings.voxelSize);
            Vector2Int key = new Vector2Int(id.x, id.z);
            if (columns.TryGetValue(key, out TerrainColumnGrid existing))
            {
                using var cacheScope = SurfaceTileCacheMarker.Auto();
                TouchColumn(key);
                range = existing.Range;
                return true;
            }

            if (!pendingColumns.TryGetValue(key, out PendingSurfaceColumn pending))
            {
                pending = new PendingSurfaceColumn(new TerrainColumnGrid(settings.SampleResolution, key.x, key.y));
                pendingColumns.Add(key, pending);
            }

            float deadline = Time.realtimeSinceStartup + Mathf.Max(.01f, budgetMs) * .001f;
            int start = pending.cursor;
            int resolution = settings.chunkResolution;
            int samplesPerAxis = resolution + 1;
            while (pending.cursor < pending.totalSamples && (pending.cursor == start || Time.realtimeSinceStartup < deadline))
            {
                int index = pending.cursor++;
                int z = index / samplesPerAxis;
                int x = index - z * samplesPerAxis;
                float worldX = (key.x * resolution + x) * settings.voxelSize;
                float worldZ = (key.y * resolution + z) * settings.voxelSize;
                float surface = noise.SurfaceHeight(worldX, worldZ, out TerrainBiome biome);
                pending.column.SurfaceHeights[index] = surface;
                pending.column.Biomes[index] = (byte)biome;
                noise.GetMaterials(biome, out pending.column.SurfaceMaterials[index], out pending.column.InteriorMaterials[index]);
                pending.min = Mathf.Min(pending.min, surface);
                pending.max = Mathf.Max(pending.max, surface);
            }

            if (pending.cursor < pending.totalSamples)
            {
                range = default;
                return false;
            }

            pending.column.Range = new TerrainSurfaceRange(pending.min, pending.max);
            columns.Add(key, pending.column);
            TouchColumn(key);
            pendingColumns.Remove(key);
            SurfaceBuildCount++;
            TrimColumnCache();
            range = pending.column.Range;
            return true;
        }

        internal void Validate() => ValidateSettings(settings.chunkResolution, settings.voxelSize);

        internal void PopulateChunk(TerrainChunkId id, int resolution, float voxelSize, float[] density, byte[] material)
        {
            using var profileScope = PopulateMarker.Auto();
            ValidateSettings(resolution, voxelSize);
            TerrainColumnGrid column = GetColumn(id);
            EnsureFull(column);
            int samplesPerAxis = resolution + 1;
            for (int z = 0; z <= resolution; z++)
            for (int y = 0; y <= resolution; y++)
            {
                float worldY = (id.y * resolution + y) * voxelSize;
                int row = samplesPerAxis * (y + samplesPerAxis * z);
                int columnRow = z * samplesPerAxis;
                for (int x = 0; x <= resolution; x++)
                {
                    int columnIndex = columnRow + x;
                    float surface = column.SurfaceHeights[columnIndex];
                    float value = surface - worldY;
                    if (worldY <= column.BedrockHeights[columnIndex] || worldY <= surface - settings.absoluteProtectionDepth)
                        value = Mathf.Max(1f, value);
                    int index = row + x;
                    density[index] = value;
                    material[index] = surface - worldY <= settings.surfaceMaterialDepth
                        ? column.SurfaceMaterials[columnIndex]
                        : column.InteriorMaterials[columnIndex];
                }
            }
        }

        private TerrainColumnGrid GetColumn(TerrainChunkId id)
        {
            ValidateSettings(settings.chunkResolution, settings.voxelSize);
            var key = new Vector2Int(id.x, id.z);
            if (columns.TryGetValue(key, out TerrainColumnGrid existing))
            {
                TouchColumn(key);
                return existing;
            }
            if (pendingColumns.ContainsKey(key))
            {
                while (!TryBuildSurfaceRange(id, 8f, out _)) { }
                return columns[key];
            }
            TerrainColumnGrid created = BuildSurfaceColumn(key);
            columns.Add(key, created);
            TouchColumn(key);
            SurfaceBuildCount++;
            TrimColumnCache();
            return created;
        }

        private void TrimColumnCache()
        {
            while (columns.Count > columnCacheCapacity && columnOrder.Count > 0)
            {
                LinkedListNode<Vector2Int> oldest = columnOrder.First;
                columnOrder.RemoveFirst();
                columnNodes.Remove(oldest.Value);
                columns.Remove(oldest.Value);
            }
        }

        private void TouchColumn(Vector2Int key)
        {
            if (columnNodes.TryGetValue(key, out LinkedListNode<Vector2Int> node))
                columnOrder.Remove(node);
            columnNodes[key] = columnOrder.AddLast(key);
        }

        private TerrainColumnGrid BuildSurfaceColumn(Vector2Int key)
        {
            using var profileScope = SurfaceColumnMarker.Auto();
            int resolution = settings.chunkResolution;
            int samplesPerAxis = resolution + 1;
            var result = new TerrainColumnGrid(samplesPerAxis, key.x, key.y);
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int z = 0; z <= resolution; z++)
            for (int x = 0; x <= resolution; x++)
            {
                float worldX = (key.x * resolution + x) * settings.voxelSize;
                float worldZ = (key.y * resolution + z) * settings.voxelSize;
                int index = x + samplesPerAxis * z;
                float surface = noise.SurfaceHeight(worldX, worldZ, out TerrainBiome biome);
                result.SurfaceHeights[index] = surface;
                result.Biomes[index] = (byte)biome;
                noise.GetMaterials(biome, out result.SurfaceMaterials[index], out result.InteriorMaterials[index]);
                min = Mathf.Min(min, surface);
                max = Mathf.Max(max, surface);
            }
            result.Range = new TerrainSurfaceRange(min, max);
            return result;
        }

        private void EnsureFull(TerrainColumnGrid column)
        {
            if (column.FullReady) return;
            using var profileScope = CompleteColumnMarker.Auto();
            int resolution = settings.chunkResolution;
            int samplesPerAxis = resolution + 1;
            for (int z = 0; z <= resolution; z++)
            for (int x = 0; x <= resolution; x++)
            {
                int index = x + samplesPerAxis * z;
                float worldX = (column.X * resolution + x) * settings.voxelSize;
                float worldZ = (column.Z * resolution + z) * settings.voxelSize;
                float depth = Mathf.Lerp(settings.minBedrockDepth, settings.maxBedrockDepth, noise.Bedrock(worldX, worldZ));
                column.BedrockHeights[index] = column.SurfaceHeights[index] - depth;
            }
            column.FullReady = true;
            FullBuildCount++;
        }

        private void ValidateSettings(int resolution, float voxelSize)
        {
            if (resolution != settings.chunkResolution || voxelSize != settings.voxelSize || !settingsKey.Equals(new TerrainGenerationSettingsKey(settings)))
                throw new System.InvalidOperationException("Terrain generation settings changed after this world began generating. Reload the terrain world before applying generation changes.");
        }

        private sealed class TerrainColumnGrid
        {
            internal readonly int X;
            internal readonly int Z;
            internal readonly float[] SurfaceHeights;
            internal readonly byte[] Biomes;
            internal readonly float[] BedrockHeights;
            internal readonly byte[] SurfaceMaterials;
            internal readonly byte[] InteriorMaterials;
            internal TerrainSurfaceRange Range;
            internal bool FullReady;

            internal TerrainColumnGrid(int samplesPerAxis, int x = 0, int z = 0)
            {
                X = x;
                Z = z;
                int count = samplesPerAxis * samplesPerAxis;
                SurfaceHeights = new float[count];
                Biomes = new byte[count];
                BedrockHeights = new float[count];
                SurfaceMaterials = new byte[count];
                InteriorMaterials = new byte[count];
            }

        }

        private sealed class PendingSurfaceColumn
        {
            internal readonly TerrainColumnGrid column;
            internal readonly int totalSamples;
            internal int cursor;
            internal float min = float.MaxValue;
            internal float max = float.MinValue;

            internal PendingSurfaceColumn(TerrainColumnGrid column)
            {
                this.column = column;
                totalSamples = column.SurfaceHeights.Length;
            }
        }

        private sealed class TerrainNoiseSampler
        {
            private readonly TerrainWorldSettings settings;
            private readonly FastNoiseLite climate;
            private readonly FastNoiseLite erosion;
            private readonly FastNoiseLite broad;
            private readonly FastNoiseLite detail;
            private readonly FastNoiseLite mountain;
            private readonly FastNoiseLite bedrock;
            private readonly TerrainBiomeKey grassland;
            private readonly TerrainBiomeKey desert;
            private readonly TerrainBiomeKey mountains;

            internal TerrainNoiseSampler(TerrainWorldSettings settings)
            {
                this.settings = settings;
                climate = CreateNoise(settings.seed + 17, 3);
                erosion = CreateNoise(settings.seed + 71, 3);
                broad = CreateNoise(settings.seed, 4);
                detail = CreateNoise(settings.seed + 37, 3);
                mountain = CreateNoise(settings.seed + 103, 5);
                bedrock = CreateNoise(settings.seed + 191, 3);
                grassland = new TerrainBiomeKey(settings.GetBiomeDefinition(TerrainBiome.Grassland));
                desert = new TerrainBiomeKey(settings.GetBiomeDefinition(TerrainBiome.Desert));
                mountains = new TerrainBiomeKey(settings.GetBiomeDefinition(TerrainBiome.RockyMountains));
            }

            internal float SurfaceHeight(float x, float z, out TerrainBiome biome)
            {
                float climateValue = Unit(climate, x * .0018f, z * .0018f);
                float erosionValue = Unit(erosion, x * .0025f, z * .0025f);
                float mountainWeight = Smooth01((erosionValue - .52f) / .20f);
                float desertWeight = (1f - mountainWeight) * (1f - Smooth01((climateValue - .34f) / .20f));
                float grasslandWeight = Mathf.Max(0f, 1f - mountainWeight - desertWeight);
                biome = mountainWeight >= grasslandWeight && mountainWeight >= desertWeight
                    ? TerrainBiome.RockyMountains
                    : desertWeight > grasslandWeight ? TerrainBiome.Desert : TerrainBiome.Grassland;
                float broadAmplitude = Blend(grassland.Broad, desert.Broad, mountains.Broad, grasslandWeight, desertWeight, mountainWeight);
                float detailAmplitude = Blend(grassland.Detail, desert.Detail, mountains.Detail, grasslandWeight, desertWeight, mountainWeight);
                float broadValue = (Unit(broad, x * .006f, z * .006f) - .5f) * broadAmplitude;
                float detailValue = (Unit(detail, x * .035f, z * .035f) - .5f) * detailAmplitude;
                float mountainShape = Mathf.Pow(Unit(mountain, x * .012f, z * .012f), 2.5f);
                float mountainAmplitude = Blend(grassland.Mountain, desert.Mountain, mountains.Mountain, grasslandWeight, desertWeight, mountainWeight);
                return broadValue + detailValue + mountainShape * mountainAmplitude + TerrainGenerator.FacetSurfaceDetail(this.settings, x, z);
            }

            internal float Bedrock(float x, float z) => Unit(bedrock, x * .009f, z * .009f);

            internal void GetMaterials(TerrainBiome biome, out byte surface, out byte interior)
            {
                TerrainBiomeKey definition = biome == TerrainBiome.Desert ? desert : biome == TerrainBiome.RockyMountains ? mountains : grassland;
                surface = definition.Surface;
                interior = definition.Interior;
            }

            private static FastNoiseLite CreateNoise(int seed, int octaves)
            {
                var result = new FastNoiseLite(seed);
                result.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
                result.SetFrequency(1f);
                result.SetFractalType(FastNoiseLite.FractalType.FBm);
                result.SetFractalOctaves(octaves);
                return result;
            }

            private static float Unit(FastNoiseLite source, float x, float z) => (source.GetNoise(x, z) + 1f) * .5f;
            private static float Smooth01(float value) { value = Mathf.Clamp01(value); return value * value * (3f - 2f * value); }
            private static float Blend(float grass, float sand, float rock, float grassWeight, float sandWeight, float rockWeight)
                => grass * grassWeight + sand * sandWeight + rock * rockWeight;
        }

        private readonly struct TerrainGenerationSettingsKey : System.IEquatable<TerrainGenerationSettingsKey>
        {
            private readonly int seed;
            private readonly int resolution;
            private readonly float voxelSize;
            private readonly float minBedrockDepth;
            private readonly float maxBedrockDepth;
            private readonly float absoluteProtectionDepth;
            private readonly float surfaceMaterialDepth;
            private readonly float facetDetailAmplitude;
            private readonly float facetDetailSpacing;
            private readonly TerrainBiomeKey grassland;
            private readonly TerrainBiomeKey desert;
            private readonly TerrainBiomeKey mountains;

            internal TerrainGenerationSettingsKey(TerrainWorldSettings settings)
            {
                seed = settings.seed;
                resolution = settings.chunkResolution;
                voxelSize = settings.voxelSize;
                minBedrockDepth = settings.minBedrockDepth;
                maxBedrockDepth = settings.maxBedrockDepth;
                absoluteProtectionDepth = settings.absoluteProtectionDepth;
                surfaceMaterialDepth = settings.surfaceMaterialDepth;
                facetDetailAmplitude = settings.facetDetailAmplitude;
                facetDetailSpacing = settings.facetDetailSpacing;
                grassland = new TerrainBiomeKey(settings.GetBiomeDefinition(TerrainBiome.Grassland));
                desert = new TerrainBiomeKey(settings.GetBiomeDefinition(TerrainBiome.Desert));
                mountains = new TerrainBiomeKey(settings.GetBiomeDefinition(TerrainBiome.RockyMountains));
            }

            public bool Equals(TerrainGenerationSettingsKey other) => seed == other.seed && resolution == other.resolution &&
                voxelSize.Equals(other.voxelSize) && minBedrockDepth.Equals(other.minBedrockDepth) && maxBedrockDepth.Equals(other.maxBedrockDepth) &&
                absoluteProtectionDepth.Equals(other.absoluteProtectionDepth) && surfaceMaterialDepth.Equals(other.surfaceMaterialDepth) &&
                facetDetailAmplitude.Equals(other.facetDetailAmplitude) && facetDetailSpacing.Equals(other.facetDetailSpacing) &&
                grassland.Equals(other.grassland) && desert.Equals(other.desert) && mountains.Equals(other.mountains);
        }

        private readonly struct TerrainBiomeKey : System.IEquatable<TerrainBiomeKey>
        {
            private readonly float broad;
            private readonly float detail;
            private readonly float mountain;
            private readonly byte surface;
            private readonly byte interior;
            internal TerrainBiomeKey(TerrainBiomeDefinition definition)
            {
                broad = definition.broadAmplitude;
                detail = definition.detailAmplitude;
                mountain = definition.mountainAmplitude;
                surface = definition.surfaceMaterial;
                interior = definition.interiorMaterial;
            }
            public bool Equals(TerrainBiomeKey other) => broad.Equals(other.broad) && detail.Equals(other.detail) &&
                mountain.Equals(other.mountain) && surface == other.surface && interior == other.interior;
            internal float Broad => broad;
            internal float Detail => detail;
            internal float Mountain => mountain;
            internal byte Surface => surface;
            internal byte Interior => interior;
        }
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

        internal static float CalculateSurfaceHeight(TerrainWorldSettings settings, float x, float z, out TerrainBiome biome)
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
            return broad + detail + mountainShape * mountainAmplitude + FacetSurfaceDetail(settings, x, z);
        }

        public static float BedrockHeight(TerrainWorldSettings settings, float x, float z)
        {
            return SampleColumn(settings, x, z).BedrockHeight;
        }

        public static float InitialDensity(TerrainWorldSettings settings, Vector3 world)
        {
            return SampleColumn(settings, world.x, world.z).InitialDensity(settings, world.y);
        }

        internal static float FacetSurfaceDetail(TerrainWorldSettings settings, float x, float z)
        {
            if (settings == null || settings.facetDetailAmplitude <= 0f) return 0f;
            float spacing = Mathf.Max(.5f, settings.facetDetailSpacing);
            float gx = x / spacing, gz = z / spacing;
            int cellX = Mathf.FloorToInt(gx), cellZ = Mathf.FloorToInt(gz);
            float tx = gx - cellX, tz = gz - cellZ;
            float v00 = HashGrid2D(settings.seed + 911, cellX, cellZ);
            float v10 = HashGrid2D(settings.seed + 911, cellX + 1, cellZ);
            float v01 = HashGrid2D(settings.seed + 911, cellX, cellZ + 1);
            float v11 = HashGrid2D(settings.seed + 911, cellX + 1, cellZ + 1);
            float value;
            if (((cellX ^ cellZ) & 1) == 0)
                value = tx + tz <= 1f
                    ? v00 + tx * (v10 - v00) + tz * (v01 - v00)
                    : v11 + (1f - tx) * (v01 - v11) + (1f - tz) * (v10 - v11);
            else
                value = tx >= tz
                    ? v00 + tx * (v10 - v00) + tz * (v11 - v10)
                    : v00 + tz * (v01 - v00) + tx * (v11 - v01);
            return value * settings.facetDetailAmplitude;
        }

        // The distance overload lets staged and direct edits reuse their already computed brush distance.
        internal static float FacetEditOffset(TerrainWorldSettings settings, Vector3 point, Vector3 center, float radius)
        {
            return FacetEditOffset(settings, point, center, radius, Vector3.Distance(point, center));
        }

        internal static float FacetEditOffset(TerrainWorldSettings settings, Vector3 point, Vector3 center, float radius, float distance)
        {
            if (settings == null || settings.facetDetailAmplitude <= 0f || radius <= 0f) return 0f;
            float spacing = Mathf.Max(.5f, settings.facetDetailSpacing);
            float gx = point.x / spacing, gy = point.y / spacing, gz = point.z / spacing;
            int cellX = Mathf.FloorToInt(gx), cellY = Mathf.FloorToInt(gy), cellZ = Mathf.FloorToInt(gz);
            float fx = gx - cellX, fy = gy - cellY, fz = gz - cellZ;
            float value = TetrahedralDetail(settings.seed + 977, cellX, cellY, cellZ, fx, fy, fz);
            float radial = Mathf.Clamp01(distance / radius);
            float amplitude = Mathf.Min(settings.facetDetailAmplitude, radius * .15f);
            return value * amplitude * radial;
        }

        private static float HashGrid2D(int seed, int x, int z)
        {
            unchecked
            {
                uint h = (uint)seed;
                h ^= (uint)x * 374761393u;
                h = (h << 13) ^ h;
                h ^= (uint)z * 668265263u;
                h *= 1274126177u;
                return ((h & 0x00ffffffu) / 16777215f) * 2f - 1f;
            }
        }

        private static float TetrahedralDetail(int seed, int x, int y, int z, float fx, float fy, float fz)
        {
            int first = 0, second = 1, third = 2;
            float a = fx, b = fy, c = fz;
            if (a < b) { (a, b) = (b, a); (first, second) = (second, first); }
            if (b < c) { (b, c) = (c, b); (second, third) = (third, second); }
            if (a < b) { (a, b) = (b, a); (first, second) = (second, first); }

            float v0 = HashGrid3D(seed, x, y, z);
            float v1 = HashGrid3D(seed, x + (first == 0 ? 1 : 0), y + (first == 1 ? 1 : 0), z + (first == 2 ? 1 : 0));
            int abX = (first == 0 ? 1 : 0) + (second == 0 ? 1 : 0);
            int abY = (first == 1 ? 1 : 0) + (second == 1 ? 1 : 0);
            int abZ = (first == 2 ? 1 : 0) + (second == 2 ? 1 : 0);
            float v2 = HashGrid3D(seed, x + abX, y + abY, z + abZ);
            float v3 = HashGrid3D(seed, x + 1, y + 1, z + 1);
            return v0 * (1f - a) + v1 * (a - b) + v2 * (b - c) + v3 * c;
        }

        private static float HashGrid3D(int seed, int x, int y, int z)
        {
            unchecked
            {
                uint h = (uint)seed;
                h ^= (uint)x * 374761393u;
                h ^= (uint)y * 1103515245u;
                h ^= (uint)z * 668265263u;
                h ^= h >> 13;
                h *= 1274126177u;
                return ((h & 0x00ffffffu) / 16777215f) * 2f - 1f;
            }
        }

        public static byte MaterialAt(TerrainWorldSettings settings, Vector3 world)
        {
            return SampleColumn(settings, world.x, world.z).MaterialAt(settings, world.y);
        }

        public static byte SurfaceMaterialAt(TerrainWorldSettings settings, float x, float z) => settings.GetBiomeDefinition(SampleBiome(settings.seed, x, z)).surfaceMaterial;

        internal static TerrainSurfaceRange SurfaceRange(TerrainWorldSettings settings, TerrainChunkId id)
        {
            return new TerrainGenerationContext(settings).SurfaceRange(id);
        }

        internal static TerrainSurfaceRange SurfaceHeightRange(TerrainWorldSettings settings, TerrainChunkId id) => SurfaceRange(settings, id);

        internal static void GetSurfaceHeightRange(TerrainWorldSettings settings, TerrainChunkId id, out float minHeight, out float maxHeight)
        {
            TerrainSurfaceRange range = SurfaceRange(settings, id);
            minHeight = range.MinHeight;
            maxHeight = range.MaxHeight;
        }

        internal static TerrainColumnSample SampleColumn(TerrainWorldSettings settings, float x, float z)
        {
            float surface = CalculateSurfaceHeight(settings, x, z, out TerrainBiome biome);
            float depth = Mathf.Lerp(settings.minBedrockDepth, settings.maxBedrockDepth, Fractal(settings.seed + 191, x * .009f, z * .009f, 3));
            TerrainBiomeDefinition definition = settings.GetBiomeDefinition(biome);
            return new TerrainColumnSample(surface, surface - depth, biome, definition.surfaceMaterial, definition.interiorMaterial);
        }

        internal static void PopulateChunk(TerrainWorldSettings settings, TerrainChunkId id, int resolution, float voxelSize, float[] density, byte[] material)
        {
            new TerrainGenerationContext(settings).PopulateChunk(id, resolution, voxelSize, density, material);
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

        internal static float Fractal(int seed, float x, float z, int octaves)
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
