#if UNITY_EDITOR
using System.IO;
using Pieced.Environment;
using Pieced.Input;
using Pieced.Player;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Pieced.Editor
{
    public static class PrototypeSceneBuilder
    {
        private const string Root = "Assets/Pieced";

        [MenuItem("Pieced/Build Prototype Scene")]
        public static void Build()
        {
            EnsureFolders();
            var pipeline = CreatePipeline();
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.28f, 0.31f, 0.38f);
            RenderSettings.ambientEquatorColor = new Color(0.16f, 0.17f, 0.2f);
            RenderSettings.ambientGroundColor = new Color(0.07f, 0.07f, 0.08f);

            var floorMat = Material("Floor", new Color(0.12f, 0.14f, 0.17f), 0.2f);
            var wallMat = Material("Walls", new Color(0.2f, 0.23f, 0.28f), 0.1f);
            var bagMat = Material("Bag", new Color(0.46f, 0.045f, 0.035f), 0.35f);
            var darkMat = Material("DarkMetal", new Color(0.035f, 0.04f, 0.05f), 0.65f);
            var gloveMat = Material("Glove", new Color(0.04f, 0.16f, 0.72f), 0.3f);

            Primitive("Floor", PrimitiveType.Cube, new Vector3(0, -0.1f, 1.5f), new Vector3(9, .2f, 10), floorMat);
            Primitive("Back Wall", PrimitiveType.Cube, new Vector3(0, 2.25f, 4.2f), new Vector3(9, 4.5f, .2f), wallMat);
            Primitive("Left Wall", PrimitiveType.Cube, new Vector3(-4.4f, 2.25f, 1.5f), new Vector3(.2f, 4.5f, 5.5f), wallMat);
            Primitive("Right Wall", PrimitiveType.Cube, new Vector3(4.4f, 2.25f, 1.5f), new Vector3(.2f, 4.5f, 5.5f), wallMat);

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.15f; light.color = new Color(1f, .93f, .82f);
            lightGo.transform.rotation = Quaternion.Euler(42, -28, 0);

            var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraGo.tag = "MainCamera";
            cameraGo.transform.SetPositionAndRotation(new Vector3(0, 1.65f, -3.5f), Quaternion.Euler(2, 0, 0));
            var camera = cameraGo.GetComponent<Camera>(); camera.fieldOfView = 58; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.035f, .045f, .065f);

            var pivot = new GameObject("Bag Suspension Pivot");
            pivot.transform.position = new Vector3(0, 3.55f, 1.8f);
            var bag = pivot.AddComponent<PunchingBag>();
            PrimitiveChild("Ceiling Mount", PrimitiveType.Cylinder, pivot.transform, new Vector3(0, .12f, 0), new Vector3(.32f, .08f, .32f), darkMat);
            PrimitiveChild("Chain", PrimitiveType.Cylinder, pivot.transform, new Vector3(0, -.36f, 0), new Vector3(.055f, .45f, .055f), darkMat);
            PrimitiveChild("Heavy Bag", PrimitiveType.Capsule, pivot.transform, new Vector3(0, -1.3f, 0), new Vector3(.62f, 1.05f, .62f), bagMat);
            PrimitiveChild("Top Band", PrimitiveType.Cylinder, pivot.transform, new Vector3(0, -.76f, 0), new Vector3(.65f, .08f, .65f), darkMat);
            PrimitiveChild("Bottom Band", PrimitiveType.Cylinder, pivot.transform, new Vector3(0, -1.84f, 0), new Vector3(.58f, .06f, .58f), darkMat);

            var rest = new GameObject("Right Glove Rest Pose").transform;
            rest.SetParent(cameraGo.transform, false); rest.localPosition = new Vector3(.53f, -.48f, .83f); rest.localRotation = Quaternion.Euler(-12, -12, 4);
            var impact = new GameObject("Right Glove Impact Pose").transform;
            impact.SetParent(cameraGo.transform, false); impact.localPosition = new Vector3(.2f, -.10f, 4.45f); impact.localRotation = Quaternion.Euler(0, 0, 0);
            var glove = new GameObject("Right Glove").transform;
            glove.SetParent(cameraGo.transform, false); glove.localPosition = rest.localPosition; glove.localRotation = rest.localRotation;
            PrimitiveChild("Fist", PrimitiveType.Sphere, glove, Vector3.zero, new Vector3(.30f, .25f, .36f), gloveMat);
            PrimitiveChild("Cuff", PrimitiveType.Cylinder, glove, new Vector3(0, -.20f, -.12f), new Vector3(.17f, .18f, .17f), darkMat);
            PrimitiveChild("Thumb", PrimitiveType.Sphere, glove, new Vector3(-.2f, -.03f, .02f), new Vector3(.13f, .17f, .18f), gloveMat);

            var systems = new GameObject("Prototype Systems");
            var input = systems.AddComponent<KeyboardInputProvider>();
            var controller = systems.AddComponent<PlayerPunchController>();
            var manager = systems.AddComponent<PrototypeGameManager>();
            Set(controller, "punchEventSource", input); Set(controller, "glove", glove); Set(controller, "restPose", rest); Set(controller, "impactPose", impact); Set(controller, "bag", bag);
            Set(manager, "resetEventSource", input); Set(manager, "punchController", controller); Set(manager, "bag", bag);

            var scenePath = Root + "/Scenes/PunchingBagPrototype.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
            PlayerSettings.productName = "Pieced Punching Bag Prototype";
            PlayerSettings.companyName = "Pieced";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "com.pieced.punchingbagprototype");
            AssetDatabase.SaveAssets();
            Debug.Log("PIECED_SCENE_BUILD_COMPLETE");
        }

        public static void BuildAndQuit() { Build(); EditorApplication.Exit(0); }

        public static void BuildWindowsDevelopment()
        {
            Directory.CreateDirectory("Builds/Windows");
            var options = new BuildPlayerOptions
            {
                scenes = new[] { Root + "/Scenes/PunchingBagPrototype.unity" },
                locationPathName = "Builds/Windows/PiecedPunchingBag.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            };
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                throw new System.Exception("Windows build failed: " + report.summary.result);
            Debug.Log("PIECED_WINDOWS_BUILD_COMPLETE");
            EditorApplication.Exit(0);
        }

        private static UniversalRenderPipelineAsset CreatePipeline()
        {
            var rendererPath = Root + "/Settings/PrototypeRenderer.asset";
            var pipelinePath = Root + "/Settings/PrototypeURP.asset";
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            if (existing != null) return existing;
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(renderer, rendererPath);
            var asset = UniversalRenderPipelineAsset.Create(renderer);
            asset.name = "Prototype URP";
            AssetDatabase.CreateAsset(asset, pipelinePath);
            return asset;
        }

        private static Material Material(string name, Color color, float smoothness)
        {
            var path = Root + "/Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(material, path); }
            material.color = color; material.SetFloat("_Smoothness", smoothness); EditorUtility.SetDirty(material); return material;
        }

        private static GameObject Primitive(string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(type); go.name = name; go.transform.position = position; go.transform.localScale = scale; go.GetComponent<Renderer>().sharedMaterial = material; return go;
        }

        private static GameObject PrimitiveChild(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(type); go.name = name; go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = scale; go.GetComponent<Renderer>().sharedMaterial = material; return go;
        }

        private static void Set(Object target, string property, Object value)
        {
            var serialized = new SerializedObject(target); serialized.FindProperty(property).objectReferenceValue = value; serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureFolders()
        {
            Directory.CreateDirectory(Root + "/Scenes"); Directory.CreateDirectory(Root + "/Scripts/Input"); Directory.CreateDirectory(Root + "/Scripts/Player");
            Directory.CreateDirectory(Root + "/Scripts/Environment"); Directory.CreateDirectory(Root + "/Materials"); Directory.CreateDirectory(Root + "/Prefabs"); Directory.CreateDirectory(Root + "/Settings"); Directory.CreateDirectory(Root + "/Editor");
            AssetDatabase.Refresh();
        }
    }
}
#endif
