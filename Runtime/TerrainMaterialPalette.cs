using UnityEngine;

namespace Humanier.Terrain
{
    /// <summary>
    /// Shared base colors for the low-poly terrain renderer and derived visual effects.
    /// Keep this mapping authoritative so particles do not drift from terrain colors.
    /// </summary>
    public static class TerrainMaterialPalette
    {
        public static Color32 GetColor(byte materialId)
        {
            return ColorFor(materialId);
        }

        public static Color32 ColorFor(byte materialId)
        {
            switch (materialId)
            {
                case 2: return new Color32(199, 148, 74, 255);
                case 3: return new Color32(97, 107, 102, 255);
                case 4: return new Color32(117, 87, 58, 255);
                default: return new Color32(64, 148, 66, 255);
            }
        }

        public static byte BucketFor(byte materialId)
        {
            switch (materialId)
            {
                case 2:
                case 3:
                case 4:
                    return materialId;
                default:
                    return 1;
            }
        }
    }
}
