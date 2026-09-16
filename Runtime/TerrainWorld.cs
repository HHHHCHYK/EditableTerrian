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
        private readonly List<StreamingGroup> streamingGroups = new List<StreamingGroup>();
        private readonly List<Vector2Int> streamingColumns = new List<Vector2Int>();
        private readonly List<TerrainChunkId> lodRelaxationScratch = new List<TerrainChunkId>();
        private readonly Queue<LoadedChunk> meshBuildQueue = new Queue<LoadedChunk>();
        private TerrainSessionCache cache;
        private TerrainWorldSettings generatedSettings;
        private Material generatedMaterial;
        private Vector3 originOffset;
        private bool cacheWriteBlocked;
        private bool processingEdit;
        private bool initialized;
        private bool runtimeSuspended;
        private bool destroyed;
        private int frameCounter;
        private TerrainChunkId plannedFocusChunk;
        private int streamingPlanIndex;
        private int streamingColumnIndex;
        private bool hasStreamingPlan;
        private int activeMeshBuilds;
        private PendingEdit activeEdit;
        private bool hasActiveEdit;

        public TerrainWorldSettings Settings
        {
            get
            {
                EnsureSettings();
                return settings != null ? settings : generatedSettings;
            }
        }
        public Vector3 OriginOffset => originOffset;
        public bool CacheWriteBlocked => cacheWriteBlocked;
        public string CacheError => cache == null ? null : cache.LastError;
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
            public int appliedMeshRevision = -1;
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
        private readonly struct ColumnKey : IEquatable<ColumnKey>
        {
            public readonly int x;
            public readonly int z;
            private readonly bool valid;

            public ColumnKey(int x, int z)
            {
                this.x = x;
                this.z = z;
                valid = true;
            }

            public bool IsValid => valid;
            public bool Equals(ColumnKey other) => valid == other.valid && (!valid || (x == other.x && z == other.z));
            public override bool Equals(object obj) => obj is ColumnKey other && Equals(other);
            public override int GetHashCode() { unchecked { return valid ? (x * 397) ^ z : 0; } }
        }
        private sealed class StreamingGroup
        {
            public int x;
            public int z;
            public int minY;
            public int maxY;
            public float distanceSquared;

            public int Count => maxY - minY + 1;
        }

        private void Awake()
        {
            EnsureInitialized();
            if (Settings.terrainMaterial == null)
            {
                Shader shader = Shader.Find("Humanier/Terrain Low Poly");
                if (shader != null) generatedMaterial = new Material(shader) { name = "Runtime Terrain Material" };
            }
            if (focus == null && Camera.main != null) focus = Camera.main.transform;
            initialized = true;
        }

        private void OnEnable()
        {
            if (!initialized || !runtimeSuspended) return;
            runtimeSuspended = false;
            foreach (LoadedChunk chunk in chunks.Values)
                if (chunk.meshDirty || chunk.appliedMeshRevision != chunk.meshRevision) QueueMeshBuild(chunk);
        }

        private void OnDisable()
        {
            if (!initialized || destroyed) return;
            SuspendRuntime("Terrain world was disabled before the edit completed.");
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
            destroyed = true;
            FailPendingEdits("Terrain world was destroyed before the edit completed.");
            StopAllCoroutines();
            foreach (LoadedChunk chunk in chunks.Values)
            {
                DisposePendingMesh(chunk);
                if (chunk.filter != null && chunk.filter.sharedMesh != null) Destroy(chunk.filter.sharedMesh);
            }
            meshBuildQueue.Clear();
            activeMeshBuilds = 0;
            if (cache != null && !cache.Dispose()) CacheWriteFailed?.Invoke($"Could not clear terrain session cache: {cache.LastError}");
            if (generatedSettings != null) Destroy(generatedSettings);
            if (generatedMaterial != null) Destroy(generatedMaterial);
        }

        public void SetFocus(Transform value) => focus = value;
        public void SetOriginOffset(Vector3 value)
        {
            if (value == originOffset) return;
            Vector3 compensation = originOffset - value;
            originOffset = value;
            foreach (LoadedChunk chunk in chunks.Values)
            {
                chunk.gameObject.transform.localPosition += compensation;
                RebuildChunk(chunk);
            }
        }

        public TerrainBiome SampleBiome(Vector3 worldPosition) => TerrainGenerator.SampleBiome(Settings.seed, worldPosition.x + originOffset.x, worldPosition.z + originOffset.z);
        public float SampleSurfaceHeight(Vector3 worldPosition) => TerrainGenerator.SurfaceHeight(Settings, worldPosition.x + originOffset.x, worldPosition.z + originOffset.z) - originOffset.y;
        public float SampleDensity(Vector3 worldPosition)
        {
            if (!TrySampleDensity(worldPosition, out float density))
                throw new InvalidOperationException("The cached terrain data could not be read. Inspect CacheError before retrying the query.");
            return density;
        }

        public float SampleGeneratedDensity(Vector3 worldPosition)
        {
            Vector3 globalPosition = worldPosition + originOffset;
            return TerrainGenerator.InitialDensity(Settings, globalPosition);
        }

        public bool TrySampleDensity(Vector3 worldPosition, out float density)
        {
            EnsureInitialized();
            Vector3 globalPosition = worldPosition + originOffset;
            TerrainChunkId id = WorldToChunk(globalPosition);
            if (chunks.TryGetValue(id, out LoadedChunk chunk))
            {
                density = chunk.data.SampleDensity(globalPosition);
                return true;
            }
            if (cache != null && cache.HasEntry(id))
            {
                var cached = new TerrainChunkData(Settings, id);
                if (!cache.TryLoad(cached))
                {
                    density = default;
                    return false;
                }
                density = cached.SampleDensity(globalPosition);
                return true;
            }
            density = TerrainGenerator.InitialDensity(Settings, globalPosition);
            return true;
        }
        public bool IsCollisionReady(Vector3 worldPosition)
        {
            TerrainChunkId id = WorldToChunk(worldPosition + originOffset);
            return chunks.TryGetValue(id, out LoadedChunk chunk) && chunk.collider != null && chunk.collider.sharedMesh != null && chunk.appliedMeshRevision == chunk.meshRevision;
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
            EnsureInitialized();
            var handle = new TerrainEditHandle();
            if (runtimeSuspended || !isActiveAndEnabled) { handle.Status = TerrainEditStatus.Cancelled; handle.Error = "Terrain world is disabled."; handle.Notify(); return handle; }
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

        public bool TryResumeCacheWrites()
        {
            EnsureInitialized();
            if (!cacheWriteBlocked) return true;
            if (!cache.TryResumeWrites())
            {
                CacheWriteFailed?.Invoke(cache.LastError);
                return false;
            }
            cacheWriteBlocked = false;
            return true;
        }

        private IEnumerator ProcessEdits()
        {
            processingEdit = true;
            while (editQueue.Count > 0)
            {
                if (cacheWriteBlocked) { yield return null; continue; }
                PendingEdit pending = editQueue.Dequeue();
                activeEdit = pending;
                hasActiveEdit = true;
                yield return ApplyEdit(pending.request, pending.handle);
                hasActiveEdit = false;
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
            var requiredMeshRevisions = new Dictionary<TerrainChunkId, int>();
            request.worldCenter = globalCenter;
            for (int i = 0; i < candidates.Count; i++)
            {
                LoadedChunk chunk = GetOrCreateChunk(candidates[i], globalCenter);
                if (chunk == null) { handle.Status = TerrainEditStatus.CacheFailure; handle.Error = "Terrain cache is unavailable; edit was paused before data could be replaced."; handle.Notify(); yield break; }
                if (chunk.data.Apply(Settings, request))
                {
                    RebuildChunk(chunk);
                    requiredMeshRevisions[chunk.data.Id] = chunk.meshRevision;
                }
                handle.Progress = .8f * (i + 1f) / candidates.Count; handle.Notify();
                if (i % 6 == 5) yield return null;
            }
            while (!AreEditedMeshesApplied(requiredMeshRevisions, out int applied))
            {
                handle.Progress = .8f + .2f * applied / Mathf.Max(1, requiredMeshRevisions.Count);
                handle.Notify();
                yield return null;
            }
            handle.Progress = 1f; handle.Status = TerrainEditStatus.Completed; handle.Notify();
        }

        private void UpdateStreamingPlan(Vector3 globalFocus)
        {
            TerrainChunkId center = WorldToChunk(globalFocus);
            if (!hasStreamingPlan || !center.Equals(plannedFocusChunk))
            {
                // Queued entries are prioritized for the previous focus and cannot be
                // reprioritized in-place. Drop them before appending the new plan so a
                // teleport cannot starve the destination area behind a full old queue.
                requestedChunks.Clear();
                queuedChunks.Clear();
                plannedFocusChunk = center;
                hasStreamingPlan = true;
                streamingPlanIndex = 0;
                streamingColumnIndex = 0;
                streamingPlan.Clear();
                streamingGroups.Clear();
                PrepareStreamingColumns(center);
            }
            int budget = Mathf.Max(1, Settings.chunksBuiltPerFrame);
            ContinueStreamingPlan(budget);
            QueueStreamingPlan(budget);
        }

        private void BuildStreamingPlan(TerrainChunkId center)
        {
            requestedChunks.Clear();
            queuedChunks.Clear();
            plannedFocusChunk = center;
            hasStreamingPlan = true;
            streamingPlanIndex = 0;
            streamingColumnIndex = 0;
            streamingPlan.Clear();
            streamingGroups.Clear();
            PrepareStreamingColumns(center);
            ContinueStreamingPlan(int.MaxValue);
        }

        private void PrepareStreamingColumns(TerrainChunkId center)
        {
            streamingColumns.Clear();
            int horizontal = Mathf.CeilToInt(Settings.viewDistance / Settings.ChunkSize);
            int nearRadius = Mathf.CeilToInt(Settings.nearUndergroundDistance / Settings.ChunkSize);
            int radius = Mathf.Max(horizontal, nearRadius);
            int horizontalSquared = horizontal * horizontal;
            int nearRadiusSquared = nearRadius * nearRadius;
            for (int z = -radius; z <= radius; z++)
            for (int x = -radius; x <= radius; x++)
            {
                if (x * x + z * z <= horizontalSquared || x * x + z * z <= nearRadiusSquared)
                    streamingColumns.Add(new Vector2Int(center.x + x, center.z + z));
            }
            streamingColumns.Sort((a, b) =>
            {
                int aX = a.x - center.x, aZ = a.y - center.z;
                int bX = b.x - center.x, bZ = b.y - center.z;
                int distance = (aX * aX + aZ * aZ).CompareTo(bX * bX + bZ * bZ);
                if (distance != 0) return distance;
                distance = a.x.CompareTo(b.x);
                return distance != 0 ? distance : a.y.CompareTo(b.y);
            });
        }

        private void ContinueStreamingPlan(int budget)
        {
            if (budget <= 0) return;
            Vector3 planFocus = ChunkCenter(plannedFocusChunk);
            int horizontal = Mathf.CeilToInt(Settings.viewDistance / Settings.ChunkSize);
            int horizontalSquared = horizontal * horizontal;
            int nearRadius = Mathf.CeilToInt(Settings.nearUndergroundDistance / Settings.ChunkSize);
            int nearRadiusSquared = nearRadius * nearRadius;
            int processed = 0;
            while (streamingColumnIndex < streamingColumns.Count && processed++ < budget)
            {
                Vector2Int column = streamingColumns[streamingColumnIndex++];
                int offsetX = column.x - plannedFocusChunk.x;
                int offsetZ = column.y - plannedFocusChunk.z;
                int distanceSquared = offsetX * offsetX + offsetZ * offsetZ;
                if (distanceSquared <= horizontalSquared)
                {
                    var id = new TerrainChunkId(column.x, 0, column.y);
                    TerrainSurfaceRange range = TerrainGenerator.SurfaceRange(Settings, id);
                    float safety = Settings.voxelSize;
                    int minY = Mathf.FloorToInt((range.MinHeight - safety) / Settings.ChunkSize);
                    int maxY = Mathf.FloorToInt((range.MaxHeight + safety) / Settings.ChunkSize);
                    AddStreamingGroup(column.x, column.y, minY, maxY, planFocus);
                }
                if (distanceSquared <= nearRadiusSquared)
                {
                    AddStreamingGroup(column.x, column.y, plannedFocusChunk.y - verticalChunksBelowFocus, plannedFocusChunk.y + verticalChunksAboveFocus, planFocus);
                }
            }
            if (processed > 0) RebuildStreamingPlan();
        }

        private void AddStreamingGroup(int x, int z, int minY, int maxY, Vector3 planFocus)
        {
            if (maxY < minY) { int swap = minY; minY = maxY; maxY = swap; }
            float distance = float.MaxValue;
            for (int y = minY; y <= maxY; y++)
                distance = Mathf.Min(distance, (ChunkCenter(new TerrainChunkId(x, y, z)) - planFocus).sqrMagnitude);

            for (int i = 0; i < streamingGroups.Count; i++)
            {
                StreamingGroup existing = streamingGroups[i];
                if (existing.x != x || existing.z != z || minY > existing.maxY + 1 || maxY < existing.minY - 1) continue;
                existing.minY = Mathf.Min(existing.minY, minY);
                existing.maxY = Mathf.Max(existing.maxY, maxY);
                existing.distanceSquared = Mathf.Min(existing.distanceSquared, distance);
                for (int j = streamingGroups.Count - 1; j > i; j--)
                {
                    StreamingGroup other = streamingGroups[j];
                    if (other.x != x || other.z != z || existing.minY > other.maxY + 1 || existing.maxY < other.minY - 1) continue;
                    existing.minY = Mathf.Min(existing.minY, other.minY);
                    existing.maxY = Mathf.Max(existing.maxY, other.maxY);
                    existing.distanceSquared = Mathf.Min(existing.distanceSquared, other.distanceSquared);
                    streamingGroups.RemoveAt(j);
                }
                return;
            }
            streamingGroups.Add(new StreamingGroup { x = x, z = z, minY = minY, maxY = maxY, distanceSquared = distance });
        }

        private void RebuildStreamingPlan()
        {
            streamingPlan.Clear();
            int reserved = 0;
            int capacity = MaxResidentChunkCount();
            streamingGroups.Sort((a, b) => a.distanceSquared.CompareTo(b.distanceSquared));
            foreach (StreamingGroup group in streamingGroups)
            {
                // Reserve the entire column group, including members already
                // resident. This keeps capacity pressure from evicting a near
                // layer halfway through loading the same XZ column.
                if (reserved + group.Count > capacity) break;
                for (int y = group.minY; y <= group.maxY; y++)
                {
                    var id = new TerrainChunkId(group.x, y, group.z);
                    if (!chunks.ContainsKey(id)) streamingPlan.Add(new ChunkPriority { id = id, distanceSquared = group.distanceSquared });
                }
                reserved += group.Count;
            }
            streamingPlanIndex = 0;
        }

        private int MissingGroupChunkCount(StreamingGroup group)
        {
            int missing = 0;
            for (int y = group.minY; y <= group.maxY; y++)
                if (!chunks.ContainsKey(new TerrainChunkId(group.x, y, group.z))) missing++;
            return missing;
        }

        private void QueueStreamingPlan(int budget)
        {
            while (budget > 0 && requestedChunks.Count < Settings.maxQueuedChunks && streamingPlanIndex < streamingPlan.Count)
            {
                if (QueueChunk(streamingPlan[streamingPlanIndex].id)) budget--;
                streamingPlanIndex++;
            }
        }
        private bool QueueChunk(TerrainChunkId id)
        {
            if (requestedChunks.Count >= Settings.maxQueuedChunks || chunks.ContainsKey(id) || !queuedChunks.Add(id)) return false;
            requestedChunks.Enqueue(id);
            return true;
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
                string error = cache.LastError;
                if (!cache.TryDiscard(id))
                {
                    cacheWriteBlocked = true;
                    CacheWriteFailed?.Invoke($"Could not discard invalid terrain session cache: {cache.LastError}");
                    return false;
                }
                CacheWriteFailed?.Invoke($"Discarded invalid terrain session cache for chunk {id}: {error}");
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
            RelaxLoadedLods();
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
                AssignMesh(chunk, next, chunk.pendingMeshRevision);
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
        private static void AssignMesh(LoadedChunk chunk, Mesh next, int appliedRevision)
        {
            Mesh old = chunk.filter.sharedMesh;
            chunk.gameObject.transform.localPosition = Vector3.zero;
            chunk.filter.sharedMesh = next;
            if (chunk.collider != null) chunk.collider.sharedMesh = next;
            chunk.appliedMeshRevision = appliedRevision;
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
            var removeColumns = new HashSet<ColumnKey>();
            foreach (KeyValuePair<TerrainChunkId, LoadedChunk> pair in chunks)
            {
                LoadedChunk chunk = pair.Value;
                float cx = (pair.Key.x + .5f) * Settings.ChunkSize, cz = (pair.Key.z + .5f) * Settings.ChunkSize;
                float distance = Vector2.Distance(new Vector2(cx, cz), new Vector2(globalFocus.x, globalFocus.z));
                if (distance > unloadDistance)
                {
                    removeColumns.Add(new ColumnKey(pair.Key.x, pair.Key.z));
                    continue;
                }
                int lod = GetLod(pair.Key);
                if (lod != chunk.lod) { chunk.lod = lod; RebuildChunk(chunk); RefreshNeighbours(pair.Key); }
                chunk.lastAccessFrame = frameCounter;
            }
            foreach (ColumnKey column in removeColumns)
            {
                if (!SaveColumnBeforeUnload(column)) continue;
                RemoveColumn(column, remove);
            }
            EvictForMemoryLimit(globalFocus, remove);
            RelaxLoadedLods();
        }

        private void EvictForMemoryLimit(Vector3 priorityCenter, List<TerrainChunkId> scratch)
        {
            scratch.Clear();
            while (chunks.Count > MaxResidentChunkCount())
            {
                ColumnKey candidate = FindFurthestColumn(priorityCenter, out float furthest);
                if (!candidate.IsValid || !SaveColumnBeforeUnload(candidate)) break;
                RemoveColumn(candidate, scratch);
            }
            RelaxLoadedLods();
        }

        private bool SaveBeforeUnload(LoadedChunk chunk)
        {
            if (cache.Save(chunk.data)) return true;
            cacheWriteBlocked = true;
            CacheWriteFailed?.Invoke(cache.LastError);
            return false;
        }

        private bool SaveColumnBeforeUnload(ColumnKey column)
        {
            if (!column.IsValid) return false;
            var members = new List<LoadedChunk>();
            foreach (KeyValuePair<TerrainChunkId, LoadedChunk> pair in chunks)
                if (pair.Key.x == column.x && pair.Key.z == column.z) members.Add(pair.Value);
            foreach (LoadedChunk member in members)
                if (!SaveBeforeUnload(member)) return false;
            return members.Count > 0;
        }

        private bool EnsureDataCapacity(TerrainChunkId incomingId, Vector3 priorityCenter, bool streamRequest)
        {
            int capacity = MaxResidentChunkCount();
            StreamingGroup incomingGroup = streamRequest ? FindStreamingGroup(incomingId) : null;
            int required = incomingGroup == null ? 1 : MissingGroupChunkCount(incomingGroup);
            if (required > capacity) return false;
            while (chunks.Count + required > capacity)
            {
                ColumnKey candidate = FindFurthestColumn(priorityCenter, out float furthest, incomingGroup == null ? default : new ColumnKey(incomingGroup.x, incomingGroup.z));
                Vector3 incomingPosition = ChunkCenter(incomingId);
                float incomingDistance = (incomingPosition - priorityCenter).sqrMagnitude;
                if (!candidate.IsValid || (streamRequest && furthest <= incomingDistance)) return false;
                if (!SaveColumnBeforeUnload(candidate)) return false;
                RemoveColumn(candidate, null);
            }
            return true;
        }

        private void RemoveChunk(TerrainChunkId id)
        {
            if (!chunks.TryGetValue(id, out LoadedChunk chunk)) return;
            RemoveChunkInternal(id);
            RelaxLoadedLods();
        }

        private void RemoveChunkInternal(TerrainChunkId id)
        {
            if (!chunks.TryGetValue(id, out LoadedChunk chunk)) return;
            chunks.Remove(id);
            chunk.isLoaded = false;
            DisposePendingMesh(chunk);
            if (chunk.filter.sharedMesh != null) Destroy(chunk.filter.sharedMesh);
            Destroy(chunk.gameObject);
            RefreshNeighbours(id);
        }

        private void RemoveColumn(ColumnKey column, List<TerrainChunkId> scratch)
        {
            if (!column.IsValid) return;
            if (scratch != null) scratch.Clear();
            var ids = new List<TerrainChunkId>();
            foreach (KeyValuePair<TerrainChunkId, LoadedChunk> pair in chunks)
                if (pair.Key.x == column.x && pair.Key.z == column.z) ids.Add(pair.Key);
            foreach (TerrainChunkId id in ids)
            {
                if (scratch != null) scratch.Add(id);
                RemoveChunkInternal(id);
            }
            if (ids.Count > 0) RelaxLoadedLods();
        }

        private StreamingGroup FindStreamingGroup(TerrainChunkId id)
        {
            for (int i = 0; i < streamingGroups.Count; i++)
            {
                StreamingGroup group = streamingGroups[i];
                if (group.x == id.x && group.z == id.z && id.y >= group.minY && id.y <= group.maxY) return group;
            }
            return null;
        }

        private ColumnKey FindFurthestColumn(Vector3 priorityCenter, out float furthestDistance)
        {
            return FindFurthestColumn(priorityCenter, out furthestDistance, default);
        }

        private ColumnKey FindFurthestColumn(Vector3 priorityCenter, out float furthestDistance, ColumnKey excluded)
        {
            ColumnKey furthest = default;
            furthestDistance = float.MinValue;
            foreach (KeyValuePair<TerrainChunkId, LoadedChunk> pair in chunks)
            {
                ColumnKey column = new ColumnKey(pair.Key.x, pair.Key.z);
                if (excluded.IsValid && column.Equals(excluded)) continue;
                float distance = (ChunkCenter(pair.Key) - priorityCenter).sqrMagnitude;
                if (distance <= furthestDistance) continue;
                furthestDistance = distance;
                furthest = column;
            }
            return furthest;
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

        private void AddStreamingCandidate(Dictionary<TerrainChunkId, float> candidates, TerrainChunkId id, Vector3 focusPoint)
        {
            float priority = (ChunkCenter(id) - focusPoint).sqrMagnitude;
            if (!candidates.TryGetValue(id, out float existing) || priority < existing) candidates[id] = priority;
        }

        private void EnsureSettings()
        {
            if (settings != null || generatedSettings != null) return;
            generatedSettings = ScriptableObject.CreateInstance<TerrainWorldSettings>();
            generatedSettings.name = "Runtime Terrain Settings";
        }

        private void EnsureInitialized()
        {
            EnsureSettings();
            if (cache == null) cache = new TerrainSessionCache(Settings.seed, Guid.NewGuid().ToString("N"));
        }

        private void FailPendingEdits(string error)
        {
            if (hasActiveEdit)
            {
                activeEdit.handle.Status = TerrainEditStatus.Cancelled;
                activeEdit.handle.Error = error;
                activeEdit.handle.Notify();
                hasActiveEdit = false;
            }
            while (editQueue.Count > 0)
            {
                PendingEdit pending = editQueue.Dequeue();
                pending.handle.Status = TerrainEditStatus.Cancelled;
                pending.handle.Error = error;
                pending.handle.Notify();
            }
        }

        private bool AreEditedMeshesApplied(Dictionary<TerrainChunkId, int> requiredRevisions, out int applied)
        {
            applied = 0;
            foreach (KeyValuePair<TerrainChunkId, int> required in requiredRevisions)
            {
                // A chunk is only removed after SaveBeforeUnload succeeds, so an
                // unloaded edited chunk is committed and will rebuild on demand.
                if (!chunks.TryGetValue(required.Key, out LoadedChunk chunk)) { applied++; continue; }
                if (chunk.appliedMeshRevision < required.Value || chunk.appliedMeshRevision != chunk.meshRevision) continue;
                applied++;
            }
            return applied == requiredRevisions.Count;
        }

        private void SuspendRuntime(string error)
        {
            runtimeSuspended = true;
            FailPendingEdits(error);
            StopAllCoroutines();
            processingEdit = false;
            hasActiveEdit = false;
            requestedChunks.Clear();
            queuedChunks.Clear();
            hasStreamingPlan = false;
            streamingPlanIndex = 0;
            streamingColumnIndex = 0;
            streamingPlan.Clear();
            streamingGroups.Clear();
            streamingColumns.Clear();
            meshBuildQueue.Clear();
            foreach (LoadedChunk chunk in chunks.Values)
            {
                chunk.meshQueued = false;
                if (chunk.pendingMesh == null) continue;
                DisposePendingMesh(chunk);
                chunk.meshDirty = true;
            }
            activeMeshBuilds = 0;
        }

        private void DisposePendingMesh(LoadedChunk chunk)
        {
            if (chunk.pendingMesh == null) return;
            chunk.pendingMesh.Dispose();
            chunk.pendingMesh = null;
            if (activeMeshBuilds > 0) activeMeshBuilds--;
        }
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

        private void RelaxLoadedLods()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                lodRelaxationScratch.Clear();
                foreach (KeyValuePair<TerrainChunkId, LoadedChunk> pair in chunks)
                {
                    TerrainChunkId id = pair.Key;
                    LoadedChunk chunk = pair.Value;
                    foreach (TerrainChunkId neighbourId in Neighbours(id))
                    {
                        if (!IsBefore(id, neighbourId) || !chunks.TryGetValue(neighbourId, out LoadedChunk neighbour)) continue;
                        int difference = chunk.lod - neighbour.lod;
                        if (difference > 1)
                        {
                            chunk.lod = neighbour.lod + 1;
                            if (!lodRelaxationScratch.Contains(id)) lodRelaxationScratch.Add(id);
                        }
                        else if (difference < -1)
                        {
                            neighbour.lod = chunk.lod + 1;
                            if (!lodRelaxationScratch.Contains(neighbourId)) lodRelaxationScratch.Add(neighbourId);
                        }
                    }
                }
                foreach (TerrainChunkId changed in lodRelaxationScratch)
                {
                    if (!chunks.TryGetValue(changed, out LoadedChunk chunk)) continue;
                    RebuildChunk(chunk);
                    RefreshNeighbours(changed);
                }
                if (lodRelaxationScratch.Count == 0) break;
            }
        }

        private static IEnumerable<TerrainChunkId> Neighbours(TerrainChunkId id)
        {
            yield return new TerrainChunkId(id.x - 1, id.y, id.z);
            yield return new TerrainChunkId(id.x + 1, id.y, id.z);
            yield return new TerrainChunkId(id.x, id.y - 1, id.z);
            yield return new TerrainChunkId(id.x, id.y + 1, id.z);
            yield return new TerrainChunkId(id.x, id.y, id.z - 1);
            yield return new TerrainChunkId(id.x, id.y, id.z + 1);
        }

        private static bool IsBefore(TerrainChunkId left, TerrainChunkId right)
        {
            if (left.x != right.x) return left.x < right.x;
            if (left.y != right.y) return left.y < right.y;
            return left.z < right.z;
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
