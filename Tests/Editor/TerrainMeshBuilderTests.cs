using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Humanier.Terrain.Tests
{
    public sealed class TerrainMeshBuilderTests
    {
        private TerrainWorldSettings settings;

        [SetUp] public void SetUp() { settings = ScriptableObject.CreateInstance<TerrainWorldSettings>(); settings.chunkResolution = 32; settings.voxelSize = .5f; }
        [TearDown] public void TearDown() { Object.DestroyImmediate(settings); }

        [Test]
        public void ClosedRegularSurfaceHasOnlyPairedEdges()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            for (int z = 0; z <= data.Resolution; z++) for (int y = 0; y <= data.Resolution; y++) for (int x = 0; x <= data.Resolution; x++)
                data.Density[data.Index(x, y, z)] = (new Vector3(x, y, z) - new Vector3(16, 16, 16)).magnitude - 11f;
            Mesh mesh = TerrainMeshBuilder.Build(data, Vector3.zero, 0);
            Assert.That(mesh, Is.Not.Null);
            var edges = new Dictionary<string, int>();
            int[] triangles = mesh.triangles; Vector3[] vertices = mesh.vertices;
            for (int i = 0; i < triangles.Length; i += 3) { Add(edges, Key(vertices[triangles[i]], vertices[triangles[i + 1]])); Add(edges, Key(vertices[triangles[i + 1]], vertices[triangles[i + 2]])); Add(edges, Key(vertices[triangles[i + 2]], vertices[triangles[i]])); }
            foreach (int uses in edges.Values) Assert.AreEqual(2, uses);
            Object.DestroyImmediate(mesh);
        }

        [TestCase(TransitionFaceMask.NegativeX)] [TestCase(TransitionFaceMask.PositiveX)]
        [TestCase(TransitionFaceMask.NegativeY)] [TestCase(TransitionFaceMask.PositiveY)]
        [TestCase(TransitionFaceMask.NegativeZ)] [TestCase(TransitionFaceMask.PositiveZ)]
        public void TransitionFaceAddsFiniteColoredGeometry(TransitionFaceMask face)
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            for (int z = 0; z <= data.Resolution; z++) for (int y = 0; y <= data.Resolution; y++) for (int x = 0; x <= data.Resolution; x++) data.Density[data.Index(x, y, z)] = x + y + z - 48f;
            Mesh regular = TerrainMeshBuilder.Build(data, Vector3.zero, 1);
            Mesh transition = TerrainMeshBuilder.Build(data, Vector3.zero, 1, face);
            Assert.That(transition.vertexCount, Is.GreaterThan(regular.vertexCount));
            Assert.AreEqual(transition.vertexCount, transition.colors32.Length);
            foreach (Vector3 vertex in transition.vertices) Assert.IsTrue(float.IsFinite(vertex.x) && float.IsFinite(vertex.y) && float.IsFinite(vertex.z));
            Object.DestroyImmediate(regular); Object.DestroyImmediate(transition);
        }

        [Test]
        public void SchedulingRejectsAStaleExpectedVersion()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            data.Restore((float[])data.Density.Clone(), data.Material);
            Assert.Throws<System.InvalidOperationException>(() => TerrainMeshBuilder.Schedule(data, Vector3.zero, 0, TransitionFaceMask.None, data.Version - 1));
        }

        private static void Add(Dictionary<string, int> edges, string edge) { edges.TryGetValue(edge, out int count); edges[edge] = count + 1; }
        private static string Key(Vector3 a, Vector3 b) { string aa = a.ToString("F4"), bb = b.ToString("F4"); return string.CompareOrdinal(aa, bb) < 0 ? aa + bb : bb + aa; }
    }
}
