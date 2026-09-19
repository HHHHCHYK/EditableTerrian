using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
            Assert.Greater(edit.RemovedSolidVolume, 0f);
            Assert.AreEqual(edit.RemovedSolidVolume, edit.RemovedMaterialVolumes.TotalVolume, 0.0001f,
                "Material volume buckets must preserve the authoritative removed volume.");
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

        [UnityTest]
        public IEnumerator EmptyChunkCompletesItsMeshAndColliderLifecycle()
        {
            var settings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            settings.viewDistance = settings.ChunkSize;
            settings.nearUndergroundDistance = settings.ChunkSize;
            settings.chunksBuiltPerFrame = 4;
            settings.maxMeshReplacementsPerFrame = 8;
            var focusObject = new GameObject("Empty chunk focus");
            focusObject.transform.position = new Vector3(0f, 800f, 0f);
            var worldObject = new GameObject("Empty chunk lifecycle world");
            var world = worldObject.AddComponent<TerrainWorld>();
            world.Configure(settings, focusObject.transform);
            TerrainChunkId target = world.GetChunkId(focusObject.transform.position);
            bool applied = false;
            world.ChunkMeshApplied += id => applied |= id.Equals(target);

            int frames = 0;
            while (!world.IsCollisionReady(focusObject.transform.position) && frames++ < 120) yield return null;

            Assert.IsTrue(world.IsCollisionReady(focusObject.transform.position));
            Assert.IsTrue(applied, "An empty chunk must still publish its applied revision.");
            Object.Destroy(worldObject);
            Object.Destroy(focusObject);
            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator FillBudgetReportsActualAddedVolume()
        {
            var worldObject = new GameObject("Terrain fill budget test");
            var world = worldObject.AddComponent<TerrainWorld>();
            yield return null;

            float surface = world.SampleSurfaceHeight(Vector3.zero);
            Vector3 point = new Vector3(0f, surface + .5f, 0f);
            const float budget = .02f;
            TerrainEditHandle edit = world.RequestEdit(TerrainEditRequest.Fill(point, 1.5f, 1f, 7, budget));
            while (edit.Status == TerrainEditStatus.Queued || edit.Status == TerrainEditStatus.Processing) yield return null;

            Assert.AreEqual(TerrainEditStatus.Completed, edit.Status);
            Assert.IsTrue(edit.DataCommitted);
            Assert.GreaterOrEqual(edit.RemovedSolidVolume, 0f);
            Assert.LessOrEqual(edit.AddedSolidVolume, budget);
            Assert.Greater(edit.AddedSolidVolume, 0f);
            Object.Destroy(worldObject);
        }

        [UnityTest]
        public IEnumerator CommitCancellationPreservesCommittedDataAndAccounting()
        {
            var worldObject = new GameObject("Terrain commit cancellation test");
            var world = worldObject.AddComponent<TerrainWorld>();
            yield return null;

            float surface = world.SampleSurfaceHeight(Vector3.zero);
            Vector3 point = new Vector3(0f, surface - .5f, 0f);
            float before = world.SampleDensity(point);
            TerrainEditHandle edit = world.RequestEdit(TerrainEditRequest.Dig(point, 1.5f));
            bool disabledAtCommit = false;
            float removedAtCommit = -1f;
            float addedAtCommit = -1f;
            edit.Changed += changed =>
            {
                if (disabledAtCommit || !changed.DataCommitted || changed.Status != TerrainEditStatus.Processing) return;
                removedAtCommit = changed.RemovedSolidVolume;
                addedAtCommit = changed.AddedSolidVolume;
                disabledAtCommit = true;
                worldObject.SetActive(false);
            };

            while (edit.Status == TerrainEditStatus.Queued || edit.Status == TerrainEditStatus.Processing) yield return null;

            Assert.IsTrue(disabledAtCommit, "The lifecycle callback must run after the density commit.");
            Assert.AreEqual(TerrainEditStatus.Cancelled, edit.Status);
            Assert.IsTrue(edit.DataCommitted);
            Assert.AreEqual(removedAtCommit, edit.RemovedSolidVolume);
            Assert.AreEqual(addedAtCommit, edit.AddedSolidVolume);
            Assert.Less(world.SampleDensity(point), before, "Cancellation after commit must retain authoritative density changes.");
            Object.Destroy(worldObject);
        }

        [UnityTest]
        public IEnumerator SkyFillThenLargerDigNeverRemovesMoreThanWasAdded()
        {
            var worldObject = new GameObject("Terrain repeated volume accounting test");
            var world = worldObject.AddComponent<TerrainWorld>();
            yield return null;

            float surface = world.SampleSurfaceHeight(Vector3.zero);
            float voxelSize = world.Settings.voxelSize;
            // Put the brush center on a sample so one full-strength fill produces
            // solid matter even this far from the original signed-distance surface.
            Vector3 point = new Vector3(0f, Mathf.Ceil((surface + 20f) / voxelSize) * voxelSize, 0f);
            TerrainEditHandle fill = world.RequestEdit(TerrainEditRequest.Fill(point, 1.5f, 1f, 7));
            while (fill.Status == TerrainEditStatus.Queued || fill.Status == TerrainEditStatus.Processing) yield return null;
            Assert.AreEqual(TerrainEditStatus.Completed, fill.Status);
            Assert.IsTrue(fill.DataCommitted);
            Assert.Greater(fill.AddedSolidVolume, 0f);

            double recovered = 0d;
            for (int pass = 0; pass < 4; pass++)
            {
                TerrainEditHandle dig = world.RequestEdit(TerrainEditRequest.Dig(point, 2f));
                while (dig.Status == TerrainEditStatus.Queued || dig.Status == TerrainEditStatus.Processing) yield return null;
                Assert.AreEqual(TerrainEditStatus.Completed, dig.Status);
                Assert.IsTrue(dig.DataCommitted);
                recovered += dig.RemovedSolidVolume;
            }
            Assert.LessOrEqual(recovered, fill.AddedSolidVolume + 1e-5f,
                "A larger dig over previously empty sky cannot remove more solid volume than the fill added.");
            Object.Destroy(worldObject);
        }

        [UnityTest]
        public IEnumerator ZeroFillBudgetCommitsAConfirmedNoop()
        {
            var worldObject = new GameObject("Terrain zero fill budget test");
            var world = worldObject.AddComponent<TerrainWorld>();
            yield return null;

            float surface = world.SampleSurfaceHeight(Vector3.zero);
            Vector3 point = new Vector3(0f, surface + .5f, 0f);
            float before = world.SampleDensity(point);
            TerrainEditHandle edit = world.RequestEdit(TerrainEditRequest.Fill(point, 1.5f, 1f, 7, 0f));
            while (edit.Status == TerrainEditStatus.Queued || edit.Status == TerrainEditStatus.Processing) yield return null;

            Assert.AreEqual(TerrainEditStatus.Completed, edit.Status);
            Assert.IsTrue(edit.DataCommitted);
            Assert.AreEqual(0f, edit.RemovedSolidVolume);
            Assert.AreEqual(0f, edit.AddedSolidVolume);
            // The first edit loads interpolated resident samples; the initial
            // procedural query can differ by a few float ulps without any edit.
            Assert.AreEqual(before, world.SampleDensity(point), 1e-6f);
            Object.Destroy(worldObject);
        }

        [UnityTest]
        public IEnumerator EditPreparationFailureLeavesAuthoritativeDensityUnchanged()
        {
            var settings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            settings.memoryCacheLimitMb = 32;
            var worldObject = new GameObject("Terrain edit preparation failure test");
            var world = worldObject.AddComponent<TerrainWorld>();
            world.Configure(settings, null);
            yield return null;

            Vector3 point = new Vector3(0f, world.SampleSurfaceHeight(Vector3.zero) - .5f, 0f);
            float before = world.SampleDensity(point);
            TerrainEditHandle edit = world.RequestEdit(TerrainEditRequest.Dig(point, 64f));
            while (edit.Status == TerrainEditStatus.Queued || edit.Status == TerrainEditStatus.Processing) yield return null;

            Assert.AreEqual(TerrainEditStatus.CacheFailure, edit.Status);
            Assert.IsFalse(edit.DataCommitted);
            Assert.AreEqual(before, world.SampleDensity(point));
            Object.Destroy(worldObject);
            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator ChunkBorderSamplesStayCoherentAfterAnEdit()
        {
            var settings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            var worldObject = new GameObject("Terrain border coherence test");
            var world = worldObject.AddComponent<TerrainWorld>();
            world.Configure(settings, null);
            yield return null;

            float boundary = settings.ChunkSize;
            float pointY = world.SampleSurfaceHeight(new Vector3(boundary, 0f, 0f)) - .5f;
            Vector3 point = new Vector3(boundary, pointY, 0f);
            float before = world.SampleDensity(point);
            TerrainEditHandle edit = world.RequestEdit(TerrainEditRequest.Dig(point, 1.5f));
            while (edit.Status == TerrainEditStatus.Queued || edit.Status == TerrainEditStatus.Processing) yield return null;

            Assert.AreEqual(TerrainEditStatus.Completed, edit.Status);
            Assert.Less(world.SampleDensity(point), before);

            var chunksField = typeof(TerrainWorld).GetField("chunks", BindingFlags.Instance | BindingFlags.NonPublic);
            var entries = (IEnumerable)chunksField.GetValue(world);
            TerrainChunkData left = null;
            TerrainChunkData right = null;
            int chunkY = Mathf.FloorToInt(pointY / settings.ChunkSize);
            foreach (object entry in entries)
            {
                PropertyInfo keyProperty = entry.GetType().GetProperty("Key");
                PropertyInfo valueProperty = entry.GetType().GetProperty("Value");
                TerrainChunkId id = (TerrainChunkId)keyProperty.GetValue(entry, null);
                if (id.y != chunkY || id.z != 0) continue;
                object loaded = valueProperty.GetValue(entry, null);
                FieldInfo dataField = loaded.GetType().GetField("data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (id.x == 0) left = (TerrainChunkData)dataField.GetValue(loaded);
                else if (id.x == 1) right = (TerrainChunkData)dataField.GetValue(loaded);
            }

            Assert.IsNotNull(left);
            Assert.IsNotNull(right);
            for (int z = 0; z <= settings.chunkResolution; z++)
            for (int y = 0; y <= settings.chunkResolution; y++)
                Assert.AreEqual(left.GetDensity(settings.chunkResolution, y, z), right.GetDensity(0, y, z),
                    $"Density border differs at y={y}, z={z}.");
            Object.Destroy(worldObject);
            Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator NegativeChunkBorderSamplesStayCoherentAfterAnEdit()
        {
            var settings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            var worldObject = new GameObject("Terrain negative border coherence test");
            var world = worldObject.AddComponent<TerrainWorld>();
            world.Configure(settings, null);
            yield return null;

            float boundary = -settings.ChunkSize;
            float pointY = world.SampleSurfaceHeight(new Vector3(boundary, 0f, 0f)) - .5f;
            Vector3 point = new Vector3(boundary, pointY, 0f);
            float before = world.SampleDensity(point);
            TerrainEditHandle edit = world.RequestEdit(TerrainEditRequest.Dig(point, 1.5f));
            while (edit.Status == TerrainEditStatus.Queued || edit.Status == TerrainEditStatus.Processing) yield return null;

            Assert.AreEqual(TerrainEditStatus.Completed, edit.Status);
            Assert.Less(world.SampleDensity(point), before);

            var chunksField = typeof(TerrainWorld).GetField("chunks", BindingFlags.Instance | BindingFlags.NonPublic);
            var entries = (IEnumerable)chunksField.GetValue(world);
            TerrainChunkData left = null;
            TerrainChunkData right = null;
            int chunkY = Mathf.FloorToInt(pointY / settings.ChunkSize);
            foreach (object entry in entries)
            {
                PropertyInfo keyProperty = entry.GetType().GetProperty("Key");
                PropertyInfo valueProperty = entry.GetType().GetProperty("Value");
                TerrainChunkId id = (TerrainChunkId)keyProperty.GetValue(entry, null);
                if (id.y != chunkY || id.z != 0) continue;
                object loaded = valueProperty.GetValue(entry, null);
                FieldInfo dataField = loaded.GetType().GetField("data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (id.x == -2) left = (TerrainChunkData)dataField.GetValue(loaded);
                else if (id.x == -1) right = (TerrainChunkData)dataField.GetValue(loaded);
            }

            Assert.IsNotNull(left);
            Assert.IsNotNull(right);
            for (int z = 0; z <= settings.chunkResolution; z++)
            for (int y = 0; y <= settings.chunkResolution; y++)
                Assert.AreEqual(left.GetDensity(settings.chunkResolution, y, z), right.GetDensity(0, y, z),
                    $"Density border differs at y={y}, z={z}.");
            Object.Destroy(worldObject);
            Object.Destroy(settings);
        }
    }
}
