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
            cameraGo.transform.SetPositionAndRotation(new Vector3(0, 1.65f, 0.65f), Quaternion.Euler(5, 0, 0));
            var camera = cameraGo.GetComponent<Camera>(); camera.fieldOfView = 70; camera.nearClipPlane = 0.05f; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.035f, .045f, .065f);

            var faceMat = Material("BagFace", Color.white, 0.35f);
            faceMat.mainTexture = SmileyTexture(new Color(0.46f, 0.045f, 0.035f));
            var trailMat = UnlitMaterial("GloveTrail", new Color(0.35f, 0.85f, 1f));
            var skinMat = Material("Skin", new Color(0.86f, 0.64f, 0.5f), 0.25f);
            var gloveDebugMat = TransparentMaterial("GloveDebug", "Universal Render Pipeline/Lit", new Color(0.04f, 0.16f, 0.72f, 0.3f));
            var lineMat = TransparentMaterial("DebugLine", "Universal Render Pipeline/Unlit", new Color(0.55f, 1f, 0.75f, 0.6f));

            // The bag rigidbody's origin is the suspension point; PunchingBag adds the ball joint at runtime.
            Primitive("Ceiling Mount", PrimitiveType.Cylinder, new Vector3(0, 3.67f, 1.8f), new Vector3(.32f, .08f, .32f), darkMat);
            var bagGo = new GameObject("Heavy Bag", typeof(Rigidbody));
            bagGo.transform.position = new Vector3(0, 3.55f, 1.8f);
            var bag = bagGo.AddComponent<PunchingBag>();
            VisualChild("Chain", PrimitiveType.Cylinder, bagGo.transform, new Vector3(0, -.43f, 0), new Vector3(.055f, .43f, .055f), darkMat);
            PrimitiveChild("Bag Body", PrimitiveType.Capsule, bagGo.transform, new Vector3(0, -1.9f, 0), new Vector3(.62f, 1.05f, .62f), bagMat);
            VisualChild("Top Band", PrimitiveType.Cylinder, bagGo.transform, new Vector3(0, -1.36f, 0), new Vector3(.65f, .08f, .65f), darkMat);
            VisualChild("Bottom Band", PrimitiveType.Cylinder, bagGo.transform, new Vector3(0, -2.44f, 0), new Vector3(.65f, .06f, .65f), darkMat);
            var face = new GameObject("Smiley Face", typeof(MeshFilter), typeof(MeshRenderer));
            face.transform.SetParent(bagGo.transform, false); face.transform.localPosition = new Vector3(0, -1.95f, 0);
            face.GetComponent<MeshRenderer>().sharedMaterial = faceMat;
            face.AddComponent<CurvedPatch>();

            var systems = new GameObject("Prototype Systems");
            var input = systems.AddComponent<KeyboardInputProvider>();
            var demo = systems.AddComponent<DemoComboInputProvider>();
            var sources = new Object[] { input, demo };

            var leftController = BuildHand(PunchHand.Left, cameraGo.transform, systems, sources, gloveMat, darkMat, trailMat, skinMat, gloveDebugMat, lineMat, out var leftGlove);
            var rightController = BuildHand(PunchHand.Right, cameraGo.transform, systems, sources, gloveMat, darkMat, trailMat, skinMat, gloveDebugMat, lineMat, out var rightGlove);

            var manager = systems.AddComponent<PrototypeGameManager>();
            Set(manager, "resetEventSource", input); Set(manager, "bag", bag);
            SetArray(manager, "punchControllers", leftController, rightController); SetArray(manager, "gloves", leftGlove, rightGlove);
            var overlay = systems.AddComponent<PhysicsDebugOverlay>();
            Set(overlay, "bag", bag); Set(overlay, "demo", demo); SetArray(overlay, "gloves", leftGlove, rightGlove);

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
            BuildWindowsPlayer();
            EditorApplication.Exit(0);
        }

        [MenuItem("Pieced/Build Windows Player")]
        public static void BuildWindowsFromMenu()
        {
            BuildWindowsPlayer();
            EditorUtility.RevealInFinder(WindowsPlayerPath);
        }

        private const string WindowsPlayerPath = "Builds/Windows/PiecedPunchingBag.exe";

        private static void BuildWindowsPlayer()
        {
            Directory.CreateDirectory("Builds/Windows");
            var options = new BuildPlayerOptions
            {
                scenes = new[] { Root + "/Scenes/PunchingBagPrototype.unity" },
                locationPathName = WindowsPlayerPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            };
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                throw new System.Exception("Windows build failed: " + report.summary.result);
            Debug.Log("PIECED_WINDOWS_BUILD_COMPLETE");
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

        private static PlayerPunchController BuildHand(PunchHand hand, Transform camera, GameObject systems, Object[] sources,
            Material gloveMat, Material darkMat, Material trailMat, Material skinMat, Material gloveDebugMat, Material lineMat,
            out PhysicsGlove glove)
        {
            float side = hand == PunchHand.Right ? 1f : -1f;
            var guard = new GameObject(hand + " Guard Pose").transform;
            guard.SetParent(camera, false);
            guard.localPosition = new Vector3(.27f * side, -.25f, hand == PunchHand.Right ? .32f : .38f);
            guard.localRotation = Quaternion.Euler(-12, -12 * side, 4 * side);
            var target = new GameObject(hand + " Tracked Hand").transform;
            target.SetParent(camera, false); target.localPosition = guard.localPosition; target.localRotation = guard.localRotation;
            var handModel = BuildBareFist(target, side, skinMat);

            var gloveGo = new GameObject(hand + " Glove", typeof(Rigidbody));
            gloveGo.transform.SetPositionAndRotation(target.position, target.rotation);
            gloveGo.AddComponent<SphereCollider>().radius = .14f;
            VisualChild("Fist", PrimitiveType.Sphere, gloveGo.transform, Vector3.zero, new Vector3(.30f, .25f, .36f), gloveMat);
            VisualChild("Cuff", PrimitiveType.Cylinder, gloveGo.transform, new Vector3(0, -.20f, -.12f), new Vector3(.17f, .18f, .17f), darkMat);
            VisualChild("Thumb", PrimitiveType.Sphere, gloveGo.transform, new Vector3(-.2f * side, -.03f, .02f), new Vector3(.13f, .17f, .18f), gloveMat);
            var trail = gloveGo.AddComponent<TrailRenderer>();
            trail.sharedMaterial = trailMat; trail.time = .35f; trail.minVertexDistance = .01f;
            trail.widthCurve = AnimationCurve.Linear(0, .04f, 1, 0); trail.shadowCastingMode = ShadowCastingMode.Off; trail.receiveShadows = false;
            var line = new GameObject("Hand Stretch Line").AddComponent<LineRenderer>();
            line.transform.SetParent(gloveGo.transform, false);
            line.sharedMaterial = lineMat; line.positionCount = 2; line.widthMultiplier = .012f;
            line.shadowCastingMode = ShadowCastingMode.Off; line.receiveShadows = false;
            glove = gloveGo.AddComponent<PhysicsGlove>();
            SetEnum(glove, "hand", (int)hand); Set(glove, "target", target);
            Set(glove, "trackedHandModel", handModel); Set(glove, "stretchLine", line); Set(glove, "debugGloveMaterial", gloveDebugMat);

            var controller = systems.AddComponent<PlayerPunchController>();
            SetEnum(controller, "hand", (int)hand); Set(controller, "target", target); Set(controller, "guardPose", guard);
            SetArray(controller, "punchEventSources", sources);
            return controller;
        }

        private static Texture2D SmileyTexture(Color background)
        {
            const int size = 512;
            var path = Root + "/Textures/BagSmiley.png";
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var yellow = new Color(1f, .8f, .12f);
            var ink = new Color(.07f, .05f, .04f);
            float pixel = 1.5f / size;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = new Vector2((x + .5f) / size, (y + .5f) / size);
                float d = Vector2.Distance(p, new Vector2(.5f, .5f));
                var color = background;
                color = Color.Lerp(color, ink, Coverage(.43f - d, pixel));
                color = Color.Lerp(color, yellow, Coverage(.40f - d, pixel));
                foreach (var eye in new[] { new Vector2(.37f, .60f), new Vector2(.63f, .60f) })
                {
                    var e = (p - eye) / new Vector2(.045f, .075f);
                    color = Color.Lerp(color, ink, Coverage((1f - e.magnitude) * .045f, pixel));
                }
                if (p.y < .45f) color = Color.Lerp(color, ink, Coverage(.035f - Mathf.Abs(d - .24f), pixel));
                texture.SetPixel(x, y, color);
            }
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static float Coverage(float signedDistance, float pixel) => Mathf.Clamp01(signedDistance / pixel + .5f);

        private static Material UnlitMaterial(string name, Color color)
        {
            var path = Root + "/Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(Shader.Find("Universal Render Pipeline/Unlit")); AssetDatabase.CreateAsset(material, path); }
            material.color = color; EditorUtility.SetDirty(material); return material;
        }

        /// <summary>A bare fist (knuckles forward along +Z) representing where the tracked hand really is.</summary>
        private static GameObject BuildBareFist(Transform parent, float side, Material skin)
        {
            var fist = new GameObject("Tracked Fist").transform;
            fist.SetParent(parent, false);
            // Back of the hand plus a rounded bar of curled fingers whose top forms the knuckle ridge; thumb tucked underneath.
            VisualChild("Back Of Hand", PrimitiveType.Sphere, fist, new Vector3(0, .004f, -.012f), new Vector3(.088f, .07f, .1f), skin);
            var fingers = VisualChild("Curled Fingers", PrimitiveType.Capsule, fist, new Vector3(0, -.002f, .03f), new Vector3(.056f, .046f, .056f), skin);
            fingers.transform.localRotation = Quaternion.Euler(0, 0, 90);
            var thumb = VisualChild("Thumb", PrimitiveType.Capsule, fist, new Vector3(-.012f * side, -.03f, .034f), new Vector3(.024f, .028f, .024f), skin);
            thumb.transform.localRotation = Quaternion.Euler(0, 0, 90);
            var forearm = VisualChild("Forearm", PrimitiveType.Capsule, fist, new Vector3(0, -.006f, -.15f), new Vector3(.062f, .1f, .056f), skin);
            forearm.transform.localRotation = Quaternion.Euler(90, 0, 0);
            return fist.gameObject;
        }

        private static Material TransparentMaterial(string name, string shader, Color color)
        {
            var path = Root + "/Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(Shader.Find(shader)); AssetDatabase.CreateAsset(material, path); }
            material.color = color;
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static GameObject VisualChild(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            var go = PrimitiveChild(name, type, parent, position, scale, material);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        private static void SetArray(Object target, string property, params Object[] values)
        {
            var serialized = new SerializedObject(target); var array = serialized.FindProperty(property);
            array.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetEnum(Object target, string property, int value)
        {
            var serialized = new SerializedObject(target); serialized.FindProperty(property).enumValueIndex = value; serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Set(Object target, string property, Object value)
        {
            var serialized = new SerializedObject(target); serialized.FindProperty(property).objectReferenceValue = value; serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureFolders()
        {
            Directory.CreateDirectory(Root + "/Scenes"); Directory.CreateDirectory(Root + "/Scripts/Input"); Directory.CreateDirectory(Root + "/Scripts/Player");
            Directory.CreateDirectory(Root + "/Scripts/Environment"); Directory.CreateDirectory(Root + "/Materials"); Directory.CreateDirectory(Root + "/Textures"); Directory.CreateDirectory(Root + "/Prefabs"); Directory.CreateDirectory(Root + "/Settings"); Directory.CreateDirectory(Root + "/Editor");
            AssetDatabase.Refresh();
        }
    }
}
#endif
