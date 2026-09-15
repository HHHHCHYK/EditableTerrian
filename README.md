# Humanier Destructible Terrain

`com.humanier.terrain` is an embedded UPM package for continuous, editable terrain. It uses a deterministic signed-density field sampled in 32-cube chunks at a default 0.5 m cell size. Marching tetrahedra produces a smooth low-poly surface and allows caves, tunnels, overhangs, digging, filling, and flattening.

## Use

Create a `TerrainWorld` GameObject and assign a `TerrainWorldSettings` asset. Set a camera or player transform as its focus. Call `RequestEdit` with `TerrainEditRequest.Dig`, `Fill`, or `Flatten`. Edits accept radii from 0.1 through 64 metres and return a `TerrainEditHandle` that reports completion progress.

`SampleBiome`, `SampleSurfaceHeight`, `SampleDensity`, `TryRaycast`, `IsCollisionReady`, and `SetOriginOffset` are public query/control APIs. The writable density field stays package-internal, so edits always obey the bedrock and 60-metre protection rules.

## Limits in 0.1.0

The current package provides full-resolution streamed chunks and incremental edit batching. It deliberately does not claim LOD, Transvoxel transitions, Burst/Jobs, or persistent cross-session saves yet. Modified chunks are gzip-cached only in `Application.temporaryCachePath` for the current Unity session. The public data types were designed so those optimizations can be added without changing gameplay callers.
