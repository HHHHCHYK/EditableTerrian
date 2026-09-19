using Humanier.Terrain;
using NUnit.Framework;
using UnityEngine;

namespace Humanier.Terrain.Tests
{
    public sealed class TerrainMaterialPaletteTests
    {
        [Test]
        public void PaletteMatchesTerrainMaterialBuckets()
        {
            Assert.AreEqual(new Color32(64, 148, 66, 255), TerrainMaterialPalette.ColorFor(0));
            Assert.AreEqual(new Color32(64, 148, 66, 255), TerrainMaterialPalette.ColorFor(1));
            Assert.AreEqual(new Color32(199, 148, 74, 255), TerrainMaterialPalette.ColorFor(2));
            Assert.AreEqual(new Color32(97, 107, 102, 255), TerrainMaterialPalette.ColorFor(3));
            Assert.AreEqual(new Color32(117, 87, 58, 255), TerrainMaterialPalette.ColorFor(4));
        }

        [Test]
        public void UnknownMaterialsUseDefaultRenderedBucket()
        {
            TerrainMaterialVolumeBreakdown breakdown = new TerrainMaterialVolumeBreakdown(1f, 2f, 3f, 4f);
            Assert.AreEqual(10f, breakdown.TotalVolume, 0.0001f);
            Assert.AreEqual(1f, breakdown.GetVolume(0), 0.0001f);
            Assert.AreEqual(1f, breakdown.GetVolume(99), 0.0001f);
            Assert.AreEqual(2f, breakdown.GetVolume(2), 0.0001f);
            Assert.AreEqual(3f, breakdown.GetVolume(3), 0.0001f);
            Assert.AreEqual(4f, breakdown.GetVolume(4), 0.0001f);
        }
    }
}
