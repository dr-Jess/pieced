#if UNITY_EDITOR
using Pieced.Networking;
using Pieced.Tracking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using B = Pieced.Editor.PrototypeSceneBuilder;

namespace Pieced.Editor
{
    /// <summary>Generates a scene that shows the streaming phone's dead-reckoned pose, for measuring IMU drift.</summary>
    public static class PhoneDriftSceneBuilder
    {
        public const string ScenePath = "Assets/Pieced/Scenes/PhoneDrift.unity";

        // iPhone 13: 71.5 × 146.7 × 7.65 mm. Prism axes match PhonePoseEstimator's device frame in Unity:
        // x = right edge, y = out of the screen, z = top edge.
        private static readonly Vector3 PhoneSize = new Vector3(0.0715f, 0.00765f, 0.1467f);

        [MenuItem("Pieced/Build Phone Drift Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.32f, 0.35f, 0.42f);
            RenderSettings.ambientEquatorColor = new Color(0.18f, 0.19f, 0.22f);
            RenderSettings.ambientGroundColor = new Color(0.08f, 0.08f, 0.09f);

            var floorMat = B.Material("Floor", new Color(0.12f, 0.14f, 0.17f), 0.2f);
            var gridMat = B.UnlitMaterial("Grid", new Color(0.24f, 0.27f, 0.33f));
            var bodyMat = B.Material("PhoneBody", new Color(0.78f, 0.8f, 0.84f), 0.6f);
            var screenMat = B.Material("PhoneScreen", new Color(0.02f, 0.02f, 0.03f), 0.9f);
            var cameraBumpMat = B.Material("DarkMetal", new Color(0.035f, 0.04f, 0.05f), 0.65f);
            var trailMat = B.UnlitMaterial("GloveTrail", new Color(0.35f, 0.85f, 1f));
            var axisMats = new[]
            {
                B.UnlitMaterial("AxisX", new Color(1f, 0.32f, 0.32f)),
                B.UnlitMaterial("AxisY", new Color(0.4f, 0.9f, 0.4f)),
                B.UnlitMaterial("AxisZ", new Color(0.35f, 0.62f, 1f)),
            };

            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.1f; light.color = new Color(1f, .95f, .88f);
            light.transform.rotation = Quaternion.Euler(50, -30, 0);

            // Origin = where the phone was at reset. The floor sits a hand-height below it.
            var floor = B.Primitive("Floor", PrimitiveType.Cube, new Vector3(0, -1.05f, 0), new Vector3(8, .1f, 8), floorMat);
            for (int i = -8; i <= 8; i++)
            {
                B.VisualChild("Grid X", PrimitiveType.Cube, floor.transform, new Vector3(i * .0625f, .51f, 0), new Vector3(.0015f, .01f, 1f), gridMat);
                B.VisualChild("Grid Z", PrimitiveType.Cube, floor.transform, new Vector3(0, .51f, i * .0625f), new Vector3(1f, .01f, .0015f), gridMat);
            }

            var axes = new GameObject("Origin Axes (x red, y green, z blue = forward at reset)").transform;
            var length = .25f;
            B.VisualChild("X", PrimitiveType.Cube, axes, new Vector3(length / 2, 0, 0), new Vector3(length, .004f, .004f), axisMats[0]);
            B.VisualChild("Y", PrimitiveType.Cube, axes, new Vector3(0, length / 2, 0), new Vector3(.004f, length, .004f), axisMats[1]);
            B.VisualChild("Z", PrimitiveType.Cube, axes, new Vector3(0, 0, length / 2), new Vector3(.004f, .004f, length), axisMats[2]);

            var phone = new GameObject("Phone").transform;
            B.VisualChild("Body", PrimitiveType.Cube, phone, Vector3.zero, PhoneSize, bodyMat);
            B.VisualChild("Screen", PrimitiveType.Cube, phone, new Vector3(0, PhoneSize.y / 2 + .0005f, 0),
                new Vector3(PhoneSize.x - .006f, .001f, PhoneSize.z - .006f), screenMat);
            B.VisualChild("Camera Bump (back, top)", PrimitiveType.Cube, phone, new Vector3(.015f, -PhoneSize.y / 2 - .0015f, .052f),
                new Vector3(.03f, .003f, .03f), cameraBumpMat);
            B.VisualChild("Top Edge Marker", PrimitiveType.Cube, phone, new Vector3(0, 0, PhoneSize.z / 2 + .002f),
                new Vector3(.02f, .004f, .004f), axisMats[2]);

            var trail = new GameObject("Trail").AddComponent<LineRenderer>();
            trail.sharedMaterial = trailMat; trail.widthMultiplier = .004f; trail.positionCount = 0;
            trail.shadowCastingMode = ShadowCastingMode.Off; trail.receiveShadows = false;

            var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraGo.tag = "MainCamera";
            cameraGo.transform.position = new Vector3(.35f, .45f, -.7f);
            cameraGo.transform.LookAt(Vector3.zero);
            var camera = cameraGo.GetComponent<Camera>();
            camera.fieldOfView = 55; camera.nearClipPlane = .01f;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.035f, .045f, .065f);

            var systems = new GameObject("Phone Drift Systems");
            var server = systems.AddComponent<MotionStreamServer>();
            var viewer = systems.AddComponent<PhoneDriftViewer>();
            B.Set(viewer, "server", server); B.Set(viewer, "phone", phone); B.Set(viewer, "trail", trail); B.Set(viewer, "cameraRig", cameraGo.transform);

            EditorSceneManager.SaveScene(scene, ScenePath);
            B.AddToBuildSettings(ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("PIECED_DRIFT_SCENE_BUILD_COMPLETE");
        }

        public static void BuildAndQuit() { Build(); EditorApplication.Exit(0); }
    }
}
#endif
