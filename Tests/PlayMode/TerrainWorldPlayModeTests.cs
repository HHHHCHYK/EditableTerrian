using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Humanier.Terrain.Tests
{
    public sealed class TerrainWorldPlayModeTests
    {
        [UnityTest]
        public IEnumerator DigChangesDensityInAnInitializedWorld()
        {
            var worldObject = new GameObject("Terrain play mode test");
            var world = worldObject.AddComponent<TerrainWorld>();
            yield return null;

            float height = world.SampleSurfaceHeight(Vector3.zero);
            Vector3 point = new Vector3(0f, height - .5f, 0f);
            float before = world.SampleDensity(point);
            TerrainEditHandle edit = world.RequestEdit(TerrainEditRequest.Dig(point, 2f));
            while (edit.Status == TerrainEditStatus.Queued || edit.Status == TerrainEditStatus.Processing) yield return null;

            Assert.AreEqual(TerrainEditStatus.Completed, edit.Status);
            Assert.Less(world.SampleDensity(point), before);
            Object.Destroy(worldObject);
        }
    }
}
