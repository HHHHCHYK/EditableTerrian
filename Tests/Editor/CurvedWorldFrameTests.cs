using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Humanier.Terrain.Tests
{
    public sealed class CurvedWorldFrameTests
    {
        [Test]
        public void Radius600ProjectionAndInversePreserveLogicalPosition()
        {
            var frame = new CurvedWorldFrame(600f, 16f);
            InfiniteWorldPosition logical = frame.FromLogical(120d, 160d, 7.5d);

            Vector3 scene = frame.LogicalToScene(logical);
            InfiniteWorldPosition roundTrip = frame.SceneToLogical(scene);
            float expectedRadius = 600f + 7.5f;
            float expectedChordSquared = 600f * 600f + expectedRadius * expectedRadius -
                2f * 600f * expectedRadius * Mathf.Cos(200f / 600f);

            Assert.AreEqual(600d, frame.Radius);
            Assert.AreEqual(expectedRadius, Vector3.Distance(frame.SceneSphereCenter, scene), .001f);
            Assert.AreEqual(Mathf.Sqrt(expectedChordSquared), scene.magnitude, .01f);
            Assert.AreEqual(120d, roundTrip.LogicalX(16f), .01d);
            Assert.AreEqual(160d, roundTrip.LogicalZ(16f), .01d);
            Assert.AreEqual(7.5d, roundTrip.radialHeight, .001d);
        }

        [Test]
        public void ReanchoringAfter32MetresReturnsAnchorToTopAndProvidesRigidShift()
        {
            var frame = new CurvedWorldFrame(600f, 16f);
            InfiniteWorldPosition nextAnchor = frame.FromLogical(32d, 0d, 0d);
            Vector3 oldAnchorScene = frame.LogicalToScene(nextAnchor);

            CurvedWorldFrameShift shift = frame.Reanchor(nextAnchor);
            Vector3 newAnchorScene = frame.LogicalToScene(nextAnchor);

            Assert.That(newAnchorScene.magnitude, Is.LessThan(.001f));
            Assert.That(Vector3.Angle(frame.UpAt(newAnchorScene), Vector3.up), Is.LessThan(.01f));
            Assert.That(Vector3.Distance(shift.TransformPoint(oldAnchorScene), newAnchorScene), Is.LessThan(.01f));
            Assert.AreEqual(32d, frame.Anchor.LogicalX(16f), .001d);
            Assert.AreEqual(0d, frame.Anchor.LogicalZ(16f), .001d);
        }

        [Test]
        public void ReanchoringRigidlyTransportsEveryPointInTheResidentWindow()
        {
            var frame = new CurvedWorldFrame(600f, 16f);
            var probes = new[]
            {
                frame.FromLogical(-320d, -240d, -12d),
                frame.FromLogical(-32d, 320d, 0d),
                frame.FromLogical(0d, -320d, 18d),
                frame.FromLogical(96d, 224d, 4d),
                frame.FromLogical(320d, 320d, 35d)
            };
            var before = new Vector3[probes.Length];
            for (int i = 0; i < probes.Length; i++) before[i] = frame.LogicalToScene(probes[i]);

            CurvedWorldProjectionSnapshot captured = frame.ProjectionSnapshot;
            CurvedWorldFrameShift shift = frame.Reanchor(frame.FromLogical(32d, 0d, 0d));

            Assert.AreEqual(0, captured.Revision);
            Assert.AreEqual(1, frame.ProjectionSnapshot.Revision);
            for (int i = 0; i < probes.Length; i++)
                Assert.That(Vector3.Distance(shift.TransformPoint(before[i]), frame.LogicalToScene(probes[i])),
                    Is.LessThan(.001f), $"Off-axis probe {i} changed chart during reanchor.");
        }

        [Test]
        public void RepeatedReanchorsComposeToTheCurrentProjectionSnapshot()
        {
            var frame = new CurvedWorldFrame(600f, 16f);
            InfiniteWorldPosition probe = frame.FromLogical(175d, -260d, 11d);
            Vector3 original = frame.LogicalToScene(probe);

            CurvedWorldFrameShift first = frame.Reanchor(frame.FromLogical(32d, 12d, 0d));
            CurvedWorldFrameShift second = frame.Reanchor(frame.FromLogical(58d, 36d, 0d));
            CurvedWorldFrameShift third = frame.Reanchor(frame.FromLogical(83d, 58d, 0d));
            CurvedWorldFrameShift combined = first.Then(second).Then(third);

            Assert.AreEqual(3, frame.Revision);
            Assert.That(Vector3.Distance(combined.TransformPoint(original), frame.LogicalToScene(probe)),
                Is.LessThan(.001f));
        }

        [Test]
        public void MeshRequestKeepsTheProjectionSnapshotCapturedWhenScheduled()
        {
            TerrainWorldSettings settings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            Mesh mesh = null;
            try
            {
                var data = new TerrainChunkData(settings, new TerrainChunkId(2, 0, 9), null, false);
                for (int z = 0; z <= data.Resolution; z++)
                for (int y = 0; y <= data.Resolution; y++)
                for (int x = 0; x <= data.Resolution; x++)
                {
                    data.Density[data.Index(x, y, z)] = 8f - y * data.VoxelSize;
                    data.Material[data.Index(x, y, z)] = 1;
                }

                var frame = new CurvedWorldFrame(600f, settings.ChunkSize);
                CurvedWorldProjectionSnapshot snapshot = frame.ProjectionSnapshot;
                using (TerrainMeshBuildRequest request = TerrainMeshBuilder.Schedule(data, Vector3.zero, 0,
                           TransitionFaceMask.None, data.Version, null, null, snapshot))
                {
                    frame.Reanchor(frame.FromLogical(32d, 0d, 0d));
                    frame.Reanchor(frame.FromLogical(64d, 12d, 0d));
                    mesh = request.Complete();
                    Assert.AreEqual(snapshot.Revision, request.FrameRevision);
                }

                Assert.IsNotNull(mesh);
                Assert.Greater(mesh.vertexCount, 0);
            }
            finally
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void AdjacentChunksBuiltOnOppositeSidesOfAReanchorShareTheirBoundary()
        {
            TerrainWorldSettings settings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            Mesh oldMesh = null, newMesh = null;
            try
            {
                var left = new TerrainChunkData(settings, new TerrainChunkId(0, 0, 0), null, false);
                var right = new TerrainChunkData(settings, new TerrainChunkId(1, 0, 0), null, false);
                PopulateSharedSlope(left);
                PopulateSharedSlope(right);

                var frame = new CurvedWorldFrame(600f, settings.ChunkSize);
                CurvedWorldProjectionSnapshot oldSnapshot = frame.ProjectionSnapshot;
                using (TerrainMeshBuildRequest request = TerrainMeshBuilder.Schedule(left, Vector3.zero, 0,
                           TransitionFaceMask.None, left.Version, null, null, oldSnapshot))
                    oldMesh = request.Complete();

                CurvedWorldFrameShift shift = frame.Reanchor(frame.FromLogical(32d, 0d, 0d));
                CurvedWorldProjectionSnapshot newSnapshot = frame.ProjectionSnapshot;
                using (TerrainMeshBuildRequest request = TerrainMeshBuilder.Schedule(right,
                           new Vector3(32f, 0f, 0f), 0, TransitionFaceMask.None, right.Version,
                           null, null, newSnapshot))
                    newMesh = request.Complete();

                List<Vector3> oldBoundary = CollectLogicalBoundary(oldMesh, shift, frame, settings.ChunkSize);
                List<Vector3> newBoundary = CollectLogicalBoundary(newMesh, default, frame, settings.ChunkSize);
                Assert.That(oldBoundary.Count, Is.GreaterThan(0));
                Assert.AreEqual(oldBoundary.Count, newBoundary.Count);
                AssertBoundaryMatches(oldBoundary, newBoundary);
                AssertBoundaryMatches(newBoundary, oldBoundary);
            }
            finally
            {
                if (oldMesh != null) Object.DestroyImmediate(oldMesh);
                if (newMesh != null) Object.DestroyImmediate(newMesh);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ReanchoringInSmallStepsPreservesMonotonicLogicalTravelAcrossFiveKilometres()
        {
            const double distance = 5000d;
            const double maxStep = 32d;
            var frame = new CurvedWorldFrame(600f, 16f);
            double previousLogicalX = 0d;
            int steps = 0;

            while (previousLogicalX < distance)
            {
                double step = System.Math.Min(maxStep, distance - previousLogicalX);
                InfiniteWorldPosition nextAnchor = frame.FromLogical(previousLogicalX + step, 0d, 0d);
                Vector3 oldScenePosition = frame.LogicalToScene(nextAnchor);

                CurvedWorldFrameShift shift = frame.Reanchor(nextAnchor);
                Vector3 newScenePosition = shift.TransformPoint(oldScenePosition);
                InfiniteWorldPosition recovered = frame.SceneToLogical(newScenePosition);
                double recoveredX = recovered.LogicalX(16f);

                AssertFinite(oldScenePosition, $"Old scene position at step {steps} must stay finite.");
                AssertFinite(newScenePosition, $"Rebased scene position at step {steps} must stay finite.");
                Assert.That(recoveredX, Is.GreaterThan(previousLogicalX),
                    $"Logical X stopped increasing at step {steps}.");
                Assert.That(System.Math.Abs(recoveredX - (previousLogicalX + step)), Is.LessThan(.02d),
                    $"Logical X drifted at step {steps}.");

                previousLogicalX = recoveredX;
                steps++;
            }

            Assert.That(steps, Is.GreaterThan(100));
            Assert.That(previousLogicalX, Is.EqualTo(distance).Within(.02d));
        }

        [TestCase(-1d, 0d)]
        [TestCase(0d, 1d)]
        [TestCase(0d, -1d)]
        [TestCase(1d, 1d)]
        public void ReanchoringPreservesUnwrappedFiveKilometreRoutes(double directionX, double directionZ)
        {
            const double distance = 5000d;
            const double stepSize = 32d;
            double length = System.Math.Sqrt(directionX * directionX + directionZ * directionZ);
            directionX /= length;
            directionZ /= length;
            var frame = new CurvedWorldFrame(600f, 16f);

            for (double travelled = stepSize; travelled <= distance + stepSize; travelled += stepSize)
            {
                double expectedDistance = System.Math.Min(travelled, distance);
                double expectedX = directionX * expectedDistance;
                double expectedZ = directionZ * expectedDistance;
                InfiniteWorldPosition next = frame.FromLogical(expectedX, expectedZ, 0d);
                Vector3 oldScene = frame.LogicalToScene(next);
                CurvedWorldFrameShift shift = frame.Reanchor(next);
                InfiniteWorldPosition recovered = frame.SceneToLogical(shift.TransformPoint(oldScene));

                Assert.That(recovered.LogicalX(16d), Is.EqualTo(expectedX).Within(.03d));
                Assert.That(recovered.LogicalZ(16d), Is.EqualTo(expectedZ).Within(.03d));
                if (expectedDistance >= distance) break;
            }
        }

        [Test]
        public void LongChunkAddressesBeyondIntRangeRemainDistinct()
        {
            long x = (long)int.MaxValue + 123456789L;
            long z = (long)int.MinValue - 987654321L;
            var first = new TerrainChunkId(x, -7, z);
            var same = new TerrainChunkId(x, -7, z);
            var adjacent = new TerrainChunkId(x + 1, -7, z);
            var addresses = new HashSet<TerrainChunkId> { first, adjacent };

            Assert.IsTrue(first.Equals(same));
            Assert.IsFalse(first.Equals(adjacent));
            Assert.AreEqual(2, addresses.Count);
            Assert.IsTrue(addresses.Contains(same));
            Assert.IsTrue(addresses.Contains(adjacent));
            StringAssert.Contains(x.ToString(), first.ToString());
            StringAssert.Contains(z.ToString(), first.ToString());
        }

        [Test]
        public void CurvedMeshProjectionProducesFiniteVerticesAndNormals()
        {
            TerrainWorldSettings settings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            Mesh mesh = null;
            try
            {
                var id = new TerrainChunkId(10, 4, -7);
                var data = new TerrainChunkData(settings, id, null, false);
                for (int z = 0; z <= data.Resolution; z++)
                for (int y = 0; y <= data.Resolution; y++)
                for (int x = 0; x <= data.Resolution; x++)
                {
                    int index = data.Index(x, y, z);
                    data.Density[index] = 8f - y * data.VoxelSize;
                    data.Material[index] = 1;
                }

                var frame = new CurvedWorldFrame(600f, data.VoxelSize * data.Resolution);
                using (TerrainMeshBuildRequest request = TerrainMeshBuilder.Schedule(
                           data, Vector3.zero, 0, TransitionFaceMask.None, data.Version, null, frame))
                    mesh = request.Complete();

                Assert.IsNotNull(mesh);
                Assert.Greater(mesh.vertexCount, 0);
                foreach (Vector3 vertex in mesh.vertices)
                {
                    Assert.IsFalse(float.IsNaN(vertex.x) || float.IsInfinity(vertex.x));
                    Assert.IsFalse(float.IsNaN(vertex.y) || float.IsInfinity(vertex.y));
                    Assert.IsFalse(float.IsNaN(vertex.z) || float.IsInfinity(vertex.z));
                }
                foreach (Vector3 normal in mesh.normals)
                {
                    Assert.IsFalse(float.IsNaN(normal.x) || float.IsInfinity(normal.x));
                    Assert.IsFalse(float.IsNaN(normal.y) || float.IsInfinity(normal.y));
                    Assert.IsFalse(float.IsNaN(normal.z) || float.IsInfinity(normal.z));
                    Assert.That(normal.sqrMagnitude, Is.GreaterThan(.5f));
                }
            }
            finally
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(settings);
            }
        }

        private static void AssertFinite(Vector3 value, string message)
        {
            Assert.IsFalse(float.IsNaN(value.x) || float.IsInfinity(value.x), message);
            Assert.IsFalse(float.IsNaN(value.y) || float.IsInfinity(value.y), message);
            Assert.IsFalse(float.IsNaN(value.z) || float.IsInfinity(value.z), message);
        }

        private static void PopulateSharedSlope(TerrainChunkData data)
        {
            for (int z = 0; z <= data.Resolution; z++)
            for (int y = 0; y <= data.Resolution; y++)
            for (int x = 0; x <= data.Resolution; x++)
            {
                Vector3 point = data.WorldPoint(x, y, z);
                int index = data.Index(x, y, z);
                data.Density[index] = 8f + point.z * .125f - point.y;
                data.Material[index] = 1;
            }
        }

        private static List<Vector3> CollectLogicalBoundary(Mesh mesh, CurvedWorldFrameShift shift,
            CurvedWorldFrame currentFrame, float boundaryX)
        {
            var result = new List<Vector3>();
            foreach (Vector3 vertex in mesh.vertices)
            {
                Vector3 world = shift.Rotation == default ? vertex : shift.TransformPoint(vertex);
                InfiniteWorldPosition logical = currentFrame.SceneToLogical(world);
                if (System.Math.Abs(logical.LogicalX(16d) - boundaryX) < .002d)
                    result.Add(world);
            }
            return result;
        }

        private static void AssertBoundaryMatches(List<Vector3> source, List<Vector3> destination)
        {
            foreach (Vector3 point in source)
            {
                float closest = float.PositiveInfinity;
                foreach (Vector3 candidate in destination)
                    closest = Mathf.Min(closest, Vector3.Distance(point, candidate));
                Assert.That(closest, Is.LessThan(.001f));
            }
        }
    }
}
