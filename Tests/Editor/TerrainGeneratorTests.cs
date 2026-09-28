using NUnit.Framework;
using UnityEngine;
using System.IO;
using System.Reflection;

namespace Humanier.Terrain.Tests
{
    public sealed class TerrainGeneratorTests
    {
        private TerrainWorldSettings settings;
        [SetUp] public void SetUp() { settings = ScriptableObject.CreateInstance<TerrainWorldSettings>(); settings.seed = 42; }
        [TearDown] public void TearDown() { Object.DestroyImmediate(settings); }
        [Test] public void SameSeedAndCoordinatesProduceSameSurface() => Assert.AreEqual(TerrainGenerator.SurfaceHeight(settings, 321.25f, -19.5f), TerrainGenerator.SurfaceHeight(settings, 321.25f, -19.5f));
        [Test]
        public void SphericalCapAddsTheExpectedHeightAndStaysFiniteOutsideTheRadius()
        {
            const float radius = 100f;
            settings.sphericalCapRadius = radius;
            float flatCenter = TerrainGenerator.SurfaceHeight(settings, 0f, 0f);
            float flatMiddle = TerrainGenerator.SurfaceHeight(settings, 50f, 0f);
            settings.sphericalCapEnabled = true;

            Assert.AreEqual(flatCenter, TerrainGenerator.SurfaceHeight(settings, 0f, 0f), .0001f);
            Assert.AreEqual(flatMiddle + Mathf.Sqrt(radius * radius - 50f * 50f) - radius,
                TerrainGenerator.SurfaceHeight(settings, 50f, 0f), .0001f);

            settings.sphericalCapEnabled = false;
            float flatOutside = TerrainGenerator.SurfaceHeight(settings, radius + 10f, 0f);
            settings.sphericalCapEnabled = true;
            float outside = TerrainGenerator.SurfaceHeight(settings, radius + 10f, 0f);
            Assert.IsFalse(float.IsNaN(outside));
            Assert.IsFalse(float.IsInfinity(outside));
            Assert.AreEqual(flatOutside + .001f - radius, outside, .001f);
        }
        [Test]
        public void SphericalCapStaticAndCachedGenerationShareSurfaceRangeAndSamples()
        {
            settings.sphericalCapEnabled = true;
            settings.sphericalCapRadius = 96f;
            var id = new TerrainChunkId(2, -1, -3);
            TerrainSurfaceRange directRange = TerrainGenerator.SurfaceRange(settings, id);
            var context = new TerrainGenerationContext(settings);
            Assert.IsTrue(context.TryBuildSurfaceRange(id, 1000f, out TerrainSurfaceRange range));
            var cached = new TerrainChunkData(settings, id, context, true);
            TerrainChunkData staticGeneration = CreateReferenceChunk(id);

            AssertChunkDataBitwiseEqual(staticGeneration, cached);
            Assert.AreEqual(directRange.MinHeight, range.MinHeight);
            Assert.AreEqual(directRange.MaxHeight, range.MaxHeight);
            Assert.LessOrEqual(range.MinHeight, range.MaxHeight);
            for (int z = 0; z <= settings.chunkResolution; z++)
            for (int x = 0; x <= settings.chunkResolution; x++)
            {
                float worldX = (id.x * settings.chunkResolution + x) * settings.voxelSize;
                float worldZ = (id.z * settings.chunkResolution + z) * settings.voxelSize;
                float height = TerrainGenerator.SurfaceHeight(settings, worldX, worldZ);
                Assert.That(height, Is.InRange(range.MinHeight - .0001f, range.MaxHeight + .0001f));
            }
        }
        [Test]
        public void SphericalCapSettingsArePartOfTheGenerationCacheKey()
        {
            var context = new TerrainGenerationContext(settings);
            context.SurfaceRange(new TerrainChunkId(0, 0, 0));
            settings.sphericalCapEnabled = true;
            Assert.Throws<System.InvalidOperationException>(() => context.SurfaceRange(new TerrainChunkId(1, 0, 0)));

            context = new TerrainGenerationContext(settings);
            context.SurfaceRange(new TerrainChunkId(0, 0, 0));
            settings.sphericalCapRadius += 10f;
            Assert.Throws<System.InvalidOperationException>(() => context.SurfaceRange(new TerrainChunkId(1, 0, 0)));
        }
        [Test]
        public void SphereCenterAndUpDirectionFollowWorldOriginOffset()
        {
            settings.sphericalCapEnabled = true;
            settings.sphericalCapRadius = 100f;
            var worldObject = new GameObject("Spherical terrain up direction");
            TerrainWorld world = worldObject.AddComponent<TerrainWorld>();
            SetField(world, "settings", settings);
            Vector3 offset = new Vector3(120f, 30f, -40f);
            world.SetOriginOffset(offset);

            Assert.AreEqual(new Vector3(-120f, -130f, 40f), world.SphereCenter);
            Assert.AreEqual(Vector3.up, world.SampleUpDirection(new Vector3(-120f, -30f, 40f)));
            Assert.AreEqual(Vector3.right, world.SampleUpDirection(new Vector3(-20f, -130f, 40f)));
            Object.DestroyImmediate(worldObject);
        }
        [Test]
        public void FacetSurfaceDetailIsDeterministicAndBounded()
        {
            settings.facetDetailAmplitude = .15f;
            settings.facetDetailSpacing = 2f;
            float first = TerrainGenerator.FacetSurfaceDetail(settings, -3.25f, 4.75f);
            float second = TerrainGenerator.FacetSurfaceDetail(settings, -3.25f, 4.75f);
            Assert.AreEqual(first, second);
            for (int z = -8; z <= 8; z++)
            for (int x = -8; x <= 8; x++)
                Assert.That(Mathf.Abs(TerrainGenerator.FacetSurfaceDetail(settings, x * .5f, z * .5f)), Is.LessThanOrEqualTo(settings.facetDetailAmplitude));
        }
        [Test]
        public void FacetSurfaceDetailStaysContinuousAcrossNegativeCellBoundaries()
        {
            settings.facetDetailAmplitude = .15f;
            settings.facetDetailSpacing = 2f;
            const float epsilon = .0001f;
            foreach (float edge in new[] { -4f, -2f, 0f, 2f, 4f })
            {
                float acrossX = TerrainGenerator.FacetSurfaceDetail(settings, edge - epsilon, .75f) -
                    TerrainGenerator.FacetSurfaceDetail(settings, edge + epsilon, .75f);
                float acrossZ = TerrainGenerator.FacetSurfaceDetail(settings, .75f, edge - epsilon) -
                    TerrainGenerator.FacetSurfaceDetail(settings, .75f, edge + epsilon);
                Assert.That(Mathf.Abs(acrossX), Is.LessThan(.0001f), $"X boundary at {edge} has a visible jump.");
                Assert.That(Mathf.Abs(acrossZ), Is.LessThan(.0001f), $"Z boundary at {edge} has a visible jump.");
            }
        }
        [Test]
        public void FacetEditOffsetIsZeroAtBrushCenterAndBounded()
        {
            settings.facetDetailAmplitude = .15f;
            settings.facetDetailSpacing = 2f;
            Vector3 center = new Vector3(-1.25f, .75f, 2.5f);
            Assert.AreEqual(0f, TerrainGenerator.FacetEditOffset(settings, center, center, 2f));
            for (int i = 0; i < 32; i++)
            {
                Vector3 point = center + new Vector3((i % 4) * .25f, ((i / 4) % 4) * .2f, (i / 16) * .35f);
                Assert.That(Mathf.Abs(TerrainGenerator.FacetEditOffset(settings, point, center, 2f)), Is.LessThanOrEqualTo(settings.facetDetailAmplitude));
            }
        }
        [Test] public void BedrockDepthStaysWithinConfiguredRange()
        {
            for (int x = -100; x <= 100; x += 10)
            {
                float depth = TerrainGenerator.SurfaceHeight(settings, x, 3) - TerrainGenerator.BedrockHeight(settings, x, 3);
                Assert.That(depth, Is.InRange(settings.minBedrockDepth, settings.maxBedrockDepth));
            }
        }
        [Test] public void ProtectedDepthIsAlwaysSolid()
        {
            float surface = TerrainGenerator.SurfaceHeight(settings, 11, -23);
            Assert.Greater(TerrainGenerator.InitialDensity(settings, new Vector3(11, surface - 60.1f, -23)), 0f);
        }
        [Test]
        public void BiomeDefinitionsControlSurfaceAndInteriorMaterials()
        {
            settings.grassland.surfaceMaterial = 11;
            settings.grassland.interiorMaterial = 12;
            settings.desert.surfaceMaterial = 21;
            settings.desert.interiorMaterial = 22;
            settings.rockyMountains.surfaceMaterial = 31;
            settings.rockyMountains.interiorMaterial = 32;
            for (int x = -256; x <= 256; x += 64)
            for (int z = -256; z <= 256; z += 64)
            {
                TerrainBiome biome = TerrainGenerator.SampleBiome(settings.seed, x, z);
                TerrainBiomeDefinition definition = settings.GetBiomeDefinition(biome);
                float surface = TerrainGenerator.SurfaceHeight(settings, x, z);
                Assert.AreEqual(definition.surfaceMaterial, TerrainGenerator.MaterialAt(settings, new Vector3(x, surface - .1f, z)));
                Assert.AreEqual(definition.interiorMaterial, TerrainGenerator.MaterialAt(settings, new Vector3(x, surface - settings.surfaceMaterialDepth - 1f, z)));
            }
        }
        [Test]
        public void DefaultFillUsesLocalSurfaceMaterial()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 6, 0));
            Vector3 point = data.WorldPoint(0, 0, 0);
            Assert.IsTrue(data.Apply(settings, TerrainEditRequest.Fill(point, 1f)));
            Assert.AreEqual(TerrainGenerator.SurfaceMaterialAt(settings, point.x, point.z), data.GetMaterial(0, 0, 0));
        }
        [Test]
        public void PrecomputedChunkColumnsMatchDensityAndMaterialQueries()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(-1, 0, 1));
            foreach (Vector3Int sample in new[] { new Vector3Int(0, 0, 0), new Vector3Int(16, 12, 21), new Vector3Int(32, 32, 32) })
            {
                Vector3 point = data.WorldPoint(sample.x, sample.y, sample.z);
                Assert.AreEqual(TerrainGenerator.InitialDensity(settings, point), data.GetDensity(sample.x, sample.y, sample.z));
                Assert.AreEqual(TerrainGenerator.MaterialAt(settings, point), data.GetMaterial(sample.x, sample.y, sample.z));
            }
        }
        [Test]
        public void SharedGenerationContextReusesOneColumnAndPreservesChunkSamples()
        {
            var context = new TerrainGenerationContext(settings);
            var lowerId = new TerrainChunkId(-2, -1, 3);
            var upperId = new TerrainChunkId(-2, 2, 3);
            TerrainSurfaceRange range = context.SurfaceRange(lowerId);
            var optimizedLower = new TerrainChunkData(settings, lowerId, context, true);
            var optimizedUpper = new TerrainChunkData(settings, upperId, context, true);
            TerrainChunkData referenceLower = CreateReferenceChunk(lowerId);
            TerrainChunkData referenceUpper = CreateReferenceChunk(upperId);

            Assert.AreEqual(1, context.SurfaceBuildCount);
            Assert.AreEqual(1, context.FullBuildCount);
            Assert.AreEqual(1, context.CachedColumnCount);
            Assert.LessOrEqual(range.MinHeight, range.MaxHeight);
            AssertChunkDataBitwiseEqual(referenceLower, optimizedLower);
            AssertChunkDataBitwiseEqual(referenceUpper, optimizedUpper);
        }
        [Test]
        public void SharedGenerationContextRejectsRuntimeSettingMutation()
        {
            var context = new TerrainGenerationContext(settings);
            context.SurfaceRange(new TerrainChunkId(0, 0, 0));
            settings.seed++;
            Assert.Throws<System.InvalidOperationException>(() => context.SurfaceRange(new TerrainChunkId(1, 0, 0)));
        }
        [Test]
        public void SurfaceColumnCacheKeepsThe256MetreWorkingSetWithinThe32MbBudget()
        {
            settings.viewDistance = 256f;
            settings.surfaceTileCacheBudgetMb = 32;
            int horizontal = Mathf.CeilToInt(settings.viewDistance / settings.ChunkSize);
            int visibleColumns = 0;
            for (int z = -horizontal; z <= horizontal; z++)
            for (int x = -horizontal; x <= horizontal; x++)
                if (x * x + z * z <= horizontal * horizontal) visibleColumns++;

            int capacity = TerrainGenerationContext.RecommendedColumnCacheCapacity(settings);
            Assert.GreaterOrEqual(capacity, visibleColumns);
            Assert.LessOrEqual(TerrainGenerationContext.EstimatedCacheCapacityBytes(settings), 32L * 1024L * 1024L);
        }
        [Test]
        public void RaycastOnlyReturnsChunksOwnedByThisWorld()
        {
            var firstObject = new GameObject("First terrain world");
            var secondObject = new GameObject("Second terrain world");
            var first = firstObject.AddComponent<TerrainWorld>();
            var second = secondObject.AddComponent<TerrainWorld>();
            GameObject otherChunk = CreateRaycastChunk("Other terrain chunk", new Vector3(0f, 0f, 2f), second);
            GameObject ownChunk = CreateRaycastChunk("Owned terrain chunk", new Vector3(0f, 0f, 4f), first);
            Physics.SyncTransforms();

            Assert.IsTrue(first.TryRaycast(new Ray(Vector3.zero, Vector3.forward), 10f, out TerrainRaycastHit hit, 1 << 8));
            Assert.AreSame(ownChunk.GetComponent<Collider>(), hit.collider);

            Object.DestroyImmediate(otherChunk);
            Object.DestroyImmediate(ownChunk);
            Object.DestroyImmediate(firstObject);
            Object.DestroyImmediate(secondObject);
        }
        [Test]
        public void ConfigureBeforeFirstUpdateUsesTheProvidedRuntimeSettings()
        {
            TerrainWorldSettings runtimeSettings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            runtimeSettings.seed = 9876;
            GameObject focusObject = new GameObject("Terrain configure focus");
            GameObject worldObject = new GameObject("Terrain configured before update");
            TerrainWorld world = worldObject.AddComponent<TerrainWorld>();

            Assert.DoesNotThrow(() => world.Configure(runtimeSettings, focusObject.transform));
            Assert.AreSame(runtimeSettings, world.Settings);
            Assert.AreEqual(9876, world.Settings.seed);

            Object.DestroyImmediate(worldObject);
            Object.DestroyImmediate(focusObject);
            Object.DestroyImmediate(runtimeSettings);
        }
        [Test]
        public void ConfigureRejectsAWorldThatAlreadyCreatedGenerationState()
        {
            TerrainWorldSettings replacement = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            var worldObject = new GameObject("Terrain configure after query");
            TerrainWorld world = worldObject.AddComponent<TerrainWorld>();

            world.SampleSurfaceHeight(Vector3.zero);

            Assert.Throws<System.InvalidOperationException>(() => world.Configure(replacement, null));
            Object.DestroyImmediate(worldObject);
            Object.DestroyImmediate(replacement);
        }
        [Test]
        public void WorldQueriesAndEditsRejectGenerationSettingMutation()
        {
            var worldObject = new GameObject("Terrain settings mutation guard");
            TerrainWorld world = worldObject.AddComponent<TerrainWorld>();
            SetField(world, "settings", settings);
            world.SampleSurfaceHeight(Vector3.zero);
            settings.seed++;

            Assert.Throws<System.InvalidOperationException>(() => world.SampleBiome(Vector3.zero));
            Assert.Throws<System.InvalidOperationException>(() => world.SampleSurfaceHeight(Vector3.zero));
            Assert.Throws<System.InvalidOperationException>(() => world.SampleGeneratedDensity(Vector3.zero));
            Assert.Throws<System.InvalidOperationException>(() => world.RequestEdit(TerrainEditRequest.Dig(Vector3.zero, 1f)));
            Object.DestroyImmediate(worldObject);
        }
        [Test]
        public void StreamingPlanStaysWithinConfiguredDensityCapacity()
        {
            settings.viewDistance = settings.ChunkSize * 2f;
            settings.nearUndergroundDistance = settings.ChunkSize;
            var worldObject = new GameObject("Terrain streaming plan");
            var world = worldObject.AddComponent<TerrainWorld>();
            Invoke(world, "BuildStreamingPlan", new TerrainChunkId(0, 0, 0));
            var plan = (System.Collections.ICollection)GetField(world, "streamingPlan");
            int maxResident = (int)Invoke(world, "MaxResidentChunkCount");
            Assert.LessOrEqual(plan.Count, maxResident);
            Object.DestroyImmediate(worldObject);
        }
        [Test]
        public void StreamingPlanIncludesNegativeHeightSurfaceChunks()
        {
            Vector2Int column = FindNegativeSurfaceColumn();
            float surface = TerrainGenerator.SurfaceHeight(settings, column.x, column.y);
            int columnX = Mathf.FloorToInt(column.x / settings.ChunkSize);
            int columnZ = Mathf.FloorToInt(column.y / settings.ChunkSize);
            settings.viewDistance = settings.ChunkSize * 2f;
            settings.nearUndergroundDistance = settings.ChunkSize;
            var worldObject = new GameObject("Terrain surface streaming plan");
            var world = worldObject.AddComponent<TerrainWorld>();
            SetField(world, "settings", settings);
            Invoke(world, "BuildStreamingPlan", new TerrainChunkId(columnX, 1, columnZ));
            var plan = (System.Collections.IEnumerable)GetField(world, "streamingPlan");
            var surfaceId = new TerrainChunkId(columnX, Mathf.FloorToInt(surface / settings.ChunkSize), columnZ);
            Assert.IsTrue(ContainsChunk(plan, surfaceId));
            Object.DestroyImmediate(worldObject);
        }
        [Test]
        public void SurfaceRangeCoversEveryVoxelColumnWithBoundarySafety()
        {
            var id = new TerrainChunkId(3, 0, -2);
            TerrainSurfaceRange range = TerrainGenerator.SurfaceRange(settings, id);
            float safety = settings.voxelSize;
            int minY = Mathf.FloorToInt((range.MinHeight - safety) / settings.ChunkSize);
            int maxY = Mathf.FloorToInt((range.MaxHeight + safety) / settings.ChunkSize);

            for (int z = 0; z <= settings.chunkResolution; z++)
            for (int x = 0; x <= settings.chunkResolution; x++)
            {
                float worldX = (id.x * settings.chunkResolution + x) * settings.voxelSize;
                float worldZ = (id.z * settings.chunkResolution + z) * settings.voxelSize;
                float height = TerrainGenerator.SurfaceHeight(settings, worldX, worldZ);
                Assert.GreaterOrEqual(height, minY * settings.ChunkSize - safety);
                Assert.LessOrEqual(height, (maxY + 1) * settings.ChunkSize + safety);
            }
        }
        [Test]
        public void MovingFocusDropsQueuedChunksFromThePreviousStreamingPlan()
        {
            var worldObject = new GameObject("Terrain streaming queue reset");
            var world = worldObject.AddComponent<TerrainWorld>();
            SetField(world, "settings", settings);
            Invoke(world, "UpdateStreamingPlan", Vector3.zero);
            var beforeMove = (System.Collections.ICollection)GetField(world, "requestedChunks");
            Assert.That(beforeMove.Count, Is.GreaterThan(0));

            int destinationChunkX = 20;
            Invoke(world, "UpdateStreamingPlan", new Vector3(destinationChunkX * settings.ChunkSize, 0f, 0f));
            foreach (TerrainChunkId id in (System.Collections.IEnumerable)GetField(world, "requestedChunks"))
                Assert.GreaterOrEqual(id.x, destinationChunkX - Mathf.CeilToInt(settings.viewDistance / settings.ChunkSize));
            Object.DestroyImmediate(worldObject);
        }
        [Test]
        public void SuspendingWorldClearsStreamingPlanSoResumeRebuildsItAtTheCurrentFocus()
        {
            settings.viewDistance = settings.ChunkSize * 2f;
            settings.nearUndergroundDistance = settings.ChunkSize;
            var worldObject = new GameObject("Terrain streaming resume");
            var world = worldObject.AddComponent<TerrainWorld>();
            SetField(world, "settings", settings);
            SetField(world, "initialized", true);
            Invoke(world, "UpdateStreamingPlan", Vector3.zero);
            Assert.IsTrue((bool)GetField(world, "hasStreamingPlan"));

            Invoke(world, "SuspendRuntime", "test suspension");
            Assert.IsFalse((bool)GetField(world, "hasStreamingPlan"));
            Invoke(world, "OnEnable");
            Invoke(world, "UpdateStreamingPlan", Vector3.zero);

            Assert.IsTrue((bool)GetField(world, "hasStreamingPlan"));
            Assert.That(((System.Collections.ICollection)GetField(world, "requestedChunks")).Count, Is.GreaterThan(0));
            Object.DestroyImmediate(worldObject);
        }
        [Test]
        public void CacheWritesCanBeExplicitlyResumedAfterFailure()
        {
            var worldObject = new GameObject("Terrain cache recovery");
            var world = worldObject.AddComponent<TerrainWorld>();
            SetField(world, "cacheWriteBlocked", true);
            Assert.IsTrue(world.TryResumeCacheWrites());
            Assert.IsFalse(world.CacheWriteBlocked);
            Object.DestroyImmediate(worldObject);
        }
        [Test]
        public void CacheFailureRejectsNewEditsWithFinalStatus()
        {
            var worldObject = new GameObject("Terrain cache failure");
            var world = worldObject.AddComponent<TerrainWorld>();
            SetField(world, "cacheWriteBlocked", true);
            TerrainEditHandle edit = world.RequestEdit(TerrainEditRequest.Dig(Vector3.zero, 1f));
            Assert.AreEqual(TerrainEditStatus.CacheFailure, edit.Status);
            Object.DestroyImmediate(worldObject);
        }
        [Test]
        public void DigChangesChunkDensity()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            Vector3 point = data.WorldPoint(0, 0, 0);
            float before = data.GetDensity(0, 0, 0);
            Assert.IsTrue(data.Apply(settings, TerrainEditRequest.Dig(point, 2f)));
            Assert.Less(data.GetDensity(0, 0, 0), before);
        }
        [Test]
        public void SessionCacheRestoresOnlyWithinItsSessionNamespace()
        {
            var id = new TerrainChunkId(0, 0, 0);
            var source = new TerrainChunkData(settings, id);
            var values = (float[])source.Density.Clone();
            values[0] = -3f;
            source.Restore(values, source.Material);
            string session = System.Guid.NewGuid().ToString("N");
            var sameSession = new TerrainSessionCache(settings.seed, session);
            Assert.IsTrue(sameSession.Save(source));
            var restored = new TerrainChunkData(settings, id);
            Assert.IsTrue(sameSession.TryLoad(restored));
            Assert.AreEqual(-3f, restored.Density[0]);
            var newSession = new TerrainSessionCache(settings.seed, System.Guid.NewGuid().ToString("N"));
            Assert.IsFalse(newSession.TryLoad(new TerrainChunkData(settings, id)));
        }
        [Test]
        public void AuthoritativeDensityQueryReadsAnUnloadedModifiedChunkFromSessionCache()
        {
            float height = TerrainGenerator.SurfaceHeight(settings, 0f, 0f);
            Vector3 point = new Vector3(0f, height - .5f, 0f);
            var id = new TerrainChunkId(
                Mathf.FloorToInt(point.x / settings.ChunkSize),
                Mathf.FloorToInt(point.y / settings.ChunkSize),
                Mathf.FloorToInt(point.z / settings.ChunkSize));
            var edited = new TerrainChunkData(settings, id);
            Assert.IsTrue(edited.Apply(settings, TerrainEditRequest.Dig(point, 2f)));
            float expected = edited.SampleDensity(point);
            string session = System.Guid.NewGuid().ToString("N");
            var cache = new TerrainSessionCache(settings.seed, session);
            Assert.IsTrue(cache.Save(edited));

            var worldObject = new GameObject("Terrain cached density query");
            var world = worldObject.AddComponent<TerrainWorld>();
            SetField(world, "settings", settings);
            SetField(world, "cache", cache);

            Assert.IsTrue(world.TrySampleDensity(point, out float actual));
            Assert.AreEqual(expected, actual);
            Assert.AreEqual(TerrainGenerator.InitialDensity(settings, point), world.SampleGeneratedDensity(point));
            Object.DestroyImmediate(worldObject);
        }
        [Test]
        public void CorruptSessionCacheReportsAnError()
        {
            string session = System.Guid.NewGuid().ToString("N");
            var cache = new TerrainSessionCache(settings.seed, session);
            string directory = (string)typeof(TerrainSessionCache)
                .GetField("directory", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cache);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "0_0_0.bin"), "not a gzip terrain cache");
            Assert.IsFalse(cache.TryLoad(new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0))));
            Assert.IsNotEmpty(cache.LastError);
        }
        [Test]
        public void DiscardingAnInvalidSessionCacheAllowsGenerationToContinue()
        {
            string session = System.Guid.NewGuid().ToString("N");
            var cache = new TerrainSessionCache(settings.seed, session);
            string directory = (string)typeof(TerrainSessionCache)
                .GetField("directory", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cache);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "0_0_0.bin"), "not a gzip terrain cache");
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));

            Assert.IsFalse(cache.TryLoad(data));
            Assert.IsTrue(cache.TryDiscard(data.Id));
            Assert.IsFalse(cache.TryLoad(data));
            Assert.IsNull(cache.LastError);
        }

        private static GameObject CreateRaycastChunk(string name, Vector3 position, TerrainWorld owner)
        {
            var result = new GameObject(name) { layer = 8 };
            result.transform.position = position;
            result.AddComponent<BoxCollider>();
            var marker = result.AddComponent<TerrainChunkMarker>();
            marker.Owner = owner;
            return result;
        }
        private static void AssertChunkDataBitwiseEqual(TerrainChunkData expected, TerrainChunkData actual)
        {
            Assert.AreEqual(expected.Density.Length, actual.Density.Length);
            Assert.AreEqual(expected.Material.Length, actual.Material.Length);
            for (int i = 0; i < expected.Density.Length; i++)
            {
                Assert.AreEqual(System.BitConverter.SingleToInt32Bits(expected.Density[i]), System.BitConverter.SingleToInt32Bits(actual.Density[i]), $"Density differs at sample {i}.");
                Assert.AreEqual(expected.Material[i], actual.Material[i], $"Material differs at sample {i}.");
            }
        }
        private TerrainChunkData CreateReferenceChunk(TerrainChunkId id)
        {
            var result = new TerrainChunkData(settings, id, null, false);
            int resolution = result.Resolution;
            int samplesPerAxis = resolution + 1;
            for (int z = 0; z <= resolution; z++)
            for (int x = 0; x <= resolution; x++)
            {
                float worldX = (id.x * resolution + x) * result.VoxelSize;
                float worldZ = (id.z * resolution + z) * result.VoxelSize;
                TerrainColumnSample column = TerrainGenerator.SampleColumn(settings, worldX, worldZ);
                for (int y = 0; y <= resolution; y++)
                {
                    float worldY = (id.y * resolution + y) * result.VoxelSize;
                    int index = x + samplesPerAxis * (y + samplesPerAxis * z);
                    result.Density[index] = column.InitialDensity(settings, worldY);
                    result.Material[index] = column.MaterialAt(settings, worldY);
                }
            }
            return result;
        }
        private static object Invoke(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments);
        private static object GetField(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private Vector2Int FindNegativeSurfaceColumn()
        {
            for (int z = -240; z <= 240; z += 16)
            for (int x = -240; x <= 240; x += 16)
                if (x * x + z * z <= 256 * 256 && TerrainGenerator.SurfaceHeight(settings, x + 8f, z + 8f) < 0f) return new Vector2Int(x + 8, z + 8);
            Assert.Fail("Test seed did not produce a negative terrain surface in the streaming area.");
            return default;
        }
        private static bool ContainsChunk(System.Collections.IEnumerable plan, TerrainChunkId expected)
        {
            foreach (object item in plan)
            {
                TerrainChunkId id = (TerrainChunkId)item.GetType().GetField("id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(item);
                if (id.Equals(expected)) return true;
            }
            return false;
        }
    }
}
