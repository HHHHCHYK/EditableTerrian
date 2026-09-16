# Humanier Destructible Terrain

`com.humanier.terrain` is an embedded UPM package for continuous, editable terrain. It uses a deterministic signed-density field sampled in 32-cube chunks at a default 0.5 m cell size. Burst-compiled Transvoxel mesh jobs produce a smooth low-poly surface and allow caves, tunnels, overhangs, digging, filling, and flattening.

## Use

Create a `TerrainWorld` GameObject and assign a `TerrainWorldSettings` asset. Set a camera or player transform as its focus. Call `RequestEdit` with `TerrainEditRequest.Dig`, `Fill`, or `Flatten`. Edits accept radii from 0.1 through 64 metres and return a `TerrainEditHandle` that reports completion progress.

For the included scene, run `Humanier > Terrain > Create Demo Scene` in the Unity Editor. The created explorer camera uses the mouse to target terrain, left click to apply the selected brush, number keys `1` through `3` to select dig/fill/flatten, and the wheel to set a 0.5–64 m brush radius.

`SampleBiome`, `SampleSurfaceHeight`, `SampleDensity`, `TrySampleDensity`, `SampleGeneratedDensity`, `TryRaycast`, `IsCollisionReady`, `SetOriginOffset`, and `TryResumeCacheWrites` are public query/control APIs. `SampleDensity` is authoritative and includes cached edits for unloaded chunks; use `SampleGeneratedDensity` when the original seeded field is needed. `TrySampleDensity` reports a cache-read failure without silently returning generated terrain. An edit handle becomes `Completed` after its density is committed and every affected resident chunk mesh is current; unloaded chunks are persisted and rebuild when streamed back in. With colliders enabled, resident meshes are also assigned to the colliders. Edits execute in request order. The writable density field stays package-internal, so edits always obey the bedrock and 60-metre protection rules.

## Limits in 0.1.1

The current package provides distance-based mesh decimation with Transvoxel transition cells, bounded mesh jobs, incremental edit batching, bounded CPU chunk caching, and gzip session caching in `Application.temporaryCachePath`. Each `TerrainWorld` instance owns a unique cache namespace, so a fresh application session always regenerates the source world. Cache files are atomically replaced and are deleted with the world; they are session recovery data, not save-game persistence. Disabling a world cancels outstanding edits and releases queued mesh jobs; re-enabling it rebuilds only stale meshes. Cache write failures pause streaming and edits until `TryResumeCacheWrites` verifies the temporary cache is writable again.
