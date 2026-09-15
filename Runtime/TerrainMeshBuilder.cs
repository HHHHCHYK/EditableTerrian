using System.Collections.Generic;
using UnityEngine;

namespace Humanier.Terrain
{
    internal static class TerrainMeshBuilder
    {
        private static readonly int[,] Tetrahedra =
        {
            { 0, 5, 1, 6 }, { 0, 1, 2, 6 }, { 0, 2, 3, 6 },
            { 0, 3, 7, 6 }, { 0, 7, 4, 6 }, { 0, 4, 5, 6 }
        };
        private static readonly int[,] Edges = { { 0, 1 }, { 0, 2 }, { 0, 3 }, { 1, 2 }, { 1, 3 }, { 2, 3 } };
        private static readonly int[,] Corners = { { 0, 0, 0 }, { 1, 0, 0 }, { 1, 0, 1 }, { 0, 0, 1 }, { 0, 1, 0 }, { 1, 1, 0 }, { 1, 1, 1 }, { 0, 1, 1 } };

        public static Mesh Build(TerrainChunkData data, Vector3 origin, int lod)
        {
            int resolution = data.Resolution;
            int stride = Mathf.Min(1 << Mathf.Clamp(lod, 0, 5), resolution);
            var vertices = new List<Vector3>(); var triangles = new List<int>(); var colors = new List<Color>();
            var p = new Vector3[8]; var d = new float[8]; var intersections = new Vector3[4];
            for (int z = 0; z < resolution; z += stride)
            for (int y = 0; y < resolution; y += stride)
            for (int x = 0; x < resolution; x += stride)
            {
                FillCube(data, x, y, z, stride, p, d, origin);
                bool hasSolid = false, hasAir = false;
                for (int i = 0; i < 8; i++) { hasSolid |= d[i] > 0f; hasAir |= d[i] <= 0f; }
                if (!hasSolid || !hasAir) continue;
                for (int t = 0; t < 6; t++) PolygonizeTetra(data, origin, p, d, Tetrahedra[t, 0], Tetrahedra[t, 1], Tetrahedra[t, 2], Tetrahedra[t, 3], intersections, vertices, colors, triangles);
            }
            if (vertices.Count == 0) return null;
            var mesh = new Mesh { indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetTriangles(triangles, 0, true); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        private static void FillCube(TerrainChunkData data, int x, int y, int z, int stride, Vector3[] p, float[] d, Vector3 origin)
        {
            for (int i = 0; i < 8; i++)
            {
                int sx = x + Corners[i, 0] * stride, sy = y + Corners[i, 1] * stride, sz = z + Corners[i, 2] * stride;
                p[i] = data.WorldPoint(sx, sy, sz) - origin; d[i] = data.GetDensity(sx, sy, sz);
            }
        }

        private static void PolygonizeTetra(TerrainChunkData data, Vector3 origin, Vector3[] p, float[] d, int a, int b, int c, int e, Vector3[] intersections, List<Vector3> vertices, List<Color> colors, List<int> triangles)
        {
            int count = 0;
            for (int edge = 0; edge < 6; edge++)
            {
                int i = TetraIndex(a, b, c, e, Edges[edge, 0]), j = TetraIndex(a, b, c, e, Edges[edge, 1]);
                if ((d[i] > 0f) == (d[j] > 0f)) continue;
                float t = d[i] / (d[i] - d[j]); intersections[count++] = Vector3.Lerp(p[i], p[j], t);
            }
            if (count < 3) return;
            Vector3 solidCenter = Vector3.zero; int solids = 0;
            AddSolid(p, d, a, ref solidCenter, ref solids); AddSolid(p, d, b, ref solidCenter, ref solids); AddSolid(p, d, c, ref solidCenter, ref solids); AddSolid(p, d, e, ref solidCenter, ref solids);
            if (solids > 0) solidCenter /= solids;
            AddTriangle(intersections[0], intersections[1], intersections[2], solidCenter, data, origin, vertices, colors, triangles);
            if (count == 4) AddTriangle(intersections[0], intersections[2], intersections[3], solidCenter, data, origin, vertices, colors, triangles);
        }
        private static int TetraIndex(int a, int b, int c, int d, int index) => index == 0 ? a : index == 1 ? b : index == 2 ? c : d;
        private static void AddSolid(Vector3[] p, float[] d, int index, ref Vector3 center, ref int count) { if (d[index] > 0f) { center += p[index]; count++; } }
        private static void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 solidCenter, TerrainChunkData data, Vector3 origin, List<Vector3> vertices, List<Color> colors, List<int> triangles)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), ((a + b + c) / 3f) - solidCenter) < 0f) { Vector3 swap = b; b = c; c = swap; }
            int index = vertices.Count; vertices.Add(a); vertices.Add(b); vertices.Add(c);
            Color color = ColorFor(data, (a + b + c) / 3f + origin); colors.Add(color); colors.Add(color); colors.Add(color);
            triangles.Add(index); triangles.Add(index + 1); triangles.Add(index + 2);
        }
        private static Color ColorFor(TerrainChunkData data, Vector3 point)
        {
            int x = Mathf.Clamp(Mathf.RoundToInt(point.x / data.VoxelSize) - data.Id.x * data.Resolution, 0, data.Resolution);
            int y = Mathf.Clamp(Mathf.RoundToInt(point.y / data.VoxelSize) - data.Id.y * data.Resolution, 0, data.Resolution);
            int z = Mathf.Clamp(Mathf.RoundToInt(point.z / data.VoxelSize) - data.Id.z * data.Resolution, 0, data.Resolution);
            switch (data.GetMaterial(x, y, z))
            {
                case 2: return new Color(.78f, .58f, .29f);
                case 3: return new Color(.38f, .42f, .40f);
                default: return new Color(.25f, .58f, .26f);
            }
        }
    }
}
