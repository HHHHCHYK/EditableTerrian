# Runtime architecture

`TerrainWorld` owns a FIFO edit queue and a finite set of active `TerrainChunkData` instances. Chunks use integer IDs and derive every sample position from the ID plus a local cell coordinate, which makes generation independent of load order and handles negative coordinates. `SetOriginOffset` shifts rendered mesh coordinates while preserving the deterministic global sample domain.

Each sample stores signed density and material. Positive density is solid, zero is the surface, and negative density is air. A modified chunk is saved through `TerrainSessionCache` before streaming eviction. Cache namespaces are generated per `TerrainWorld`, so reloads inside one runtime restore modifications while later application sessions begin from the seed.

Protected samples at the generated bedrock height and at least 60 metres under their original surface cannot be changed. Protection is evaluated for every brush write rather than using the edited density, so filling or digging never moves the floor.
