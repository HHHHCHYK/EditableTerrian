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

        [Test]
        public void PlaneNormalsPointOutOfPositiveDensity()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            for (int z = 0; z <= data.Resolution; z++) for (int y = 0; y <= data.Resolution; y++) for (int x = 0; x <= data.Resolution; x++) data.Density[data.Index(x, y, z)] = x - 16f;
            Mesh mesh = TerrainMeshBuilder.Build(data, Vector3.zero, 0);
            float normalX = 0f; foreach (Vector3 normal in mesh.normals) normalX += normal.x;
            Assert.Less(normalX / mesh.vertexCount, -.99f);
            Object.DestroyImmediate(mesh);
        }

        [TestCase(TransitionFaceMask.NegativeX, 0f, 0)] [TestCase(TransitionFaceMask.PositiveX, 16f, 0)]
        [TestCase(TransitionFaceMask.NegativeY, 0f, 1)] [TestCase(TransitionFaceMask.PositiveY, 16f, 1)]
        [TestCase(TransitionFaceMask.NegativeZ, 0f, 2)] [TestCase(TransitionFaceMask.PositiveZ, 16f, 2)]
        public void AdjacentTwoToOneChunksPairSeamEdges(TransitionFaceMask face, float boundary, int axis)
        {
            TerrainChunkId neighbour = axis == 0 ? new TerrainChunkId(boundary == 0f ? -1 : 1, 0, 0) : axis == 1 ? new TerrainChunkId(0, boundary == 0f ? -1 : 1, 0) : new TerrainChunkId(0, 0, boundary == 0f ? -1 : 1);
            var fine = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0)); var coarse = new TerrainChunkData(settings, neighbour);
            FillField(fine); FillField(coarse);
            Mesh fineMesh = TerrainMeshBuilder.Build(fine, Vector3.zero, 0, face);
            Mesh coarseMesh = TerrainMeshBuilder.Build(coarse, Vector3.zero, 1);
            var edges = new Dictionary<string, int>(); AddFaceEdges(edges, fineMesh, boundary, axis); AddFaceEdges(edges, coarseMesh, boundary, axis);
            Assert.That(edges.Count, Is.GreaterThan(0));
            foreach (int uses in edges.Values) Assert.AreEqual(2, uses, "A seam edge is left unmatched or duplicated.");
            Object.DestroyImmediate(fineMesh); Object.DestroyImmediate(coarseMesh);
        }

        private static void Add(Dictionary<string, int> edges, string edge) { edges.TryGetValue(edge, out int count); edges[edge] = count + 1; }
        private static string Key(Vector3 a, Vector3 b) { string aa = a.ToString("F4"), bb = b.ToString("F4"); return string.CompareOrdinal(aa, bb) < 0 ? aa + bb : bb + aa; }
        private static void FillField(TerrainChunkData data)
        {
            for (int z = 0; z <= data.Resolution; z++) for (int y = 0; y <= data.Resolution; y++) for (int x = 0; x <= data.Resolution; x++)
            {
                Vector3 p = data.WorldPoint(x, y, z);
                data.Density[data.Index(x, y, z)] = .20f * p.x + .23f * p.y - .17f * p.z - 2f + .008f * (p.x * p.x - p.y * p.y + p.z * p.z);
            }
        }
        private static void AddFaceEdges(Dictionary<string, int> edges, Mesh mesh, float boundary, int axis)
        {
            Vector3[] vertices = mesh.vertices; int[] triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                if (OnFace(a, boundary, axis) && OnFace(b, boundary, axis)) Add(edges, Key(a, b));
                if (OnFace(b, boundary, axis) && OnFace(c, boundary, axis)) Add(edges, Key(b, c));
                if (OnFace(c, boundary, axis) && OnFace(a, boundary, axis)) Add(edges, Key(c, a));
            }
        }
        private static bool OnFace(Vector3 point, float boundary, int axis) => Mathf.Abs((axis == 0 ? point.x : axis == 1 ? point.y : point.z) - boundary) < .0001f;
    }
}
