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
        public void StreamingPlanStaysWithinConfiguredDensityCapacity()
        {
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
            var worldObject = new GameObject("Terrain surface streaming plan");
            var world = worldObject.AddComponent<TerrainWorld>();
            SetField(world, "settings", settings);
            Invoke(world, "BuildStreamingPlan", new TerrainChunkId(0, 1, 0));
            var plan = (System.Collections.IEnumerable)GetField(world, "streamingPlan");
            var surfaceId = new TerrainChunkId(Mathf.FloorToInt(column.x / settings.ChunkSize), Mathf.FloorToInt(surface / settings.ChunkSize), Mathf.FloorToInt(column.y / settings.ChunkSize));
            Assert.IsTrue(ContainsChunk(plan, surfaceId));
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
        public void CorruptSessionCacheReportsAnError()
        {
            string session = System.Guid.NewGuid().ToString("N");
            string directory = Path.Combine(Application.temporaryCachePath, "HumanierTerrain", settings.seed.ToString(), session);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "0_0_0.bin"), "not a gzip terrain cache");
            var cache = new TerrainSessionCache(settings.seed, session);
            Assert.IsFalse(cache.TryLoad(new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0))));
            Assert.IsNotEmpty(cache.LastError);
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
        private static object Invoke(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments);
        private static object GetField(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private Vector2Int FindNegativeSurfaceColumn()
        {
            for (int z = -240; z <= 240; z += 16)
            for (int x = -240; x <= 240; x += 16)
                if (TerrainGenerator.SurfaceHeight(settings, x + 8f, z + 8f) < 0f) return new Vector2Int(x + 8, z + 8);
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
