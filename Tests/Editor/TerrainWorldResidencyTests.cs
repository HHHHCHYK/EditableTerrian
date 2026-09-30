using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Humanier.Terrain.Tests
{
    public sealed class TerrainWorldResidencyTests
    {
        private TerrainWorldSettings settings;

        [SetUp]
        public void SetUp()
        {
            settings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            settings.chunkResolution = 16;
            settings.voxelSize = .5f;
            settings.viewDistance = 32f;
            settings.nearUndergroundDistance = 16f;
            settings.chunksBuiltPerFrame = 1;
            settings.maxQueuedChunks = 32;
            settings.maxMeshReplacementsPerFrame = 4;
        }

        [TearDown]
        public void TearDown()
        {
            if (settings != null) UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public void RetainRegionReturnsDisposableLeaseAndPinsColumnsUntilDisposed()
        {
            var worldObject = new GameObject("Terrain residency test");
            var world = worldObject.AddComponent<TerrainWorld>();
            world.Configure(settings, null);

            IDisposable lease = world.RetainRegion(new InfiniteWorldPosition(0, 0, 4d, 4d, 0d), 1f);
            IDictionary references = (IDictionary)GetField(world, "retainedColumnReferences");
            Assert.That(references.Count, Is.EqualTo(1));

            TerrainChunkId id = new TerrainChunkId(0, 0, 0);
            Assert.AreEqual(0, (int)Invoke(world, "GetLod", id), "A retained surface column must force collision LOD 0.");

            lease.Dispose();
            Assert.That(references.Count, Is.EqualTo(0));
            Assert.DoesNotThrow(() => lease.Dispose(), "A retention lease must be idempotent.");
            UnityEngine.Object.DestroyImmediate(worldObject);
        }

        [Test]
        public void RetainRegionUsesLogicalCoordinatesAcrossOriginChanges()
        {
            var worldObject = new GameObject("Terrain logical residency test");
            var world = worldObject.AddComponent<TerrainWorld>();
            world.Configure(settings, null);
            var center = new InfiniteWorldPosition(4, -3, 2d, 5d, 7d);

            IDisposable lease = world.RetainRegion(center, 0f);
            IDictionary before = (IDictionary)GetField(world, "retainedColumnReferences");
            var keysBefore = new List<object>();
            foreach (object key in before.Keys) keysBefore.Add(key);

            world.SetOriginOffset(new Vector3(10000f, 12f, -9000f));

            IDictionary after = (IDictionary)GetField(world, "retainedColumnReferences");
            Assert.That(after.Count, Is.EqualTo(keysBefore.Count));
            foreach (object key in keysBefore) Assert.IsTrue(after.Contains(key), "Origin rebasing must not change logical retention keys.");
            lease.Dispose();
            UnityEngine.Object.DestroyImmediate(worldObject);
        }

        [Test]
        public void RetainRegionRejectsARegionLargerThanResidentBudget()
        {
            settings.memoryCacheLimitMb = 32;
            settings.chunkResolution = 64;
            settings.surfaceTileCacheBudgetMb = 8;
            var worldObject = new GameObject("Terrain residency capacity test");
            var world = worldObject.AddComponent<TerrainWorld>();
            world.Configure(settings, null);

            Assert.Throws<InvalidOperationException>(() => world.RetainRegion(
                new InfiniteWorldPosition(0, 0, 0d, 0d, 0d), settings.ChunkSize * 10f));
            UnityEngine.Object.DestroyImmediate(worldObject);
        }

        [Test]
        public void RetainRegionRejectsNonFiniteRadius()
        {
            var worldObject = new GameObject("Terrain residency validation test");
            var world = worldObject.AddComponent<TerrainWorld>();
            world.Configure(settings, null);

            Assert.Throws<ArgumentOutOfRangeException>(() => world.RetainRegion(
                new InfiniteWorldPosition(0, 0, 0d, 0d, 0d), float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.RetainRegion(
                new InfiniteWorldPosition(0, 0, 0d, 0d, 0d), -1f));
            UnityEngine.Object.DestroyImmediate(worldObject);
        }

        [Test]
        public void RetainedRegionQueuesNegativeSurfaceChunksFromComputedMinimum()
        {
            settings.sphericalCapEnabled = true;
            settings.sphericalCapRadius = 600f;
            var worldObject = new GameObject("Terrain negative surface residency test");
            var world = worldObject.AddComponent<TerrainWorld>();
            world.Configure(settings, null);
            // Near the edge of the spherical cap the generated surface is
            // unambiguously below logical Y=0, while the region still covers
            // exactly one XZ column.
            IDisposable lease = world.RetainRegion(new InfiniteWorldPosition(62, 0, 0d, 0d, 0d), 0f);

            for (int i = 0; i < 32; i++) Invoke(world, "ContinueRetainedRegionPlanning");

            IDictionary plans = (IDictionary)GetField(world, "retainedColumnPlans");
            Assert.AreEqual(1, plans.Count);
            object plan = null;
            foreach (object value in plans.Values) { plan = value; break; }
            Assert.IsNotNull(plan);
            Assert.IsTrue((bool)GetField(plan, "rangeReady"));
            int minY = (int)GetField(plan, "minY");
            Assert.Less(minY, 0, "The test column must exercise a negative surface chunk range.");

            Invoke(world, "QueueRetainedRegionChunks");
            IEnumerable queued = (IEnumerable)GetField(world, "retainedChunkRequests");
            TerrainChunkId first = default;
            bool hasQueued = false;
            foreach (object value in queued)
            {
                first = (TerrainChunkId)value;
                hasQueued = true;
                break;
            }
            Assert.IsTrue(hasQueued);
            Assert.AreEqual(minY, first.y, "A retained column must start at its computed negative minY.");

            lease.Dispose();
            UnityEngine.Object.DestroyImmediate(worldObject);
        }

        [Test]
        public void RetainedLodUpgradeKeepsExistingCollisionReadyAcrossNeighborRebuilds()
        {
            settings.minimumMeshLod = 1;
            var worldObject = new GameObject("Terrain retained LOD transition test");
            var world = worldObject.AddComponent<TerrainWorld>();
            world.Configure(settings, null);
            Invoke(world, "EnsureInitialized");
            Invoke(world, "CreateChunk", new TerrainChunkId(0, 0, 0), Vector3.zero, false);
            Invoke(world, "CreateChunk", new TerrainChunkId(1, 0, 0), Vector3.zero, false);

            IDictionary chunks = (IDictionary)GetField(world, "chunks");
            object target = chunks[new TerrainChunkId(0, 0, 0)];
            MeshCollider collider = (MeshCollider)GetField(target, "collider");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.forward }, triangles = new[] { 0, 1, 2 } };
            collider.sharedMesh = mesh;
            int revision = (int)GetField(target, "meshRevision");
            object data = GetField(target, "data");
            int dataVersion = (int)data.GetType().GetProperty("Version", BindingFlags.Instance | BindingFlags.Public).GetValue(data);
            SetField(target, "appliedMeshRevision", revision);
            SetField(target, "appliedDataVersion", dataVersion);

            IDisposable lease = world.RetainRegion(new InfiniteWorldPosition(0, 0, 4d, 4d, 0d), 5f);
            try
            {
                Vector3 targetPoint = world.LogicalToScene(new Vector3(4f, .1f, 4f));
                Assert.IsTrue(world.IsCollisionReady(targetPoint),
                    "A neighbor transition rebuild must not hide an existing valid collider while retained LOD0 is pending.");
            }
            finally
            {
                lease.Dispose();
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(worldObject);
            }
        }

        private static object GetField(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(target);

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(target, value);

        private static object Invoke(object target, string name, params object[] arguments) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments);
    }
}
