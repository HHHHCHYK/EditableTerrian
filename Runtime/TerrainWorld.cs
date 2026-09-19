using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;

namespace Humanier.Terrain
{
    [DisallowMultipleComponent]
    public sealed class TerrainWorld : MonoBehaviour
    {
        private static readonly ProfilerMarker StreamingMarker = new ProfilerMarker("Terrain.StreamingPlan");
        private static readonly ProfilerMarker StreamingBudgetMarker = new ProfilerMarker("Terrain.StreamingPlan.SurfaceBudget");
        private static readonly ProfilerMarker CreateChunkMarker = new ProfilerMarker("Terrain.CreateChunk");
        private static readonly ProfilerMarker ApplyMeshesMarker = new ProfilerMarker("Terrain.ApplyCompletedMeshes");
        private static readonly ProfilerMarker ScheduleMeshesMarker = new ProfilerMarker("Terrain.ScheduleMeshBuilds");
        private static readonly ProfilerMarker AssignMeshMarker = new ProfilerMarker("Terrain.AssignMesh");
        private static readonly ProfilerMarker LodEvictionMarker = new ProfilerMarker("Terrain.UpdateLodsAndEvict");
        private static readonly ProfilerMarker RelaxLodsMarker = new ProfilerMarker("Terrain.RelaxLoadedLods");
        private static readonly ProfilerMarker BuildEditCandidatesMarker = new ProfilerMarker("Terrain.Edit.BuildCandidates");
        private static readonly ProfilerMarker PrepareEditChunkMarker = new ProfilerMarker("Terrain.Edit.PrepareChunk");
        private static readonly ProfilerMarker StageEditSamplesMarker = new ProfilerMarker("Terrain.Edit.StageSamples");
        private static readonly ProfilerMarker ResolveEditStrengthMarker = new ProfilerMarker("Terrain.Edit.ResolveStrength");
        private static readonly ProfilerMarker BuildChunkEditPlansMarker = new ProfilerMarker("Terrain.Edit.BuildChunkPlans");
        private static readonly ProfilerMarker CommitEditMarker = new ProfilerMarker("Terrain.Edit.CommitDensity");
        private static readonly ProfilerMarker NotifyEditSubscribersMarker = new ProfilerMarker("Terrain.Edit.NotifySubscribers");
        private static readonly ProfilerMarker QueueEditedMeshesMarker = new ProfilerMarker("Terrain.Edit.QueueMeshes");
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
        // Surface ranges are stable for a world seed/settings pair. Keeping the
        // ranges separately lets a focus move rebuild only the new outer ring;
        // existing columns do not need to resample procedural noise.
        private readonly Dictionary<Vector2Int, TerrainSurfaceRange> streamingColumnRanges = new Dictionary<Vector2Int, TerrainSurfaceRange>();
        private readonly List<TerrainChunkId> lodRelaxationScratch = new List<TerrainChunkId>();
        private readonly Queue<LoadedChunk> meshBuildQueue = new Queue<LoadedChunk>();
        private readonly List<LoadedChunk> inFlightMeshBuilds = new List<LoadedChunk>();
        private TerrainSessionCache cache;
        private TerrainGenerationContext generation;
        private TerrainMeshResources meshResources;
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
        private FarTerrainHeightfield farHeightfield;
        private PendingEdit activeEdit;
        private bool hasActiveEdit;
        // Candidate columns are pinned while an edit is staged so streaming and
        // capacity eviction cannot remove a chunk whose samples are part of the
        // transaction. They are cleared before the mesh-wait phase.
        private readonly HashSet<ColumnKey> editStagingColumns = new HashSet<ColumnKey>();

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
        /// <summary>Raised after a chunk mesh and collider have been replaced, including empty chunks.</summary>
        public event Action<TerrainChunkId> ChunkMeshApplied;
        /// <summary>Raised after a resident chunk is removed from this world.</summary>
        public event Action<TerrainChunkId> ChunkUnloaded;
        /// <summary>Scene-space compensation applied when the global origin changes.</summary>
        public event Action<Vector3> OriginOffsetChanged;
        /// <summary>Raised after a terrain edit's density transaction is committed.</summary>
        /// <summary>Raised after a non-empty edit; center is in the public scene-space coordinate system.</summary>
        public event Action<Vector3, float> EditCommitted;
        /// <summary>Raised after a committed edit with enough information for derived renderers to rebuild.</summary>
        public event Action<TerrainEditSummary> EditSummaryCommitted;

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
            public MeshSignature desiredMesh;
            public bool hasDesiredMesh;
        }

        private readonly struct MeshSignature : IEquatable<MeshSignature>
        {
            private readonly int dataVersion;
            private readonly int lod;
            private readonly TransitionFaceMask transitionFaces;
            private readonly Vector3 origin;
            public MeshSignature(int dataVersion, int lod, TransitionFaceMask transitionFaces, Vector3 origin)
            {
                this.dataVersion = dataVersion;
                this.lod = lod;
                this.transitionFaces = transitionFaces;
                this.origin = origin;
            }
            public bool Equals(MeshSignature other) => dataVersion == other.dataVersion && lod == other.lod &&
                transitionFaces == other.transitionFaces && origin == other.origin;
            public override bool Equals(object obj) => obj is MeshSignature other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = dataVersion;
                    hash = hash * 397 ^ lod;
                    hash = hash * 397 ^ (int)transitionFaces;
                    return hash * 397 ^ origin.GetHashCode();
                }
            }
        }
        private struct PendingEdit
        {
            public TerrainEditRequest request;
            public TerrainEditHandle handle;
        }
        private readonly struct StagedSample
        {
            public readonly float before;
            public readonly float target;
            public readonly float blend;
            public readonly byte fillMaterial;
            public readonly byte beforeMaterial;

            public StagedSample(float before, float target, float blend, byte fillMaterial, byte beforeMaterial)
            {
                this.before = before;
                this.target = target;
                this.blend = blend;
                this.fillMaterial = fillMaterial;
                this.beforeMaterial = beforeMaterial;
            }
        }
        private sealed class ChunkEditPlan
        {
            public readonly LoadedChunk chunk;
            public readonly List<int> indices = new List<int>();
            public readonly List<float> density = new List<float>();
            public readonly List<byte> material = new List<byte>();

            public ChunkEditPlan(LoadedChunk chunk) { this.chunk = chunk; }
        }
        private sealed class EditPreparationResult
        {
            public bool completed;
            public string error;
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
            if (focus == null && Camera.main != null) focus = Camera.main.transform;
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
            EnsureInitialized();
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
            // Refresh far coverage after near meshes have been applied and any
            // streaming eviction has completed. This keeps the mask aligned with
            // what is actually rendered during this frame, rather than waiting
            // for the next frame after a collider or chunk event.
            if (farHeightfield != null && focus != null) farHeightfield.Tick(focus.position);
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
            inFlightMeshBuilds.Clear();
            ClearEditStagingPins();
            activeMeshBuilds = 0;
            meshResources?.Dispose();
            meshResources = null;
            if (cache != null && !cache.Dispose()) CacheWriteFailed?.Invoke($"Could not clear terrain session cache: {cache.LastError}");
            generation = null;
            if (generatedSettings != null) Destroy(generatedSettings);
            if (generatedMaterial != null) Destroy(generatedMaterial);
        }

        public void SetFocus(Transform value) => focus = value;

        /// <summary>
        /// Supplies the runtime settings and streaming focus before this world starts
        /// generating chunks. Directly authored worlds may omit this call and retain
        /// the package's generated default settings.
        /// </summary>
        public void Configure(TerrainWorldSettings configuredSettings, Transform configuredFocus)
        {
            if (configuredSettings == null) throw new ArgumentNullException(nameof(configuredSettings));
            if (initialized || cache != null || generation != null || chunks.Count > 0)
                throw new InvalidOperationException("TerrainWorld.Configure must be called before terrain generation starts.");

            if (generatedSettings != null)
            {
                Destroy(generatedSettings);
                generatedSettings = null;
            }

            settings = configuredSettings;
            focus = configuredFocus;
        }

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
            OriginOffsetChanged?.Invoke(compensation);
        }

        /// <summary>Copies resident IDs without generating terrain or reading the session cache.</summary>
        public void CopyLoadedChunkIds(List<TerrainChunkId> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            foreach (TerrainChunkId id in chunks.Keys) destination.Add(id);
        }

        /// <summary>Read-only access to a current, non-empty resident collider. Never loads a chunk.</summary>
        public bool TryGetChunkCollider(TerrainChunkId id, out MeshCollider collider)
        {
            collider = null;
            if (!chunks.TryGetValue(id, out LoadedChunk chunk) || chunk.collider == null ||
                chunk.appliedMeshRevision != chunk.meshRevision || chunk.collider.sharedMesh == null ||
                chunk.collider.sharedMesh.vertexCount == 0) return false;
            collider = chunk.collider;
            return true;
        }

        /// <summary>
        /// Returns whether a resident render mesh is currently assigned. This
        /// deliberately does not wait for the collider: the far-terrain
        /// coverage mask follows what the player can see, not collision upload
        /// timing.
        /// </summary>
        public bool TryGetChunkRenderMesh(TerrainChunkId id)
        {
            if (!chunks.TryGetValue(id, out LoadedChunk chunk) || chunk.filter == null) return false;
            Mesh mesh = chunk.filter.sharedMesh;
            return mesh != null && mesh.vertexCount > 0;
        }

        public TerrainBiome SampleBiome(Vector3 worldPosition)
        {
            GetGenerationContext().Validate();
            return TerrainGenerator.SampleBiome(Settings.seed, worldPosition.x + originOffset.x, worldPosition.z + originOffset.z);
        }
        public byte SampleSurfaceMaterial(Vector3 worldPosition)
        {
            GetGenerationContext().Validate();
            return TerrainGenerator.SurfaceMaterialAt(Settings, worldPosition.x + originOffset.x, worldPosition.z + originOffset.z);
        }
        public float SampleSurfaceHeight(Vector3 worldPosition)
        {
            GetGenerationContext().Validate();
            return TerrainGenerator.SurfaceHeight(Settings, worldPosition.x + originOffset.x, worldPosition.z + originOffset.z) - originOffset.y;
        }
        public TerrainChunkId GetChunkId(Vector3 worldPosition) => WorldToChunk(worldPosition + originOffset);
        public float SampleDensity(Vector3 worldPosition)
        {
            if (!TrySampleDensity(worldPosition, out float density))
                throw new InvalidOperationException("The cached terrain data could not be read. Inspect CacheError before retrying the query.");
            return density;
        }

        public float SampleGeneratedDensity(Vector3 worldPosition)
        {
            GetGenerationContext().Validate();
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
                var cached = new TerrainChunkData(Settings, id, GetGenerationContext(), false);
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
            return chunks.TryGetValue(id, out LoadedChunk chunk) && chunk.collider != null && chunk.appliedMeshRevision == chunk.meshRevision;
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
            GetGenerationContext().Validate();
            var handle = new TerrainEditHandle();
            if (runtimeSuspended || !isActiveAndEnabled) { handle.Status = TerrainEditStatus.Cancelled; handle.Error = "Terrain world is disabled."; handle.Notify(); return handle; }
            if (cacheWriteBlocked) { handle.Status = TerrainEditStatus.CacheFailure; handle.Error = "Terrain cache is unavailable; editing is paused to protect modified terrain."; handle.Notify(); return handle; }
            if (!IsFinite(request.worldCenter) || !IsFinite(request.radius) || !IsFinite(request.strength) || !IsFinite(request.flattenHeight) ||
                request.radius < 0.1f || request.radius > 64f)
            { handle.Status = TerrainEditStatus.Rejected; handle.Error = "Brush values must be finite and radius must be within 0.1 and 64 metres."; handle.Notify(); return handle; }
            if (request.maxAddedSolidVolume.HasValue &&
                (!IsFinite(request.maxAddedSolidVolume.Value) || request.maxAddedSolidVolume.Value < 0f))
            { handle.Status = TerrainEditStatus.Rejected; handle.Error = "maxAddedSolidVolume must be finite and non-negative."; handle.Notify(); return handle; }
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
            if (runtimeSuspended || destroyed || !isActiveAndEnabled) yield break;

            Vector3 globalCenter = request.worldCenter;
            List<TerrainChunkId> candidates;
            string candidateError;
            using (BuildEditCandidatesMarker.Auto())
                candidates = BuildEditCandidates(globalCenter, request.radius, out candidateError);
            if (candidates == null)
            {
                handle.Status = TerrainEditStatus.CacheFailure;
                handle.Error = candidateError;
                handle.Notify();
                yield break;
            }

            var stagedChunks = new List<LoadedChunk>(candidates.Count);
            var preparationResult = new EditPreparationResult();
            IEnumerator preparationRoutine = PrepareEditChunks(candidates, globalCenter, stagedChunks, handle, preparationResult);
            Exception preparationException = null;
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = preparationRoutine.MoveNext();
                }
                catch (Exception exception)
                {
                    preparationException = exception;
                    break;
                }
                if (!hasNext) break;
                yield return preparationRoutine.Current;
            }
            if (preparationException != null)
            {
                ClearEditStagingPins();
                if (runtimeSuspended || destroyed || !isActiveAndEnabled) yield break;
                handle.Status = TerrainEditStatus.CacheFailure;
                handle.Error = $"Terrain edit preparation failed: {preparationException.Message}";
                handle.Notify();
                yield break;
            }
            if (!preparationResult.completed)
            {
                ClearEditStagingPins();
                if (runtimeSuspended || destroyed || !isActiveAndEnabled) yield break;
                handle.Status = TerrainEditStatus.CacheFailure;
                handle.Error = preparationResult.error;
                handle.Notify();
                yield break;
            }

            Dictionary<TerrainChunkId, LoadedChunk> stagedChunkMap = BuildStagedChunkMap(stagedChunks);
            Dictionary<Vector3Int, StagedSample> samples;
            try
            {
                using (StageEditSamplesMarker.Auto())
                    samples = StageEditSamples(request, stagedChunkMap);
            }
            catch (Exception exception)
            {
                ClearEditStagingPins();
                if (runtimeSuspended || destroyed || !isActiveAndEnabled) yield break;
                handle.Status = TerrainEditStatus.CacheFailure;
                handle.Error = $"Terrain edit staging failed: {exception.Message}";
                handle.Notify();
                yield break;
            }

            float effectiveStrength;
            using (ResolveEditStrengthMarker.Auto())
                effectiveStrength = ResolveEditStrength(request, samples);
            List<ChunkEditPlan> plans;
            using (BuildChunkEditPlansMarker.Auto())
                plans = BuildChunkEditPlans(request, stagedChunks, samples, effectiveStrength);
            var requiredMeshRevisions = new Dictionary<TerrainChunkId, int>(candidates.Count);

            // Commit every prepared density/material write without yielding or
            // invoking callbacks. This is the transaction boundary: once it is
            // crossed, all accounting remains valid even if lifecycle cancellation
            // occurs while resident meshes are waiting to catch up.
            double removedVolume;
            double addedVolume;
            TerrainMaterialVolumeBreakdown removedMaterialVolumes;
            using (CommitEditMarker.Auto())
            {
                CalculateVolumeDelta(samples, effectiveStrength, out removedVolume, out addedVolume,
                    out removedMaterialVolumes);
                foreach (ChunkEditPlan plan in plans)
                    plan.chunk.data.CommitSamples(plan.indices, plan.density, plan.material);
            }

            ClearEditStagingPins();
            handle.DataCommitted = true;
            handle.RemovedSolidVolume = (float)removedVolume;
            handle.AddedSolidVolume = (float)addedVolume;
            handle.RemovedMaterialVolumes = removedMaterialVolumes;
            using (NotifyEditSubscribersMarker.Auto())
            {
                // Requests are converted to global coordinates before staging. Publish
                // the public scene-space center so consumers remain stable across origin shifts.
                try { if (plans.Count > 0) EditCommitted?.Invoke(request.worldCenter - originOffset, request.radius); }
                catch (Exception exception) { Debug.LogException(exception, this); }
                try
                {
                    if (plans.Count > 0)
                    {
                        EditSummaryCommitted?.Invoke(new TerrainEditSummary(
                            request.mode, request.worldCenter - originOffset, request.radius, effectiveStrength,
                            request.flattenHeight - originOffset.y, (float)removedVolume, (float)addedVolume,
                            removedMaterialVolumes));
                    }
                }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }

            using (QueueEditedMeshesMarker.Auto())
            {
                foreach (ChunkEditPlan plan in plans)
                {
                    RebuildChunk(plan.chunk);
                    requiredMeshRevisions[plan.chunk.data.Id] = plan.chunk.meshRevision;
                    RefreshNeighbours(plan.chunk.data.Id);
                }
                foreach (TerrainChunkId candidate in candidates)
                {
                    if (chunks.TryGetValue(candidate, out LoadedChunk chunk))
                        requiredMeshRevisions[candidate] = chunk.meshRevision;
                }
                foreach (ChunkEditPlan plan in plans)
                {
                    foreach (TerrainChunkId neighbour in Neighbours(plan.chunk.data.Id))
                        if (chunks.TryGetValue(neighbour, out LoadedChunk adjacent)) requiredMeshRevisions[neighbour] = adjacent.meshRevision;
                }
            }

            handle.Progress = .8f;
            handle.Notify();

            if (handle.Status == TerrainEditStatus.Cancelled || destroyed || runtimeSuspended || !isActiveAndEnabled) yield break;

            while (!AreEditedMeshesApplied(requiredMeshRevisions, out int applied))
            {
                if (handle.Status == TerrainEditStatus.Cancelled || destroyed || runtimeSuspended) yield break;
                handle.Progress = .8f + .2f * applied / Mathf.Max(1, requiredMeshRevisions.Count);
                handle.Notify();
                yield return null;
            }
            handle.Progress = 1f; handle.Status = TerrainEditStatus.Completed; handle.Notify();
        }

        private List<TerrainChunkId> BuildEditCandidates(Vector3 globalCenter, float radius, out string error)
        {
            error = null;
            float size = Settings.ChunkSize;
            if (!IsFinite(size) || size <= 0f)
            {
                error = "Terrain chunk size is invalid; edit preparation was rejected.";
                return null;
            }

            int minX = Mathf.FloorToInt((globalCenter.x - radius) / size);
            int maxX = Mathf.FloorToInt((globalCenter.x + radius) / size);
            int minY = Mathf.FloorToInt((globalCenter.y - radius) / size);
            int maxY = Mathf.FloorToInt((globalCenter.y + radius) / size);
            int minZ = Mathf.FloorToInt((globalCenter.z - radius) / size);
            int maxZ = Mathf.FloorToInt((globalCenter.z + radius) / size);

            long xCount = (long)maxX - minX + 1L;
            long yCount = (long)maxY - minY + 1L;
            long zCount = (long)maxZ - minZ + 1L;
            long candidateCount = xCount * yCount * zCount;
            int capacity = MaxResidentChunkCount();
            if (candidateCount <= 0L || candidateCount > capacity || candidateCount > int.MaxValue)
            {
                error = $"Edit touches {candidateCount} chunks, but this terrain can stage at most {capacity} resident chunks.";
                return null;
            }

            // The staged sample index is intentionally bounded as well as the
            // resident chunk set. A very small voxel size combined with the
            // public 64 m brush limit can otherwise allocate a multi-gigabyte
            // dictionary before the edit has a chance to fail cleanly.
            double samplesPerAxis = Math.Ceiling((2d * radius) / Settings.voxelSize) + 1d;
            double estimatedStagingBytes = samplesPerAxis * samplesPerAxis * samplesPerAxis * 64d;
            double availableBytes = Math.Max(1d, (double)Settings.memoryCacheLimitMb * 1024d * 1024d);
            if (estimatedStagingBytes > availableBytes / 3d)
            {
                error = "Edit sample staging would exceed the terrain memory budget; reduce the brush radius or increase memoryCacheLimitMb.";
                return null;
            }

            var candidates = new List<TerrainChunkId>((int)candidateCount);
            for (int z = minZ; z <= maxZ; z++)
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                candidates.Add(new TerrainChunkId(x, y, z));
            return candidates;
        }

        private IEnumerator PrepareEditChunks(List<TerrainChunkId> candidates, Vector3 priorityCenter,
            List<LoadedChunk> stagedChunks, TerrainEditHandle handle, EditPreparationResult result)
        {
            result.completed = false;
            result.error = null;

            // Let the request enter Processing before the potentially expensive
            // cache work starts. Pins are installed below and remain in place
            // across every preparation yield.
            yield return null;
            if (runtimeSuspended || destroyed || !isActiveAndEnabled)
            {
                result.error = "Terrain world was disabled before edit preparation completed.";
                yield break;
            }

            editStagingColumns.Clear();
            for (int i = 0; i < candidates.Count; i++)
                editStagingColumns.Add(new ColumnKey(candidates[i].x, candidates[i].z));

            int capacity = MaxResidentChunkCount();
            if (candidates.Count > capacity)
            {
                result.error = $"Edit touches {candidates.Count} chunks, but this terrain can stage at most {capacity} resident chunks.";
                yield break;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (runtimeSuspended || destroyed || !isActiveAndEnabled)
                {
                    result.error = "Terrain world was disabled before edit preparation completed.";
                    yield break;
                }

                LoadedChunk chunk;
                using (PrepareEditChunkMarker.Auto())
                    chunk = GetOrCreateChunk(candidates[i], priorityCenter);
                if (chunk == null)
                {
                    result.error = "Terrain cache or resident data capacity is unavailable; edit was paused before any samples were changed.";
                    yield break;
                }
                stagedChunks.Add(chunk);

                if ((i + 1) % 6 == 0 || i == candidates.Count - 1)
                {
                    handle.Progress = .55f * (i + 1f) / candidates.Count;
                    handle.Notify();
                    if (runtimeSuspended || destroyed || !isActiveAndEnabled)
                    {
                        result.error = "Terrain world was disabled before edit preparation completed.";
                        yield break;
                    }
                    yield return null;
                }
            }
            result.completed = true;
        }

        private static Dictionary<TerrainChunkId, LoadedChunk> BuildStagedChunkMap(List<LoadedChunk> stagedChunks)
        {
            var result = new Dictionary<TerrainChunkId, LoadedChunk>(stagedChunks.Count);
            for (int i = 0; i < stagedChunks.Count; i++)
                result[stagedChunks[i].data.Id] = stagedChunks[i];
            return result;
        }

        private Dictionary<Vector3Int, StagedSample> StageEditSamples(TerrainEditRequest request,
            Dictionary<TerrainChunkId, LoadedChunk> stagedChunks)
        {
            GetGenerationContext().Validate();
            int resolution = Settings.chunkResolution;
            float voxelSize = Settings.voxelSize;
            float radius = Mathf.Clamp(request.radius, .1f, 64f);
            float radiusSquared = radius * radius;
            GetEditSampleBounds(request.worldCenter, radius, voxelSize,
                out int minGlobalX, out int maxGlobalX, out int minGlobalY,
                out int maxGlobalY, out int minGlobalZ, out int maxGlobalZ);
            var result = new Dictionary<Vector3Int, StagedSample>();
            for (int globalZ = minGlobalZ; globalZ <= maxGlobalZ; globalZ++)
            for (int globalX = minGlobalX; globalX <= maxGlobalX; globalX++)
            {
                float worldX = globalX * voxelSize;
                float worldZ = globalZ * voxelSize;
                float deltaX = worldX - request.worldCenter.x;
                float deltaZ = worldZ - request.worldCenter.z;
                float horizontalSquared = deltaX * deltaX + deltaZ * deltaZ;
                if (horizontalSquared > radiusSquared) continue;

                float verticalExtent = Mathf.Sqrt(radiusSquared - horizontalSquared);
                int columnMinY = Mathf.Max(minGlobalY,
                    Mathf.CeilToInt((request.worldCenter.y - verticalExtent) / voxelSize));
                int columnMaxY = Mathf.Min(maxGlobalY,
                    Mathf.FloorToInt((request.worldCenter.y + verticalExtent) / voxelSize));
                TerrainColumnSample column = TerrainGenerator.SampleColumn(Settings, worldX, worldZ);
                for (int globalY = columnMinY; globalY <= columnMaxY; globalY++)
                {
                    TerrainChunkId chunkId = CanonicalChunk(globalX, globalY, globalZ, resolution);
                    if (!stagedChunks.TryGetValue(chunkId, out LoadedChunk chunk)) continue;
                    int localX = PositiveModulo(globalX, resolution);
                    int localY = PositiveModulo(globalY, resolution);
                    int localZ = PositiveModulo(globalZ, resolution);
                    Vector3 point = new Vector3(worldX, globalY * voxelSize, worldZ);
                    if (column.IsProtected(Settings, point.y)) continue;

                    float deltaY = point.y - request.worldCenter.y;
                    float distance = Mathf.Sqrt(horizontalSquared + deltaY * deltaY);
                    float before = chunk.data.GetDensity(localX, localY, localZ);
                    EvaluateEditSample(Settings, before, point, request, radius, distance, out float target, out float blend);
                    result.Add(new Vector3Int(globalX, globalY, globalZ),
                        new StagedSample(before, target, blend, column.SurfaceMaterial,
                            chunk.data.GetMaterial(localX, localY, localZ)));
                }
            }
            return result;
        }

        private float ResolveEditStrength(TerrainEditRequest request, Dictionary<Vector3Int, StagedSample> samples)
        {
            float requestedStrength = NormalizeStrength(request.strength);
            if (!request.maxAddedSolidVolume.HasValue) return requestedStrength;

            double budget = request.maxAddedSolidVolume.Value;
            if (request.mode == TerrainBrushMode.Fill && budget <= 0d) return 0f;
            CalculateVolumeDelta(samples, requestedStrength, out _, out double fullAdded);
            if (fullAdded <= budget || fullAdded <= 0d) return requestedStrength;

            float low = 0f;
            float high = requestedStrength;
            // Occupancy is monotonic in strength. Sixteen bisection steps are
            // below the density precision needed by the voxel grid and avoid
            // repeatedly walking the full staged sample set 32 times.
            for (int iteration = 0; iteration < 16; iteration++)
            {
                float middle = (low + high) * .5f;
                CalculateVolumeDelta(samples, middle, out _, out double added);
                if (added <= budget) low = middle;
                else high = middle;
            }
            return low;
        }

        private List<ChunkEditPlan> BuildChunkEditPlans(TerrainEditRequest request, List<LoadedChunk> stagedChunks,
            Dictionary<Vector3Int, StagedSample> samples, float effectiveStrength)
        {
            var plans = new List<ChunkEditPlan>();
            bool zeroBudgetFill = request.mode == TerrainBrushMode.Fill && request.maxAddedSolidVolume.HasValue &&
                request.maxAddedSolidVolume.Value <= 0f;
            if (zeroBudgetFill || samples.Count == 0) return plans;
            int resolution = Settings.chunkResolution;
            GetEditSampleBounds(request.worldCenter, Mathf.Clamp(request.radius, .1f, 64f), Settings.voxelSize,
                out int minGlobalX, out int maxGlobalX, out int minGlobalY,
                out int maxGlobalY, out int minGlobalZ, out int maxGlobalZ);
            for (int chunkIndex = 0; chunkIndex < stagedChunks.Count; chunkIndex++)
            {
                LoadedChunk chunk = stagedChunks[chunkIndex];
                TerrainChunkData data = chunk.data;
                int chunkOriginX = data.Id.x * resolution;
                int chunkOriginY = data.Id.y * resolution;
                int chunkOriginZ = data.Id.z * resolution;
                int minX = Mathf.Max(0, minGlobalX - chunkOriginX);
                int maxX = Mathf.Min(resolution, maxGlobalX - chunkOriginX);
                int minY = Mathf.Max(0, minGlobalY - chunkOriginY);
                int maxY = Mathf.Min(resolution, maxGlobalY - chunkOriginY);
                int minZ = Mathf.Max(0, minGlobalZ - chunkOriginZ);
                int maxZ = Mathf.Min(resolution, maxGlobalZ - chunkOriginZ);
                if (minX > maxX || minY > maxY || minZ > maxZ) continue;
                var plan = new ChunkEditPlan(chunk);
                for (int z = minZ; z <= maxZ; z++)
                for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    int globalX = chunkOriginX + x;
                    int globalY = chunkOriginY + y;
                    int globalZ = chunkOriginZ + z;
                    if (!samples.TryGetValue(new Vector3Int(globalX, globalY, globalZ), out StagedSample sample)) continue;
                    float after = Mathf.Lerp(sample.before, sample.target, sample.blend * effectiveStrength);
                    byte afterMaterial = data.GetMaterial(x, y, z);
                    if (request.mode == TerrainBrushMode.Fill && after > 0f)
                        afterMaterial = request.material == 0 ? sample.fillMaterial : request.material;
                    int index = data.Index(x, y, z);
                    if (data.Density[index] == after && data.Material[index] == afterMaterial) continue;
                    plan.indices.Add(index);
                    plan.density.Add(after);
                    plan.material.Add(afterMaterial);
                }
                if (plan.indices.Count > 0) plans.Add(plan);
            }
            return plans;
        }

        private static void GetEditSampleBounds(Vector3 center, float radius, float voxelSize,
            out int minX, out int maxX, out int minY, out int maxY, out int minZ, out int maxZ)
        {
            minX = Mathf.CeilToInt((center.x - radius) / voxelSize);
            maxX = Mathf.FloorToInt((center.x + radius) / voxelSize);
            minY = Mathf.CeilToInt((center.y - radius) / voxelSize);
            maxY = Mathf.FloorToInt((center.y + radius) / voxelSize);
            minZ = Mathf.CeilToInt((center.z - radius) / voxelSize);
            maxZ = Mathf.FloorToInt((center.z + radius) / voxelSize);
        }

        private void CalculateVolumeDelta(Dictionary<Vector3Int, StagedSample> samples,
            float effectiveStrength, out double removed, out double added)
        {
            CalculateVolumeDelta(samples, effectiveStrength, out removed, out added, out _);
        }

        private void CalculateVolumeDelta(Dictionary<Vector3Int, StagedSample> samples,
            float effectiveStrength, out double removed, out double added,
            out TerrainMaterialVolumeBreakdown removedMaterials)
        {
            removed = 0d;
            added = 0d;
            double material1 = 0d;
            double material2 = 0d;
            double material3 = 0d;
            double material4 = 0d;
            double voxelVolume = (double)Settings.voxelSize * Settings.voxelSize * Settings.voxelSize;
            foreach (StagedSample sample in samples.Values)
            {
                float after = Mathf.Lerp(sample.before, sample.target, sample.blend * effectiveStrength);
                float beforeOccupancy = Occupancy(sample.before, Settings.voxelSize);
                float afterOccupancy = Occupancy(after, Settings.voxelSize);
                if (afterOccupancy < beforeOccupancy)
                {
                    double delta = (beforeOccupancy - afterOccupancy) * voxelVolume;
                    removed += delta;
                    switch (TerrainMaterialPalette.BucketFor(sample.beforeMaterial))
                    {
                        case 2: material2 += delta; break;
                        case 3: material3 += delta; break;
                        case 4: material4 += delta; break;
                        default: material1 += delta; break;
                    }
                }
                else if (afterOccupancy > beforeOccupancy) added += (afterOccupancy - beforeOccupancy) * voxelVolume;
            }
            removedMaterials = new TerrainMaterialVolumeBreakdown(
                (float)material1, (float)material2, (float)material3, (float)material4);
        }

        private static void EvaluateEditSample(TerrainWorldSettings settings, float before, Vector3 point, TerrainEditRequest request,
            float radius, float distance, out float target, out float blend)
        {
            blend = Mathf.Clamp01(1f - distance / radius);
            float facetOffset = TerrainGenerator.FacetEditOffset(settings, point, request.worldCenter, radius, distance);
            switch (request.mode)
            {
                case TerrainBrushMode.Dig:
                    target = Mathf.Min(before, distance - radius + facetOffset);
                    break;
                case TerrainBrushMode.Fill:
                    target = Mathf.Max(before, radius - distance - facetOffset);
                    break;
                default:
                    target = request.flattenHeight - point.y;
                    break;
            }
        }

        private static float NormalizeStrength(float strength) => Mathf.Clamp01(strength <= 0f ? 1f : strength);
        private static float Occupancy(float density, float voxelSize) => Mathf.Clamp01(.5f + density / voxelSize);

        private static TerrainChunkId CanonicalChunk(int globalX, int globalY, int globalZ, int resolution) => new TerrainChunkId(
            FloorDivide(globalX, resolution), FloorDivide(globalY, resolution), FloorDivide(globalZ, resolution));

        private static int FloorDivide(int value, int divisor)
        {
            int quotient = value / divisor;
            if (value < 0 && value % divisor != 0) quotient--;
            return quotient;
        }

        private static int PositiveModulo(int value, int divisor)
        {
            int result = value % divisor;
            return result < 0 ? result + divisor : result;
        }

        private void UpdateStreamingPlan(Vector3 globalFocus)
        {
            using var profileScope = StreamingMarker.Auto();
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
            ContinueStreamingPlan(int.MaxValue, true);
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
            if (streamingColumnRanges.Count > 0)
            {
                var active = new HashSet<Vector2Int>(streamingColumns);
                var stale = new List<Vector2Int>();
                foreach (Vector2Int key in streamingColumnRanges.Keys)
                    if (!active.Contains(key)) stale.Add(key);
                for (int i = 0; i < stale.Count; i++) streamingColumnRanges.Remove(stale[i]);
            }
        }

        private void ContinueStreamingPlan(int budget, bool ignoreTimeBudget = false)
        {
            if (budget <= 0) return;
            using var budgetProfileScope = StreamingBudgetMarker.Auto();
            Vector3 planFocus = ChunkCenter(plannedFocusChunk);
            int horizontal = Mathf.CeilToInt(Settings.viewDistance / Settings.ChunkSize);
            int horizontalSquared = horizontal * horizontal;
            int nearRadius = Mathf.CeilToInt(Settings.nearUndergroundDistance / Settings.ChunkSize);
            int nearRadiusSquared = nearRadius * nearRadius;
            int processed = 0;
            TerrainGenerationContext context = GetGenerationContext();
            float budgetDeadline = ignoreTimeBudget
                ? float.MaxValue
                : Time.realtimeSinceStartup + Mathf.Max(.25f, Settings.streamingPlanningBudgetMs) * .001f;
            while (streamingColumnIndex < streamingColumns.Count && processed < budget)
            {
                Vector2Int column = streamingColumns[streamingColumnIndex];
                int offsetX = column.x - plannedFocusChunk.x;
                int offsetZ = column.y - plannedFocusChunk.z;
                int distanceSquared = offsetX * offsetX + offsetZ * offsetZ;
                if (distanceSquared <= horizontalSquared)
                {
                    var id = new TerrainChunkId(column.x, 0, column.y);
                    if (!streamingColumnRanges.TryGetValue(column, out TerrainSurfaceRange range))
                    {
                        float remainingMs = Mathf.Max(.01f, (budgetDeadline - Time.realtimeSinceStartup) * 1000f);
                        if (!context.TryBuildSurfaceRange(id, remainingMs, out range))
                        {
                            // Editor-only reflection tests and tooling expect a
                            // complete plan in one call. Runtime streaming keeps
                            // the resumable budgeted path and never takes this
                            // synchronous fallback.
                            if (Application.isPlaying) break;
                            range = context.SurfaceRange(id);
                        }
                        streamingColumnRanges[column] = range;
                    }
                    float safety = Settings.voxelSize;
                    int minY = Mathf.FloorToInt((range.MinHeight - safety) / Settings.ChunkSize);
                    int maxY = Mathf.FloorToInt((range.MaxHeight + safety) / Settings.ChunkSize);
                    AddStreamingGroup(column.x, column.y, minY, maxY, planFocus);
                }
                if (distanceSquared <= nearRadiusSquared)
                {
                    AddStreamingGroup(column.x, column.y, plannedFocusChunk.y - verticalChunksBelowFocus, plannedFocusChunk.y + verticalChunksAboveFocus, planFocus);
                }
                streamingColumnIndex++;
                processed++;
                if (Time.realtimeSinceStartup >= budgetDeadline) break;
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
            using var profileScope = CreateChunkMarker.Auto();
            if (!EnsureDataCapacity(id, priorityCenter, streamRequest)) return false;
            bool hasCachedData = cache.HasEntry(id);
            var data = new TerrainChunkData(Settings, id, GetGenerationContext(), !hasCachedData);
            if (hasCachedData && !cache.TryLoad(data))
            {
                if (!string.IsNullOrEmpty(cache.LastError))
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
                data.GenerateInitial(Settings, GetGenerationContext());
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
            MeshSignature desired = GetMeshSignature(chunk);
            if (chunk.hasDesiredMesh && chunk.desiredMesh.Equals(desired)) return;
            chunk.desiredMesh = desired;
            chunk.hasDesiredMesh = true;
            chunk.meshDirty = true;
            chunk.meshRevision++;
            QueueMeshBuild(chunk);
        }
        private void ApplyCompletedMeshes()
        {
            using var profileScope = ApplyMeshesMarker.Auto();
            int remaining = Settings.maxMeshReplacementsPerFrame;
            for (int i = inFlightMeshBuilds.Count - 1; i >= 0; i--)
            {
                LoadedChunk chunk = inFlightMeshBuilds[i];
                if (remaining <= 0 || chunk.pendingMesh == null || !chunk.pendingMesh.IsCompleted) continue;
                TerrainMeshBuildRequest request = chunk.pendingMesh;
                chunk.pendingMesh = null;
                inFlightMeshBuilds.RemoveAt(i);
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
            using var profileScope = ScheduleMeshesMarker.Auto();
            int limit = Mathf.Max(1, Settings.maxInFlightMeshBuilds);
            while (activeMeshBuilds < limit && meshBuildQueue.Count > 0)
            {
                LoadedChunk chunk = meshBuildQueue.Dequeue();
                chunk.meshQueued = false;
                if (!chunk.isLoaded || chunk.pendingMesh != null || !chunk.meshDirty) continue;
                chunk.meshDirty = false;
                chunk.pendingMeshRevision = chunk.meshRevision;
                chunk.pendingMesh = TerrainMeshBuilder.Schedule(chunk.data, originOffset, chunk.lod, GetTransitionFaces(chunk.data.Id, chunk.lod), chunk.data.Version, meshResources);
                inFlightMeshBuilds.Add(chunk);
                activeMeshBuilds++;
            }
        }
        private void AssignMesh(LoadedChunk chunk, Mesh next, int appliedRevision)
        {
            using var profileScope = AssignMeshMarker.Auto();
            Mesh old = chunk.filter.sharedMesh;
            chunk.gameObject.transform.localPosition = Vector3.zero;
            chunk.filter.sharedMesh = next;
            if (chunk.collider != null) chunk.collider.sharedMesh = next;
            chunk.appliedMeshRevision = appliedRevision;
            if (old != null) Destroy(old);
            ChunkMeshApplied?.Invoke(chunk.data.Id);
        }
        private TerrainChunkId WorldToChunk(Vector3 globalPosition)
        {
            float size = Settings.ChunkSize;
            return new TerrainChunkId(Mathf.FloorToInt(globalPosition.x / size), Mathf.FloorToInt(globalPosition.y / size), Mathf.FloorToInt(globalPosition.z / size));
        }

        private int GetLod(TerrainChunkId id)
        {
            if (focus == null) return Mathf.Clamp(Settings.minimumMeshLod, 0, 3);
            Vector3 point = new Vector3((id.x + .5f) * Settings.ChunkSize, (id.y + .5f) * Settings.ChunkSize, (id.z + .5f) * Settings.ChunkSize);
            float distance = Vector2.Distance(new Vector2(point.x, point.z), new Vector2(focus.position.x + originOffset.x, focus.position.z + originOffset.z));
            int lod = 0;
            foreach (float threshold in Settings.lodDistances) { if (distance >= threshold) lod++; else break; }
            return Mathf.Clamp(Mathf.Max(lod, Settings.minimumMeshLod), 0, 3);
        }

        private void UpdateLodsAndEvict(Vector3 globalFocus)
        {
            using var profileScope = LodEvictionMarker.Auto();
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
                    if (IsEditStagingColumn(new ColumnKey(pair.Key.x, pair.Key.z))) continue;
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
            ChunkUnloaded?.Invoke(id);
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
                if (IsEditStagingColumn(column)) continue;
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

        private bool IsEditStagingColumn(ColumnKey column) => column.IsValid && editStagingColumns.Contains(column);
        private void ClearEditStagingPins() => editStagingColumns.Clear();

        private void EnsureInitialized()
        {
            EnsureSettings();
            if (generation == null) generation = new TerrainGenerationContext(Settings);
            generation.Validate();
            if (meshResources == null) meshResources = new TerrainMeshResources();
            if (cache != null) { initialized = true; return; }

            if (Settings.terrainMaterial == null)
            {
                Shader shader = Shader.Find("Humanier/Terrain Low Poly");
                if (shader == null)
                    throw new InvalidOperationException("Terrain material is missing and shader 'Humanier/Terrain Low Poly' could not be found. Assign a terrain material in TerrainWorldSettings.");
                generatedMaterial = new Material(shader) { name = "Runtime Terrain Material" };
            }

            cache = new TerrainSessionCache(Settings.seed, Guid.NewGuid().ToString("N"));
            if (Settings.farHeightfieldEnabled)
            {
                GameObject farObject = new GameObject("FarTerrainHeightfield");
                farObject.transform.SetParent(transform, false);
                farHeightfield = farObject.AddComponent<FarTerrainHeightfield>();
                farHeightfield.Configure(this, Settings);
                farHeightfield.Tick(focus == null ? Vector3.zero : focus.position);
            }
            initialized = true;
        }

        private TerrainGenerationContext GetGenerationContext()
        {
            EnsureSettings();
            if (generation == null) generation = new TerrainGenerationContext(Settings);
            generation.Validate();
            return generation;
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
            streamingColumnRanges.Clear();
            ClearEditStagingPins();
            meshBuildQueue.Clear();
            foreach (LoadedChunk chunk in chunks.Values)
            {
                chunk.meshQueued = false;
                if (chunk.pendingMesh == null) continue;
                DisposePendingMesh(chunk);
                chunk.meshDirty = true;
            }
            inFlightMeshBuilds.Clear();
            activeMeshBuilds = 0;
        }

        private void DisposePendingMesh(LoadedChunk chunk)
        {
            if (chunk.pendingMesh == null) return;
            inFlightMeshBuilds.Remove(chunk);
            chunk.pendingMesh.Dispose();
            chunk.pendingMesh = null;
            if (activeMeshBuilds > 0) activeMeshBuilds--;
        }
        private int MaxResidentChunkCount()
        {
            long bytesPerChunk = (long)Settings.SampleResolution * Settings.SampleResolution * Settings.SampleResolution * (sizeof(float) + sizeof(byte));
            long limit = Settings.memoryCacheLimitMb * 1024L * 1024L - TerrainGenerationContext.EstimatedCacheCapacityBytes(Settings);
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
        private MeshSignature GetMeshSignature(LoadedChunk chunk) => new MeshSignature(
            chunk.data.Version, chunk.lod, GetTransitionFaces(chunk.data.Id, chunk.lod), originOffset);

        private void RelaxLoadedLods()
        {
            using var profileScope = RelaxLodsMarker.Auto();
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
