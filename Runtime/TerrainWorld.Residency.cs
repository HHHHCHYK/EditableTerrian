using System;
using System.Collections.Generic;
using UnityEngine;

namespace Humanier.Terrain
{
    public sealed partial class TerrainWorld
    {
        private sealed class RetainedColumnPlan
        {
            public readonly ColumnKey column;
            public bool rangeReady;
            public int minY;
            public int maxY;
            public int nextY;

            public RetainedColumnPlan(ColumnKey column)
            {
                this.column = column;
            }
        }

        private sealed class RetainedRegionLease : IDisposable
        {
            private TerrainWorld owner;
            internal readonly HashSet<ColumnKey> columns;

            internal RetainedRegionLease(TerrainWorld owner, HashSet<ColumnKey> columns)
            {
                this.owner = owner;
                this.columns = columns;
            }

            public void Dispose()
            {
                TerrainWorld target = owner;
                if (target == null) return;
                owner = null;
                target.ReleaseRetainedRegion(this);
            }
        }

        private readonly Dictionary<ColumnKey, int> retainedColumnReferences = new Dictionary<ColumnKey, int>();
        private readonly Dictionary<ColumnKey, RetainedColumnPlan> retainedColumnPlans = new Dictionary<ColumnKey, RetainedColumnPlan>();
        private readonly Queue<ColumnKey> retainedRangeRequests = new Queue<ColumnKey>();
        private readonly HashSet<ColumnKey> retainedRangeQueued = new HashSet<ColumnKey>();
        private readonly Queue<TerrainChunkId> retainedChunkRequests = new Queue<TerrainChunkId>();
        private readonly HashSet<TerrainChunkId> retainedQueuedChunks = new HashSet<TerrainChunkId>();
        private readonly List<RetainedRegionLease> retainedRegionLeases = new List<RetainedRegionLease>();
        private string retainedRegionError;

        /// <summary>
        /// Describes a retention request that could not be completed within the
        /// configured resident terrain budget or cache constraints. The value is
        /// null while every active request is still being planned/generated.
        /// Consumers should dispose their lease after observing an error.
        /// </summary>
        public string RetainedRegionError => retainedRegionError;

        /// <summary>
        /// Keeps every terrain surface column intersecting the logical XZ region
        /// resident. Its surface chunks are generated incrementally under the
        /// normal per-frame chunk budget and always use collision LOD 0.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the radius or logical centre is not finite, or when the
        /// region contains more columns than this world's resident budget can hold.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when an active retention request already consumes the entire
        /// minimum resident budget, or when the world cannot initialize its
        /// generation context.
        /// </exception>
        public IDisposable RetainRegion(InfiniteWorldPosition center, float radius)
        {
            EnsureSettings();
            ValidateRetainedRegionArguments(center, radius);
            if (!string.IsNullOrEmpty(retainedRegionError))
                throw new InvalidOperationException(retainedRegionError);

            HashSet<ColumnKey> columns = BuildRetainedRegionColumns(center, radius);
            EnsureInitialized();
            GetGenerationContext().Validate();
            int capacity = MaxResidentChunkCount();
            int newColumnCount = 0;
            foreach (ColumnKey column in columns)
                if (!retainedColumnReferences.ContainsKey(column)) newColumnCount++;

            // Every retained column needs at least one resident terrain chunk.
            // This lower bound catches impossible requests without synchronously
            // generating every procedural surface range. Actual chunks still use
            // the normal incremental creation path and report later failures.
            if (retainedColumnReferences.Count + newColumnCount > capacity)
                throw new InvalidOperationException($"Retained terrain region needs {retainedColumnReferences.Count + newColumnCount} columns, but the resident terrain budget allows {capacity} chunks. Increase memoryCacheLimitMb or reduce the retention radius.");

            var lease = new RetainedRegionLease(this, columns);
            retainedRegionLeases.Add(lease);
            foreach (ColumnKey column in columns)
            {
                if (retainedColumnReferences.TryGetValue(column, out int references))
                    retainedColumnReferences[column] = references + 1;
                else
                {
                    retainedColumnReferences.Add(column, 1);
                    var plan = new RetainedColumnPlan(column);
                    retainedColumnPlans.Add(column, plan);
                    retainedRangeRequests.Enqueue(column);
                    retainedRangeQueued.Add(column);
                }
            }

            // Register the complete XZ set before changing any LOD. Neighbor
            // transition rebuilds can then recognize every column as retained,
            // even when the hash set iteration order reaches a neighbor first.
            foreach (ColumnKey column in columns)
            {
                // A column may already be resident from ordinary streaming. Make
                // it collision LOD 0 immediately rather than waiting for Update.
                ForceRetainedColumnLod(column);
            }
            return lease;
        }

        private static void ValidateRetainedRegionArguments(InfiniteWorldPosition center, float radius)
        {
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0f)
                throw new ArgumentOutOfRangeException(nameof(radius), radius, "Retained terrain radius must be finite and non-negative.");
            if (!IsFinite(center.localX) || !IsFinite(center.localZ) || !IsFinite(center.radialHeight))
                throw new ArgumentOutOfRangeException(nameof(center), "Retained terrain centre must have finite local coordinates and height.");
            double maxLong = long.MaxValue;
            double minLong = long.MinValue;
            if (center.chunkX > maxLong || center.chunkX < minLong || center.chunkZ > maxLong || center.chunkZ < minLong)
                throw new ArgumentOutOfRangeException(nameof(center), "Retained terrain centre has invalid chunk coordinates.");
        }

        private HashSet<ColumnKey> BuildRetainedRegionColumns(InfiniteWorldPosition center, float radius)
        {
            double size = Settings.ChunkSize;
            double logicalX = center.LogicalX(size);
            double logicalZ = center.LogicalZ(size);
            if (!IsFinite(logicalX) || !IsFinite(logicalZ) || size <= 0d)
                throw new ArgumentOutOfRangeException(nameof(center), "Retained terrain centre cannot be represented by the current chunk size.");

            long minX = FloorToLong((logicalX - radius) / size);
            long maxX = FloorToLong((logicalX + radius) / size);
            long minZ = FloorToLong((logicalZ - radius) / size);
            long maxZ = FloorToLong((logicalZ + radius) / size);
            long xCount = InclusiveCount(minX, maxX);
            long zCount = InclusiveCount(minZ, maxZ);
            int capacity = MaxResidentChunkCount();
            if (xCount > capacity || zCount > capacity || xCount > capacity / zCount)
                throw new InvalidOperationException($"Retained terrain region intersects more than {capacity} columns, but the resident terrain budget allows at most {capacity} chunks. Increase memoryCacheLimitMb or reduce the retention radius.");

            var columns = new HashSet<ColumnKey>();
            double radiusSquared = (double)radius * radius;
            for (long z = minZ; ; z++)
            {
                double z0 = z * size;
                double z1 = z0 + size;
                double nearestZ = logicalZ < z0 ? z0 : logicalZ > z1 ? z1 : logicalZ;
                double dz = nearestZ - logicalZ;
                for (long x = minX; ; x++)
                {
                    double x0 = x * size;
                    double x1 = x0 + size;
                    double nearestX = logicalX < x0 ? x0 : logicalX > x1 ? x1 : logicalX;
                    double dx = nearestX - logicalX;
                    if (dx * dx + dz * dz <= radiusSquared)
                        columns.Add(new ColumnKey(x, z));
                    if (x == maxX) break;
                }
                if (z == maxZ) break;
            }
            return columns;
        }

        private static long FloorToLong(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < long.MinValue || value > long.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Retained terrain coordinate is outside the supported logical range.");
            return (long)Math.Floor(value);
        }

        private static long InclusiveCount(long min, long max)
        {
            if (max < min) return 0;
            if (max == long.MaxValue && min == long.MinValue) return long.MaxValue;
            if (min < 0 && max >= 0 && max > long.MaxValue + min) return long.MaxValue;
            return checked(max - min + 1L);
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private bool HasPendingChunkRequests() => retainedChunkRequests.Count > 0 || requestedChunks.Count > 0;

        private bool IsRetainedColumn(ColumnKey column) => column.IsValid && retainedColumnReferences.ContainsKey(column);

        private bool IsRetainedChunk(TerrainChunkId id) => IsRetainedColumn(new ColumnKey(id.x, id.z));

        private void ForceRetainedColumnLod(ColumnKey column)
        {
            foreach (KeyValuePair<TerrainChunkId, LoadedChunk> pair in chunks)
            {
                if (pair.Key.x != column.x || pair.Key.z != column.z) continue;
                LoadedChunk chunk = pair.Value;
                if (chunk.lod == 0) continue;
                bool hadUsableCollision = chunk.collider != null && chunk.collider.sharedMesh != null &&
                    chunk.collider.sharedMesh.vertexCount > 0 && chunk.appliedDataVersion == chunk.data.Version;
                chunk.lod = 0;
                RebuildChunk(chunk);
                if (hadUsableCollision)
                    chunk.retainedLodUpgradeRevision = chunk.meshRevision;
                RefreshNeighbours(pair.Key);
            }
        }

        private void ContinueRetainedRegionPlanning()
        {
            if (!string.IsNullOrEmpty(retainedRegionError) || retainedRangeRequests.Count == 0 || generation == null) return;
            float deadline = Time.realtimeSinceStartup + Mathf.Max(.25f, Settings.streamingPlanningBudgetMs) * .001f;
            while (retainedRangeRequests.Count > 0)
            {
                ColumnKey column = retainedRangeRequests.Peek();
                if (!retainedColumnReferences.ContainsKey(column))
                {
                    retainedRangeRequests.Dequeue();
                    retainedRangeQueued.Remove(column);
                    continue;
                }

                float remainingMs = Mathf.Max(.01f, (deadline - Time.realtimeSinceStartup) * 1000f);
                if (!generation.TryBuildSurfaceRange(new TerrainChunkId(column.x, 0, column.z), remainingMs, out TerrainSurfaceRange range))
                    break;

                retainedRangeRequests.Dequeue();
                retainedRangeQueued.Remove(column);
                RetainedColumnPlan plan = retainedColumnPlans[column];
                float safety = Settings.voxelSize;
                plan.minY = Mathf.FloorToInt((range.MinHeight - safety) / Settings.ChunkSize);
                plan.maxY = Mathf.FloorToInt((range.MaxHeight + safety) / Settings.ChunkSize);
                if (plan.maxY < plan.minY)
                {
                    int swap = plan.minY;
                    plan.minY = plan.maxY;
                    plan.maxY = swap;
                }
                // Surface ranges may be entirely below logical Y=0. The
                // default value of nextY is zero, so initialize it explicitly
                // or those negative chunks would never be requested.
                plan.nextY = plan.minY;
                plan.rangeReady = true;
                if (!ValidateRetainedChunkCapacity()) break;
                if (Time.realtimeSinceStartup >= deadline) break;
            }
        }

        private void QueueRetainedRegionChunks()
        {
            if (!string.IsNullOrEmpty(retainedRegionError)) return;
            // Retained work has priority over ordinary streaming. Keep its
            // bounded queue independent of maxQueuedChunks so a full, stale
            // streaming queue cannot starve a newly requested evacuation area.
            int maxQueued = MaxResidentChunkCount();
            if (retainedChunkRequests.Count >= maxQueued) return;
            foreach (RetainedColumnPlan plan in retainedColumnPlans.Values)
            {
                if (!plan.rangeReady) continue;
                while (plan.nextY <= plan.maxY && retainedChunkRequests.Count < maxQueued)
                {
                    TerrainChunkId id = new TerrainChunkId(plan.column.x, plan.nextY++, plan.column.z);
                    if (chunks.ContainsKey(id) || !retainedQueuedChunks.Add(id)) continue;
                    retainedChunkRequests.Enqueue(id);
                }
                if (retainedChunkRequests.Count >= maxQueued) break;
            }
        }

        private void RegisterRetainedRegionError(string error)
        {
            if (string.IsNullOrEmpty(retainedRegionError))
            {
                retainedRegionError = error;
                // A failed retention must not be retried forever ahead of the
                // ordinary streaming queue. Existing retained chunks remain
                // pinned until the consumer disposes its lease, but missing
                // work is discarded and the error is observable publicly.
                retainedRangeRequests.Clear();
                retainedRangeQueued.Clear();
                retainedChunkRequests.Clear();
                retainedQueuedChunks.Clear();
                Debug.LogError(error, this);
            }
        }

        private bool ValidateRetainedChunkCapacity()
        {
            long required = 0L;
            foreach (RetainedColumnPlan plan in retainedColumnPlans.Values)
            {
                if (!plan.rangeReady) continue;
                long count = (long)plan.maxY - plan.minY + 1L;
                required += count;
                if (required > MaxResidentChunkCount())
                {
                    RegisterRetainedRegionError($"Retained terrain region needs at least {required} resident chunks, but the terrain budget allows {MaxResidentChunkCount()}. Increase memoryCacheLimitMb or reduce the retention radius.");
                    return false;
                }
            }
            return true;
        }

        private void RequeueRetainedChunk(TerrainChunkId id)
        {
            if (!IsRetainedChunk(id) || !retainedQueuedChunks.Add(id)) return;
            retainedChunkRequests.Enqueue(id);
        }

        private void ReleaseRetainedRegion(RetainedRegionLease lease)
        {
            if (destroyed) return;
            retainedRegionLeases.Remove(lease);
            foreach (ColumnKey column in lease.columns)
            {
                if (!retainedColumnReferences.TryGetValue(column, out int references)) continue;
                if (references > 1)
                {
                    retainedColumnReferences[column] = references - 1;
                    continue;
                }
                retainedColumnReferences.Remove(column);
                retainedColumnPlans.Remove(column);
                retainedRangeQueued.Remove(column);
            }
            RemoveReleasedRetentionRequests();
            if (retainedRegionLeases.Count == 0)
                retainedRegionError = null;
        }

        private void RemoveReleasedRetentionRequests()
        {
            if (retainedChunkRequests.Count == 0) return;
            var keep = new Queue<TerrainChunkId>(retainedChunkRequests.Count);
            while (retainedChunkRequests.Count > 0)
            {
                TerrainChunkId id = retainedChunkRequests.Dequeue();
                retainedQueuedChunks.Remove(id);
                if (IsRetainedChunk(id))
                {
                    keep.Enqueue(id);
                    retainedQueuedChunks.Add(id);
                }
            }
            while (keep.Count > 0) retainedChunkRequests.Enqueue(keep.Dequeue());
        }

        private void ClearRetainedRegions()
        {
            retainedRegionLeases.Clear();
            retainedColumnReferences.Clear();
            retainedColumnPlans.Clear();
            retainedRangeRequests.Clear();
            retainedRangeQueued.Clear();
            retainedChunkRequests.Clear();
            retainedQueuedChunks.Clear();
            retainedRegionError = null;
        }
    }
}
