using NUnit.Framework;
using UnityEngine;

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
    }
}
