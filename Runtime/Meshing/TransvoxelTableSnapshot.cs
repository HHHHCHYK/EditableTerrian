using Unity.Collections;
using Unity.Mathematics;

namespace Humanier.Terrain.Meshing
{
    // Burst jobs must never dereference the managed source tables. A terrain world
    // keeps one immutable native snapshot and shares it between its read-only jobs.
    internal struct TransvoxelTableSnapshot
    {
        internal NativeArray<byte> regularVertexCount;
        internal NativeArray<byte> regularTriangleIndexCount;
        internal NativeArray<ushort> regularVertices;
        internal NativeArray<byte> regularIndices;
        internal NativeArray<byte> transitionVertexCount;
        internal NativeArray<byte> transitionTriangleIndexCount;
        internal NativeArray<byte> transitionFlip;
        internal NativeArray<ushort> transitionVertices;
        internal NativeArray<byte> transitionIndices;
        internal NativeArray<int2> transitionCornerOffsets;

        internal TransvoxelTableSnapshot(Allocator allocator)
        {
            regularVertexCount = new NativeArray<byte>(256, allocator);
            regularTriangleIndexCount = new NativeArray<byte>(256, allocator);
            regularVertices = new NativeArray<ushort>(256 * 12, allocator);
            regularIndices = new NativeArray<byte>(256 * 36, allocator);
            transitionVertexCount = new NativeArray<byte>(512, allocator);
            transitionTriangleIndexCount = new NativeArray<byte>(512, allocator);
            transitionFlip = new NativeArray<byte>(512, allocator);
            transitionVertices = new NativeArray<ushort>(512 * 12, allocator);
            transitionIndices = new NativeArray<byte>(512 * 36, allocator);
            transitionCornerOffsets = new NativeArray<int2>(13, allocator);
            CopyRegular();
            CopyTransitions();
        }

        private void CopyRegular()
        {
            for (int code = 0; code < 256; code++)
            {
                RegularCellData cell = TransvoxelTables.RegularCellData[TransvoxelTables.RegularCellClass[code]];
                int vertexCount = (int)cell.GetVertexCount();
                byte[] indices = cell.GetIndices();
                regularVertexCount[code] = (byte)vertexCount;
                regularTriangleIndexCount[code] = (byte)indices.Length;
                for (int i = 0; i < vertexCount; i++) regularVertices[code * 12 + i] = TransvoxelTables.RegularVertexData[code][i];
                for (int i = 0; i < indices.Length; i++) regularIndices[code * 36 + i] = indices[i];
            }
        }

        private void CopyTransitions()
        {
            for (int i = 0; i < transitionCornerOffsets.Length; i++)
            {
                UnityEngine.Vector3Int offset = TransvoxelTables.TransitionCornerOffset[i];
                transitionCornerOffsets[i] = new int2(offset.x, offset.y);
            }
            for (int code = 0; code < 512; code++)
            {
                RegularCellData cell = TransvoxelTables.TransitionRegularCellData[TransvoxelTables.TransitionCellClass[code] & 0x7f];
                int vertexCount = (int)cell.GetVertexCount();
                byte[] indices = cell.GetIndices();
                transitionVertexCount[code] = (byte)vertexCount;
                transitionTriangleIndexCount[code] = (byte)indices.Length;
                transitionFlip[code] = (byte)((TransvoxelTables.TransitionCellClass[code] & 0x80) != 0 ? 1 : 0);
                for (int i = 0; i < vertexCount; i++) transitionVertices[code * 12 + i] = TransvoxelTables.TransitionVertexData[code][i];
                for (int i = 0; i < indices.Length; i++) transitionIndices[code * 36 + i] = indices[i];
            }
        }

        internal void Dispose()
        {
            if (regularVertexCount.IsCreated) regularVertexCount.Dispose();
            if (regularTriangleIndexCount.IsCreated) regularTriangleIndexCount.Dispose();
            if (regularVertices.IsCreated) regularVertices.Dispose();
            if (regularIndices.IsCreated) regularIndices.Dispose();
            if (transitionVertexCount.IsCreated) transitionVertexCount.Dispose();
            if (transitionTriangleIndexCount.IsCreated) transitionTriangleIndexCount.Dispose();
            if (transitionFlip.IsCreated) transitionFlip.Dispose();
            if (transitionVertices.IsCreated) transitionVertices.Dispose();
            if (transitionIndices.IsCreated) transitionIndices.Dispose();
            if (transitionCornerOffsets.IsCreated) transitionCornerOffsets.Dispose();
        }
    }
}
