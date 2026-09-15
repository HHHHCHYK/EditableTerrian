using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Collections;
using System.IO;

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
        [UnityTest]
        public IEnumerator DigChangesLoadedDensity()
        {
            var worldObject = new GameObject("Terrain test");
            var world = worldObject.AddComponent<TerrainWorld>();
            float height = world.SampleSurfaceHeight(Vector3.zero);
            Vector3 point = new Vector3(0, height - .5f, 0);
            float before = world.SampleDensity(point);
            TerrainEditHandle edit = world.RequestEdit(TerrainEditRequest.Dig(point, 2f));
            while (edit.Status == TerrainEditStatus.Queued || edit.Status == TerrainEditStatus.Processing) yield return null;
            Assert.AreEqual(TerrainEditStatus.Completed, edit.Status);
            Assert.Less(world.SampleDensity(point), before);
            Object.DestroyImmediate(worldObject);
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
    }
}
