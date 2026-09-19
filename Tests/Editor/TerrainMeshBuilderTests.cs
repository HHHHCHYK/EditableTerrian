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

        [TestCase(TransitionFaceMask.NegativeX, 0f, 0)] [TestCase(TransitionFaceMask.PositiveX, 16f, 0)]
        [TestCase(TransitionFaceMask.NegativeY, 0f, 1)] [TestCase(TransitionFaceMask.PositiveY, 16f, 1)]
        [TestCase(TransitionFaceMask.NegativeZ, 0f, 2)] [TestCase(TransitionFaceMask.PositiveZ, 16f, 2)]
        public void AdjacentTwoToOneChunksPairSharedLowResolutionSeamEdges(TransitionFaceMask face, float boundary, int axis)
        {
            TerrainChunkId neighbour = axis == 0 ? new TerrainChunkId(boundary == 0f ? -1 : 1, 0, 0) : axis == 1 ? new TerrainChunkId(0, boundary == 0f ? -1 : 1, 0) : new TerrainChunkId(0, 0, boundary == 0f ? -1 : 1);
            var fine = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            var coarse = new TerrainChunkData(settings, neighbour);
            // The sphere intersects every chunk face, including the thin
            // neighbouring coarse cells on the negative and positive X sides.
            FillTransitionField(fine);
            FillTransitionField(coarse);

            Mesh fineMesh = TerrainMeshBuilder.Build(fine, Vector3.zero, 0, face);
            Mesh coarseMesh = TerrainMeshBuilder.Build(coarse, Vector3.zero, 1);
            var edges = new Dictionary<string, int>();
            AddFaceEdges(edges, fineMesh, boundary, axis);
            AddFaceEdges(edges, coarseMesh, boundary, axis);

            Assert.That(edges.Count, Is.GreaterThan(0));
            foreach (int uses in edges.Values)
                Assert.AreEqual(2, uses, "A shared low-resolution seam edge is left unmatched or duplicated.");

            Object.DestroyImmediate(fineMesh);
            Object.DestroyImmediate(coarseMesh);
        }

        [Test]
        public void SchedulingRejectsAStaleExpectedVersion()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            data.Restore((float[])data.Density.Clone(), data.Material);
            Assert.Throws<System.InvalidOperationException>(() => TerrainMeshBuilder.Schedule(data, Vector3.zero, 0, TransitionFaceMask.None, data.Version - 1));
        }

        [TestCase(0, TransitionFaceMask.None)]
        [TestCase(1, TransitionFaceMask.None)]
        [TestCase(1, TransitionFaceMask.PositiveX)]
        [TestCase(1, TransitionFaceMask.NegativeX)]
        [TestCase(1, TransitionFaceMask.PositiveY)]
        [TestCase(1, TransitionFaceMask.NegativeY)]
        [TestCase(1, TransitionFaceMask.PositiveZ)]
        [TestCase(1, TransitionFaceMask.NegativeZ)]
        [TestCase(2, TransitionFaceMask.PositiveX)]
        public void MaterialBoundariesUseSolidFaceColorsWithoutChangingGeometry(int lod, TransitionFaceMask faces)
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            FillTransitionField(data);
            for (int i = 0; i < data.Material.Length; i++) data.Material[i] = 2;
            Mesh baseline = TerrainMeshBuilder.Build(data, Vector3.zero, lod, faces);
            for (int z = 0; z <= data.Resolution; z++)
            for (int y = 0; y <= data.Resolution; y++)
            for (int x = 0; x <= data.Resolution; x++)
                data.Material[data.Index(x, y, z)] = (byte)(x + y + z < 48 ? 2 : 3);
            byte[] materialBefore = (byte[])data.Material.Clone();
            Mesh boundary = TerrainMeshBuilder.Build(data, Vector3.zero, lod, faces);
            try
            {
                int[] expected = baseline.triangles, actual = boundary.triangles;
                Vector3[] originalPositions = baseline.vertices, positions = boundary.vertices;
                Vector3[] originalNormals = baseline.normals, normals = boundary.normals;
                Color32[] colors = boundary.colors32;
                var faceColors = new HashSet<Color32>();
                Assert.AreEqual(expected.Length, actual.Length);
                for (int i = 0; i < actual.Length; i++)
                {
                    Assert.AreEqual(originalPositions[expected[i]], positions[actual[i]], "Material boundaries must preserve triangle positions and winding.");
                    Assert.AreEqual(originalNormals[expected[i]], normals[actual[i]]);
                    Assert.AreEqual(colors[actual[i - i % 3]], colors[actual[i]], "A triangle must not blend different material colors.");
                    faceColors.Add(colors[actual[i]]);
                }
                Assert.AreEqual(2, faceColors.Count, "Both material regions must remain visible.");
                CollectionAssert.AreEqual(materialBefore, data.Material);
            }
            finally { Object.DestroyImmediate(baseline); Object.DestroyImmediate(boundary); }
        }

        [TestCase(1f)]
        [TestCase(0f)]
        [TestCase(-1f)]
        public void UniformDensitySkipsMeshConstruction(float density)
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            for (int i = 0; i < data.Density.Length; i++) data.Density[i] = density;
            using var request = TerrainMeshBuilder.Schedule(data, Vector3.zero, 0, TransitionFaceMask.PositiveX, data.Version);
            Assert.IsFalse(request.HasScheduledJob);
            Assert.IsTrue(request.IsCompleted);
            Assert.IsNull(request.Complete());
        }

        [Test]
        public void ZeroAndPositiveSamplesRemainAMixedSurface()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            for (int i = 0; i < data.Density.Length; i++) data.Density[i] = 0f;
            data.Density[data.Index(16, 16, 16)] = 1f;
            Mesh mesh = TerrainMeshBuilder.Build(data, Vector3.zero, 0);
            Assert.That(mesh, Is.Not.Null);
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void RestoreIsReflectedByDensityClassification()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            var restored = new float[data.Density.Length];
            for (int i = 0; i < restored.Length; i++) restored[i] = 1f;
            data.Restore(restored, data.Material);
            Assert.AreEqual(TerrainDensityClass.AllPositive, data.ClassifyDensity());

            for (int i = 0; i < restored.Length; i++) restored[i] = -1f;
            data.Restore(restored, data.Material);
            Assert.AreEqual(TerrainDensityClass.AllNonPositive, data.ClassifyDensity());
        }

        [Test]
        public void NaNDensityCannotBeClassifiedAsUniform()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            for (int i = 0; i < data.Density.Length; i++) data.Density[i] = -1f;
            data.Density[data.Index(16, 16, 16)] = float.NaN;
            Assert.AreEqual(TerrainDensityClass.Mixed, data.ClassifyDensity());
        }

        [Test]
        public void SharedMeshingResourcesSurviveCompletedRequests()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            for (int z = 0; z <= data.Resolution; z++)
            for (int y = 0; y <= data.Resolution; y++)
            for (int x = 0; x <= data.Resolution; x++)
                data.Density[data.Index(x, y, z)] = x - 16f;

            using var resources = new TerrainMeshResources();
            using var first = TerrainMeshBuilder.Schedule(data, Vector3.zero, 0, TransitionFaceMask.None, data.Version, resources);
            using var second = TerrainMeshBuilder.Schedule(data, Vector3.zero, 0, TransitionFaceMask.None, data.Version, resources);
            Mesh firstMesh = first.Complete();
            Mesh secondMesh = second.Complete();
            Assert.That(firstMesh, Is.Not.Null);
            Assert.That(secondMesh, Is.Not.Null);
            Object.DestroyImmediate(firstMesh);
            Object.DestroyImmediate(secondMesh);
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

        [TestCase(TransitionFaceMask.NegativeX, 0f, 0, 0)] [TestCase(TransitionFaceMask.NegativeX, 0f, 0, 1)] [TestCase(TransitionFaceMask.NegativeX, 0f, 0, 2)]
        [TestCase(TransitionFaceMask.PositiveX, 16f, 0, 0)] [TestCase(TransitionFaceMask.PositiveX, 16f, 0, 1)] [TestCase(TransitionFaceMask.PositiveX, 16f, 0, 2)]
        [TestCase(TransitionFaceMask.NegativeY, 0f, 1, 0)] [TestCase(TransitionFaceMask.NegativeY, 0f, 1, 1)] [TestCase(TransitionFaceMask.NegativeY, 0f, 1, 2)]
        [TestCase(TransitionFaceMask.PositiveY, 16f, 1, 0)] [TestCase(TransitionFaceMask.PositiveY, 16f, 1, 1)] [TestCase(TransitionFaceMask.PositiveY, 16f, 1, 2)]
        [TestCase(TransitionFaceMask.NegativeZ, 0f, 2, 0)] [TestCase(TransitionFaceMask.NegativeZ, 0f, 2, 1)] [TestCase(TransitionFaceMask.NegativeZ, 0f, 2, 2)]
        [TestCase(TransitionFaceMask.PositiveZ, 16f, 2, 0)] [TestCase(TransitionFaceMask.PositiveZ, 16f, 2, 1)] [TestCase(TransitionFaceMask.PositiveZ, 16f, 2, 2)]
        public void TransitionFaceAddsNonDegenerateGeometryAwayFromTheBoundary(TransitionFaceMask face, float boundary, int axis, int lod)
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            FillTransitionField(data);
            Mesh regular = TerrainMeshBuilder.Build(data, Vector3.zero, lod);
            Mesh transition = TerrainMeshBuilder.Build(data, Vector3.zero, lod, face);
            Assert.That(transition.vertexCount, Is.GreaterThan(regular.vertexCount));

            Vector3[] vertices = transition.vertices;
            int[] triangles = transition.triangles;
            bool hasOffsetVertex = false;
            int transitionTriangleCount = 0;
            var triangleKeys = new HashSet<string>();
            for (int i = 0; i < triangles.Length; i += 3)
            {
                if (triangles[i] < regular.vertexCount && triangles[i + 1] < regular.vertexCount && triangles[i + 2] < regular.vertexCount) continue;
                transitionTriangleCount++;
                Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                Assert.Greater(Vector3.Cross(b - a, c - a).sqrMagnitude, .00000001f, "Transition geometry contains a collapsed triangle.");
                Assert.IsTrue(triangleKeys.Add(TriangleKey(a, b, c)), "Transition geometry contains a duplicate triangle.");
            }
            for (int i = regular.vertexCount; i < vertices.Length; i++)
                hasOffsetVertex |= Mathf.Abs((axis == 0 ? vertices[i].x : axis == 1 ? vertices[i].y : vertices[i].z) - boundary) > .0001f;
            Assert.That(transitionTriangleCount, Is.GreaterThan(0));
            Assert.IsTrue(hasOffsetVertex, "Transition geometry must occupy a non-zero-width band.");
            Object.DestroyImmediate(regular); Object.DestroyImmediate(transition);
        }

        [Test]
        public void ExactIsosurfaceSamplesDoNotProduceCollapsedTriangles()
        {
            var data = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0));
            for (int z = 0; z <= data.Resolution; z++) for (int y = 0; y <= data.Resolution; y++) for (int x = 0; x <= data.Resolution; x++)
                data.Density[data.Index(x, y, z)] = x - 16f;

            Mesh mesh = TerrainMeshBuilder.Build(data, Vector3.zero, 0);
            Assert.That(mesh, Is.Not.Null);
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                Assert.Greater(Vector3.Cross(b - a, c - a).sqrMagnitude, .0000000001f);
            }
            Object.DestroyImmediate(mesh);
        }

        private static void Add(Dictionary<string, int> edges, string edge) { edges.TryGetValue(edge, out int count); edges[edge] = count + 1; }
        private static string Key(Vector3 a, Vector3 b) { string aa = a.ToString("F4"), bb = b.ToString("F4"); return string.CompareOrdinal(aa, bb) < 0 ? aa + bb : bb + aa; }
        private static string TriangleKey(Vector3 a, Vector3 b, Vector3 c)
        {
            var points = new[] { a.ToString("F4"), b.ToString("F4"), c.ToString("F4") };
            System.Array.Sort(points, System.StringComparer.Ordinal);
            return points[0] + points[1] + points[2];
        }
        private static void FillField(TerrainChunkData data)
        {
            for (int z = 0; z <= data.Resolution; z++) for (int y = 0; y <= data.Resolution; y++) for (int x = 0; x <= data.Resolution; x++)
            {
                Vector3 p = data.WorldPoint(x, y, z);
                data.Density[data.Index(x, y, z)] = .20f * p.x + .23f * p.y - .17f * p.z - 2f + .008f * (p.x * p.x - p.y * p.y + p.z * p.z);
            }
        }
        private static void FillTransitionField(TerrainChunkData data)
        {
            Vector3 center = new Vector3(8f, 8f, 8f);
            for (int z = 0; z <= data.Resolution; z++) for (int y = 0; y <= data.Resolution; y++) for (int x = 0; x <= data.Resolution; x++)
                data.Density[data.Index(x, y, z)] = 10.3f - Vector3.Distance(data.WorldPoint(x, y, z), center);
        }
        private static void AddFaceEdges(Dictionary<string, int> edges, Mesh mesh, float boundary, int axis)
        {
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
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
