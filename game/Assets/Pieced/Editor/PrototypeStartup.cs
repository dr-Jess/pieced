#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Pieced.Editor
{
    [InitializeOnLoad]
    internal static class PrototypeStartup
    {
        private const string PrototypeScene = "Assets/Pieced/Scenes/PunchingBagPrototype.unity";

        static PrototypeStartup()
        {
            EditorApplication.update += OpenPrototypeWhenStartingFromTemporaryScene;
        }

        private static void OpenPrototypeWhenStartingFromTemporaryScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            EditorApplication.update -= OpenPrototypeWhenStartingFromTemporaryScene;

            var activeScene = SceneManager.GetActiveScene();
            var isTemporaryScene = string.IsNullOrEmpty(activeScene.path) ||
                                   activeScene.path.StartsWith("Temp/");
            if (!isTemporaryScene)
                return;

            EditorSceneManager.OpenScene(PrototypeScene, OpenSceneMode.Single);
        }
    }
}
#endif
