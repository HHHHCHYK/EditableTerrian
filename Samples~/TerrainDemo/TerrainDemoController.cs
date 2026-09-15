using Humanier.Terrain;
using UnityEngine;

namespace Humanier.TerrainSamples
{
    public sealed class TerrainDemoController : MonoBehaviour
    {
        [SerializeField] private TerrainWorld terrain;
        [SerializeField] private Camera playerCamera;
        [SerializeField, Range(.5f, 8f)] private float brushRadius = 2f;
        [SerializeField] private float reach = 100f;
        private TerrainBrushMode mode = TerrainBrushMode.Dig;

        private void Awake()
        {
            if (playerCamera == null) playerCamera = Camera.main;
            if (terrain == null) terrain = FindFirstObjectByType<TerrainWorld>();
        }
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) mode = TerrainBrushMode.Dig;
            if (Input.GetKeyDown(KeyCode.Alpha2)) mode = TerrainBrushMode.Fill;
            if (Input.GetKeyDown(KeyCode.Alpha3)) mode = TerrainBrushMode.Flatten;
            brushRadius = Mathf.Clamp(brushRadius + Input.mouseScrollDelta.y * .25f, .5f, 8f);
            if (terrain == null || playerCamera == null || !Input.GetMouseButton(0)) return;
            Ray ray = playerCamera.ScreenPointToRay(Input.mousePosition);
            if (!terrain.TryRaycast(ray, reach, out TerrainRaycastHit hit)) return;
            TerrainEditRequest request = mode == TerrainBrushMode.Dig ? TerrainEditRequest.Dig(hit.point, brushRadius) :
                mode == TerrainBrushMode.Fill ? TerrainEditRequest.Fill(hit.point, brushRadius) : TerrainEditRequest.Flatten(hit.point, brushRadius, hit.point.y);
            terrain.RequestEdit(request);
        }
    }
}
