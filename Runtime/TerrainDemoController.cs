using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

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
            if (DigitPressed(1)) mode = TerrainBrushMode.Dig;
            if (DigitPressed(2)) mode = TerrainBrushMode.Fill;
            if (DigitPressed(3)) mode = TerrainBrushMode.Flatten;
            if (!TryGetPointer(out Vector2 pointer, out float scroll, out bool primaryPressed)) { preview.enabled = false; return; }
            brushRadius = Mathf.Clamp(brushRadius + scroll * Mathf.Max(.25f, brushRadius * .1f), .5f, 64f);
            hasHit = terrain.TryRaycast(playerCamera.ScreenPointToRay(pointer), reach, out hit);
            preview.enabled = hasHit;
            if (hasHit) UpdatePreview();
            if (hasHit && primaryPressed && Time.unscaledTime >= nextEditTime)
            {
                terrain.RequestEdit(CreateRequest(hit.point));
                nextEditTime = Time.unscaledTime + continuousEditInterval;
            }
        }
        private static bool DigitPressed(int digit)
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return false;
            return digit == 1 ? keyboard.digit1Key.wasPressedThisFrame : digit == 2 ? keyboard.digit2Key.wasPressedThisFrame : keyboard.digit3Key.wasPressedThisFrame;
#else
            return Input.GetKeyDown(digit == 1 ? KeyCode.Alpha1 : digit == 2 ? KeyCode.Alpha2 : KeyCode.Alpha3);
#endif
        }
        private static bool TryGetPointer(out Vector2 position, out float scroll, out bool primaryPressed)
        {
#if ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            if (mouse == null) { position = default; scroll = 0f; primaryPressed = false; return false; }
            position = mouse.position.ReadValue();
            scroll = mouse.scroll.ReadValue().y;
            primaryPressed = mouse.leftButton.isPressed;
            return true;
#else
            position = Input.mousePosition;
            scroll = Input.mouseScrollDelta.y;
            primaryPressed = Input.GetMouseButton(0);
            return true;
#endif
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
