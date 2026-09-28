using System;
using UnityEngine;

namespace Humanier.Terrain
{
    /// <summary>
    /// Local spherical chart backed by unbounded double precision logical coordinates.
    /// The chart is periodically advanced while stable logical addresses keep growing.
    /// </summary>
    public sealed class CurvedWorldFrame
    {
        private readonly double chunkSize;
        private double anchorX;
        private double anchorZ;

        public CurvedWorldFrame(float radius, float chunkSize)
        {
            Radius = Math.Max(32d, radius);
            this.chunkSize = Math.Max(.001d, chunkSize);
        }

        public double Radius { get; }
        public InfiniteWorldPosition Anchor => FromLogical(anchorX, anchorZ, 0d);
        public Vector3 SceneSphereCenter => new Vector3(0f, (float)-Radius, 0f);

        public InfiniteWorldPosition FromLogical(double x, double z, double radialHeight)
        {
            long cx = FloorToLong(x / chunkSize);
            long cz = FloorToLong(z / chunkSize);
            return new InfiniteWorldPosition(cx, cz, x - cx * chunkSize, z - cz * chunkSize, radialHeight);
        }

        public Vector3 LogicalToScene(InfiniteWorldPosition position) =>
            LogicalToScene(position.LogicalX(chunkSize), position.LogicalZ(chunkSize), position.radialHeight);

        public Vector3 LogicalToScene(double logicalX, double logicalZ, double radialHeight)
        {
            double dx = logicalX - anchorX;
            double dz = logicalZ - anchorZ;
            double arc = Math.Sqrt(dx * dx + dz * dz);
            Vector3 up;
            if (arc < 1e-9)
                up = Vector3.up;
            else
            {
                double angle = arc / Radius;
                double horizontal = Math.Sin(angle) / arc;
                up = new Vector3((float)(dx * horizontal), (float)Math.Cos(angle), (float)(dz * horizontal));
            }
            return SceneSphereCenter + up * (float)(Radius + radialHeight);
        }

        public InfiniteWorldPosition SceneToLogical(Vector3 scenePosition)
        {
            Vector3 radial = scenePosition - SceneSphereCenter;
            double magnitude = radial.magnitude;
            if (magnitude < 1e-8) return FromLogical(anchorX, anchorZ, -Radius);
            Vector3 up = radial / (float)magnitude;
            double horizontal = Math.Sqrt(up.x * up.x + up.z * up.z);
            double arc = Math.Atan2(horizontal, up.y) * Radius;
            double dx = horizontal < 1e-9 ? 0d : arc * up.x / horizontal;
            double dz = horizontal < 1e-9 ? 0d : arc * up.z / horizontal;
            return FromLogical(anchorX + dx, anchorZ + dz, magnitude - Radius);
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
            anchorX = newAnchor.LogicalX(chunkSize);
            anchorZ = newAnchor.LogicalZ(chunkSize);
            return new CurvedWorldFrameShift(SceneSphereCenter, rotation);
        }

        private static long FloorToLong(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
            return checked((long)Math.Floor(value));
        }
    }
}
