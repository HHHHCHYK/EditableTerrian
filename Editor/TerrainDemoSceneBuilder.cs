using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Humanier.Terrain.Editor
{
    public static class TerrainDemoSceneBuilder
    {
        [MenuItem("Humanier/Terrain/Create Demo Scene")]
        public static void CreateDemoScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var world = new GameObject("Humanier Destructible Terrain").AddComponent<TerrainWorld>();
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.15f; light.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
            var cameraObject = new GameObject("Terrain Explorer");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(24f, 28f, -32f);
            cameraObject.transform.rotation = Quaternion.LookRotation(new Vector3(-.55f, -.32f, .76f));
            var camera = cameraObject.AddComponent<Camera>();
            camera.farClipPlane = 600f; camera.clearFlags = CameraClearFlags.Skybox;
            cameraObject.AddComponent<AudioListener>();
            cameraObject.AddComponent<TerrainDemoController>();
            world.SetFocus(camera.transform);
            const string path = "Assets/Scenes/TerrainDemo.unity";
            EditorSceneManager.SaveScene(scene, path);
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(path));
        }
    }
}
