using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Humanier.Terrain
{
    [DisallowMultipleComponent]
    public sealed class TerrainWorld : MonoBehaviour
    {
        [SerializeField] private TerrainWorldSettings settings;
        [SerializeField] private Transform focus;
        [SerializeField] private bool generateColliders = true;
        [SerializeField] private int verticalChunksBelowFocus = 5;
        [SerializeField] private int verticalChunksAboveFocus = 4;
        private readonly Dictionary<TerrainChunkId, LoadedChunk> chunks = new Dictionary<TerrainChunkId, LoadedChunk>();
        private readonly Queue<TerrainChunkId> requestedChunks = new Queue<TerrainChunkId>();
        private readonly HashSet<TerrainChunkId> queuedChunks = new HashSet<TerrainChunkId>();
        private TerrainSessionCache cache;
        private TerrainWorldSettings generatedSettings;
        private Vector3 originOffset;
        private bool cacheWriteBlocked;

        public TerrainWorldSettings Settings => settings != null ? settings : generatedSettings;
        public Vector3 OriginOffset => originOffset;
        public bool CacheWriteBlocked => cacheWriteBlocked;
        public event Action<string> CacheWriteFailed;

        private sealed class LoadedChunk
        {
            public TerrainChunkData data;
            public GameObject gameObject;
            public MeshFilter filter;
            public MeshCollider collider;
        }

        private void Awake()
        {
            if (settings == null)
            {
                generatedSettings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
                generatedSettings.name = "Runtime Terrain Settings";
            }
            cache = new TerrainSessionCache(Settings.seed);
            if (focus == null && Camera.main != null) focus = Camera.main.transform;
        }

        private void Update()
        {
            if (focus != null && !cacheWriteBlocked) QueueChunksNear(focus.position + originOffset);
            int buildCount = Settings.chunksBuiltPerFrame;
            while (buildCount-- > 0 && requestedChunks.Count > 0)
            {
                TerrainChunkId id = requestedChunks.Dequeue(); queuedChunks.Remove(id);
                if (!chunks.ContainsKey(id)) CreateChunk(id);
            }
        }

        private void OnDestroy()
        {
            foreach (LoadedChunk chunk in chunks.Values) if (chunk.filter != null && chunk.filter.sharedMesh != null) Destroy(chunk.filter.sharedMesh);
            if (generatedSettings != null) Destroy(generatedSettings);
        }

        public void SetFocus(Transform value) => focus = value;
        public void SetOriginOffset(Vector3 value)
        {
            if (value == originOffset) return;
            originOffset = value;
            foreach (LoadedChunk chunk in chunks.Values) RebuildChunk(chunk);
        }

        public TerrainBiome SampleBiome(Vector3 worldPosition) => TerrainGenerator.SampleBiome(Settings.seed, worldPosition.x + originOffset.x, worldPosition.z + originOffset.z);
        public float SampleSurfaceHeight(Vector3 worldPosition) => TerrainGenerator.SurfaceHeight(Settings, worldPosition.x + originOffset.x, worldPosition.z + originOffset.z) - originOffset.y;
        public float SampleDensity(Vector3 worldPosition) => TerrainGenerator.InitialDensity(Settings, worldPosition + originOffset);
        public bool IsCollisionReady(Vector3 worldPosition)
        {
            TerrainChunkId id = WorldToChunk(worldPosition + originOffset);
            return chunks.TryGetValue(id, out LoadedChunk chunk) && chunk.collider != null && chunk.collider.sharedMesh != null;
        }

        public bool TryRaycast(Ray ray, float maxDistance, out TerrainRaycastHit result, int layerMask = Physics.DefaultRaycastLayers)
        {
            if (Physics.Raycast(ray, out RaycastHit hit, maxDistance, layerMask, QueryTriggerInteraction.Ignore))
            {
                result = new TerrainRaycastHit { point = hit.point, normal = hit.normal, collider = hit.collider, chunk = WorldToChunk(hit.point + originOffset) };
                return true;
            }
            result = default; return false;
        }

        public TerrainEditHandle RequestEdit(TerrainEditRequest request)
        {
            var handle = new TerrainEditHandle();
            if (request.radius <= 0f || request.radius > 64f) { handle.Status = TerrainEditStatus.Rejected; handle.Error = "Radius must be within 0.1 and 64 metres."; handle.Notify(); return handle; }
            StartCoroutine(ApplyEdit(request, handle));
            return handle;
        }

        private IEnumerator ApplyEdit(TerrainEditRequest request, TerrainEditHandle handle)
        {
            handle.Status = TerrainEditStatus.Processing; handle.Notify();
            Vector3 globalCenter = request.worldCenter + originOffset;
            float size = Settings.ChunkSize;
            int minX = Mathf.FloorToInt((globalCenter.x - request.radius) / size), maxX = Mathf.FloorToInt((globalCenter.x + request.radius) / size);
            int minY = Mathf.FloorToInt((globalCenter.y - request.radius) / size), maxY = Mathf.FloorToInt((globalCenter.y + request.radius) / size);
            int minZ = Mathf.FloorToInt((globalCenter.z - request.radius) / size), maxZ = Mathf.FloorToInt((globalCenter.z + request.radius) / size);
            var candidates = new List<TerrainChunkId>();
            for (int z = minZ; z <= maxZ; z++) for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++) candidates.Add(new TerrainChunkId(x, y, z));
            int changed = 0;
            request.worldCenter = globalCenter;
            for (int i = 0; i < candidates.Count; i++)
            {
                LoadedChunk chunk = GetOrCreateChunk(candidates[i]);
                if (chunk.data.Apply(Settings, request)) { RebuildChunk(chunk); changed++; }
                handle.Progress = (i + 1f) / candidates.Count; handle.Notify();
                if (i % 6 == 5) yield return null;
            }
            handle.Progress = 1f; handle.Status = TerrainEditStatus.Completed; handle.Notify();
        }

        private void QueueChunksNear(Vector3 globalFocus)
        {
            TerrainChunkId center = WorldToChunk(globalFocus);
            int horizontal = Mathf.CeilToInt(Settings.viewDistance / Settings.ChunkSize);
            int radiusSquared = horizontal * horizontal;
            for (int z = -horizontal; z <= horizontal; z++)
            for (int x = -horizontal; x <= horizontal; x++)
            {
                if (x * x + z * z > radiusSquared) continue;
                for (int y = -verticalChunksBelowFocus; y <= verticalChunksAboveFocus; y++) QueueChunk(new TerrainChunkId(center.x + x, center.y + y, center.z + z));
            }
        }
        private void QueueChunk(TerrainChunkId id)
        {
            if (!chunks.ContainsKey(id) && queuedChunks.Add(id)) requestedChunks.Enqueue(id);
        }
        private LoadedChunk GetOrCreateChunk(TerrainChunkId id)
        {
            if (chunks.TryGetValue(id, out LoadedChunk existing)) return existing;
            CreateChunk(id); return chunks[id];
        }
        private void CreateChunk(TerrainChunkId id)
        {
            var data = new TerrainChunkData(Settings, id); cache.TryLoad(data);
            var go = new GameObject($"Terrain {id}"); go.transform.SetParent(transform, false);
            var loaded = new LoadedChunk { data = data, gameObject = go, filter = go.AddComponent<MeshFilter>() };
            go.AddComponent<MeshRenderer>().sharedMaterial = Settings.terrainMaterial;
            if (generateColliders) loaded.collider = go.AddComponent<MeshCollider>();
            chunks.Add(id, loaded); RebuildChunk(loaded);
        }
        private void RebuildChunk(LoadedChunk chunk)
        {
            Mesh next = TerrainMeshBuilder.Build(chunk.data, originOffset);
            Mesh old = chunk.filter.sharedMesh;
            chunk.filter.sharedMesh = next;
            if (chunk.collider != null) chunk.collider.sharedMesh = next;
            if (old != null) Destroy(old);
        }
        private TerrainChunkId WorldToChunk(Vector3 globalPosition)
        {
            float size = Settings.ChunkSize;
            return new TerrainChunkId(Mathf.FloorToInt(globalPosition.x / size), Mathf.FloorToInt(globalPosition.y / size), Mathf.FloorToInt(globalPosition.z / size));
        }
    }
}
