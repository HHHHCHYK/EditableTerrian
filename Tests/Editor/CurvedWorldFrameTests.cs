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
    }
}
