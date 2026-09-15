using UnityEngine;

namespace Humanier.Terrain
{
    [DisallowMultipleComponent]
    public sealed class TerrainDemoController : MonoBehaviour
    {
        [SerializeField] private TerrainWorld terrain;
        [SerializeField] private Camera playerCamera;
        [SerializeField, Range(.5f, 64f)] private float brushRadius = 2f;
        [SerializeField, Min(1f)] private float reach = 120f;
        [SerializeField, Min(.02f)] private float continuousEditInterval = .12f;
        [SerializeField] private TerrainBrushMode mode = TerrainBrushMode.Dig;
        private LineRenderer preview;
        private TerrainRaycastHit hit;
        private bool hasHit;
        private float nextEditTime;

        private void Awake()
        {
            if (playerCamera == null) playerCamera = GetComponent<Camera>();
            if (playerCamera == null) playerCamera = Camera.main;
            if (terrain == null) terrain = FindFirstObjectByType<TerrainWorld>();
            preview = gameObject.AddComponent<LineRenderer>();
            preview.loop = true; preview.useWorldSpace = true; preview.widthMultiplier = .035f; preview.positionCount = 40;
            preview.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            preview.startColor = new Color(.85f, .95f, 1f, .85f); preview.endColor = preview.startColor;
        }
        private void Update()
        {
            if (terrain == null || playerCamera == null) return;
            if (Input.GetKeyDown(KeyCode.Alpha1)) mode = TerrainBrushMode.Dig;
            if (Input.GetKeyDown(KeyCode.Alpha2)) mode = TerrainBrushMode.Fill;
            if (Input.GetKeyDown(KeyCode.Alpha3)) mode = TerrainBrushMode.Flatten;
            brushRadius = Mathf.Clamp(brushRadius + Input.mouseScrollDelta.y * Mathf.Max(.25f, brushRadius * .1f), .5f, 64f);
            hasHit = terrain.TryRaycast(playerCamera.ScreenPointToRay(Input.mousePosition), reach, out hit);
            preview.enabled = hasHit;
            if (hasHit) UpdatePreview();
            if (hasHit && Input.GetMouseButton(0) && Time.unscaledTime >= nextEditTime)
            {
                terrain.RequestEdit(CreateRequest(hit.point));
                nextEditTime = Time.unscaledTime + continuousEditInterval;
            }
        }
        private TerrainEditRequest CreateRequest(Vector3 point)
        {
            if (mode == TerrainBrushMode.Fill) return TerrainEditRequest.Fill(point, brushRadius, 1f, 1);
            if (mode == TerrainBrushMode.Flatten) return TerrainEditRequest.Flatten(point, brushRadius, point.y);
            return TerrainEditRequest.Dig(point, brushRadius);
        }
        private void UpdatePreview()
        {
            Vector3 tangent = Vector3.Cross(hit.normal, Vector3.up);
            if (tangent.sqrMagnitude < .01f) tangent = Vector3.Cross(hit.normal, Vector3.right);
            tangent.Normalize(); Vector3 bitangent = Vector3.Cross(hit.normal, tangent).normalized;
            for (int i = 0; i < preview.positionCount; i++)
            {
                float angle = i * Mathf.PI * 2f / preview.positionCount;
                preview.SetPosition(i, hit.point + (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * brushRadius + hit.normal * .025f);
            }
        }
        private void OnDestroy() { if (preview != null && preview.sharedMaterial != null) Destroy(preview.sharedMaterial); }
    }
}
