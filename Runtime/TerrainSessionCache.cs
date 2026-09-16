using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace Humanier.Terrain
{
    internal sealed class TerrainSessionCache
    {
        private const int FormatMarker = 0x48545231; // HTR1
        private readonly string directory;
        public string LastError { get; private set; }
        public TerrainSessionCache(int seed, string sessionId)
        {
            directory = Path.Combine(Application.temporaryCachePath, "HumanierTerrain", seed.ToString(), sessionId);
        }
        public bool Save(TerrainChunkData data)
        {
            if (!data.IsModified) return true;
            string target = PathFor(data.Id);
            string temporary = target + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
                using (var writer = new BinaryWriter(gzip))
                {
                    writer.Write(FormatMarker);
                    writer.Write(data.Density.Length);
                    foreach (float value in data.Density) writer.Write(value);
                    writer.Write(data.Material.Length);
                    writer.Write(data.Material);
                }
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
                LastError = null; return true;
            }
            catch (Exception exception)
            {
                TryDelete(temporary);
                LastError = exception.Message;
                return false;
            }
        }
        public bool TryLoad(TerrainChunkData data)
        {
            string target = PathFor(data.Id);
            LastError = null;
            if (!File.Exists(target)) return false;
            try
            {
                using (var file = File.OpenRead(target))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var reader = new BinaryReader(gzip))
                {
                    if (reader.ReadInt32() != FormatMarker) { LastError = "Cached terrain data has an unsupported format."; return false; }
                    int count = reader.ReadInt32(); if (count != data.Density.Length) { LastError = "Cached density resolution does not match this terrain world."; return false; }
                    var density = new float[count]; for (int i = 0; i < count; i++) density[i] = reader.ReadSingle();
                    int materials = reader.ReadInt32(); if (materials != data.Material.Length) { LastError = "Cached material resolution does not match this terrain world."; return false; }
                    var material = reader.ReadBytes(materials); if (material.Length != materials) { LastError = "Cached material data is truncated."; return false; }
                    data.Restore(density, material); return true;
                }
            }
            catch (Exception exception) { LastError = exception.Message; return false; }
        }

        public bool TryResumeWrites()
        {
            string probe = Path.Combine(directory, ".write-probe");
            try
            {
                Directory.CreateDirectory(directory);
                using (FileStream file = File.Open(probe, FileMode.Create, FileAccess.Write, FileShare.None)) file.WriteByte(0);
                File.Delete(probe);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                return false;
            }
        }

        public bool TryDiscard(TerrainChunkId id)
        {
            string target = PathFor(id);
            try
            {
                if (File.Exists(target)) File.Delete(target);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                return false;
            }
        }

        public bool HasEntry(TerrainChunkId id) => File.Exists(PathFor(id));

        public bool Dispose()
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                return false;
            }
        }

        private string PathFor(TerrainChunkId id) => Path.Combine(directory, $"{id.x}_{id.y}_{id.z}.bin");
        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* Preserve the original write failure. */ }
        }
    }
}
