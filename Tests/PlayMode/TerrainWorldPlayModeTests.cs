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
            Assert.IsTrue(world.IsCollisionReady(point), "Completed edits must have their current collision mesh assigned.");
            Object.Destroy(worldObject);
        }

        [UnityTest]
        public IEnumerator DestroyedWorldCancelsQueuedEdit()
        {
            var worldObject = new GameObject("Terrain cancellation test");
            var world = worldObject.AddComponent<TerrainWorld>();
            yield return null;

            TerrainEditHandle edit = world.RequestEdit(TerrainEditRequest.Dig(Vector3.zero, 64f));
            Object.Destroy(worldObject);
            yield return null;

            Assert.AreEqual(TerrainEditStatus.Cancelled, edit.Status);
        }

        [UnityTest]
        public IEnumerator DisablingWorldCancelsActiveEditsAndAllowsLaterEditsAfterReenable()
        {
            var worldObject = new GameObject("Terrain disable lifecycle test");
            var world = worldObject.AddComponent<TerrainWorld>();
            yield return null;

            TerrainEditHandle cancelled = world.RequestEdit(TerrainEditRequest.Dig(Vector3.zero, 64f));
            worldObject.SetActive(false);
            yield return null;
            Assert.AreEqual(TerrainEditStatus.Cancelled, cancelled.Status);

            worldObject.SetActive(true);
            yield return null;
            float height = world.SampleSurfaceHeight(Vector3.zero);
            TerrainEditHandle resumed = world.RequestEdit(TerrainEditRequest.Dig(new Vector3(0f, height - .5f, 0f), 2f));
            while (resumed.Status == TerrainEditStatus.Queued || resumed.Status == TerrainEditStatus.Processing) yield return null;

            Assert.AreEqual(TerrainEditStatus.Completed, resumed.Status);
            Object.Destroy(worldObject);
        }
    }
}
