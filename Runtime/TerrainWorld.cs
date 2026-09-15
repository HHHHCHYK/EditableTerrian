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
        private readonly Queue<PendingEdit> editQueue = new Queue<PendingEdit>();
        private readonly List<ChunkPriority> streamingPlan = new List<ChunkPriority>();
        private readonly Queue<LoadedChunk> meshBuildQueue = new Queue<LoadedChunk>();
        private TerrainSessionCache cache;
        private TerrainWorldSettings generatedSettings;
        private Material generatedMaterial;
        private Vector3 originOffset;
        private bool cacheWriteBlocked;
        private bool processingEdit;
        private int frameCounter;
        private TerrainChunkId plannedFocusChunk;
        private int streamingPlanIndex;
        private bool hasStreamingPlan;
        private int activeMeshBuilds;

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
            public int lod;
            public int lastAccessFrame;
            public TerrainMeshBuildRequest pendingMesh;
            public bool meshDirty;
            public bool meshQueued;
            public bool isLoaded;
            public int meshRevision;
            public int pendingMeshRevision;
        }
        private struct PendingEdit
        {
            public TerrainEditRequest request;
            public TerrainEditHandle handle;
        }
        private struct ChunkPriority
        {
            public TerrainChunkId id;
            public float distanceSquared;
        }

        private void Awake()
        {
            if (settings == null)
            {
                generatedSettings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
                generatedSettings.name = "Runtime Terrain Settings";
            }
            cache = new TerrainSessionCache(Settings.seed, Guid.NewGuid().ToString("N"));
            if (Settings.terrainMaterial == null)
            {
                Shader shader = Shader.Find("Humanier/Terrain Low Poly");
                if (shader != null) generatedMaterial = new Material(shader) { name = "Runtime Terrain Material" };
            }
            if (focus == null && Camera.main != null) focus = Camera.main.transform;
        }

        private void Update()
        {
            ApplyCompletedMeshes();
            ScheduleMeshBuilds();
            if (focus != null && !cacheWriteBlocked) UpdateStreamingPlan(focus.position + originOffset);
            int buildCount = Settings.chunksBuiltPerFrame;
            while (!cacheWriteBlocked && buildCount-- > 0 && requestedChunks.Count > 0)
            {
                TerrainChunkId id = requestedChunks.Dequeue(); queuedChunks.Remove(id);
                if (!chunks.ContainsKey(id) && !CreateChunk(id, focus == null ? Vector3.zero : focus.position + originOffset, true))
                {
                    QueueChunk(id);
                    break;
                }
            }
            ScheduleMeshBuilds();
            if (++frameCounter % 30 == 0 && focus != null) UpdateLodsAndEvict(focus.position + originOffset);
        }

        private void OnDestroy()
        {
            foreach (LoadedChunk chunk in chunks.Values)
            {
                if (chunk.pendingMesh != null) { chunk.pendingMesh.Dispose(); activeMeshBuilds--; }
                if (chunk.filter != null && chunk.filter.sharedMesh != null) Destroy(chunk.filter.sharedMesh);
            }
            if (generatedSettings != null) Destroy(generatedSettings);
            if (generatedMaterial != null) Destroy(generatedMaterial);
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
        public float SampleDensity(Vector3 worldPosition)
        {
            Vector3 globalPosition = worldPosition + originOffset;
            return chunks.TryGetValue(WorldToChunk(globalPosition), out LoadedChunk chunk) ? chunk.data.SampleDensity(globalPosition) : TerrainGenerator.InitialDensity(Settings, globalPosition);
        }
        public bool IsCollisionReady(Vector3 worldPosition)
        {
            TerrainChunkId id = WorldToChunk(worldPosition + originOffset);
            return chunks.TryGetValue(id, out LoadedChunk chunk) && chunk.collider != null && chunk.collider.sharedMesh != null;
        }

        public bool TryRaycast(Ray ray, float maxDistance, out TerrainRaycastHit result, int layerMask = Physics.DefaultRaycastLayers)
        {
            RaycastHit[] hits = Physics.RaycastAll(ray, maxDistance, layerMask, QueryTriggerInteraction.Ignore);
            float closest = float.MaxValue;
            TerrainRaycastHit closestHit = default;
            foreach (RaycastHit hit in hits)
            {
                TerrainChunkMarker marker = hit.collider.GetComponent<TerrainChunkMarker>();
                if (marker == null || marker.Owner != this || hit.distance >= closest) continue;
                closest = hit.distance;
                closestHit = new TerrainRaycastHit { point = hit.point, normal = hit.normal, collider = hit.collider, chunk = marker.Id };
            }
            if (closest < float.MaxValue) { result = closestHit; return true; }
            result = default; return false;
        }

        public TerrainEditHandle RequestEdit(TerrainEditRequest request)
        {
            var handle = new TerrainEditHandle();
            if (cacheWriteBlocked) { handle.Status = TerrainEditStatus.CacheFailure; handle.Error = "Terrain cache is unavailable; editing is paused to protect modified terrain."; handle.Notify(); return handle; }
            if (!IsFinite(request.worldCenter) || !IsFinite(request.radius) || !IsFinite(request.strength) || !IsFinite(request.flattenHeight) || request.radius <= 0f || request.radius > 64f)
            { handle.Status = TerrainEditStatus.Rejected; handle.Error = "Brush values must be finite and radius must be within 0.1 and 64 metres."; handle.Notify(); return handle; }
            if (editQueue.Count >= Settings.maxQueuedEdits) { handle.Status = TerrainEditStatus.Rejected; handle.Error = "Terrain edit queue is full."; handle.Notify(); return handle; }
            request.worldCenter += originOffset;
            if (request.mode == TerrainBrushMode.Flatten) request.flattenHeight += originOffset.y;
            editQueue.Enqueue(new PendingEdit { request = request, handle = handle });
            if (!processingEdit) StartCoroutine(ProcessEdits());
            return handle;
        }

        private IEnumerator ProcessEdits()
        {
            processingEdit = true;
            while (editQueue.Count > 0)
            {
                if (cacheWriteBlocked) { yield return null; continue; }
                PendingEdit pending = editQueue.Dequeue();
                yield return ApplyEdit(pending.request, pending.handle);
            }
            processingEdit = false;
        }

        private IEnumerator ApplyEdit(TerrainEditRequest request, TerrainEditHandle handle)
        {
            handle.Status = TerrainEditStatus.Processing; handle.Notify();
            Vector3 globalCenter = request.worldCenter;
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
                LoadedChunk chunk = GetOrCreateChunk(candidates[i], globalCenter);
                if (chunk == null) { handle.Status = TerrainEditStatus.CacheFailure; handle.Error = "Terrain cache is unavailable; edit was paused before data could be replaced."; handle.Notify(); yield break; }
                if (chunk.data.Apply(Settings, request)) { RebuildChunk(chunk); changed++; }
                handle.Progress = (i + 1f) / candidates.Count; handle.Notify();
                if (i % 6 == 5) yield return null;
            }
            handle.Progress = 1f; handle.Status = TerrainEditStatus.Completed; handle.Notify();
        }

        private void UpdateStreamingPlan(Vector3 globalFocus)
        {
            TerrainChunkId center = WorldToChunk(globalFocus);
            if (!hasStreamingPlan || !center.Equals(plannedFocusChunk))
            {
                plannedFocusChunk = center; hasStreamingPlan = true; streamingPlanIndex = 0; streamingPlan.Clear();
                BuildStreamingPlan(center);
            }
            while (requestedChunks.Count < Settings.maxQueuedChunks && streamingPlanIndex < streamingPlan.Count)
            {
                QueueChunk(streamingPlan[streamingPlanIndex++].id);
            }
        }
        private void BuildStreamingPlan(TerrainChunkId center)
        {
            int horizontal = Mathf.CeilToInt(Settings.viewDistance / Settings.ChunkSize);
            int radiusSquared = horizontal * horizontal;
            int horizontalColumns = 0;
            for (int z = -horizontal; z <= horizontal; z++)
            for (int x = -horizontal; x <= horizontal; x++)
                if (x * x + z * z <= radiusSquared) horizontalColumns++;

            int requestedVerticalCount = Mathf.Max(1, verticalChunksBelowFocus + verticalChunksAboveFocus + 1);
            int verticalCount = Mathf.Min(requestedVerticalCount, Mathf.Max(1, MaxResidentChunkCount() / horizontalColumns));
            float belowRatio = (float)verticalChunksBelowFocus / Mathf.Max(1, requestedVerticalCount - 1);
            int below = Mathf.Clamp(Mathf.RoundToInt((verticalCount - 1) * belowRatio), 0, verticalCount - 1);
            int above = verticalCount - below - 1;
            for (int z = -horizontal; z <= horizontal; z++)
            for (int x = -horizontal; x <= horizontal; x++)
            {
                if (x * x + z * z > radiusSquared) continue;
                for (int y = -below; y <= above; y++)
                {
                    var id = new TerrainChunkId(center.x + x, center.y + y, center.z + z);
                    if (!chunks.ContainsKey(id)) streamingPlan.Add(new ChunkPriority { id = id, distanceSquared = x * x + y * y + z * z });
                }
            }
            streamingPlan.Sort((a, b) => a.distanceSquared.CompareTo(b.distanceSquared));
        }
        private void QueueChunk(TerrainChunkId id)
        {
            if (requestedChunks.Count < Settings.maxQueuedChunks && !chunks.ContainsKey(id) && queuedChunks.Add(id)) requestedChunks.Enqueue(id);
        }
        private LoadedChunk GetOrCreateChunk(TerrainChunkId id, Vector3 priorityCenter)
        {
            if (chunks.TryGetValue(id, out LoadedChunk existing)) return existing;
            return CreateChunk(id, priorityCenter, false) ? chunks[id] : null;
        }
        private bool CreateChunk(TerrainChunkId id, Vector3 priorityCenter, bool streamRequest)
        {
            if (!EnsureDataCapacity(id, priorityCenter, streamRequest)) return false;
            var data = new TerrainChunkData(Settings, id);
            if (!cache.TryLoad(data) && !string.IsNullOrEmpty(cache.LastError))
            {
                cacheWriteBlocked = true;
                CacheWriteFailed?.Invoke($"Could not read terrain session cache: {cache.LastError}");
                return false;
            }
            var go = new GameObject($"Terrain {id}"); go.transform.SetParent(transform, false);
            var loaded = new LoadedChunk { data = data, gameObject = go, filter = go.AddComponent<MeshFilter>(), lod = GetLod(id), lastAccessFrame = frameCounter, isLoaded = true };
            go.AddComponent<MeshRenderer>().sharedMaterial = Settings.terrainMaterial != null ? Settings.terrainMaterial : generatedMaterial;
            if (generateColliders) loaded.collider = go.AddComponent<MeshCollider>();
            TerrainChunkMarker marker = go.AddComponent<TerrainChunkMarker>();
            marker.Id = id;
            marker.Owner = this;
            chunks.Add(id, loaded);
            RebuildChunk(loaded);
            RefreshNeighbours(id);
            return true;
        }
        private void RebuildChunk(LoadedChunk chunk)
        {
            if (!chunk.isLoaded) return;
            chunk.meshDirty = true;
            chunk.meshRevision++;
            QueueMeshBuild(chunk);
        }
        private void ApplyCompletedMeshes()
        {
            int remaining = Settings.maxMeshReplacementsPerFrame;
            foreach (LoadedChunk chunk in chunks.Values)
            {
                if (remaining <= 0 || chunk.pendingMesh == null || !chunk.pendingMesh.IsCompleted) continue;
                TerrainMeshBuildRequest request = chunk.pendingMesh;
                chunk.pendingMesh = null;
                Mesh next = request.Complete();
                activeMeshBuilds--;
                bool current = request.Version == chunk.data.Version && chunk.pendingMeshRevision == chunk.meshRevision;
                request.Dispose();
                if (!current) { if (next != null) Destroy(next); QueueMeshBuild(chunk); continue; }
                AssignMesh(chunk, next);
                remaining--;
            }
        }

        private void QueueMeshBuild(LoadedChunk chunk)
        {
            if (chunk.pendingMesh == null && !chunk.meshQueued)
            {
                chunk.meshQueued = true;
                meshBuildQueue.Enqueue(chunk);
            }
        }

        private void ScheduleMeshBuilds()
        {
            int limit = Mathf.Max(1, Settings.maxInFlightMeshBuilds);
            while (activeMeshBuilds < limit && meshBuildQueue.Count > 0)
            {
                LoadedChunk chunk = meshBuildQueue.Dequeue();
                chunk.meshQueued = false;
                if (!chunk.isLoaded || chunk.pendingMesh != null || !chunk.meshDirty) continue;
                chunk.meshDirty = false;
                chunk.pendingMeshRevision = chunk.meshRevision;
                chunk.pendingMesh = TerrainMeshBuilder.Schedule(chunk.data, originOffset, chunk.lod, GetTransitionFaces(chunk.data.Id, chunk.lod), chunk.data.Version);
                activeMeshBuilds++;
            }
        }
        private static void AssignMesh(LoadedChunk chunk, Mesh next)
        {
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

        private int GetLod(TerrainChunkId id)
        {
            if (focus == null) return 0;
            Vector3 point = new Vector3((id.x + .5f) * Settings.ChunkSize, (id.y + .5f) * Settings.ChunkSize, (id.z + .5f) * Settings.ChunkSize);
            float distance = Vector2.Distance(new Vector2(point.x, point.z), new Vector2(focus.position.x + originOffset.x, focus.position.z + originOffset.z));
            int lod = 0;
            foreach (float threshold in Settings.lodDistances) { if (distance >= threshold) lod++; else break; }
            return Mathf.Min(lod, 3);
        }

        private void UpdateLodsAndEvict(Vector3 globalFocus)
        {
            float unloadDistance = Settings.viewDistance + Settings.ChunkSize * 2f;
            var remove = new List<TerrainChunkId>();
            foreach (KeyValuePair<TerrainChunkId, LoadedChunk> pair in chunks)
            {
                LoadedChunk chunk = pair.Value;
                float cx = (pair.Key.x + .5f) * Settings.ChunkSize, cz = (pair.Key.z + .5f) * Settings.ChunkSize;
                float distance = Vector2.Distance(new Vector2(cx, cz), new Vector2(globalFocus.x, globalFocus.z));
                if (distance > unloadDistance)
                {
                    if (!SaveBeforeUnload(chunk)) continue;
                    remove.Add(pair.Key); continue;
                }
                int lod = GetLod(pair.Key);
                if (lod != chunk.lod) { chunk.lod = lod; RebuildChunk(chunk); RefreshNeighbours(pair.Key); }
                chunk.lastAccessFrame = frameCounter;
            }
            foreach (TerrainChunkId id in remove) RemoveChunk(id);
            EvictForMemoryLimit(globalFocus, remove);
        }

        private void EvictForMemoryLimit(Vector3 priorityCenter, List<TerrainChunkId> scratch)
        {
            scratch.Clear();
            while (chunks.Count > MaxResidentChunkCount())
            {
                TerrainChunkId candidate = FindFurthestChunk(priorityCenter, out LoadedChunk loaded);
                if (loaded == null || !SaveBeforeUnload(loaded)) break;
                scratch.Add(candidate);
                RemoveChunk(candidate);
            }
        }

        private bool SaveBeforeUnload(LoadedChunk chunk)
        {
            if (cache.Save(chunk.data)) return true;
            cacheWriteBlocked = true;
            CacheWriteFailed?.Invoke(cache.LastError);
            return false;
        }

        private bool EnsureDataCapacity(TerrainChunkId incomingId, Vector3 priorityCenter, bool streamRequest)
        {
            while (chunks.Count + 1 > MaxResidentChunkCount())
            {
                TerrainChunkId candidateId = FindFurthestChunk(priorityCenter, out LoadedChunk candidate, out float furthest);
                Vector3 incomingPosition = ChunkCenter(incomingId);
                float incomingDistance = (incomingPosition - priorityCenter).sqrMagnitude;
                if (streamRequest && (candidate == null || furthest <= incomingDistance)) return false;
                if (candidate == null || !SaveBeforeUnload(candidate)) return false;
                RemoveChunk(candidateId);
            }
            return true;
        }

        private void RemoveChunk(TerrainChunkId id)
        {
            if (!chunks.TryGetValue(id, out LoadedChunk chunk)) return;
            chunks.Remove(id);
            chunk.isLoaded = false;
            if (chunk.pendingMesh != null) { chunk.pendingMesh.Dispose(); activeMeshBuilds--; }
            if (chunk.filter.sharedMesh != null) Destroy(chunk.filter.sharedMesh);
            Destroy(chunk.gameObject);
            RefreshNeighbours(id);
        }

        private TerrainChunkId FindFurthestChunk(Vector3 priorityCenter, out LoadedChunk furthestChunk) => FindFurthestChunk(priorityCenter, out furthestChunk, out _);
        private TerrainChunkId FindFurthestChunk(Vector3 priorityCenter, out LoadedChunk furthestChunk, out float furthestDistance)
        {
            TerrainChunkId furthestId = default;
            furthestChunk = null;
            furthestDistance = float.MinValue;
            foreach (KeyValuePair<TerrainChunkId, LoadedChunk> pair in chunks)
            {
                float distance = (ChunkCenter(pair.Key) - priorityCenter).sqrMagnitude;
                if (distance <= furthestDistance) continue;
                furthestDistance = distance;
                furthestId = pair.Key;
                furthestChunk = pair.Value;
            }
            return furthestId;
        }

        private Vector3 ChunkCenter(TerrainChunkId id) => new Vector3((id.x + .5f) * Settings.ChunkSize, (id.y + .5f) * Settings.ChunkSize, (id.z + .5f) * Settings.ChunkSize);
        private int MaxResidentChunkCount()
        {
            long bytesPerChunk = (long)Settings.SampleResolution * Settings.SampleResolution * Settings.SampleResolution * (sizeof(float) + sizeof(byte));
            long limit = Settings.memoryCacheLimitMb * 1024L * 1024L;
            return (int)System.Math.Max(1L, limit / bytesPerChunk);
        }

        private TransitionFaceMask GetTransitionFaces(TerrainChunkId id, int lod)
        {
            TransitionFaceMask mask = TransitionFaceMask.None;
            AddTransition(ref mask, new TerrainChunkId(id.x - 1, id.y, id.z), lod, TransitionFaceMask.NegativeX);
            AddTransition(ref mask, new TerrainChunkId(id.x + 1, id.y, id.z), lod, TransitionFaceMask.PositiveX);
            AddTransition(ref mask, new TerrainChunkId(id.x, id.y - 1, id.z), lod, TransitionFaceMask.NegativeY);
            AddTransition(ref mask, new TerrainChunkId(id.x, id.y + 1, id.z), lod, TransitionFaceMask.PositiveY);
            AddTransition(ref mask, new TerrainChunkId(id.x, id.y, id.z - 1), lod, TransitionFaceMask.NegativeZ);
            AddTransition(ref mask, new TerrainChunkId(id.x, id.y, id.z + 1), lod, TransitionFaceMask.PositiveZ);
            return mask;
        }
        private void AddTransition(ref TransitionFaceMask mask, TerrainChunkId neighbour, int lod, TransitionFaceMask face)
        {
            if (chunks.TryGetValue(neighbour, out LoadedChunk adjacent) && adjacent.lod == lod + 1) mask |= face;
        }
        private void RefreshNeighbours(TerrainChunkId id)
        {
            RefreshAdjacent(new TerrainChunkId(id.x - 1, id.y, id.z)); RefreshAdjacent(new TerrainChunkId(id.x + 1, id.y, id.z));
            RefreshAdjacent(new TerrainChunkId(id.x, id.y - 1, id.z)); RefreshAdjacent(new TerrainChunkId(id.x, id.y + 1, id.z));
            RefreshAdjacent(new TerrainChunkId(id.x, id.y, id.z - 1)); RefreshAdjacent(new TerrainChunkId(id.x, id.y, id.z + 1));
        }
        private void RefreshAdjacent(TerrainChunkId id)
        {
            if (chunks.TryGetValue(id, out LoadedChunk adjacent)) RebuildChunk(adjacent);
        }

        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal sealed class TerrainChunkMarker : MonoBehaviour
    {
        public TerrainChunkId Id { get; set; }
        public TerrainWorld Owner { get; set; }
    }
}
