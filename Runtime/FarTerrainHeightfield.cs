using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;

namespace Humanier.Terrain
{
    /// <summary>
    /// Visual-only heightfield used outside the resident voxel terrain. It is
    /// deliberately independent from colliders and decoration streaming.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class FarTerrainHeightfield : MonoBehaviour
    {
        private static readonly ProfilerMarker CoverageMaskMarker = new ProfilerMarker("Terrain.FarCoverage.Update");
        private static readonly int FarMaskEnabledId = Shader.PropertyToID("_FarMaskEnabled");
        private static readonly int FarCoverageMaskId = Shader.PropertyToID("_FarCoverageMask");
        private static readonly int FarMaskOriginInvSizeId = Shader.PropertyToID("_FarMaskOriginInvSize");
        private static readonly int FarHeightOffsetId = Shader.PropertyToID("_FarHeightOffset");
        private static readonly int FarFadeOriginId = Shader.PropertyToID("_FarFadeOrigin");
        private static readonly int FarFadeStartEndId = Shader.PropertyToID("_FarFadeStartEnd");
        private readonly Dictionary<Vector2Int, Patch> patches = new Dictionary<Vector2Int, Patch>();
        private readonly HashSet<Vector2Int> requiredPatches = new HashSet<Vector2Int>();
        private readonly HashSet<Vector2Int> pendingPatches = new HashSet<Vector2Int>();
        private readonly Queue<Vector2Int> pendingQueue = new Queue<Vector2Int>();
        private readonly HashSet<Vector2Int> dirtyPatches = new HashSet<Vector2Int>();
        private readonly Queue<Vector2Int> dirtyQueue = new Queue<Vector2Int>();
        private readonly HashSet<Vector2Int> dirtyCoverageColumns = new HashSet<Vector2Int>();
        private readonly Dictionary<Vector2Int, List<TerrainEditSummary>> editBuckets = new Dictionary<Vector2Int, List<TerrainEditSummary>>();
        private readonly List<TerrainEditSummary> editSummaries = new List<TerrainEditSummary>();
        private TerrainWorld world;
        private TerrainWorldSettings settings;
        private Material material;
        private bool ownsMaterial;
        private Texture2D coverageMask;
        private byte[] coverageBytes;
        private Vector2Int lastCenter;
        private Vector2Int lastMaskCenter = new Vector2Int(int.MinValue, int.MinValue);
        private Vector2 focusXZ;
        private bool configured;
        private bool coverageDirty = true;

        private sealed class Patch
        {
            public GameObject gameObject;
            public Mesh mesh;
            public Vector2Int id;
        }

        internal void Configure(TerrainWorld owner, TerrainWorldSettings configuration)
        {
            if (configured) throw new System.InvalidOperationException("Far terrain heightfield is configured once.");
            world = owner;
            settings = configuration;
            if (world == null || settings == null) throw new System.ArgumentException("Far terrain requires a world and settings.");
            if (settings.terrainMaterial != null)
            {
                material = new Material(settings.terrainMaterial) { name = "Runtime Far Terrain Material" };
                ownsMaterial = true;
            }
            else
            {
                Shader shader = Shader.Find("Humanier/Terrain Low Poly");
                if (shader != null) { material = new Material(shader) { name = "Runtime Far Terrain Material" }; ownsMaterial = true; }
            }
            if (material == null) throw new System.InvalidOperationException("Far terrain material could not be created.");
            material.SetFloat(FarMaskEnabledId, 1f);
            // The far surface uses the same authoritative height as the near
            // terrain. A fixed one-metre offset creates a visible step at the
            // hand-off and is not a valid seam treatment.
            material.SetFloat(FarHeightOffsetId, 0f);
            material.SetVector(FarFadeStartEndId, new Vector4(settings.viewDistance * .75f, settings.viewDistance * 1.25f, 0f, 0f));
            configured = true;
            world.EditSummaryCommitted += OnEditSummaryCommitted;
            world.OriginOffsetChanged += OnOriginOffsetChanged;
            world.ChunkMeshApplied += OnTerrainChunkChanged;
            world.ChunkUnloaded += OnTerrainChunkChanged;
        }

        internal void Tick(Vector3 focus)
        {
            if (!configured) return;
            float patchSize = Mathf.Max(32f, settings.farHeightfieldPatchSize);
            Vector2 nextFocus = new Vector2(focus.x, focus.z);
            UpdateCoverageMask(focus);
            Vector2Int center = new Vector2Int(Mathf.FloorToInt(focus.x / patchSize), Mathf.FloorToInt(focus.z / patchSize));
            float recenterDistance = Mathf.Max(settings.ChunkSize * 8f, 1f);
            bool moved = patches.Count == 0 || center != lastCenter || Vector2.Distance(focusXZ, nextFocus) >= recenterDistance;
            if (moved)
            {
                lastCenter = center;
                focusXZ = nextFocus;
                EnsurePatches(center, patchSize);
            }
            ProcessDirtyPatch(patchSize);
        }

        private void EnsurePatches(Vector2Int center, float patchSize)
        {
            int radius = Mathf.Max(1, Mathf.CeilToInt(settings.farHeightfieldCoverage / patchSize));
            var required = new HashSet<Vector2Int>();
            for (int z = -radius; z <= radius; z++)
            for (int x = -radius; x <= radius; x++)
            {
                Vector2Int id = new Vector2Int(center.x + x, center.y + z);
                required.Add(id);
                if (!patches.ContainsKey(id))
                    QueuePatch(id);
            }
            requiredPatches.Clear();
            foreach (Vector2Int id in required) requiredPatches.Add(id);
            var remove = new List<Vector2Int>();
            foreach (Vector2Int id in patches.Keys)
                if (!required.Contains(id)) remove.Add(id);
            for (int i = 0; i < remove.Count; i++)
            {
                DestroyPatch(remove[i]);
                dirtyPatches.Remove(remove[i]);
                pendingPatches.Remove(remove[i]);
            }
        }

        private void QueuePatch(Vector2Int id)
        {
            if (patches.ContainsKey(id) || !pendingPatches.Add(id)) return;
            pendingQueue.Enqueue(id);
        }

        private void ProcessDirtyPatch(float patchSize)
        {
            int builds = Mathf.Max(1, settings.maxFarPatchesBuiltPerFrame);
            while (builds > 0 && pendingQueue.Count > 0)
            {
                Vector2Int id = pendingQueue.Dequeue();
                if (!pendingPatches.Remove(id) || !requiredPatches.Contains(id)) continue;
                if (patches.ContainsKey(id)) continue;
                Patch patch = BuildPatch(id, patchSize, PatchResolution(id, patchSize));
                if (patch != null) patches.Add(id, patch);
                dirtyPatches.Remove(id);
                builds--;
            }

            int count = dirtyQueue.Count;
            while (builds > 0 && count-- > 0 && dirtyQueue.Count > 0)
            {
                Vector2Int id = dirtyQueue.Dequeue();
                if (!dirtyPatches.Remove(id) || !patches.ContainsKey(id)) continue;

                // Keep the old far mesh while the location is covered by the
                // resident voxel terrain. The dirty mesh is built only when it
                // can actually become visible.
                Vector2 patchCenter = (new Vector2(id.x + .5f, id.y + .5f)) * patchSize;
                if (Vector2.Distance(patchCenter, focusXZ) < settings.viewDistance + patchSize)
                {
                    MarkDirty(id);
                    continue;
                }

                Patch replacement = BuildPatch(id, patchSize, PatchResolution(id, patchSize));
                if (replacement == null) continue;
                Patch old = patches[id];
                patches[id] = replacement;
                DestroyPatch(old);
                builds--;
                break;
            }
        }

        private int PatchResolution(Vector2Int id, float patchSize)
        {
            Vector2 center = (new Vector2(id.x + .5f, id.y + .5f)) * patchSize;
            bool near = Vector2.Distance(center, focusXZ) < settings.viewDistance + patchSize * 2f;
            return Mathf.Clamp(near ? settings.farHeightfieldNearResolution : settings.farHeightfieldResolution, 2, 64);
        }

        private Patch BuildPatch(Vector2Int id, float size, int resolution)
        {
            int side = resolution + 1;
            var vertices = new Vector3[side * side];
            var uvs = new Vector2[vertices.Length];
            var colors = new Color32[vertices.Length];
            var indices = new List<int>(resolution * resolution * 6);
            float originX = id.x * size;
            float originZ = id.y * size;
            for (int z = 0; z <= resolution; z++)
            for (int x = 0; x <= resolution; x++)
            {
                int index = x + side * z;
                float px = originX + size * x / resolution;
                float pz = originZ + size * z / resolution;
                float height = world.SampleSurfaceHeight(new Vector3(px, 0f, pz));
                if (editBuckets.TryGetValue(EditBucket(new Vector2(px, pz)), out List<TerrainEditSummary> candidates))
                    for (int i = 0; i < candidates.Count; i++)
                        height = ApplyEdit(height, new Vector2(px, pz), candidates[i]);
                vertices[index] = new Vector3(px, height, pz);
                uvs[index] = new Vector2((float)x / resolution, (float)z / resolution);
                colors[index] = MaterialColor(world.SampleSurfaceMaterial(new Vector3(px, 0f, pz)));
            }
            for (int z = 0; z < resolution; z++)
            for (int x = 0; x < resolution; x++)
            {
                int a = x + side * z, b = a + 1, c = a + side, d = c + 1;
                indices.Add(a); indices.Add(c); indices.Add(b);
                indices.Add(b); indices.Add(c); indices.Add(d);
            }
            if (indices.Count == 0) return null;
            var mesh = new Mesh { name = $"FarTerrain_{id.x}_{id.y}" };
            mesh.indexFormat = vertices.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = vertices; mesh.colors32 = colors; mesh.uv = uvs; mesh.SetIndices(indices, MeshTopology.Triangles, 0); mesh.RecalculateNormals();
            var go = new GameObject(mesh.name);
            go.transform.SetParent(transform, false);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return new Patch { gameObject = go, mesh = mesh, id = id };
        }

        private void DestroyPatch(Vector2Int id)
        {
            if (!patches.TryGetValue(id, out Patch patch)) return;
            DestroyPatch(patch);
            patches.Remove(id);
        }

        private static void DestroyPatch(Patch patch)
        {
            if (patch == null) return;
            if (patch.mesh != null) Destroy(patch.mesh);
            if (patch.gameObject != null) Destroy(patch.gameObject);
        }

        private void ClearPatches()
        {
            foreach (Patch patch in patches.Values) DestroyPatch(patch);
            patches.Clear();
            dirtyPatches.Clear();
            dirtyQueue.Clear();
            pendingPatches.Clear();
            pendingQueue.Clear();
            requiredPatches.Clear();
        }

        private void UpdateCoverageMask(Vector3 focus)
        {
            using var profileScope = CoverageMaskMarker.Auto();
            float chunkSize = settings.ChunkSize;
            int radius = Mathf.CeilToInt(settings.viewDistance / chunkSize) + 2;
            int side = radius * 2 + 1;
            if (coverageMask == null || coverageMask.width != side)
            {
                if (coverageMask != null) Destroy(coverageMask);
                coverageMask = new Texture2D(side, side, TextureFormat.R8, false, true)
                {
                    name = "Far Terrain Coverage Mask",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                coverageBytes = new byte[side * side];
                material.SetTexture(FarCoverageMaskId, coverageMask);
            }

            int centerX = Mathf.FloorToInt(focus.x / chunkSize);
            int centerZ = Mathf.FloorToInt(focus.z / chunkSize);
            var maskCenter = new Vector2Int(centerX, centerZ);
            if (!coverageDirty && dirtyCoverageColumns.Count == 0 && maskCenter == lastMaskCenter) return;
            int minX = centerX - radius;
            int minZ = centerZ - radius;
            if (coverageDirty || maskCenter != lastMaskCenter)
            {
                System.Array.Clear(coverageBytes, 0, coverageBytes.Length);
                lastMaskCenter = maskCenter;
                for (int z = 0; z < side; z++)
                for (int x = 0; x < side; x++)
                    UpdateCoverageCell(minX + x, minZ + z, minX, minZ, side, chunkSize);
                coverageDirty = false;
                dirtyCoverageColumns.Clear();
            }
            else
            {
                foreach (Vector2Int column in dirtyCoverageColumns)
                    UpdateCoverageCell(column.x, column.y, minX, minZ, side, chunkSize);
                dirtyCoverageColumns.Clear();
            }
            coverageMask.SetPixelData(coverageBytes, 0);
            coverageMask.Apply(false, false);
            float worldSize = side * chunkSize;
            material.SetVector(FarMaskOriginInvSizeId, new Vector4(minX * chunkSize, minZ * chunkSize, 1f / worldSize, side));
            material.SetVector(FarFadeOriginId, new Vector4(focus.x, focus.z, 0f, 0f));
        }

        private void OnTerrainChunkChanged(TerrainChunkId id)
        {
            if (lastMaskCenter.x == int.MinValue)
            {
                coverageDirty = true;
                return;
            }
            dirtyCoverageColumns.Add(new Vector2Int(id.x, id.z));
        }

        private void OnEditSummaryCommitted(TerrainEditSummary summary)
        {
            editSummaries.Add(summary);
            AddToBuckets(summary);
            MarkDirty(summary);
        }

        private void MarkDirty(TerrainEditSummary summary)
        {
            float patchSize = Mathf.Max(32f, settings.farHeightfieldPatchSize);
            Vector2 center = new Vector2(summary.worldCenter.x, summary.worldCenter.z);
            int minX = Mathf.FloorToInt((center.x - summary.radius) / patchSize);
            int maxX = Mathf.FloorToInt((center.x + summary.radius) / patchSize);
            int minZ = Mathf.FloorToInt((center.y - summary.radius) / patchSize);
            int maxZ = Mathf.FloorToInt((center.y + summary.radius) / patchSize);
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++) MarkDirty(new Vector2Int(x, z));
        }

        private void MarkDirty(Vector2Int id)
        {
            if (dirtyPatches.Add(id)) dirtyQueue.Enqueue(id);
        }

        private void AddToBuckets(TerrainEditSummary summary)
        {
            float bucketSize = Mathf.Max(32f, settings.farHeightfieldPatchSize / settings.farHeightfieldResolution);
            Vector2 center = new Vector2(summary.worldCenter.x, summary.worldCenter.z);
            int minX = Mathf.FloorToInt((center.x - summary.radius) / bucketSize);
            int maxX = Mathf.FloorToInt((center.x + summary.radius) / bucketSize);
            int minZ = Mathf.FloorToInt((center.y - summary.radius) / bucketSize);
            int maxZ = Mathf.FloorToInt((center.y + summary.radius) / bucketSize);
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                var key = new Vector2Int(x, z);
                if (!editBuckets.TryGetValue(key, out List<TerrainEditSummary> bucket))
                    editBuckets.Add(key, bucket = new List<TerrainEditSummary>());
                bucket.Add(summary);
            }
        }

        private void OnOriginOffsetChanged(Vector3 compensation)
        {
            transform.localPosition = Vector3.zero;
            coverageDirty = true;
            lastMaskCenter = new Vector2Int(int.MinValue, int.MinValue);
            dirtyCoverageColumns.Clear();
            for (int i = 0; i < editSummaries.Count; i++)
            {
                TerrainEditSummary old = editSummaries[i];
                editSummaries[i] = new TerrainEditSummary(old.mode, old.worldCenter + compensation, old.radius,
                    old.effectiveStrength, old.flattenHeight + compensation.y, old.removedSolidVolume, old.addedSolidVolume);
            }
            editBuckets.Clear();
            for (int i = 0; i < editSummaries.Count; i++) AddToBuckets(editSummaries[i]);
            ClearPatches();
            EnsurePatches(lastCenter, Mathf.Max(32f, settings.farHeightfieldPatchSize));
        }

        private void UpdateCoverageCell(int columnX, int columnZ, int minX, int minZ, int side, float chunkSize)
        {
            int x = columnX - minX;
            int z = columnZ - minZ;
            if (x < 0 || x >= side || z < 0 || z >= side) return;
            float worldX = (columnX + .5f) * chunkSize;
            float worldZ = (columnZ + .5f) * chunkSize;
            var point = new Vector3(worldX, 0f, worldZ);
            float surface = world.SampleSurfaceHeight(point);
            float probeStep = Mathf.Max(.1f, settings.voxelSize);
            bool ready = false;
            for (int probe = -1; probe <= 1 && !ready; probe++)
            {
                point.y = surface + probe * probeStep;
                TerrainChunkId surfaceChunk = world.GetChunkId(point);
                ready = world.TryGetChunkRenderMesh(surfaceChunk);
            }
            coverageBytes[x + side * z] = ready ? byte.MaxValue : (byte)0;
        }

        private static float ApplyEdit(float height, Vector2 point, TerrainEditSummary edit)
        {
            Vector2 center = new Vector2(edit.worldCenter.x, edit.worldCenter.z);
            float radius = Mathf.Max(.1f, edit.radius);
            float distance = Vector2.Distance(point, center);
            if (distance > radius) return height;
            if (Mathf.Abs(edit.worldCenter.y - height) > radius) return height;
            float blend = Mathf.Clamp01(1f - distance / radius) * Mathf.Clamp01(edit.effectiveStrength);
            switch (edit.mode)
            {
                case TerrainBrushMode.Dig: return height - radius * blend;
                case TerrainBrushMode.Fill: return height + radius * blend;
                case TerrainBrushMode.Flatten: return Mathf.Lerp(height, edit.flattenHeight, blend);
                default: return height;
            }
        }

        private Vector2Int EditBucket(Vector2 point)
        {
            float size = Mathf.Max(32f, settings.farHeightfieldPatchSize / settings.farHeightfieldResolution);
            return new Vector2Int(Mathf.FloorToInt(point.x / size), Mathf.FloorToInt(point.y / size));
        }

        private static Color32 MaterialColor(byte materialId)
        {
            return TerrainMaterialPalette.ColorFor(materialId);
        }

        private void OnDestroy()
        {
            if (world != null) world.EditSummaryCommitted -= OnEditSummaryCommitted;
            if (world != null) world.OriginOffsetChanged -= OnOriginOffsetChanged;
            if (world != null) world.ChunkMeshApplied -= OnTerrainChunkChanged;
            if (world != null) world.ChunkUnloaded -= OnTerrainChunkChanged;
            ClearPatches();
            if (coverageMask != null) Destroy(coverageMask);
            if (ownsMaterial && material != null) Destroy(material);
        }
    }
}
