using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace Humanier.Terrain
{
    internal sealed class TerrainSessionCache
    {
        private readonly string directory;
        public string LastError { get; private set; }
        public TerrainSessionCache(int seed, string sessionId)
        {
            directory = Path.Combine(Application.temporaryCachePath, "HumanierTerrain", seed.ToString(), sessionId);
        }
        public bool Save(TerrainChunkData data)
        {
            if (!data.IsModified) return true;
            try
            {
                Directory.CreateDirectory(directory);
                string target = Path.Combine(directory, $"{data.Id.x}_{data.Id.y}_{data.Id.z}.bin");
                using (var file = File.Create(target))
                using (var gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
                using (var writer = new BinaryWriter(gzip))
                {
                    writer.Write(data.Density.Length);
                    foreach (float value in data.Density) writer.Write(value);
                    writer.Write(data.Material.Length);
                    writer.Write(data.Material);
                }
                LastError = null; return true;
            }
            catch (Exception exception) { LastError = exception.Message; return false; }
        }
        public bool TryLoad(TerrainChunkData data)
        {
            string target = Path.Combine(directory, $"{data.Id.x}_{data.Id.y}_{data.Id.z}.bin");
            LastError = null;
            if (!File.Exists(target)) return false;
            try
            {
                using (var file = File.OpenRead(target))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var reader = new BinaryReader(gzip))
                {
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
    }
}
