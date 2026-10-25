#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Pieced.Editor
{
    /// <summary>Renders the prototype's main camera to a PNG, from the menu or batch mode (-executeMethod ... -previewOut path).</summary>
    public static class PreviewCapture
    {
        private const string ScenePath = "Assets/Pieced/Scenes/PunchingBagPrototype.unity";

        [MenuItem("Pieced/Capture Camera Preview")]
        public static void CaptureFromMenu() => Capture("Builds/Preview/camera.png");

        /// <summary>
        /// Batch mode entry point. -previewDebug shows debug visuals with each tracked hand pushed past its glove,
        /// as if the bag were blocking a punch.
        /// </summary>
        public static void CaptureAndQuit()
        {
            var args = System.Environment.GetCommandLineArgs();
            int index = System.Array.IndexOf(args, "-previewOut");
            bool debug = System.Array.IndexOf(args, "-previewDebug") >= 0;
            Capture(index >= 0 && index + 1 < args.Length ? args[index + 1] : "Builds/Preview/camera.png", debug);
            EditorApplication.Exit(0);
        }

        private static void Capture(string outputPath, bool debug = false)
        {
            if (EditorSceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var camera = Camera.main;
            foreach (var glove in Object.FindObjectsByType<Pieced.Player.PhysicsGlove>(FindObjectsSortMode.None))
            {
                if (debug) glove.Target.position += camera.transform.forward * 0.3f;
                glove.SetDebugVisualsVisible(debug);
            }
            var texture = new RenderTexture(1280, 720, 24);
            camera.targetTexture = texture;
            // A one-off edit-mode render through the SRP batcher can reuse stale per-material data.
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            bool batcher = pipeline != null && pipeline.useSRPBatcher;
            if (pipeline != null) pipeline.useSRPBatcher = false;
            camera.Render();
            camera.Render();
            if (pipeline != null) pipeline.useSRPBatcher = batcher;
            RenderTexture.active = texture;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            camera.targetTexture = null;
            RenderTexture.active = null;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
            File.WriteAllBytes(outputPath, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            Object.DestroyImmediate(texture);
            Debug.Log("PIECED_PREVIEW_WRITTEN " + outputPath);
        }
    }
}
#endif
