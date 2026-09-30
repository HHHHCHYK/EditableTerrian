using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Humanier.Terrain.Tests
{
    public sealed class TerrainWorldResidencyPlayModeTests
    {
        [UnityTest]
        public IEnumerator RetainedSurfaceColumnBuildsCollisionAndSurvivesFocusStreaming()
        {
            var settings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            settings.chunkResolution = 8;
            settings.voxelSize = .5f;
            settings.viewDistance = 16f;
            settings.nearUndergroundDistance = 8f;
            settings.chunksBuiltPerFrame = 1;
            settings.maxQueuedChunks = 32;
            settings.maxMeshReplacementsPerFrame = 4;

            var focusObject = new GameObject("Terrain residency focus");
            var worldObject = new GameObject("Terrain residency play mode world");
            var world = worldObject.AddComponent<TerrainWorld>();
            world.Configure(settings, focusObject.transform);
            System.IDisposable lease = world.RetainRegion(new InfiniteWorldPosition(0, 0, 0d, 0d, 0d), .5f);

            float surface = TerrainGenerator.SurfaceHeight(settings, 0f, 0f);
            TerrainChunkId surfaceChunk = new TerrainChunkId(0, Mathf.FloorToInt(surface / settings.ChunkSize), 0);
            bool ready = false;
            for (int frame = 0; frame < 240 && !ready; frame++)
            {
                yield return null;
                ready = world.TryGetChunkCollider(surfaceChunk, out MeshCollider collider) &&
                        collider != null && collider.sharedMesh != null && collider.sharedMesh.vertexCount > 0;
            }
            Assert.IsTrue(ready, "A retained surface column must eventually receive a collision LOD 0 mesh.");

            focusObject.transform.position = new Vector3(512f, 0f, 512f);
            for (int frame = 0; frame < 120; frame++) yield return null;

            List<TerrainChunkId> loaded = new List<TerrainChunkId>();
            world.CopyLoadedChunkIds(loaded);
            Assert.Contains(surfaceChunk, loaded, "Streaming eviction must not unload a retained surface chunk.");
            Assert.IsTrue(world.TryGetChunkCollider(surfaceChunk, out MeshCollider retainedCollider));
            Assert.IsNotNull(retainedCollider);
            Assert.IsNotNull(retainedCollider.sharedMesh);

            lease.Dispose();
            Object.Destroy(worldObject);
            Object.Destroy(focusObject);
            Object.Destroy(settings);
            yield return null;
        }
    }
}
