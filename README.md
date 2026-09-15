# Humanier Destructible Terrain

`com.humanier.terrain` is an embedded UPM package for continuous, editable terrain. It uses a deterministic signed-density field sampled in 32-cube chunks at a default 0.5 m cell size. Marching tetrahedra produces a smooth low-poly surface and allows caves, tunnels, overhangs, digging, filling, and flattening.

## Use

Create a `TerrainWorld` GameObject and assign a `TerrainWorldSettings` asset. Set a camera or player transform as its focus. Call `RequestEdit` with `TerrainEditRequest.Dig`, `Fill`, or `Flatten`. Edits accept radii from 0.1 through 64 metres and return a `TerrainEditHandle` that reports completion progress.

`SampleBiome`, `SampleSurfaceHeight`, `SampleDensity`, `TryRaycast`, `IsCollisionReady`, and `SetOriginOffset` are public query/control APIs. The writable density field stays package-internal, so edits always obey the bedrock and 60-metre protection rules.

## Limits in 0.1.0

The current package provides distance-based mesh decimation, incremental edit batching, bounded CPU chunk caching, and gzip session caching in `Application.temporaryCachePath`. Mesh LOD uses shared global density samples and is intended for low-poly visuals; a full Transvoxel transition-cell implementation remains the next rendering upgrade. Modified chunks are deliberately not restored after an application restart.
