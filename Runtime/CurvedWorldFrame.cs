using System;
using UnityEngine;

namespace Humanier.Terrain
{
    /// <summary>
    /// Immutable mapping captured by a mesh request. Reanchors compose a rigid
    /// transform onto this chart instead of choosing a new exponential-map chart,
    /// so every logical point keeps exactly the same relative placement.
    /// </summary>
    public readonly struct CurvedWorldProjectionSnapshot
    {
        private readonly double radius;
        private readonly double chartAnchorX;
        private readonly double chartAnchorZ;
        private readonly Quaternion rotation;
        private readonly Vector3 translation;

        public int Revision { get; }
        public double Radius => radius;
        public Vector3 SceneSphereCenter => TransformPoint(new Vector3(0f, (float)-radius, 0f));

        internal CurvedWorldProjectionSnapshot(double radius, double chartAnchorX, double chartAnchorZ,
            Quaternion rotation, Vector3 translation, int revision)
        {
            this.radius = radius;
            this.chartAnchorX = chartAnchorX;
            this.chartAnchorZ = chartAnchorZ;
            this.rotation = rotation;
            this.translation = translation;
            Revision = revision;
        }

        public Vector3 LogicalToScene(double logicalX, double logicalZ, double radialHeight)
        {
            double dx = logicalX - chartAnchorX;
            double dz = logicalZ - chartAnchorZ;
            double arc = Math.Sqrt(dx * dx + dz * dz);
            Vector3 up;
            if (arc < 1e-9d)
                up = Vector3.up;
            else
            {
                double angle = arc / radius;
                double horizontal = Math.Sin(angle) / arc;
                up = new Vector3((float)(dx * horizontal), (float)Math.Cos(angle), (float)(dz * horizontal));
            }

            Vector3 point = new Vector3(0f, (float)-radius, 0f) + up * (float)(radius + radialHeight);
            return TransformPoint(point);
        }

        internal Vector3 ProjectFlatScene(Vector3 flatScene, double logicalOriginX,
            double logicalOriginY, double logicalOriginZ) =>
            LogicalToScene(logicalOriginX + flatScene.x, logicalOriginZ + flatScene.z,
                logicalOriginY + flatScene.y);

        internal Vector3 TransformPoint(Vector3 point) => rotation * point + translation;
        internal Vector3 InverseTransformPoint(Vector3 point) => Quaternion.Inverse(rotation) * (point - translation);
        internal Vector3 TransformDirection(Vector3 direction) => rotation * direction;
    }

    /// <summary>
    /// Local spherical chart backed by unbounded double precision logical coordinates.
    /// The projection chart is immutable across reanchors; reanchors only compose
    /// rigid transforms. As with any spherical exponential map, this chart is not
    /// globally one-to-one outside its local injective region.
    /// </summary>
    public sealed class CurvedWorldFrame
    {
        private readonly double chunkSize;
        private readonly double chartAnchorX;
        private readonly double chartAnchorZ;
        private double anchorX;
        private double anchorZ;
        private Quaternion projectionRotation = Quaternion.identity;
        private Vector3 projectionTranslation;
        private int revision;

        public CurvedWorldFrame(float radius, float chunkSize)
        {
            Radius = Math.Max(32d, radius);
            this.chunkSize = Math.Max(.001d, chunkSize);
        }

        public double Radius { get; }
        public int Revision => revision;
        public InfiniteWorldPosition Anchor => FromLogical(anchorX, anchorZ, 0d);
        public CurvedWorldProjectionSnapshot ProjectionSnapshot => new CurvedWorldProjectionSnapshot(
            Radius, chartAnchorX, chartAnchorZ, projectionRotation, projectionTranslation, revision);
        public Vector3 SceneSphereCenter => ProjectionSnapshot.SceneSphereCenter;

        public InfiniteWorldPosition FromLogical(double x, double z, double radialHeight)
        {
            long cx = FloorToLong(x / chunkSize);
            long cz = FloorToLong(z / chunkSize);
            return new InfiniteWorldPosition(cx, cz, x - cx * chunkSize, z - cz * chunkSize, radialHeight);
        }

        public Vector3 LogicalToScene(InfiniteWorldPosition position) =>
            ProjectionSnapshot.LogicalToScene(position.LogicalX(chunkSize), position.LogicalZ(chunkSize), position.radialHeight);

        public Vector3 LogicalToScene(double logicalX, double logicalZ, double radialHeight) =>
            ProjectionSnapshot.LogicalToScene(logicalX, logicalZ, radialHeight);

        public InfiniteWorldPosition SceneToLogical(Vector3 scenePosition)
        {
            CurvedWorldProjectionSnapshot snapshot = ProjectionSnapshot;
            Vector3 radial = snapshot.InverseTransformPoint(scenePosition) - new Vector3(0f, (float)-Radius, 0f);
            double magnitude = radial.magnitude;
            if (magnitude < 1e-8d) return FromLogical(anchorX, anchorZ, -Radius);
            Vector3 up = radial / (float)magnitude;
            double horizontal = Math.Sqrt(up.x * up.x + up.z * up.z);
            double theta = Math.Atan2(horizontal, up.y);
            double dx, dz;
            if (horizontal < 1e-8d)
            {
                // At a pole, azimuth is undefined. Keep the branch nearest the
                // current logical anchor, which makes repeated reanchors stable.
                double anchorDx = anchorX - chartAnchorX;
                double anchorDz = anchorZ - chartAnchorZ;
                double anchorLength = Math.Sqrt(anchorDx * anchorDx + anchorDz * anchorDz);
                double directionX = anchorLength < 1e-8d ? 1d : anchorDx / anchorLength;
                double directionZ = anchorLength < 1e-8d ? 0d : anchorDz / anchorLength;
                double angle = NearestPoleAngle(theta, anchorLength / Radius);
                dx = Radius * angle * directionX;
                dz = Radius * angle * directionZ;
            }
            else
            {
                double dirX = up.x / horizontal;
                double dirZ = up.z / horizontal;
                FindNearestBranch(theta, dirX, dirZ, out dx, out dz);
                FindNearestBranch(2d * Math.PI - theta, -dirX, -dirZ, out double otherX, out double otherZ);
                if (DistanceSquared(otherX, otherZ, anchorX - chartAnchorX, anchorZ - chartAnchorZ) <
                    DistanceSquared(dx, dz, anchorX - chartAnchorX, anchorZ - chartAnchorZ))
                { dx = otherX; dz = otherZ; }
            }

            return FromLogical(chartAnchorX + dx, chartAnchorZ + dz, magnitude - Radius);
        }

        public Vector3 UpAt(Vector3 scenePosition)
        {
            Vector3 radial = scenePosition - SceneSphereCenter;
            return radial.sqrMagnitude > 1e-10f ? radial.normalized : Vector3.up;
        }

        public Quaternion PoseAt(InfiniteWorldPosition position, Vector3 logicalForward)
        {
            Vector3 point = LogicalToScene(position);
            Vector3 up = UpAt(point);
            Vector3 ahead = LogicalToScene(position.LogicalX(chunkSize) + logicalForward.x,
                position.LogicalZ(chunkSize) + logicalForward.z, position.radialHeight) - point;
            ahead = Vector3.ProjectOnPlane(ahead, up).normalized;
            return Quaternion.LookRotation(ahead.sqrMagnitude > 1e-8f ? ahead : Vector3.forward, up);
        }

        public CurvedWorldFrameShift Reanchor(InfiniteWorldPosition newAnchor)
        {
            Vector3 oldPoint = LogicalToScene(newAnchor);
            Vector3 oldUp = UpAt(oldPoint);
            Quaternion rotation = Quaternion.FromToRotation(oldUp, Vector3.up);
            CurvedWorldFrameShift shift = new CurvedWorldFrameShift(SceneSphereCenter, rotation);

            projectionTranslation = shift.Rotation * projectionTranslation + shift.Translation;
            projectionRotation = shift.Rotation * projectionRotation;
            anchorX = newAnchor.LogicalX(chunkSize);
            anchorZ = newAnchor.LogicalZ(chunkSize);
            revision = unchecked(revision + 1);
            return shift;
        }

        private void FindNearestBranch(double angleBase, double directionX, double directionZ,
            out double dx, out double dz)
        {
            double targetX = anchorX - chartAnchorX;
            double targetZ = anchorZ - chartAnchorZ;
            double targetRadius = targetX * directionX + targetZ * directionZ;
            double period = 2d * Math.PI;
            double ideal = (targetRadius / Radius - angleBase) / period;
            long k = ideal <= 0d ? 0L : checked((long)Math.Floor(ideal));
            double bestDistance = double.PositiveInfinity;
            dx = dz = 0d;
            for (int i = 0; i < 2; i++)
            {
                long candidateK = k + i;
                double angle = angleBase + period * candidateK;
                if (angle < 0d) continue;
                double candidateX = Radius * angle * directionX;
                double candidateZ = Radius * angle * directionZ;
                double distance = DistanceSquared(candidateX, candidateZ, targetX, targetZ);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                dx = candidateX;
                dz = candidateZ;
            }
        }

        private static double NearestPoleAngle(double theta, double targetAngle)
        {
            double period = 2d * Math.PI;
            double baseAngle = theta < Math.PI * .5d ? 0d : Math.PI;
            double ideal = (targetAngle - baseAngle) / period;
            long k = ideal <= 0d ? 0L : checked((long)Math.Floor(ideal));
            double first = baseAngle + period * k;
            double second = baseAngle + period * (k + 1L);
            return Math.Abs(first - targetAngle) <= Math.Abs(second - targetAngle) ? first : second;
        }

        private static double DistanceSquared(double x, double z, double targetX, double targetZ)
        {
            double dx = x - targetX, dz = z - targetZ;
            return dx * dx + dz * dz;
        }

        private static long FloorToLong(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
            return checked((long)Math.Floor(value));
        }
    }
}
