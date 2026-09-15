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

        public static Mesh Build(TerrainChunkData data, Vector3 origin)
        {
            int resolution = data.Resolution;
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            var p = new Vector3[8]; var d = new float[8];
            for (int z = 0; z < resolution; z++)
            for (int y = 0; y < resolution; y++)
            for (int x = 0; x < resolution; x++)
            {
                FillCube(data, x, y, z, p, d, origin);
                bool hasSolid = false, hasAir = false;
                for (int i = 0; i < 8; i++) { hasSolid |= d[i] > 0f; hasAir |= d[i] <= 0f; }
                if (!hasSolid || !hasAir) continue;
                for (int t = 0; t < 6; t++) PolygonizeTetra(p, d, Tetrahedra[t, 0], Tetrahedra[t, 1], Tetrahedra[t, 2], Tetrahedra[t, 3], vertices, triangles);
            }
            if (vertices.Count == 0) return null;
            var mesh = new Mesh { indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0, true); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        private static void FillCube(TerrainChunkData data, int x, int y, int z, Vector3[] p, float[] d, Vector3 origin)
        {
            int[] ox = { 0, 1, 1, 0, 0, 1, 1, 0 }; int[] oy = { 0, 0, 0, 0, 1, 1, 1, 1 }; int[] oz = { 0, 0, 1, 1, 0, 0, 1, 1 };
            for (int i = 0; i < 8; i++) { p[i] = data.WorldPoint(x + ox[i], y + oy[i], z + oz[i]) - origin; d[i] = data.GetDensity(x + ox[i], y + oy[i], z + oz[i]); }
        }

        private static void PolygonizeTetra(Vector3[] p, float[] d, int a, int b, int c, int e, List<Vector3> vertices, List<int> triangles)
        {
            int[] ids = { a, b, c, e }; var intersections = new List<Vector3>(4);
            for (int edge = 0; edge < 6; edge++)
            {
                int i = ids[Edges[edge, 0]], j = ids[Edges[edge, 1]];
                if ((d[i] > 0f) == (d[j] > 0f)) continue;
                float t = d[i] / (d[i] - d[j]); intersections.Add(Vector3.Lerp(p[i], p[j], t));
            }
            if (intersections.Count < 3) return;
            AddTriangle(intersections[0], intersections[1], intersections[2], vertices, triangles);
            if (intersections.Count == 4) AddTriangle(intersections[0], intersections[2], intersections[3], vertices, triangles);
        }
        private static void AddTriangle(Vector3 a, Vector3 b, Vector3 c, List<Vector3> vertices, List<int> triangles)
        {
            int index = vertices.Count; vertices.Add(a); vertices.Add(b); vertices.Add(c); triangles.Add(index); triangles.Add(index + 1); triangles.Add(index + 2);
        }
    }
}
